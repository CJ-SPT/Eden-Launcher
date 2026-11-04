using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Eden.Launcher.Core;

[assembly: AvaloniaTestApplication(typeof(Eden.Launcher.Tests.TestApplication))]

namespace Eden.Launcher.Tests;

public static class TestApplication
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public sealed class ViewModelTests
{
    [AvaloniaFact]
    public async Task PlayRequiresSignInAndVerifiedClientAndSecretsAreCleared()
    {
        var api = new Api();
        var credentials = new Credentials();
        var model = Create(api, credentials);
        await model.InitializeAsync();
        Assert.False(model.CanPlay);
        model.UserName = "Developer";
        model.Password = "secret";
        model.ConfirmPassword = model.Password;
        model.CreateAccount = true;
        model.DeveloperAccount = true;
        model.DeveloperCode = "test invitation";
        model.RememberMe = true;
        await model.AccountCommand.ExecuteAsync(null);
        Assert.True(model.IsSignedIn);
        Assert.True(api.Registration!.Developer);
        Assert.Equal("secret", api.Registration.Password);
        Assert.Equal("test invitation", api.Registration.DeveloperCode);
        Assert.Empty(model.Password);
        Assert.Empty(model.DeveloperCode);
        Assert.Equal("refresh", credentials.Value);
        model.ClientRoot = "fixture";
        await model.UseClientCommand.ExecuteAsync(null);
        Assert.True(model.CanPlay);
        var window = new MainWindow { DataContext = model };
        window.Show();
        Assert.Contains(
            window.GetVisualDescendants().OfType<Button>(),
            button => button.Command == model.PrimaryActionCommand && button.IsEnabled
        );
        await model.SignOutCommand.ExecuteAsync(null);
        Assert.False(model.CanPlay);
        Assert.Null(credentials.Value);
        Assert.True(api.SignedOut);
        window.Close();
    }

    [AvaloniaFact]
    public async Task RememberedSessionRestoresWithoutPersistingAPassword()
    {
        var credentials = new Credentials { Value = "old refresh" };
        var model = Create(new Api(), credentials);
        await model.InitializeAsync();
        Assert.True(model.IsSignedIn);
        Assert.True(model.RememberMe);
        Assert.Empty(model.Password);
        Assert.Equal("refresh", credentials.Value);
    }

    [AvaloniaFact]
    public async Task OfflineServerAndPasswordMismatchLeavePlayDisabled()
    {
        var api = new Api { Offline = true };
        var model = Create(api, new Credentials());
        await model.InitializeAsync();
        Assert.False(model.IsConnected);
        Assert.False(model.CanPlay);
        Assert.True(model.IsError);
        api.Offline = false;
        await model.ConnectCommand.ExecuteAsync(null);
        model.CreateAccount = true;
        model.Password = "a long test password";
        model.ConfirmPassword = "different";
        await model.AccountCommand.ExecuteAsync(null);
        Assert.Null(api.Registration);
        Assert.False(model.IsSignedIn);
        Assert.Contains("do not match", model.Status);
    }

    [AvaloniaFact]
    public async Task EditingServerAddressDropsThePreviousAccountSession()
    {
        var model = Create(new Api(), new Credentials { Value = "refresh" });
        await model.InitializeAsync();
        Assert.True(model.IsSignedIn);
        model.ApiAddress = "https://another.example";
        Assert.False(model.IsSignedIn);
        Assert.False(model.IsConnected);
        Assert.False(model.CanPlay);
    }

    private static MainViewModel Create(Api api, Credentials credentials) =>
        new(new Settings(), credentials, new Installation(), new Launcher(), _ => api);

    private sealed class Settings : ISettingsStore
    {
        public Task<LauncherSettings> LoadAsync() => Task.FromResult(LauncherSettings.Default());

        public Task SaveAsync(LauncherSettings settings) => Task.CompletedTask;
    }

    private sealed class Credentials : ICredentialStore
    {
        public string? Value { get; set; }

        public string? Read(string serverAddress, string userName) => Value;

        public void Write(string serverAddress, string userName, string? refreshToken) => Value = refreshToken;
    }

    private sealed class Installation : IClientInstallation
    {
        public Task AdoptAsync(string root, CancellationToken cancellation) => Task.CompletedTask;

        public Task PrepareAsync(
            string source,
            string destination,
            IProgress<PreparationProgress>? progress,
            CancellationToken cancellation
        ) => Task.CompletedTask;
    }

    private sealed class Launcher : IClientLauncher
    {
        public int RunningCount => 0;

        public bool IsRunning(string serverId, long accountId) => false;

        public event Action? Changed
        {
            add { }
            remove { }
        }

        public Task LaunchAsync(
            string root,
            ServerReply server,
            SessionReply session,
            LaunchReply launch,
            CancellationToken cancellation
        ) => Task.CompletedTask;
    }

    private sealed class Api : ILauncherApiClient
    {
        public bool Offline { get; set; }
        public AccountRequest? Registration { get; private set; }
        public bool SignedOut { get; private set; }

        public Task<ServerReply> ServerAsync(CancellationToken cancellation = default) =>
            Offline
                ? throw new HttpRequestException("offline")
                : Task.FromResult(new ServerReply("test", "Test", 1, ClientBuild.Number, "localhost:26000", "http://localhost:26001"));

        public Task<AccountReply> RegisterAsync(AccountRequest request, CancellationToken cancellation = default)
        {
            Registration = request;
            return Task.FromResult(new AccountReply(1000003, request.UserName, request.Developer));
        }

        public Task<SessionReply> SignInAsync(SignInRequest request, CancellationToken cancellation = default) =>
            Task.FromResult(
                new SessionReply(1000003, request.UserName, "access", long.MaxValue, request.RememberMe ? "refresh" : null, true)
            );

        public Task<SessionReply> RefreshAsync(string refreshToken, CancellationToken cancellation = default) =>
            SignInAsync(new("Developer", "", true), cancellation);

        public Task SignOutAsync(string accessToken, CancellationToken cancellation = default)
        {
            SignedOut = true;
            return Task.CompletedTask;
        }

        public Task<LaunchReply> LaunchAsync(string accessToken, LaunchRequest request, CancellationToken cancellation = default) =>
            throw new NotSupportedException();

        public void Dispose() { }
    }
}
