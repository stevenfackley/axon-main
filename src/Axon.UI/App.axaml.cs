using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Axon.UI.ViewModels;
using Axon.UI.Views;

// Alias to avoid clash with Android.App.Application on the android TFM.
using AvaloniaApp = Avalonia.Application;

namespace Axon.UI;

/// <summary>
/// Avalonia Application root. Wires the MVVM shell for both desktop and Android
/// lifetimes without importing any platform-specific types at this layer.
/// </summary>
public sealed class App : AvaloniaApp
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var vm = new MainWindowViewModel();

        switch (ApplicationLifetime)
        {
            // Desktop (Windows / Linux / macOS)
            case IClassicDesktopStyleApplicationLifetime desktop:
                desktop.MainWindow = new MainWindow { DataContext = vm };
                break;

            // Android (single-activity model via ISingleViewApplicationLifetime)
            case ISingleViewApplicationLifetime singleView:
                singleView.MainView = new MainView { DataContext = vm };
                break;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
