using CommunityToolkit.Mvvm.ComponentModel;
using Eden.Launcher.Core;

namespace Eden.Launcher;

public partial class MainViewModel
{
    [ObservableProperty]
    private string? _serverWorldStatus;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpenWorldSetup))]
    private Uri? _worldSetupUri;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpenAdministration))]
    private Uri? _administrationUri;

    public bool CanOpenAdministration => AdministrationUri is not null;
    public bool WorldReady => ServerWorldStatus == WorldStatus.Ready;
    public bool WorldUnavailable => ServerWorldStatus is not null && !WorldReady;
    public bool CanOpenAccount => CanWork && WorldReady;
    public bool CanOpenWorldSetup => WorldSetupUri is not null;
    public bool ShowFirstAccountHint => WorldReady && !HasAccounts;
    public string WorldHeading => ServerWorldStatus == WorldStatus.SetupRequired ? "World setup required" : "Starting world";
    public string WorldInstructions =>
        ServerWorldStatus != WorldStatus.SetupRequired ? "Accounts open when the world is ready."
        : CanOpenWorldSetup ? "An administrator must configure this world before accounts can sign in."
        : "Ask the server administrator to complete world setup.";

    partial void OnWorldSetupUriChanged(Uri? value) => OnPropertyChanged(nameof(WorldInstructions));

    partial void OnServerWorldStatusChanged(string? value)
    {
        OnPropertyChanged(nameof(WorldReady));
        OnPropertyChanged(nameof(WorldUnavailable));
        OnPropertyChanged(nameof(CanOpenAccount));
        OnPropertyChanged(nameof(CanEditAccount));
        OnPropertyChanged(nameof(ShowFirstAccountHint));
        OnPropertyChanged(nameof(WorldHeading));
        OnPropertyChanged(nameof(WorldInstructions));
        OpenAccountCommand.NotifyCanExecuteChanged();
        RefreshPlay();
    }

    private void WaitForWorld(string state, Uri? setupUri)
    {
        ServerWorldStatus = state;
        WorldSetupUri = setupUri;
        AdministrationUri = null;
        _session = null;
        _sessionAddress = null;
        string prefix = ApiAddress + "\n";
        foreach (string key in _sessions.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
        {
            _sessions.Remove(key);
        }
        IsSignedIn = false;
        ShowAccountDialog = false;
        CreateAccount = false;
        Password = "";
        ConfirmPassword = "";
        DeveloperCode = "";
        ConnectionStatus = WorldHeading.ToUpperInvariant();
        Status = WorldInstructions;
    }
}
