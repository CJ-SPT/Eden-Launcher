using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Eden.Launcher.Core;

namespace Eden.Launcher;

public partial class MainViewModel : ObservableObject
{
    private const int SessionRefreshLeadSeconds = 30;
    private readonly ISettingsStore _settings;
    private readonly ICredentialStore _credentials;
    private readonly IClientInstallation _installation;
    private readonly IClientLauncher _launcher;
    private readonly Func<string, ILauncherApiClient> _apiFactory;
    private readonly ClientBuild _build;
    private SessionReply? _session;
    private ServerReply? _server;
    private CancellationTokenSource? _operation;
    private bool _loading;
    private bool _updatingProfile;
    private string? _sessionAddress;
    private readonly List<SavedAccount> _savedAccounts = [];
    private readonly Dictionary<string, SessionReply> _sessions = [];
    private bool _selectingAccount;

    public ObservableCollection<ServerProfile> Servers { get; } = [];
    public ObservableCollection<SavedAccount> Accounts { get; } = [];

    [ObservableProperty]
    private SavedAccount? _selectedAccount;

    [ObservableProperty]
    private bool _showSettings;

    [ObservableProperty]
    private bool _showAccountDialog;
    public bool HasAccounts => Accounts.Count > 0;
    public string SelectedAccountName => IsSignedIn ? UserName : "Your universe awaits";
    public string ReadyStatus => ClientReady ? "Ready to launch" : "Client setup required";

    [ObservableProperty]
    private ServerProfile? _selectedServer;

    [ObservableProperty]
    private string _serverName = "Eden Local";

    [ObservableProperty]
    private string _apiAddress = "http://127.0.0.1:26002";

    [ObservableProperty]
    private string _clientRoot = "";

    [ObservableProperty]
    private string _sourceRoot = "";

    [ObservableProperty]
    private string _userName = "";

    [ObservableProperty]
    private string _password = "";

    [ObservableProperty]
    private string _confirmPassword = "";

    [ObservableProperty]
    private string _developerCode = "";

    [ObservableProperty]
    private bool _developerAccount;

    [ObservableProperty]
    private bool _rememberMe;

    [ObservableProperty]
    private bool _createAccount;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isSignedIn;

    [ObservableProperty]
    private bool _clientReady;

    [ObservableProperty]
    private bool _isError;

    [ObservableProperty]
    private string _status = "Choose your server and client to begin.";

    [ObservableProperty]
    private string _connectionStatus = "NOT CONNECTED";

    [ObservableProperty]
    private string _accountStatus = "Sign in to your Eden account";

    [ObservableProperty]
    private string _clientStatus = "Choose a prepared client, or make a new copy below.";

    [ObservableProperty]
    private double _preparationPercent;

    [ObservableProperty]
    private int _runningCount;
    public bool CanPlay => !IsBusy && IsConnected && IsSignedIn && ClientReady;
    public bool CanWork => !IsBusy;
    public bool CanEditAccount => !IsBusy && !IsSignedIn;
    public bool ShowConfirmation => CreateAccount && !IsSignedIn;
    public bool ShowDeveloperCode => CreateAccount && DeveloperAccount && !IsSignedIn;
    public string AccountAction => CreateAccount ? "Create account" : "Sign in";
    public string PlayLabel =>
        IsBusy ? "PLEASE WAIT"
        : !IsSignedIn ? "ADD ACCOUNT"
        : !ClientReady ? "SET UP CLIENT"
        : "PLAY";

    public MainViewModel(
        ISettingsStore settings,
        ICredentialStore credentials,
        IClientInstallation installation,
        IClientLauncher launcher,
        Func<string, ILauncherApiClient>? apiFactory = null,
        ClientBuild? clientBuild = null
    )
    {
        _settings = settings;
        _credentials = credentials;
        _installation = installation;
        _launcher = launcher;
        _apiFactory = apiFactory ?? (address => new LauncherApiClient(address));
        _build = clientBuild ?? new ClientBuild();
        _launcher.Changed += () => Dispatcher.UIThread.Post(() => RunningCount = _launcher.RunningCount);
    }

    public async Task InitializeAsync()
    {
        await RunAsync(async cancellation =>
        {
            _loading = true;
            try
            {
                var settings = await _settings.LoadAsync();
                _savedAccounts.AddRange(settings.Accounts ?? []);
                _savedGroups.AddRange(settings.Groups ?? []);
                foreach (var server in settings.Servers)
                {
                    Servers.Add(server);
                }
                SelectedServer = Servers.FirstOrDefault(server => server.Id == settings.SelectedServerId) ?? Servers[0];
            }
            finally
            {
                _loading = false;
            }
            await ConnectCoreAsync(cancellation);
            if (!string.IsNullOrWhiteSpace(ClientRoot))
            {
                await ValidateClientCoreAsync(cancellation);
            }
            string? refresh = _credentials.Read(ApiAddress, UserName);
            if (refresh is not null)
            {
                using var api = _apiFactory(ApiAddress);
                try
                {
                    var session = await api.RefreshAsync(refresh, cancellation);
                    RememberMe = true;
                    await AcceptSessionAsync(session);
                    Status = "Welcome back. Your account is ready.";
                }
                catch (LauncherApiException error) when (error.Code == "unauthorized")
                {
                    _credentials.Write(ApiAddress, UserName, null);
                    Status = "Your remembered sign-in expired. Sign in again.";
                }
            }
        });
    }

    partial void OnSelectedServerChanged(ServerProfile? value)
    {
        if (value is null || _updatingProfile)
        {
            return;
        }
        _loading = true;
        ServerName = value.Name;
        ApiAddress = value.ApiAddress;
        ClientRoot = value.ClientRoot;
        UserName = value.UserName;
        _loading = false;
        ResetConnection();
        Accounts.Clear();
        foreach (var account in _savedAccounts.Where(account => account.ServerProfileId == value.Id))
        {
            Accounts.Add(account);
        }
        OnPropertyChanged(nameof(HasAccounts));
        LoadGroups();
        ClientReady = false;
        if (!_loading)
        {
            Status = "Connect to this server to continue.";
        }
    }

    partial void OnApiAddressChanged(string value)
    {
        if (!_loading)
        {
            ResetConnection();
        }
    }

    partial void OnClientRootChanged(string value)
    {
        ClientReady = false;
        ClientStatus = "Select Use this client to verify this folder.";
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanWork));
        OnPropertyChanged(nameof(CanEditAccount));
        OnPropertyChanged(nameof(PlayLabel));
        NewGroupCommand.NotifyCanExecuteChanged();
        SaveGroupCommand.NotifyCanExecuteChanged();
        RefreshPlay();
    }

    partial void OnIsConnectedChanged(bool value) => RefreshPlay();

    partial void OnClientReadyChanged(bool value) => RefreshPlay();

    partial void OnIsSignedInChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEditAccount));
        OnPropertyChanged(nameof(ShowConfirmation));
        OnPropertyChanged(nameof(ShowDeveloperCode));
        OnPropertyChanged(nameof(SelectedAccountName));
        RefreshPlay();
    }

    partial void OnCreateAccountChanged(bool value)
    {
        OnPropertyChanged(nameof(AccountAction));
        OnPropertyChanged(nameof(ShowConfirmation));
        OnPropertyChanged(nameof(ShowDeveloperCode));
    }

    partial void OnDeveloperAccountChanged(bool value) => OnPropertyChanged(nameof(ShowDeveloperCode));

    private void RefreshPlay()
    {
        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(PlayLabel));
        OnPropertyChanged(nameof(ReadyStatus));
        PlayCommand.NotifyCanExecuteChanged();
        RefreshGroups();
    }

    partial void OnSelectedAccountChanged(SavedAccount? value)
    {
        if (value is not null && !_selectingAccount)
        {
            _ = SelectAccountAsync(value);
        }
    }

    private Task SelectAccountAsync(SavedAccount account) =>
        RunAsync(async cancellation =>
        {
            _session = null;
            IsSignedIn = false;
            UserName = account.UserName;
            CreateAccount = false;
            Password = "";
            if (!IsConnected)
            {
                await ConnectCoreAsync(cancellation);
            }
            string key = ApiAddress + "\n" + account.AccountId;
            if (
                _sessions.TryGetValue(key, out var cached)
                && cached.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds() + SessionRefreshLeadSeconds
            )
            {
                await AcceptSessionAsync(cached);
                return;
            }
            if (_credentials.Read(ApiAddress, account.UserName) is { } refresh)
            {
                using var api = _apiFactory(ApiAddress);
                await AcceptSessionAsync(await api.RefreshAsync(refresh, cancellation));
                return;
            }
            ShowAccountDialog = true;
            AccountStatus = "Sign in to " + account.UserName;
        });

    [RelayCommand]
    private void OpenSettings() => ShowSettings = true;

    [RelayCommand]
    private void CloseSettings() => ShowSettings = false;

    [RelayCommand]
    private void OpenAccount()
    {
        _session = null;
        IsSignedIn = false;
        UserName = "";
        AccountStatus = "Add an Eden account";
        ShowAccountDialog = true;
    }

    [RelayCommand]
    private void CloseAccount()
    {
        ShowAccountDialog = false;
        if (!IsSignedIn && SelectedAccount is { } account)
        {
            _ = SelectAccountAsync(account);
        }
    }

    [RelayCommand]
    private Task PrimaryActionAsync()
    {
        if (IsBusy)
        {
            return Task.CompletedTask;
        }
        if (!IsSignedIn)
        {
            OpenAccount();
            return Task.CompletedTask;
        }
        if (!ClientReady)
        {
            ShowSettings = true;
            return Task.CompletedTask;
        }
        return PlayAsync();
    }

    private void ResetConnection()
    {
        _server = null;
        _session = null;
        _sessionAddress = null;
        IsConnected = false;
        IsSignedIn = false;
        Password = "";
        ConfirmPassword = "";
        DeveloperCode = "";
        ConnectionStatus = "NOT CONNECTED";
        AccountStatus = "Sign in to your Eden account";
    }

    [RelayCommand]
    private Task ConnectAsync() => RunAsync(ConnectCoreAsync);

    private async Task ConnectCoreAsync(CancellationToken cancellation)
    {
        using var api = _apiFactory(ApiAddress);
        _server = await api.ServerAsync(cancellation);
        IsConnected = true;
        ConnectionStatus = "CONNECTED · " + _server.Name;
        Status = "Connected. Sign in or create an account.";
        await SaveCoreAsync();
    }

    [RelayCommand]
    private Task SaveServerAsync() =>
        RunAsync(async _ =>
        {
            LauncherApiClient.ValidateEndpoint(new Uri(ApiAddress));
            await SaveCoreAsync();
            Status = "Server profile saved.";
        });

    [RelayCommand]
    private void AddServer()
    {
        if (IsBusy)
        {
            return;
        }
        var profile = new ServerProfile { Name = "New server" };
        Servers.Add(profile);
        SelectedServer = profile;
    }

    private async Task SaveCoreAsync()
    {
        if (SelectedServer is null)
        {
            return;
        }
        var profile = SelectedServer with
        {
            Name = ServerName.Trim(),
            ApiAddress = ApiAddress.Trim().TrimEnd('/'),
            ClientRoot = ClientRoot.Trim(),
            UserName = UserName.Trim(),
        };
        int index = Servers.IndexOf(SelectedServer);
        _updatingProfile = true;
        try
        {
            Servers[index] = profile;
            SelectedServer = profile;
        }
        finally
        {
            _updatingProfile = false;
        }
        await _settings.SaveAsync(new(Servers.ToArray(), profile.Id, _savedAccounts.ToArray(), _savedGroups.ToArray()));
    }

    [RelayCommand]
    private Task AccountAsync() =>
        RunAsync(async cancellation =>
        {
            if (!IsConnected)
            {
                await ConnectCoreAsync(cancellation);
            }
            using var api = _apiFactory(ApiAddress);
            if (CreateAccount)
            {
                if (Password != ConfirmPassword)
                {
                    throw new InvalidDataException("The passwords do not match.");
                }
                await api.RegisterAsync(new(UserName, Password, DeveloperAccount, DeveloperCode), cancellation);
            }
            var session = await api.SignInAsync(new(UserName, Password, RememberMe), cancellation);
            await AcceptSessionAsync(session);
            Status = "Signed in. Select a prepared client and press Play.";
        });

    private async Task AcceptSessionAsync(SessionReply session)
    {
        _session = session;
        _sessionAddress = ApiAddress;
        _sessions[ApiAddress + "\n" + session.AccountId] = session;
        UserName = session.UserName;
        _credentials.Write(ApiAddress, UserName, session.RefreshToken);
        IsSignedIn = true;
        AccountStatus = session.Developer ? session.UserName + " · Developer" : session.UserName;
        Password = "";
        ConfirmPassword = "";
        DeveloperCode = "";
        var account = new SavedAccount(SelectedServer!.Id, session.AccountId, session.UserName, session.Developer);
        _savedAccounts.RemoveAll(saved => saved.ServerProfileId == account.ServerProfileId && saved.AccountId == account.AccountId);
        _savedAccounts.Add(account);
        var old = Accounts.FirstOrDefault(saved => saved.AccountId == account.AccountId);
        _selectingAccount = true;
        try
        {
            if (old is not null)
            {
                Accounts.Remove(old);
            }
            Accounts.Add(account);
            SelectedAccount = account;
        }
        finally
        {
            _selectingAccount = false;
        }
        ShowAccountDialog = false;
        OnPropertyChanged(nameof(HasAccounts));
        await SaveCoreAsync();
    }

    [RelayCommand]
    private Task SignOutAsync() =>
        RunAsync(async cancellation =>
        {
            try
            {
                if (_session is not null)
                {
                    using var api = _apiFactory(_sessionAddress!);
                    await api.SignOutAsync(_session.AccessToken, cancellation);
                }
                Status = "Signed out. Your running game clients will remain open.";
            }
            finally
            {
                if (_session is not null)
                {
                    _sessions.Remove(ApiAddress + "\n" + _session.AccountId);
                }
                _credentials.Write(ApiAddress, UserName, null);
                _session = null;
                IsSignedIn = false;
                SelectedAccount = null;
                AccountStatus = "Sign in to your Eden account";
            }
        });

    [RelayCommand]
    private Task UseClientAsync() => RunAsync(ValidateClientCoreAsync);

    private async Task ValidateClientCoreAsync(CancellationToken cancellation)
    {
        await _installation.AdoptAsync(ClientRoot, cancellation);
        ClientReady = true;
        ClientStatus = "READY · Client build 3662168 verified";
        Status = "Client verified and ready to launch.";
        await SaveCoreAsync();
    }

    [RelayCommand]
    private Task PrepareAsync() =>
        RunAsync(async cancellation =>
        {
            ClientReady = false;
            var progress = new Progress<PreparationProgress>(value =>
            {
                PreparationPercent = value.Percent;
                ClientStatus = $"{value.Percent:F0}% · {value.FileName}";
            });
            await Task.Run(() => _installation.PrepareAsync(SourceRoot, ClientRoot, progress, cancellation), cancellation);
            ClientReady = true;
            PreparationPercent = 100;
            ClientStatus = "READY · Client preparation complete";
            Status = "Your isolated Eden client is ready.";
            await SaveCoreAsync();
        });

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private Task PlayAsync() =>
        RunAsync(async cancellation =>
        {
            using var api = _apiFactory(ApiAddress);
            if (_session!.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() + SessionRefreshLeadSeconds)
            {
                string refresh =
                    _credentials.Read(ApiAddress, UserName) ?? throw new InvalidOperationException("Please sign out and sign in again.");
                await AcceptSessionAsync(await api.RefreshAsync(refresh, cancellation));
            }
            var request = await _build.CreateLaunchRequestAsync(ClientRoot, _server!, cancellation);
            var launch = await api.LaunchAsync(_session.AccessToken, request, cancellation);
            await _launcher.LaunchAsync(ClientRoot, _server!, _session, launch, cancellation);
            RunningCount = _launcher.RunningCount;
            Status = "Game launched. Fly safe.";
        });

    [RelayCommand]
    private void Cancel() => _operation?.Cancel();

    private async Task RunAsync(Func<CancellationToken, Task> operation)
    {
        if (IsBusy)
        {
            return;
        }
        IsBusy = true;
        IsError = false;
        using var cancellation = new CancellationTokenSource();
        _operation = cancellation;
        try
        {
            await operation(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            Status = cancellation.IsCancellationRequested
                ? "Operation cancelled. You can resume preparation later."
                : "The server timed out.";
        }
        catch (Exception error) when (IsOperationError(error))
        {
            IsError = true;
            Status = error is HttpRequestException ? "Could not reach the server. Check its address and certificate." : error.Message;
        }
        finally
        {
            _operation = null;
            IsBusy = false;
        }
    }

    private static bool IsOperationError(Exception error) =>
        error
            is IOException
                or InvalidDataException
                or InvalidOperationException
                or ArgumentException
                or System.Security.Cryptography.CryptographicException
                or HttpRequestException
                or LauncherApiException;
}
