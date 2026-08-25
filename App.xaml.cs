using NLog;
using System.Reflection;
using System.Windows;

namespace SophonDownloader;

public partial class App : Application
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    public static string Version { get; } = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
    internal static string Copyright { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright?.Trim() ?? string.Empty;

    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            LogManager.Setup().LoadConfigurationFromAssemblyResource(Assembly.GetExecutingAssembly());
            Logger.Info($"SophonDownloader starting. Version {Version}");
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

            MessageBox.Show(
                $"SophonDownloader failed to start.\n\nType:\n{ex.GetType().FullName}\n\nMessage:\n{ex.Message}\n\nDetails:\n{ex}",
                "SophonDownloader - Startup Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

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
        catch { }

        base.OnExit(e);
    }

    private void App_DispatcherUnhandledException(
        object sender,
        System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        try { Logger.Fatal(e.Exception, "Unhandled WPF dispatcher exception."); }
        catch { }

        MessageBox.Show(
            $"An unexpected application error occurred.\n\n{e.Exception}",
            "SophonDownloader - Runtime Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

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
        catch { }
    }

    private void TaskScheduler_UnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs e)
    {
        try { Logger.Error(e.Exception, "Unobserved task exception."); }
        catch { }

        e.SetObserved();
    }
}
