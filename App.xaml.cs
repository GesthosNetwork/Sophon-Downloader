using System.Reflection;
using System.Runtime.InteropServices;

namespace SophonDownloader;

public partial class App : Application
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    internal static void ApplyLogLevel(string level)
    {
        try
        {
            LogManager.GlobalThreshold = NLog.LogLevel.FromString(string.IsNullOrWhiteSpace(level) ? "Debug" : level);
        }
        catch (Exception ex)
        {
            LogManager.GlobalThreshold = NLog.LogLevel.Debug;
            try { Logger.Warn(ex, "Failed to apply configured log level; falling back to Debug."); } catch {}
        }
    }

    internal static void ApplyConsoleSetting(bool enabled)
    {
        try
        {
            bool shown = GetConsoleWindow() != IntPtr.Zero;
            if (enabled && !shown)
            {
                if (!AllocConsole())
                    throw new InvalidOperationException("AllocConsole failed.");

                Console.Title = "SophonDownloader Diagnostics";
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
                LogManager.ReconfigExistingLoggers();
                Logger.Info("Diagnostic console enabled.");
                Logger.Debug("Console output stream attached and NLog loggers reconfigured.");
            }
            else if (!enabled && shown)
            {
                Logger.Info("Diagnostic console disabled.");
                LogManager.Flush();
                FreeConsole();
                Console.SetOut(TextWriter.Null);
                Console.SetError(TextWriter.Null);
            }
        }
        catch (Exception ex)
        {
            try { Logger.Warn(ex, "Failed to apply Show Console setting."); }
            catch {}
        }
    }

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    public static string Version { get; } = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
    internal static string Copyright { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright?.Trim() ?? string.Empty;

    protected override void OnStartup(StartupEventArgs e)
    {
        DistributionIntegrityGuard.Verify();

        try
        {
            LogManager.Setup().LoadConfigurationFromAssemblyResource(Assembly.GetExecutingAssembly());
            AppSettings startupSettings = AppSettingsStore.Load();
            ApplyLogLevel(startupSettings.LogLevel);
            ApplyConsoleSetting(startupSettings.ShowConsole);
            Logger.Info($"SophonDownloader starting. Version {Version}. CPU={Environment.ProcessorCount}, DefaultThreads={ConcurrencyDefaults.Threads}, DefaultHttpConnections={ConcurrencyDefaults.MaxHttpConnections}, LogLevel={startupSettings.LogLevel}.");
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
            base.OnStartup(e);

            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            Logger.Fatal(ex, "Fatal application startup error.");
            LogManager.Flush();
            MessageBox.Show($"SophonDownloader failed to start.\n\nType:\n{ex.GetType().FullName}\n\nMessage:\n{ex.Message}\n\nDetails:\n{ex}", "SophonDownloader - Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Logger.Info($"SophonDownloader shutting down. Exit code: {e.ApplicationExitCode}");
            LogManager.Flush();
            LogManager.Shutdown();
        }
        catch {}

        base.OnExit(e);
    }

    private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        try { Logger.Fatal(e.Exception, "Unhandled WPF dispatcher exception."); }
        catch {}

        if (MainWindow is MainWindow mainWindow && mainWindow.IsShuttingDown)
        {
            e.Handled = true;
            return;
        }

        MessageBox.Show($"An unexpected application error occurred.\n\n{e.Exception}", "SophonDownloader - Runtime Error", MessageBoxButton.OK, MessageBoxImage.Error);

        e.Handled = true;
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is not Exception ex) return;

        try
        {
            Logger.Fatal(ex, "Unhandled AppDomain exception.");
            LogManager.Flush();
        }
        catch {}
    }

    private void TaskScheduler_UnobservedTaskException(
        object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try { Logger.Error(e.Exception, "Unobserved task exception."); }
        catch {}

        e.SetObserved();
    }
}
