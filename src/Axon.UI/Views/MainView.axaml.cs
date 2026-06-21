using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Axon.UI.Sharing;
using Axon.UI.ViewModels;

namespace Axon.UI.Views;

/// <summary>Primary dashboard view (also the Android / mobile single-view shell).</summary>
public sealed partial class MainView : UserControl
{
    private readonly InsightCardRenderer _cardRenderer = new();

    public MainView() => InitializeComponent();

    /// <summary>
    /// Renders the current anomaly insight to a shareable PNG and saves it via the
    /// OS file picker — the virality hook (rec #7). Stateless renderer; no biometric
    /// values are drawn, only the explanation copy.
    /// </summary>
    private async void OnShareInsightClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DashboardViewModel vm) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var card = new InsightCard(
            Title: "AXON INSIGHT",
            Headline: vm.AnomalyInsightTitle,
            Subtitle: vm.AnomalyInsightDetail);
        var png = _cardRenderer.RenderPng(card);

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save insight card",
            SuggestedFileName = "axon-insight.png",
            DefaultExtension = "png",
            FileTypeChoices = [new FilePickerFileType("PNG image") { Patterns = ["*.png"] }]
        });

        if (file is null) return;

        await using var stream = await file.OpenWriteAsync();
        await stream.WriteAsync(png);
    }
}
