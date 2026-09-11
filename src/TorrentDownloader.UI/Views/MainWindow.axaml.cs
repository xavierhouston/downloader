using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TorrentDownloader.UI.ViewModels;

namespace TorrentDownloader.UI.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private async void OnAddMagnetClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
            await vm.AddMagnetAsync();
    }

    private async void OnAddTorrentFileClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select a .torrent file",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Torrent files") { Patterns = new[] { "*.torrent" } },
            },
        });

        var file = files.FirstOrDefault();
        if (file is null)
            return;

        var localPath = file.TryGetLocalPath();
        if (!string.IsNullOrEmpty(localPath))
            await vm.AddTorrentFileAsync(localPath);
    }

    private async void OnAddLinkClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
            await vm.AddLinkAsync();
    }

    private async void OnChangeDownloadDirectoryClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select download folder",
            AllowMultiple = false,
        });

        var folder = folders.FirstOrDefault();
        var localPath = folder?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(localPath))
            vm.DownloadDirectory = localPath;
    }
}
