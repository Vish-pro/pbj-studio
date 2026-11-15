using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using KcsPdfSigner.App.Dialogs;

namespace KcsPdfSigner.App;

public partial class App : Application
{
    public const string ProductName = "PB&J Studio";

    /// <summary>%APPDATA%\KcsPdfStudio: recent files, error log and other per-user state.</summary>
    public static string DataFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KcsPdfStudio");

    public static string ErrorLogPath => Path.Combine(DataFolder, "error.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;

        void ShowMainWindow()
        {
            window.Show();
            window.Activate();

            // Supports dragging PDFs onto the executable, or a shortcut that names them.
            foreach (var argument in e.Args)
            {
                if (File.Exists(argument) && argument.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    window.OpenFile(argument);
                }
            }
        }

        // Brand animation first, then the main window.
        var splash = new SplashWindow();
        splash.Closed += (_, _) => ShowMainWindow();
        splash.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogError("UI thread", e.Exception);
        e.Handled = true;

        try
        {
            MessageDialog.Show(MainWindow, e.Exception.Message, ProductName,
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
            // The dialog itself failed; the log already holds the details.
        }
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        LogError("AppDomain", e.ExceptionObject as Exception);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogError("Background task", e.Exception);
        e.SetObserved();
    }

    /// <summary>Appends an exception to %APPDATA%\KcsPdfStudio\error.log. Never throws.</summary>
    public static void LogError(string source, Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            var text = new StringBuilder()
                .Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("] ")
                .Append(source).AppendLine()
                .AppendLine(exception?.ToString() ?? "(no exception object)")
                .AppendLine();
            File.AppendAllText(ErrorLogPath, text.ToString(), Encoding.UTF8);
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
