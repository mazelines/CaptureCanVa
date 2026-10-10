using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace CaptureCanva;

public partial class App : Application
{
    internal static string? LogDirectoryOverride { get; set; }
    public static string LogDirectory => LogDirectoryOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CaptureCanva", "logs");

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log("Unhandled (AppDomain)", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log("Unobserved task", args.Exception);
            args.SetObserved();
        };
        Log("Startup", null);
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log($"Exit (code {e.ApplicationExitCode})\n{Environment.StackTrace}", null);
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log("Unhandled (UI)", e.Exception);
        MessageBox.Show(e.Exception.Message, "CaptureCanva 오류", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true; // keep the app alive
    }

    public static void Log(string title, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            File.AppendAllText(Path.Combine(LogDirectory, $"{DateTime.Now:yyyyMMdd}.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {title}{(ex != null ? "\n" + ex : "")}\n");
        }
        catch
        {
        }
    }
}
