using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
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

    [AvaloniaFact]
    public async Task AccountsCannotInterruptPlayAndLaunchKeepsItsCapturedSession()
    {
        string root = Directory.CreateTempSubdirectory("eden-launch-race-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "tq"));
            byte[] prefix = [1, 2, 3, 4];
            await File.WriteAllBytesAsync(Path.Combine(root, "tq", "manifest.dat"), prefix, TestContext.Current.CancellationToken);
            var api = new Api();
            var launcher = new Launcher();
            var build = new ClientBuild([], prefix.Length, Convert.ToHexString(SHA256.HashData(prefix)));
            var model = new MainViewModel(new Settings(), new Credentials(), new Installation(), launcher, _ => api, build);
            await model.InitializeAsync();
            model.UserName = "Developer";
            await model.AccountCommand.ExecuteAsync(null);
            model.ClientRoot = root;
            await model.UseClientCommand.ExecuteAsync(null);
            var pending = new TaskCompletionSource<ServerReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            api.PendingServer = pending.Task;
            var window = new MainWindow { DataContext = model };
            window.Show();
            Task play = model.PlayCommand.ExecuteAsync(null);
            try
            {
                Dispatcher.UIThread.RunJobs();
                Assert.True(model.IsBusy);
                Assert.False(model.OpenAccountCommand.CanExecute(null));
                Assert.False(window.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Accounts")).IsEnabled);
                model.OpenAccountCommand.Execute(null);
                Assert.True(model.IsSignedIn);
                Assert.False(model.ShowAccountDialog);
                // A connection reset must not replace the identity already captured by the pending launch.
                model.ApiAddress = "https://another.example";
            }
            finally
            {
                pending.SetResult(Api.Server);
                await play;
                window.Close();
            }
            Assert.Equal("access", api.LaunchedAccess);
            Assert.Equal(1000003, launcher.LaunchedSession!.AccountId);
            Assert.Equal(root, launcher.LaunchedRoot);
            Assert.False(model.IsError);
            Assert.False(model.IsBusy);
            Assert.True(model.OpenAccountCommand.CanExecute(null));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [AvaloniaTheory]
    [InlineData("")]
    [InlineData("not a URL")]
    [InlineData("/relative/path")]
    [InlineData("https://")]
    public async Task InvalidServerAddressesAreReportedByConnectAndSave(string address)
    {
        var model = new MainViewModel(new Settings(), new Credentials(), new Installation(), new Launcher());
        model.ApiAddress = address;
        await model.ConnectCommand.ExecuteAsync(null);
        Assert.True(model.IsError);
        Assert.False(model.IsConnected);
        Assert.False(model.CanPlay);
        Assert.False(model.IsBusy);
        Assert.Contains("server address", model.Status);
        await model.SaveServerCommand.ExecuteAsync(null);
        Assert.True(model.IsError);
        Assert.False(model.IsBusy);
        Assert.Contains("server address", model.Status);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedServerRepliesAreReportedWithoutExposingTheirContents(bool json)
    {
        var api = new Api
        {
            ServerError = json ? new System.Text.Json.JsonException("private response") : new FormatException("private response"),
        };
        var model = Create(api, new Credentials());
        await model.InitializeAsync();
        Assert.True(model.IsError);
        Assert.False(model.IsBusy);
        Assert.False(model.IsConnected);
        Assert.Contains("invalid data", model.Status);
        Assert.DoesNotContain("private response", model.Status);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledClientRecheckClearsReadiness(bool cancelled)
    {
        var installation = new Installation();
        var api = new Api();
        var model = new MainViewModel(new Settings(), new Credentials(), installation, new Launcher(), _ => api);
        await model.InitializeAsync();
        await model.AccountCommand.ExecuteAsync(null);
        model.ClientRoot = "fixture";
        await model.UseClientCommand.ExecuteAsync(null);
        Assert.True(model.CanPlay);
        installation.AdoptError = cancelled ? new OperationCanceledException() : new IOException("damaged client");
        await model.UseClientCommand.ExecuteAsync(null);
        Assert.False(model.ClientReady);
        Assert.False(model.CanPlay);
        Assert.False(model.PlayCommand.CanExecute(null));
        Assert.False(model.IsBusy);
        Assert.DoesNotContain("READY", model.ClientStatus);
        installation.AdoptError = null;
        await model.UseClientCommand.ExecuteAsync(null);
        Assert.True(model.CanPlay);
    }

    [AvaloniaFact]
    public async Task ExpiredSavedSignInClearsCredentialsAndOpensThePrefilledForm()
    {
        var api = new Api { SessionExpiry = 0 };
        var credentials = new Credentials();
        var model = Create(api, credentials);
        await model.InitializeAsync();
        model.UserName = "Developer";
        model.RememberMe = true;
        await model.AccountCommand.ExecuteAsync(null);
        var account = model.SelectedAccount!;
        model.SelectedAccount = null;
        api.RejectRefresh = true;
        model.SelectedAccount = account;
        Assert.False(model.IsBusy);
        Assert.False(model.IsSignedIn);
        Assert.False(model.CanPlay);
        Assert.Null(credentials.Value);
        Assert.True(model.ShowAccountDialog);
        Assert.Equal(account.UserName, model.UserName);
        Assert.Equal("Sign in to " + account.UserName, model.AccountStatus);
        Assert.Contains("expired", model.Status);
        Assert.Equal(1, api.RefreshCalls);
        model.SelectedAccount = null;
        model.SelectedAccount = account;
        Assert.Equal(1, api.RefreshCalls);
        api.SessionExpiry = long.MaxValue;
        await model.AccountCommand.ExecuteAsync(null);
        Assert.True(model.IsSignedIn);
        Assert.False(model.ShowAccountDialog);
        Assert.Equal("refresh", credentials.Value);
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
        public Exception? AdoptError { get; set; }

        public Task AdoptAsync(string root, CancellationToken cancellation) =>
            AdoptError is { } error ? Task.FromException(error) : Task.CompletedTask;

        public Task PrepareAsync(
            string source,
            string destination,
            IProgress<PreparationProgress>? progress,
            CancellationToken cancellation
        ) => Task.CompletedTask;
    }

    private sealed class Launcher : IClientLauncher
    {
        public SessionReply? LaunchedSession { get; private set; }
        public string? LaunchedRoot { get; private set; }
        public int RunningCount => 0;

        public bool IsRunning(string serverId, long accountId) => false;

        public event Action? Changed
        {
            add { }
            remove { }
        }

        public Task LaunchAsync(string root, ServerReply server, SessionReply session, LaunchReply launch, CancellationToken cancellation)
        {
            LaunchedSession = session;
            LaunchedRoot = root;
            return Task.CompletedTask;
        }
    }

    private sealed class Api : ILauncherApiClient
    {
        public bool Offline { get; set; }
        public AccountRequest? Registration { get; private set; }
        public bool SignedOut { get; private set; }
        public Task<ServerReply>? PendingServer { get; set; }
        public Exception? ServerError { get; set; }
        public bool RejectRefresh { get; set; }
        public long SessionExpiry { get; set; } = long.MaxValue;
        public int RefreshCalls { get; private set; }
        public string? LaunchedAccess { get; private set; }
        public static ServerReply Server => new("test", "Test", 1, ClientBuild.Number, "localhost:26000", "http://localhost:26001");

        public Task<ServerReply> ServerAsync(CancellationToken cancellation = default) =>
            ServerError is { } error ? Task.FromException<ServerReply>(error)
            : Offline ? throw new HttpRequestException("offline")
            : PendingServer ?? Task.FromResult(Server);

        public Task<AccountReply> RegisterAsync(AccountRequest request, CancellationToken cancellation = default)
        {
            Registration = request;
            return Task.FromResult(new AccountReply(1000003, request.UserName, request.Developer));
        }

        public Task<SessionReply> SignInAsync(SignInRequest request, CancellationToken cancellation = default) =>
            Task.FromResult(
                new SessionReply(1000003, request.UserName, "access", SessionExpiry, request.RememberMe ? "refresh" : null, true)
            );

        public Task<SessionReply> RefreshAsync(string refreshToken, CancellationToken cancellation = default)
        {
            RefreshCalls++;
            return RejectRefresh
                ? Task.FromException<SessionReply>(new LauncherApiException("unauthorized", "expired"))
                : SignInAsync(new("Developer", "", true), cancellation);
        }

        public Task SignOutAsync(string accessToken, CancellationToken cancellation = default)
        {
            SignedOut = true;
            return Task.CompletedTask;
        }

        public Task<LaunchReply> LaunchAsync(string accessToken, LaunchRequest request, CancellationToken cancellation = default)
        {
            LaunchedAccess = accessToken;
            return Task.FromResult(new LaunchReply("ticket", long.MaxValue, "manifest"));
        }

        public void Dispose() { }
    }
}
