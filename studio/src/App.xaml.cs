using System.Windows;
using System.Windows.Threading;

namespace AnyPS5Studio;

public partial class App : Application
{
    public App()
    {
        AppLog.Info($"AnyPS5 Studio started, version {typeof(App).Assembly.GetName().Version}");
        DispatcherUnhandledException += OnUiCrash;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Error("Unhandled error", (Exception)e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => AppLog.Error("Unobserved background error", e.Exception);
        Exit += (_, _) => AppLog.Info("AnyPS5 Studio closed");
    }

    private static void OnUiCrash(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("The app crashed", e.Exception);
        MessageBox.Show($"AnyPS5 Studio hit a problem and has to close.\n\n{e.Exception.Message}\n\nDetails are in:\n{AppLog.FilePath}",
            "AnyPS5 Studio", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
