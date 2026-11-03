using System.Runtime.InteropServices;
using Avalonia;

namespace Eden.Launcher;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (OperatingSystem.IsWindows())
        {
            SetCurrentProcessExplicitAppUserModelID("Eden.Launcher.Desktop");
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
