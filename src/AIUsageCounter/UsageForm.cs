using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace AIUsageCounter;

public class Settings
{
    public int? X { get; set; }
    public int? Y { get; set; }
    public bool TopMost { get; set; } = true;
    public double Opacity { get; set; } = 0.92;
    public int RefreshMinutes { get; set; } = 5;
    public bool ShowClaudeSession { get; set; }
    public List<string> HiddenProviders { get; set; } = [];
}

public class UsageForm : Form
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIUsageCounter", "settings.json");
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "AIUsageCounter";

    private static readonly Color Bg = Color.FromArgb(28, 28, 30);
    private static readonly Color Border = Color.FromArgb(60, 60, 64);
    private static readonly Color Text1 = Color.FromArgb(235, 235, 235);
    private static readonly Color Text2 = Color.FromArgb(150, 150, 155);
    private static readonly Color Track = Color.FromArgb(55, 55, 60);
    private static readonly Color ErrorColor = Color.FromArgb(240, 110, 100);

    private readonly Settings _settings;
    private readonly System.Windows.Forms.Timer _fetchTimer = new();
    private readonly System.Windows.Forms.Timer _tickTimer = new() { Interval = 30_000 };
    private readonly ToolStripMenuItem _topMostItem;
    private readonly ToolStripMenuItem _autostartItem;
    private readonly ToolStripMenuItem _visibilityItem;
    private readonly NotifyIcon _trayIcon;

    private List<UsageSection> _sections = [];
    private DateTime? _updatedAt;
    private bool _loading;

    private const int BaseWidth = 270;
    private const int SectionH = 26;
    private const int ErrorH = 32;
    private const int RowH = 50;
    private const int FooterH = 20;

    private readonly bool _demo;
    private static readonly Icon AppIcon = LoadAppIcon();

    private static Icon LoadAppIcon()
    {
        using var stream = typeof(UsageForm).Assembly.GetManifestResourceStream("AIUsageCounter.AppIcon.ico");
        return stream is null ? SystemIcons.Application : new Icon(stream);
    }

    public UsageForm(bool demo = false)
    {
        _demo = demo;
        _settings = demo ? new Settings() : LoadSettings();

        Text = "AI Usage";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        BackColor = Bg;
        TopMost = _settings.TopMost;
        Opacity = Math.Clamp(_settings.Opacity, 0.3, 1.0);
        Font = new Font("Segoe UI", 9f);

        var menu = new ContextMenuStrip();
        _visibilityItem = new ToolStripMenuItem("Hide widget", null, (_, _) => ToggleVisibility());
        menu.Items.Add(_visibilityItem);
        menu.Items.Add("Refresh", null, async (_, _) => await RefreshUsage());
        _topMostItem = new ToolStripMenuItem("Always on top", null, (_, _) =>
        {
            TopMost = _settings.TopMost = !_settings.TopMost;
            _topMostItem!.Checked = TopMost;
            SaveSettings();
        }) { Checked = TopMost };
        menu.Items.Add(_topMostItem);

        var opacityMenu = new ToolStripMenuItem("Opacity");
        foreach (var v in new[] { 1.0, 0.92, 0.8, 0.65, 0.5 })
        {
            var item = new ToolStripMenuItem($"{v * 100:0}%") { Tag = v };
            item.Click += (_, _) => { Opacity = _settings.Opacity = v; SaveSettings(); };
            opacityMenu.DropDownItems.Add(item);
        }
        opacityMenu.DropDownOpening += (_, _) =>
        {
            foreach (ToolStripMenuItem i in opacityMenu.DropDownItems)
                i.Checked = Math.Abs((double)i.Tag! - Opacity) < 0.01;
        };
        menu.Items.Add(opacityMenu);

        var refreshMenu = new ToolStripMenuItem("Refresh interval");
        foreach (var m in new[] { 1, 2, 5, 10, 15, 30 })
        {
            var item = new ToolStripMenuItem($"{m} min") { Tag = m };
            item.Click += (_, _) =>
            {
                _settings.RefreshMinutes = m;
                _fetchTimer.Interval = m * 60_000;
                SaveSettings();
            };
            refreshMenu.DropDownItems.Add(item);
        }
        refreshMenu.DropDownOpening += (_, _) =>
        {
            foreach (ToolStripMenuItem i in refreshMenu.DropDownItems)
                i.Checked = (int)i.Tag! == _settings.RefreshMinutes;
        };
        menu.Items.Add(refreshMenu);

        _autostartItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleAutostart())
            { Checked = IsAutostartEnabled() };
        menu.Items.Add(_autostartItem);
        menu.Items.Add(new ToolStripSeparator());
        var servicesMenu = new ToolStripMenuItem("Services");
        servicesMenu.DropDownItems.Add(new ToolStripMenuItem("placeholder"));
        servicesMenu.DropDownOpening += (_, _) => BuildServicesMenu(servicesMenu);
        menu.Items.Insert(1, servicesMenu);
        var claudeSessionItem = new ToolStripMenuItem("Claude session (5h)")
        {
            Checked = _settings.ShowClaudeSession,
        };
        claudeSessionItem.Click += (_, _) =>
        {
            _settings.ShowClaudeSession = !_settings.ShowClaudeSession;
            claudeSessionItem.Checked = _settings.ShowClaudeSession;
            SaveSettings();
            ApplyLayout();
        };
        menu.Items.Insert(2, claudeSessionItem);

        var pagesMenu = new ToolStripMenuItem("Open usage page");
        pagesMenu.DropDownItems.Add(new ToolStripMenuItem("placeholder"));
        pagesMenu.DropDownOpening += (_, _) =>
        {
            pagesMenu.DropDownItems.Clear();
            foreach (var p in Providers.Active(Providers.All, _settings.HiddenProviders))
                pagesMenu.DropDownItems.Add(p.Name, null, (_, _) =>
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(p.UsagePageUrl) { UseShellExecute = true }));
            if (pagesMenu.DropDownItems.Count == 0)
                pagesMenu.DropDownItems.Add(new ToolStripMenuItem("No services") { Enabled = false });
        };
        menu.Items.Add(pagesMenu);
        menu.Items.Add("Exit", null, (_, _) => Close());
        ContextMenuStrip = menu;

        Icon = AppIcon;
        _trayIcon = new NotifyIcon
        {
            Icon = AppIcon,
            Text = "AI Usage Counter",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ToggleVisibility();
        };
        VisibleChanged += (_, _) => _visibilityItem.Text = Visible ? "Hide widget" : "Show widget";

        _fetchTimer.Interval = Math.Max(1, _settings.RefreshMinutes) * 60_000;
        _fetchTimer.Tick += async (_, _) => await RefreshUsage();
        _tickTimer.Tick += (_, _) => Invalidate();

        MouseDown += OnDragMouseDown;
        DoubleClick += async (_, _) => await RefreshUsage();

        UpdateSize();
        PlaceWindow();
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _fetchTimer.Start();
        _tickTimer.Start();
        await RefreshUsage();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _settings.X = Left;
        _settings.Y = Top;
        SaveSettings();
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _fetchTimer.Dispose();
        _tickTimer.Dispose();
        base.OnFormClosed(e);
    }

    private void ToggleVisibility()
    {
        if (Visible) Hide();
        else ShowWidget();
    }

    internal void ShowWidget()
    {
        Show();
        Activate();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        UpdateSize();
    }

    private float Scale1 => DeviceDpi / 96f;
    private int S(float v) => (int)Math.Round(v * Scale1);

    private void UpdateSize()
    {
        int h = 4;
        foreach (var sec in DisplayedSections())
            h += SectionH + sec.Limits.Count * RowH + (sec.Error != null ? ErrorH : 0);
        if (_sections.Count == 0) h += SectionH + ErrorH;
        h += FooterH + 2;
        ClientSize = new Size(S(BaseWidth), S(h));
    }

    private void PlaceWindow()
    {
        var wa = Screen.PrimaryScreen!.WorkingArea;
        if (_settings.X is int x && _settings.Y is int y &&
            Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(new Rectangle(x, y, Width, Height))))
        {
            Location = new Point(x, y);
        }
        else
        {
            Location = new Point(wa.Right - Width - 12, wa.Bottom - Height - 12);
        }
    }

    private async Task RefreshUsage()
    {
        if (_loading) return;
        _loading = true;
        Invalidate();
        try
        {
            // Detection runs on every refresh, so signing in to a CLI later shows up without a restart.
            var tasks = Providers.Active(Providers.All, _settings.HiddenProviders).Select(p => Fetch(p.Name, p.FetchAsync));
            _sections = _demo ? Providers.DemoSections(DateTimeOffset.Now) : [.. await Task.WhenAll(tasks)];
            if (_sections.Any(x => x.Error == null)) _updatedAt = DateTime.Now;
        }
        finally
        {
            _loading = false;
            ApplyLayout();
        }
    }

    private IEnumerable<UsageSection> DisplayedSections() =>
        _sections.Select(s => UsageFormat.ForDisplay(s, _settings.ShowClaudeSession));

    private void ApplyLayout()
    {
        if (IsDisposed || Disposing) return;
        int oldBottom = Bottom;
        UpdateSize();
        // Keep the window anchored at its bottom edge if it sits in the lower half of the screen.
        var workingArea = Screen.FromControl(this).WorkingArea;
        if (Top > workingArea.Top + workingArea.Height / 2) Top += oldBottom - Bottom;
        Invalidate();
    }

    private void BuildServicesMenu(ToolStripMenuItem servicesMenu)
    {
        servicesMenu.DropDownItems.Clear();
        foreach (var p in Providers.All)
        {
            bool detected = p.IsDetected();
            var item = new ToolStripMenuItem(detected ? p.Name : $"{p.Name} (not signed in)")
            {
                Checked = detected && !_settings.HiddenProviders.Contains(p.Name),
                Enabled = detected,
            };
            item.Click += async (_, _) =>
            {
                if (!_settings.HiddenProviders.Remove(p.Name)) _settings.HiddenProviders.Add(p.Name);
                SaveSettings();
                await RefreshUsage();
            };
            servicesMenu.DropDownItems.Add(item);
        }
    }

    private Task<UsageSection> Fetch(string name, Func<Task<UsageSection>> fetch) =>
        UsageFormat.FetchSafeAsync(name, fetch, _sections.FirstOrDefault(x => x.Name == name));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using var borderPen = new Pen(Border);
        g.DrawRectangle(borderPen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);

        using var titleFont = new Font("Segoe UI Semibold", 9.5f);
        using var smallFont = new Font("Segoe UI", 8f);
        using var pctFont = new Font("Segoe UI Semibold", 9f);
        using var text1 = new SolidBrush(Text1);
        using var text2 = new SolidBrush(Text2);
        using var errBrush = new SolidBrush(ErrorColor);
        var noWrap = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };

        int pad = S(10);
        int w = ClientSize.Width - pad * 2;
        int y = S(4);

        if (_sections.Count == 0)
        {
            string msg = _loading ? "Loading…"
                : Providers.All.Any(p => p.IsDetected())
                    ? "All services are hidden — right-click → Services"
                    : "No signed-in CLI found. Sign in to Claude Code, Codex or Grok CLI, then right-click → Refresh.";
            g.DrawString(msg, smallFont, text2, new RectangleF(pad, y + S(6), w, S(SectionH + ErrorH)));
        }

        foreach (var sec in DisplayedSections())
        {
            // Section header: colored dot, provider name, plan on the right
            using (var dot = new SolidBrush(Providers.All.FirstOrDefault(p => p.Name == sec.Name)?.Color ?? Text2))
                g.FillEllipse(dot, pad, y + S(8), S(8), S(8));
            g.DrawString(sec.Name, titleFont, text1, pad + S(12), y + S(3));
            string plan = sec.Plan is { Length: > 0 } ? sec.Plan.ToUpperInvariant() : "";
            var planSize = g.MeasureString(plan, smallFont);
            g.DrawString(plan, smallFont, text2, ClientSize.Width - pad - planSize.Width, y + S(5));
            y += S(SectionH);

            foreach (var l in sec.Limits)
            {
                DrawLimit(g, l, pad, y, w, smallFont, pctFont, text1, text2);
                y += S(RowH);
            }

            if (sec.Error != null)
            {
                g.DrawString("⚠ " + sec.Error, smallFont, errBrush, new RectangleF(pad, y, w, S(ErrorH)));
                y += S(ErrorH);
            }
        }

        // Footer
        string footer = _loading ? "refreshing…" : _updatedAt is DateTime u ? $"updated {u:HH:mm}" : "";
        g.DrawString(footer, smallFont, text2,
            new RectangleF(pad, ClientSize.Height - S(FooterH) - S(2), w, S(FooterH)), noWrap);
    }

    private void DrawLimit(Graphics g, UsageLimit l, int pad, int y, int w, Font smallFont, Font pctFont, Brush text1, Brush text2)
    {
        double used = UsageFormat.Clamp(l.Percent);
        Color barColor = UsageFormat.SeverityOf(used) switch
        {
            Severity.Critical => Color.FromArgb(230, 80, 70),
            Severity.Warning => Color.FromArgb(235, 170, 60),
            _ => Color.FromArgb(90, 190, 120),
        };

        g.DrawString(l.Title, smallFont, text1, pad, y);
        string pct = UsageFormat.PercentText(used);
        var ps = g.MeasureString(pct, pctFont);
        using (var pb = new SolidBrush(barColor))
            g.DrawString(pct, pctFont, pb, ClientSize.Width - pad - ps.Width, y - S(1));

        int barY = y + S(19);
        int barH = S(7);
        using (var trackBrush = new SolidBrush(Track))
            FillRounded(g, trackBrush, new Rectangle(pad, barY, w, barH), barH / 2);
        int fillW = (int)(w * used / 100);
        if (fillW > 0)
        {
            using var fillBrush = new SolidBrush(barColor);
            FillRounded(g, fillBrush, new Rectangle(pad, barY, Math.Max(fillW, barH), barH), barH / 2);
        }

        if (l.ResetsAt is DateTimeOffset r)
            g.DrawString($"resets {UsageFormat.FormatReset(r.LocalDateTime, DateTime.Now)}", smallFont, text2, pad, barY + barH + S(2));
    }

    private static void FillRounded(Graphics g, Brush b, Rectangle r, int radius)
    {
        if (radius <= 0) { g.FillRectangle(b, r); return; }
        int d = radius * 2;
        using var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        g.FillPath(b, path);
    }

    // --- Dragging the borderless window ---

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private void OnDragMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || e.Clicks > 1) return;
        ReleaseCapture();
        SendMessage(Handle, 0xA1 /* WM_NCLBUTTONDOWN */, (IntPtr)2 /* HTCAPTION */, IntPtr.Zero);
        _settings.X = Left;
        _settings.Y = Top;
        SaveSettings();
    }

    // --- Settings & autostart ---

    private static Settings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    private void SaveSettings()
    {
        if (_demo) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private static bool IsAutostartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(AppName) is string;
    }

    private void ToggleAutostart()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (IsAutostartEnabled()) key.DeleteValue(AppName, false);
        else key.SetValue(AppName, $"\"{Application.ExecutablePath}\"");
        _autostartItem.Checked = IsAutostartEnabled();
    }
}
