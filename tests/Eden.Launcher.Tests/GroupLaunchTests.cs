using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Eden.Launcher.Core;

namespace Eden.Launcher.Tests;

public sealed class GroupLaunchTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("eden-launch-groups-").FullName;
    private readonly Credentials _credentials = new();
    private readonly Api _api = new();
    private readonly Launcher _launcher = new();
    private SettingsStore Store => new(_directory);
    private static readonly byte[] s_prefix = [1, 2, 3, 4];

    private async Task<MainViewModel> CreateAsync(bool seed = true, IReadOnlyList<long>? members = null)
    {
        Directory.CreateDirectory(Path.Combine(_directory, "tq"));
        await File.WriteAllBytesAsync(Path.Combine(_directory, "tq", "manifest.dat"), s_prefix, TestContext.Current.CancellationToken);
        if (seed)
        {
            var local = new ServerProfile { Id = "local", ClientRoot = _directory };
            var remote = new ServerProfile { Id = "remote", ApiAddress = "https://remote.example" };
            SavedAccount[] accounts = [new("local", 1, "Pilot1", false), new("local", 2, "Pilot2", true), new("local", 3, "Pilot3", false)];
            LaunchGroup[] groups = [new("fleet", "local", "Fleet", members ?? [1, 2]), new("remote-fleet", "remote", "Remote", [1])];
            await Store.SaveAsync(new([local, remote], local.Id, accounts, groups));
        }
        var build = new ClientBuild([], s_prefix.Length, Convert.ToHexString(SHA256.HashData(s_prefix)));
        var model = new MainViewModel(Store, _credentials, new Installation(), _launcher, (_, _) => _api, build);
        await model.InitializeAsync();
        return model;
    }

    [AvaloniaFact]
    public async Task GroupsCanBeCreatedEditedDeletedAndReopenedWithoutChangingAccounts()
    {
        var model = await CreateAsync();
        model.NewGroupCommand.Execute(null);
        model.GroupName = "  Mining  ";
        model.GroupAccounts[0].Included = true;
        model.GroupAccounts[2].Included = true;
        await model.SaveGroupCommand.ExecuteAsync(null);
        Assert.False(model.IsError);
        Assert.False(model.ShowGroupDialog);
        Assert.Equal("Mining", model.SelectedGroup!.Name);
        Assert.Equal(new long[] { 1, 3 }, model.SelectedGroup.AccountIds);
        string id = model.SelectedGroup.Id;

        var reopened = await CreateAsync(seed: false);
        reopened.SelectedGroup = reopened.Groups.Single(group => group.Id == id);
        reopened.EditGroupCommand.Execute(null);
        reopened.GroupName = "Mining alts";
        reopened.GroupAccounts[0].Included = false;
        reopened.GroupAccounts[1].Included = true;
        await reopened.SaveGroupCommand.ExecuteAsync(null);
        Assert.Equal(id, reopened.SelectedGroup!.Id);
        Assert.Equal(new long[] { 2, 3 }, reopened.SelectedGroup.AccountIds);
        var settings = await Store.LoadAsync();
        Assert.Equal("Mining alts", settings.Groups!.Single(group => group.Id == id).Name);
        Assert.Contains(settings.Groups!, group => group.ServerProfileId == "remote");

        await reopened.DeleteGroupCommand.ExecuteAsync(null);
        var afterDelete = await CreateAsync(seed: false);
        Assert.DoesNotContain(afterDelete.Groups, group => group.Id == id);
        Assert.Equal(3, afterDelete.Accounts.Count);
    }

    [AvaloniaFact]
    public async Task InvalidNamesAndEmptyMembershipLeaveSavedGroupsUnchanged()
    {
        var model = await CreateAsync();
        foreach (string name in new[] { " ", new string('x', LaunchGroup.MaximumNameLength + 1), "fleet" })
        {
            model.NewGroupCommand.Execute(null);
            model.GroupName = name;
            model.GroupAccounts[0].Included = true;
            await model.SaveGroupCommand.ExecuteAsync(null);
            Assert.True(model.IsError);
            Assert.True(model.ShowGroupDialog);
            Assert.Single(model.Groups);
            Assert.Equal(2, (await Store.LoadAsync()).Groups!.Count);
        }
        model.NewGroupCommand.Execute(null);
        model.GroupName = "Empty";
        await model.SaveGroupCommand.ExecuteAsync(null);
        Assert.True(model.IsError);
        Assert.Contains("at least one account", model.Status);
        Assert.Single(model.Groups);

        model.EditGroupCommand.Execute(null);
        model.GroupName = "Unsaved rename";
        model.CloseGroupCommand.Execute(null);
        Assert.Equal("Fleet", model.SelectedGroup!.Name);
    }

    [AvaloniaFact]
    public async Task ServerSwitchClearsGroupEditorAndOnlyShowsThatServersGroups()
    {
        var model = await CreateAsync();
        model.EditGroupCommand.Execute(null);
        model.SelectedServer = model.Servers.Single(server => server.Id == "remote");
        Assert.False(model.ShowGroupDialog);
        Assert.Empty(model.GroupAccounts);
        Assert.Empty(model.Accounts);
        Assert.Equal("Remote", Assert.Single(model.Groups).Name);
        Assert.False(model.CanLaunchGroup);
        model.SelectedServer = model.Servers.Single(server => server.Id == "local");
        Assert.Equal("Fleet", Assert.Single(model.Groups).Name);
        Assert.Equal(3, model.Accounts.Count);
    }

    [AvaloniaFact]
    public async Task LaunchUsesCachedAndRememberedSessionsAndPreservesSelectedAccount()
    {
        var model = await CreateAsync(members: [1, 2, 2, 3]);
        model.UserName = "Pilot1";
        model.Password = "secret";
        model.RememberMe = false;
        await model.AccountCommand.ExecuteAsync(null);
        _credentials.Write(model.ApiAddress, "Pilot2", "refresh-2");
        _launcher.Running.Add(3);
        Assert.True(model.CanLaunchGroup);

        await model.LaunchGroupCommand.ExecuteAsync(null);
        Assert.Equal(new long[] { 1, 2 }, _launcher.Launched);
        Assert.Equal(new long[] { 2 }, _api.Refreshed);
        Assert.Equal(new long[] { 1, 2 }, _api.Tickets);
        Assert.Equal("refresh-2-rotated", _credentials.Read(model.ApiAddress, "Pilot2"));
        Assert.Null(_credentials.Read(model.ApiAddress, "Pilot1"));
        Assert.Equal(1, model.SelectedAccount!.AccountId);
        Assert.Equal("Pilot1", model.UserName);
        Assert.True(model.IsSignedIn);
        Assert.False(model.IsError);
        Assert.Equal("2 launched · 1 already running · 0 need attention", model.GroupStatus);
        Assert.Equal(3, model.GroupResults.Count);

        await model.LaunchGroupCommand.ExecuteAsync(null);
        Assert.Equal(2, _launcher.Launched.Count);
        Assert.Equal("0 launched · 3 already running · 0 need attention", model.GroupStatus);
    }

    [AvaloniaFact]
    public async Task MissingCredentialsAndFailedAccountsDoNotPreventOtherMembersLaunching()
    {
        var model = await CreateAsync(members: [1, 2, 3, 99]);
        _credentials.Write(model.ApiAddress, "Pilot2", "refresh-2");
        _credentials.Write(model.ApiAddress, "Pilot3", "refresh-3");
        _api.FailTickets.Add(2);
        Assert.False(model.IsSignedIn);
        Assert.True(model.CanLaunchGroup);
        await model.LaunchGroupCommand.ExecuteAsync(null);
        Assert.Equal(new long[] { 3 }, _launcher.Launched);
        Assert.True(model.IsError);
        Assert.Contains(model.GroupResults, result => result.Contains("Pilot1: Remembered sign-in expired or missing"));
        Assert.Contains(model.GroupResults, result => result.Contains("Pilot2: Could not reach"));
        Assert.Contains(model.GroupResults, result => result.Contains("Account 99: Account is no longer saved"));
        Assert.Equal("1 launched · 0 already running · 3 need attention", model.GroupStatus);
        Assert.False(model.IsSignedIn);
    }

    [AvaloniaFact]
    public async Task ExpiredOrMismatchedRefreshCannotLaunchTheWrongAccount()
    {
        var model = await CreateAsync();
        _credentials.Write(model.ApiAddress, "Pilot1", "refresh-3");
        _credentials.Write(model.ApiAddress, "Pilot2", "refresh-2");
        _api.Unauthorized.Add(2);
        await model.LaunchGroupCommand.ExecuteAsync(null);
        Assert.Empty(_launcher.Launched);
        Assert.Empty(_api.Tickets);
        Assert.Contains(model.GroupResults, result => result.Contains("another account"));
        Assert.Contains(model.GroupResults, result => result.Contains("sign-in expired"));
        Assert.Null(_credentials.Read(model.ApiAddress, "Pilot2"));
    }

    [AvaloniaFact]
    public async Task CancelStopsRemainingMembersAndPreservesTheClientsAlreadyLaunched()
    {
        var model = await CreateAsync();
        _credentials.Write(model.ApiAddress, "Pilot1", "refresh-1");
        _credentials.Write(model.ApiAddress, "Pilot2", "refresh-2");
        _launcher.AfterLaunch = () => model.CancelCommand.Execute(null);
        await model.LaunchGroupCommand.ExecuteAsync(null);
        Assert.Equal(new long[] { 1 }, _launcher.Launched);
        Assert.Equal(new long[] { 1 }, _api.Refreshed);
        Assert.Contains("Cancelled", model.GroupStatus);
        Assert.False(model.IsBusy);
        Assert.True(model.CanLaunchGroup);
    }

    [AvaloniaFact]
    public async Task ExpiredCachedSessionRenewsBeforeLaunching()
    {
        var model = await CreateAsync(members: [1]);
        _api.SignInExpiresAt = 0;
        model.UserName = "Pilot1";
        model.Password = "secret";
        model.RememberMe = true;
        await model.AccountCommand.ExecuteAsync(null);
        await model.LaunchGroupCommand.ExecuteAsync(null);
        Assert.Equal(new long[] { 1 }, _api.Refreshed);
        Assert.Equal(new long[] { 1 }, _launcher.Launched);
        Assert.False(model.IsError);
    }

    [AvaloniaFact]
    public async Task RejectedGroupAccessRenewsOnceAndUpdatesTheSelectedSession()
    {
        var model = await CreateAsync(members: [1]);
        model.UserName = "Pilot1";
        model.Password = "secret";
        await model.AccountCommand.ExecuteAsync(null);
        _api.RejectNextAccess.Add(1);

        await model.LaunchGroupCommand.ExecuteAsync(null);

        Assert.Equal(new long[] { 1, 1 }, _api.Tickets);
        Assert.Equal(1, Assert.Single(_api.Refreshed));
        Assert.Equal(1, Assert.Single(_launcher.Launched));
        Assert.True(model.IsSignedIn);
        Assert.False(model.IsError);
        await model.SignOutCommand.ExecuteAsync(null);
        Assert.Equal("access-1-renewed", _api.SignedOutAccess);
    }

    [AvaloniaFact]
    public async Task TimeoutsAndProcessFailuresAreReportedWhileLaterMembersStillLaunch()
    {
        var model = await CreateAsync(members: [1, 2, 3]);
        foreach (int id in new[] { 1, 2, 3 })
        {
            _credentials.Write(model.ApiAddress, "Pilot" + id, "refresh-" + id);
        }
        _api.TimeoutTickets.Add(1);
        _launcher.FailLaunch.Add(2);
        await model.LaunchGroupCommand.ExecuteAsync(null);
        Assert.Equal(new long[] { 3 }, _launcher.Launched);
        Assert.Contains(model.GroupResults, result => result.Contains("Pilot1: server timed out"));
        Assert.Contains(model.GroupResults, result => result.Contains("Pilot2: Could not start"));
        Assert.Equal("1 launched · 0 already running · 2 need attention", model.GroupStatus);
    }

    [AvaloniaFact]
    public async Task CancellingAnInFlightTicketStopsTheCurrentAndRemainingAccounts()
    {
        var model = await CreateAsync();
        _credentials.Write(model.ApiAddress, "Pilot1", "refresh-1");
        _credentials.Write(model.ApiAddress, "Pilot2", "refresh-2");
        _api.BeforeTicket = cancellation =>
        {
            Assert.True(model.IsBusy);
            Assert.False(model.CanLaunchGroup);
            model.CancelCommand.Execute(null);
            cancellation.ThrowIfCancellationRequested();
        };
        await model.LaunchGroupCommand.ExecuteAsync(null);
        Assert.Empty(_launcher.Launched);
        Assert.Equal(new long[] { 1 }, _api.Refreshed);
        Assert.Contains("Cancelled", model.GroupStatus);
        Assert.False(model.IsError);
    }

    [AvaloniaFact]
    public async Task GroupControlsBindToCommandsAndRequireConnectionAndVerifiedClient()
    {
        var model = await CreateAsync();
        var window = new MainWindow { DataContext = model };
        window.Show();
        var launch = window.GetVisualDescendants().OfType<Button>().Single(button => button.Command == model.LaunchGroupCommand);
        Assert.True(launch.IsEffectivelyEnabled);
        model.NewGroupCommand.Execute(null);
        window.UpdateLayout();
        Assert.Equal(
            3,
            window.GetVisualDescendants().OfType<CheckBox>().Count(box => box.Content is string name && name.StartsWith("Pilot"))
        );
        model.CloseGroupCommand.Execute(null);
        model.ClientRoot = "unverified";
        Assert.False(model.LaunchGroupCommand.CanExecute(null));
        Dispatcher.UIThread.RunJobs();
        Assert.False(launch.IsEffectivelyEnabled);
        model.ApiAddress = "https://other.example";
        Assert.False(model.CanLaunchGroup);
        window.Close();
    }

    [Fact]
    public async Task SettingsFromBeforeGroupsWereAddedRemainReadable()
    {
        var original = LauncherSettings.Default();
        string json = JsonSerializer.Serialize(
            new
            {
                original.Servers,
                original.SelectedServerId,
                original.Accounts,
            }
        );
        await File.WriteAllTextAsync(Path.Combine(_directory, "settings.json"), json, TestContext.Current.CancellationToken);
        var loaded = await Store.LoadAsync();
        Assert.Equal(original.SelectedServerId, loaded.SelectedServerId);
        Assert.Null(loaded.Groups);
    }

    [AvaloniaFact]
    public async Task MalformedRepliesDoNotExposeContentsOrStopLaterGroupMembers()
    {
        var model = await CreateAsync(members: [1, 2, 3]);
        foreach (long id in new long[] { 1, 2, 3 })
        {
            _credentials.Write(model.ApiAddress, "Pilot" + id, "refresh-" + id);
        }
        _api.ReplyErrors[1] = new FormatException("private response");
        _api.ReplyErrors[2] = new JsonException("private response");
        await model.LaunchGroupCommand.ExecuteAsync(null);
        Assert.Equal(new long[] { 3 }, _launcher.Launched);
        Assert.Equal(3, model.GroupResults.Count);
        Assert.Contains("invalid data", model.GroupResults[0]);
        Assert.Contains("invalid data", model.GroupResults[1]);
        Assert.All(model.GroupResults, result => Assert.DoesNotContain("private response", result));
        Assert.True(model.IsError);
        Assert.False(model.IsBusy);
        Assert.Contains("1 launched", model.GroupStatus);
        Assert.Contains("2 need attention", model.GroupStatus);
    }

    private sealed class Credentials : ICredentialStore
    {
        private readonly Dictionary<(string Address, string Name), string?> _values = [];

        public string? Read(string serverAddress, string userName) => _values.GetValueOrDefault((serverAddress, userName));

        public void Write(string serverAddress, string userName, string? refreshToken) => _values[(serverAddress, userName)] = refreshToken;
    }

    private sealed class Installation : IClientInstallation
    {
        public Task AdoptAsync(string root, CancellationToken cancellation) => Task.CompletedTask;

        public Task PrepareAsync(
            string source,
            string destination,
            IProgress<PreparationProgress>? progress,
            CancellationToken cancellation
        ) => throw new NotSupportedException();
    }

    private sealed class Launcher : IClientLauncher
    {
        public HashSet<long> Running { get; } = [];
        public HashSet<long> FailLaunch { get; } = [];
        public List<long> Launched { get; } = [];
        public Action? AfterLaunch { get; set; }
        public int RunningCount => Running.Count;
        public event Action? Changed;

        public bool IsRunning(string serverId, long accountId) => Running.Contains(accountId);

        public Task LaunchAsync(
            string root,
            ServerReply server,
            SessionReply session,
            LaunchReply launch,
            string? certificateAuthorityPath,
            CancellationToken cancellation
        )
        {
            Assert.Equal("ticket-" + session.AccountId, launch.Ticket);
            if (FailLaunch.Contains(session.AccountId))
            {
                throw new IOException("Could not start this game client.");
            }
            Running.Add(session.AccountId);
            Launched.Add(session.AccountId);
            Changed?.Invoke();
            AfterLaunch?.Invoke();
            return Task.CompletedTask;
        }
    }

    private sealed class Api : ILauncherApiClient
    {
        public List<long> Refreshed { get; } = [];
        public List<long> Tickets { get; } = [];
        public HashSet<long> FailTickets { get; } = [];
        public HashSet<long> Unauthorized { get; } = [];
        public HashSet<long> RejectNextAccess { get; } = [];
        public HashSet<long> TimeoutTickets { get; } = [];
        public Dictionary<long, Exception> ReplyErrors { get; } = [];
        public long SignInExpiresAt { get; set; } = long.MaxValue;
        public string? SignedOutAccess { get; private set; }
        public Action<CancellationToken>? BeforeTicket { get; set; }

        public Task<ServerReply> ServerAsync(CancellationToken cancellation = default) =>
            Task.FromResult(new ServerReply("test", "Test", 1, ClientBuild.Number, "localhost:26000", "http://localhost:26001"));

        public Task<AccountReply> RegisterAsync(AccountRequest request, CancellationToken cancellation = default) =>
            throw new NotSupportedException();

        public Task<SessionReply> SignInAsync(SignInRequest request, CancellationToken cancellation = default) =>
            Task.FromResult(Session(long.Parse(request.UserName[5..]), request.RememberMe) with { ExpiresAt = SignInExpiresAt });

        public Task<SessionReply> RefreshAsync(string refreshToken, CancellationToken cancellation = default)
        {
            long accountId = long.Parse(refreshToken.Split('-')[1]);
            Refreshed.Add(accountId);
            if (Unauthorized.Contains(accountId))
            {
                throw new LauncherApiException("unauthorized", "Sign in again.");
            }
            return Task.FromResult(Session(accountId, true) with { AccessToken = $"access-{accountId}-renewed" });
        }

        private static SessionReply Session(long id, bool remember) =>
            new(id, "Pilot" + id, "access-" + id, long.MaxValue, remember ? "refresh-" + id + "-rotated" : null);

        public Task SignOutAsync(string accessToken, CancellationToken cancellation = default)
        {
            SignedOutAccess = accessToken;
            return Task.CompletedTask;
        }

        public Task<LaunchReply> LaunchAsync(string accessToken, LaunchRequest request, CancellationToken cancellation = default)
        {
            long id = long.Parse(accessToken.Split('-')[1]);
            Tickets.Add(id);
            Assert.Equal(ClientBuild.Number, request.Build);
            Assert.Equal(Convert.ToBase64String(s_prefix), request.ManifestPrefix);
            BeforeTicket?.Invoke(cancellation);
            if (RejectNextAccess.Remove(id))
            {
                throw new LauncherApiException("unauthorized", "Sign in again.");
            }
            if (ReplyErrors.TryGetValue(id, out var error))
            {
                throw error;
            }
            if (TimeoutTickets.Contains(id))
            {
                throw new TaskCanceledException("timeout");
            }
            if (FailTickets.Contains(id))
            {
                throw new HttpRequestException("offline");
            }
            return Task.FromResult(new LaunchReply("ticket-" + id, long.MaxValue, "manifest"));
        }

        public void Dispose() { }
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
