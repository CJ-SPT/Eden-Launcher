using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Eden.Launcher.Core;

namespace Eden.Launcher;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            string? profileDirectory = Environment.GetEnvironmentVariable("EDEN_LAUNCHER_PROFILE_DIRECTORY");
            string directory =
                profileDirectory
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Eden Launcher");
            var processes = new ClientProcesses(new ClientBuild());
            var viewModel = new MainViewModel(
                new SettingsStore(directory),
                new WindowsCredentialStore(Path.Combine(directory, "credentials")),
                new ClientInstallation(new ClientBuild()),
                processes
            );
            var window = new MainWindow { DataContext = viewModel };
            if (profileDirectory is not null)
            {
                window.Title += " · " + new DirectoryInfo(directory).Name;
            }
            using var iconStream = AssetLoader.Open(new Uri("avares://Eden.Desktop/Assets/eden.ico"));
            var icon = new WindowIcon(iconStream);
            window.Icon = icon;
            var open = new NativeMenuItem("Open Eden Launcher");
            open.Click += (_, _) =>
            {
                window.Show();
                window.Activate();
            };
            var exit = new NativeMenuItem("Exit");
            exit.Click += (_, _) =>
            {
                if (processes.RunningCount == 0 && !viewModel.IsBusy)
                {
                    desktop.Shutdown();
                }
                else
                {
                    window.Show();
                    window.Activate();
                    viewModel.Status = "Close your game clients before exiting the launcher.";
                }
            };
            var tray = new TrayIcon
            {
                Icon = icon,
                ToolTipText = "Eden Launcher",
                Menu = new NativeMenu { Items = { open, exit } },
            };
            tray.Clicked += (_, _) =>
            {
                window.Show();
                window.Activate();
            };
            TrayIcon.SetIcons(this, [tray]);
            desktop.MainWindow = window;
            window.Closing += (_, args) =>
            {
                if (processes.RunningCount > 0 || viewModel.IsBusy)
                {
                    args.Cancel = true;
                    window.Hide();
                }
            };
            bool initialized = false;
            window.Opened += async (_, _) =>
            {
                if (!initialized)
                {
                    initialized = true;
                    await viewModel.InitializeAsync();
                }
            };
            desktop.Exit += (_, _) => tray.Dispose();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
