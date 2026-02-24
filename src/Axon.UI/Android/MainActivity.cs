// Android entry point for Axon – only compiled when targeting net9.0-android.
#if ANDROID
using Android.App;
using Android.Content.PM;
using Avalonia;
using Avalonia.Android;

namespace Axon.UI.Android;

/// <summary>
/// The single Activity that hosts the entire Avalonia UI surface on Android.
/// Avalonia renders directly into a SurfaceView via SkiaSharp, so no additional
/// Android Views are needed – the MVVM shell takes over from here.
/// </summary>
[Activity(
    Label = "Axon",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    ConfigurationChanges =
        ConfigChanges.Orientation |
        ConfigChanges.ScreenSize |
        ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity<App>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        => base.CustomizeAppBuilder(builder);
}
#endif
