// Desktop entry point – only active for net9.0 (WinExe / Linux / macOS).
// Android uses MainActivity as its entry point instead.
#if !ANDROID
using Avalonia;

namespace Axon.UI;

internal static class Program
{
    // Avalonia configuration, do not remove; also used by visual designer.
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
                  .UsePlatformDetect()
                  .LogToTrace();
}
#endif
