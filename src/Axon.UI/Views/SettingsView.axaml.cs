using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Axon.UI.ViewModels;

namespace Axon.UI.Views;

public sealed partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    /// <summary>
    /// Opens the OS file picker for CSV files and hands the chosen paths to the
    /// ViewModel for import. The View owns the dialog because it needs the TopLevel.
    /// </summary>
    private async void OnImportClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import biometric CSV",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("CSV / data files")
                {
                    Patterns = ["*.csv", "*.txt"]
                }
            ]
        });

        if (files.Count == 0) return;

        var paths = new List<string>(files.Count);
        foreach (var f in files)
            paths.Add(f.Path.LocalPath);

        await vm.RunImportAsync(paths);
    }
}
