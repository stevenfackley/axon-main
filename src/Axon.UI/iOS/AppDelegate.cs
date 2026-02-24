// iOS entry point for Axon – only compiled when targeting net9.0-ios.
#if IOS
using Avalonia;
using Avalonia.iOS;
using Foundation;
using UIKit;

namespace Axon.UI.iOS;

/// <summary>
/// The iOS application delegate that bootstraps the Avalonia UI surface.
///
/// Avalonia renders the full UI via SkiaSharp into a UIView/CAMetalLayer,
/// so no additional UIKit views are required. The MVVM shell takes over
/// immediately after <see cref="CustomizeAppBuilder"/> completes.
///
/// HealthKit authorisation is NOT requested here; it is deferred to the
/// <c>SettingsViewModel</c> onboarding flow so the user understands why
/// access is needed before the system permission dialog appears.
/// </summary>
[Register("AppDelegate")]
public sealed class AppDelegate : AvaloniaAppDelegate<App>
{
    /// <summary>
    /// Customise the Avalonia app builder for iOS.
    /// GPU-first rendering is handled automatically by Avalonia.iOS via Metal.
    /// </summary>
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        => base.CustomizeAppBuilder(builder)
               .WithInterFont()
               .LogToTrace();
}
#endif
