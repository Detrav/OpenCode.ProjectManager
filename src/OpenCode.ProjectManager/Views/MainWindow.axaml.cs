using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using OpenCode.ProjectManager.ViewModels;

namespace OpenCode.ProjectManager.Views;

public partial class MainWindow : Window
{
    private DispatcherTimer? _processTimer;

    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        _ = viewModel.InitializeAsync();

        _processTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _processTimer.Tick += (_, _) => viewModel.RefreshProcessesCommand.Execute(null);
        _processTimer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _processTimer?.Stop();
        _processTimer = null;
        base.OnClosed(e);
    }

    private void ProjectList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (DetailsScrollViewer is not null)
            {
                DetailsScrollViewer.Offset = new Vector(0, 0);
            }
        }, DispatcherPriority.Background);
    }

    private void DatabasePathBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is MainViewModel viewModel)
        {
            viewModel.ReloadCommand.Execute(null);
            e.Handled = true;
        }
    }

    private async void BrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select the OpenCode database",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("SQLite database")
                {
                    Patterns = ["*.db", "*.sqlite", "*.sqlite3"]
                }
            ]
        });

        if (files.Count == 0)
        {
            return;
        }

        var path = files[0].TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            await viewModel.LoadDatabaseAsync(path);
        }
    }

    private async void CopyDatabasePath_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel &&
            !string.IsNullOrWhiteSpace(viewModel.DatabasePath) &&
            Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(viewModel.DatabasePathDisplay);
            viewModel.StatusMessage = "Database path copied to the clipboard.";
        }
    }
}
