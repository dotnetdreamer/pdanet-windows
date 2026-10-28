using System.Threading;
using PdaNetPortable.Services;

namespace PdaNetPortable;

/// <summary>
/// Entry point. Routes privileged-helper invocations ("--priv ...") before any WPF
/// startup, otherwise launches the GUI (single-instance guarded).
/// </summary>
public static class Program
{
    public static bool StartMinimizedRequested { get; private set; }

    private static Mutex? _singleInstanceMutex;

    [STAThread]
    public static int Main(string[] args)
    {
        // Privileged helper path (elevated child process) — do the work and exit.
        if (args.Length > 0 && args[0] == "--priv")
            return PrivilegedHelper.Run(args);

        // Headless end-to-end verification (no GUI).
        if (args.Length > 0 && args[0] == "--selftest")
            return SelfTest.Run();
        if (args.Length > 0 && args[0] == "--selftest-agent")
            return SelfTest.RunAgent();

        if (args.Length > 0 && (args[0] == "--version" || args[0] == "-v"))
        {
            Console.WriteLine(Core.Constants.AppVersion);
            return 0;
        }

        if (args.Length > 0 && args[0] == "--selftest-location")
            return SelfTest.RunLocation(args.Length > 1 ? args[1] : "Eindhoven");

        StartMinimizedRequested = args.Any(a =>
            a.Equals("--start-minimized", StringComparison.OrdinalIgnoreCase));

        var cfg = ConfigManager.Instance.Config;
        Logger.Instance.SetLevel(cfg.LogLevel);
        Logger.Instance.Info($"{Core.Constants.AppName} {Core.Constants.AppVersion} starting (data: {Core.AppPaths.DataDir})");

        // Single instance guard (mirrors the upstream lockfile behaviour).
        if (cfg.SingleInstance)
        {
            _singleInstanceMutex = new Mutex(initiallyOwned: true, @"Global\PdaNetWindowsPortable", out bool createdNew);
            if (!createdNew)
            {
                System.Windows.MessageBox.Show(
                    "PdaNet Windows is already running.",
                    Core.Constants.AppName,
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                return 0;
            }
        }

        var app = new App();
        app.InitializeComponent();   // merges the Cyberpunk theme resource dictionary
        var window = new MainWindow();
        return app.Run(window);
    }
}
