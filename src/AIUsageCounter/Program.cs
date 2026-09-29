namespace AIUsageCounter;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // --demo shows sample data without reading any credentials (used for README screenshots).
        bool demo = args.Contains("--demo", StringComparer.OrdinalIgnoreCase);

        using var mutex = new Mutex(true, "AIUsageCounter_SingleInstance", out bool isNew);
        if (!isNew && !demo) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new UsageForm(demo));
    }
}
