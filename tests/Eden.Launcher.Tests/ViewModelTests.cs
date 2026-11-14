using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
    public async Task PrivateCaCertificatePersistsForOnlyItsServerAndCanBeCleared()
    {
        string directory = Directory.CreateTempSubdirectory("eden-launcher-ca-").FullName;
        try
        {
            var remote = new ServerProfile { ApiAddress = "https://remote.example" };
            var local = new ServerProfile();
            var store = new SettingsStore(directory);
            await store.SaveAsync(new([remote, local], remote.Id));

            string path = Path.Combine(directory, "remote-ca.cer");
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=Eden Test CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            using var root = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            await File.WriteAllBytesAsync(path, root.Export(X509ContentType.Cert), TestContext.Current.CancellationToken);

            var api = new Api();
            string? connectedAuthority = null;
            ILauncherApiClient Connect(string address, string? authority)
            {
                connectedAuthority = authority;
                return api;
            }
            var model = new MainViewModel(store, new Credentials(), new Installation(), new Launcher(), Connect);
            await model.InitializeAsync();
            Assert.True(model.IsConnected);
            model.CertificateAuthorityPath = path + ".missing";
            Assert.False(model.IsConnected);
            await model.SaveServerCommand.ExecuteAsync(null);
            Assert.Contains("Could not read the server CA certificate", model.Status);
            Assert.Null((await store.LoadAsync()).Servers[0].CertificateAuthorityPath);
            model.CertificateAuthorityPath = path;
            await model.SaveServerCommand.ExecuteAsync(null);
            Assert.False(model.IsError);

            var reopened = new MainViewModel(store, new Credentials(), new Installation(), new Launcher(), Connect);
            await reopened.InitializeAsync();
            Assert.True(reopened.IsConnected);
            Assert.Equal(path, reopened.CertificateAuthorityPath);
            Assert.Equal(path, connectedAuthority);
            reopened.SelectedServer = reopened.Servers.Single(profile => profile.Id == local.Id);
            Assert.Empty(reopened.CertificateAuthorityPath);
            await reopened.ConnectCommand.ExecuteAsync(null);
            Assert.Equal("", connectedAuthority);
            reopened.SelectedServer = reopened.Servers.Single(profile => profile.Id == remote.Id);
            Assert.Equal(path, reopened.CertificateAuthorityPath);
            reopened.CertificateAuthorityPath = "";
            await reopened.SaveServerCommand.ExecuteAsync(null);
            Assert.All((await store.LoadAsync()).Servers, profile => Assert.Null(profile.CertificateAuthorityPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

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
        var accounts = window.GetVisualDescendants().OfType<ListBox>().Single(list => list.Classes.Contains("accounts"));
        Assert.Single(accounts.Items);
        await model.SignOutCommand.ExecuteAsync(null);
        Assert.False(model.CanPlay);
        Assert.Empty(accounts.Items);
        Assert.False(model.HasAccounts);
        Assert.Null(model.SelectedAccount);
        Assert.Null(credentials.Value);
        Assert.True(api.SignedOut);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignOutRemovesOnlyTheCurrentAccountFromSavedSettings(bool serverUnavailable)
    {
        string directory = Directory.CreateTempSubdirectory("eden-launcher-sign-out-").FullName;
        try
        {
            var profile = new ServerProfile();
            var otherProfile = new ServerProfile { ApiAddress = "https://other.example" };
            var otherAccount = new SavedAccount(profile.Id, 1000004, "Other pilot", false);
            var otherServerAccount = new SavedAccount(otherProfile.Id, 1000003, "Developer", true);
            var settings = new SettingsStore(directory);
            await settings.SaveAsync(new([profile, otherProfile], profile.Id, [otherAccount, otherServerAccount]));
            var credentials = new Credentials();
            var api = new Api { FailSignOut = serverUnavailable };
            var model = new MainViewModel(settings, credentials, new Installation(), new Launcher(), (_, _) => api);
            await model.InitializeAsync();
            model.UserName = "Developer";
            model.RememberMe = true;
            await model.AccountCommand.ExecuteAsync(null);
            Assert.True(model.IsSignedIn);
            Assert.Equal(2, model.Accounts.Count);

            await model.SignOutCommand.ExecuteAsync(null);

            Assert.True(api.SignedOut);
            Assert.Equal(serverUnavailable, model.IsError);
            Assert.False(model.IsSignedIn);
            Assert.Equal(otherAccount, Assert.Single(model.Accounts));
            Assert.Null(model.SelectedAccount);
            Assert.Empty(model.UserName);
            Assert.Null(credentials.Value);
            var reopened = new MainViewModel(new SettingsStore(directory), credentials, new Installation(), new Launcher(), (_, _) => api);
            await reopened.InitializeAsync();
            Assert.False(reopened.IsSignedIn);
            Assert.Empty(reopened.UserName);
            Assert.Equal(otherAccount, Assert.Single(reopened.Accounts));
            reopened.SelectedServer = reopened.Servers.Single(server => server.Id == otherProfile.Id);
            Assert.Equal(otherServerAccount, Assert.Single(reopened.Accounts));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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
    public async Task ConnectRestoresRememberedSignInAfterStartingDuringAnOutage()
    {
        var api = new Api { Offline = true };
        var credentials = new Credentials { Value = "old refresh" };
        var model = Create(api, credentials);
        await model.InitializeAsync();
        model.ClientRoot = "prepared-client";
        Assert.False(model.IsSignedIn);
        Assert.Equal("old refresh", credentials.Value);
        Assert.Equal(0, api.RefreshCalls);

        api.Offline = false;
        await model.ConnectCommand.ExecuteAsync(null);

        Assert.True(model.CanPlay);
        Assert.False(model.ShowAccountDialog);
        Assert.Equal("refresh", credentials.Value);
        Assert.Equal(1, api.RefreshCalls);
        await model.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(1, api.RefreshCalls);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlayRenewsRejectedAccessOrOpensSignInIfRefreshExpired(bool rejectRefresh)
    {
        string root = Directory.CreateTempSubdirectory("eden-launch-reauth-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "tq"));
            byte[] prefix = [1, 2, 3, 4];
            await File.WriteAllBytesAsync(Path.Combine(root, "tq", "manifest.dat"), prefix, TestContext.Current.CancellationToken);
            var api = new Api();
            var credentials = new Credentials();
            var launcher = new Launcher();
            var build = new ClientBuild([], prefix.Length, Convert.ToHexString(SHA256.HashData(prefix)));
            var model = new MainViewModel(new Settings(), credentials, new Installation(), launcher, (_, _) => api, build);
            await model.InitializeAsync();
            model.UserName = "Developer";
            model.Password = "test password";
            await model.AccountCommand.ExecuteAsync(null);
            Assert.True(model.RememberMe);
            Assert.Equal("refresh", credentials.Value);
            model.ClientRoot = root;
            await model.UseClientCommand.ExecuteAsync(null);
            api.RejectNextAccess = true;
            api.RejectRefresh = rejectRefresh;

            await model.PlayCommand.ExecuteAsync(null);

            Assert.Equal(1, api.RefreshCalls);
            Assert.Equal(!rejectRefresh, model.IsSignedIn);
            Assert.Equal(rejectRefresh, model.ShowAccountDialog);
            Assert.Equal("Developer", model.UserName);
            if (rejectRefresh)
            {
                Assert.Equal(new[] { "access" }, api.LaunchAttempts);
                Assert.Null(launcher.LaunchedSession);
                Assert.Null(credentials.Value);
            }
            else
            {
                Assert.Equal(new[] { "access", "access-renewed" }, api.LaunchAttempts);
                Assert.Equal("access-renewed", launcher.LaunchedSession!.AccessToken);
                Assert.Equal("refresh", credentials.Value);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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
            var model = new MainViewModel(new Settings(), new Credentials(), new Installation(), launcher, (_, _) => api, build);
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
        var model = new MainViewModel(new Settings(), new Credentials(), installation, new Launcher(), (_, _) => api);
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
        new(new Settings(), credentials, new Installation(), new Launcher(), (_, _) => api);

    private sealed class Settings : ISettingsStore
    {
        public Task<LauncherSettings> LoadAsync() => Task.FromResult(LauncherSettings.Default());

        public Task SaveAsync(LauncherSettings settings) => Task.CompletedTask;

        public Task<string> SaveCertificateAuthorityAsync(byte[] certificate, CancellationToken cancellation) =>
            throw new NotSupportedException();
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

        public Task LaunchAsync(
            string root,
            ServerReply server,
            SessionReply session,
            LaunchReply launch,
            string? certificateAuthorityPath,
            CancellationToken cancellation
        )
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
        public bool FailSignOut { get; set; }
        public Task<ServerReply>? PendingServer { get; set; }
        public Exception? ServerError { get; set; }
        public bool RejectRefresh { get; set; }
        public bool RejectNextAccess { get; set; }
        public long SessionExpiry { get; set; } = long.MaxValue;
        public int RefreshCalls { get; private set; }
        public string? LaunchedAccess { get; private set; }
        public List<string> LaunchAttempts { get; } = [];
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
                : Task.FromResult(new SessionReply(1000003, "Developer", "access-renewed", SessionExpiry, "refresh", true));
        }

        public Task SignOutAsync(string accessToken, CancellationToken cancellation = default)
        {
            SignedOut = true;
            return FailSignOut ? Task.FromException(new HttpRequestException("offline")) : Task.CompletedTask;
        }

        public Task<LaunchReply> LaunchAsync(string accessToken, LaunchRequest request, CancellationToken cancellation = default)
        {
            LaunchAttempts.Add(accessToken);
            if (RejectNextAccess)
            {
                RejectNextAccess = false;
                return Task.FromException<LaunchReply>(new LauncherApiException("unauthorized", "Sign in again."));
            }
            LaunchedAccess = accessToken;
            return Task.FromResult(new LaunchReply("ticket", long.MaxValue, "manifest"));
        }

        public void Dispose() { }
    }
}
