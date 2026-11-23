using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using KcsPdfSigner.App.Dialogs;
using KcsPdfSigner.App.Services;

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
        // Command line OCR (no window): PBJStudio.exe --ocr input.pdf output.pdf [--lang hin+eng]
        if (e.Args.Length > 0 && string.Equals(e.Args[0], "--ocr", StringComparison.OrdinalIgnoreCase))
        {
            Shutdown(CommandLineOcr.Run(e.Args));
            return;
        }

        // One window per user: hand any PDFs to the copy that is already running and quit.
        if (!SingleInstance.TryBecomeFirst())
        {
            var wanted = e.Args
                .Where(a => a.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && File.Exists(a))
                .Select(Path.GetFullPath)
                .ToList();
            if (SingleInstance.SendToRunning(wanted))
            {
                Shutdown();
                return;
            }
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;

        var mainShown = false;
        var pending = new List<string>();

        void OpenAndFocus(IEnumerable<string> paths)
        {
            foreach (var path in paths)
            {
                if (File.Exists(path) && path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) window.OpenFile(path);
            }

            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
            window.Topmost = true; // brings it to the front even when another app has focus
            window.Topmost = false;
            window.Focus();
        }

        SingleInstance.Listen(paths => Dispatcher.BeginInvoke(() =>
        {
            if (mainShown) OpenAndFocus(paths);
            else pending.AddRange(paths); // still on the splash: open them as soon as the window shows
        }));

        void ShowMainWindow()
        {
            window.Show();
            window.Activate();
            mainShown = true;

            if (Services.AppSettings.Current.ReopenLastSession) window.RestoreLastSession();

            // Supports dragging PDFs onto the executable, or a shortcut that names them.
            foreach (var argument in e.Args)
            {
                if (File.Exists(argument) && argument.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    window.OpenFile(argument);
                }
            }

            if (pending.Count > 0)
            {
                OpenAndFocus(pending);
                pending.Clear();
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
