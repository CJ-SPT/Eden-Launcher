using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Eden.Launcher;

public sealed partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private void DragWindow(object? sender, PointerPressedEventArgs args)
    {
        if (args.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(args);
        }
    }

    private void Minimize(object? sender, RoutedEventArgs args) => WindowState = WindowState.Minimized;

    private void Maximize(object? sender, RoutedEventArgs args) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseWindow(object? sender, RoutedEventArgs args) => Close();

    private void OpenNews(object? sender, RoutedEventArgs args)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.ShowAccountDialog = false;
            viewModel.ShowSettings = false;
        }
    }

    private void OpenSupport(object? sender, RoutedEventArgs args) =>
        Process.Start(new ProcessStartInfo("https://github.com/CJ-SPT/Eden/issues") { UseShellExecute = true });

    private async void OpenWorldSetup(object? sender, RoutedEventArgs args)
    {
        if (DataContext is MainViewModel { WorldSetupUri: { } setup } viewModel && !await Launcher.LaunchUriAsync(setup))
        {
            viewModel.Status = "Open this address in your browser: " + setup.AbsoluteUri;
        }
    }

    private async void BrowseCertificateAuthority(object? sender, RoutedEventArgs args)
    {
        var result = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "Choose the server's CA certificate",
                FileTypeFilter = [new("Certificates") { Patterns = ["*.cer", "*.crt", "*.der", "*.pem"] }, FilePickerFileTypes.All],
            }
        );
        if (DataContext is MainViewModel viewModel && result.Count == 1 && result[0].TryGetLocalPath() is { } path)
        {
            viewModel.CertificateAuthorityPath = path;
        }
    }

    private async void BrowseClient(object? sender, RoutedEventArgs args)
    {
        if (DataContext is MainViewModel viewModel && await PickFolderAsync("Choose Eden client folder") is { } path)
        {
            viewModel.ClientRoot = path;
        }
    }

    private async void BrowseSource(object? sender, RoutedEventArgs args)
    {
        if (DataContext is MainViewModel viewModel && await PickFolderAsync("Choose original EVE installation") is { } path)
        {
            viewModel.SourceRoot = path;
        }
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return result.Count == 1 ? result[0].TryGetLocalPath() : null;
    }
}
