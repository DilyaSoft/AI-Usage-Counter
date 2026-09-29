namespace AIUsageCounter;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // --demo shows sample data without reading any credentials (used for README screenshots).
        bool demo = args.Contains("--demo", StringComparer.OrdinalIgnoreCase);

        using var showSignal = demo ? null : new EventWaitHandle(false, EventResetMode.AutoReset, "AIUsageCounter_ShowWidget");
        using var mutex = new Mutex(true, "AIUsageCounter_SingleInstance", out bool isNew);
        if (!isNew && !demo)
        {
            showSignal!.Set();
            return;
        }

        ApplicationConfiguration.Initialize();
        using var form = new UsageForm(demo);
        using var showTimer = new System.Windows.Forms.Timer { Interval = 250 };
        if (!demo)
        {
            showTimer.Tick += (_, _) =>
            {
                if (showSignal!.WaitOne(0)) form.ShowWidget();
            };
            showTimer.Start();
        }
        Application.Run(form);
    }
}
