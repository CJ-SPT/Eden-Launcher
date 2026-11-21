using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Eden.Launcher.Core;

namespace Eden.Launcher;

public sealed partial class GroupAccountChoice(SavedAccount account) : ObservableObject
{
    public SavedAccount Account { get; } = account;

    [ObservableProperty]
    private bool _included;
}

public partial class MainViewModel
{
    private readonly List<LaunchGroup> _savedGroups = [];
    private string? _editingGroupId;
    public ObservableCollection<LaunchGroup> Groups { get; } = [];
    public ObservableCollection<GroupAccountChoice> GroupAccounts { get; } = [];
    public ObservableCollection<string> GroupResults { get; } = [];

    [ObservableProperty]
    private LaunchGroup? _selectedGroup;

    [ObservableProperty]
    private bool _showGroupDialog;

    [ObservableProperty]
    private string _groupName = "";

    [ObservableProperty]
    private string _groupStatus = "Create a group to launch accounts together.";

    public bool CanEditGroup => CanWork && SelectedGroup is not null;
    public bool CanLaunchGroup => CanEditGroup && WorldReady && ClientReady && SelectedGroup!.AccountIds.Count > 0;

    private void RefreshGroups()
    {
        OnPropertyChanged(nameof(CanEditGroup));
        OnPropertyChanged(nameof(CanLaunchGroup));
        LaunchGroupCommand.NotifyCanExecuteChanged();
        EditGroupCommand.NotifyCanExecuteChanged();
        DeleteGroupCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedGroupChanged(LaunchGroup? value)
    {
        GroupResults.Clear();
        GroupStatus = value is null ? "Create a group to launch accounts together." : $"{value.AccountIds.Count} accounts in this group.";
        RefreshGroups();
    }

    private void LoadGroups()
    {
        SelectedGroup = null;
        Groups.Clear();
        foreach (var group in _savedGroups.Where(group => group.ServerProfileId == SelectedServer?.Id))
        {
            Groups.Add(group);
        }
        SelectedGroup = Groups.FirstOrDefault();
        ShowGroupDialog = false;
        GroupAccounts.Clear();
        GroupResults.Clear();
        RefreshGroups();
    }

    [RelayCommand(CanExecute = nameof(CanWork))]
    private void NewGroup() => OpenGroupEditor(null);

    [RelayCommand(CanExecute = nameof(CanEditGroup))]
    private void EditGroup() => OpenGroupEditor(SelectedGroup);

    private void OpenGroupEditor(LaunchGroup? group)
    {
        if (IsBusy)
        {
            return;
        }
        _editingGroupId = group?.Id;
        GroupName = group?.Name ?? "";
        GroupAccounts.Clear();
        foreach (var account in Accounts)
        {
            GroupAccounts.Add(new(account) { Included = group?.AccountIds.Contains(account.AccountId) == true });
        }
        Status = "Name your group and choose its accounts.";
        ShowGroupDialog = true;
    }

    [RelayCommand]
    private void CloseGroup() => ShowGroupDialog = false;

    [RelayCommand(CanExecute = nameof(CanWork))]
    private Task SaveGroupAsync() =>
        RunAsync(async _ =>
        {
            string name = GroupName.Trim();
            if (name.Length is 0 or > LaunchGroup.MaximumNameLength)
            {
                throw new InvalidDataException($"Enter a group name of 1–{LaunchGroup.MaximumNameLength} characters.");
            }
            if (Groups.Any(group => group.Id != _editingGroupId && group.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("A group with this name already exists on this server.");
            }
            long[] members = GroupAccounts.Where(choice => choice.Included).Select(choice => choice.Account.AccountId).Distinct().ToArray();
            if (members.Length == 0)
            {
                throw new InvalidDataException("Choose at least one account for this group.");
            }
            var group = new LaunchGroup(_editingGroupId ?? Guid.NewGuid().ToString("N"), SelectedServer!.Id, name, members);
            var old = Groups.FirstOrDefault(saved => saved.Id == group.Id);
            _savedGroups.RemoveAll(saved => saved.Id == group.Id);
            _savedGroups.Add(group);
            if (old is not null)
            {
                Groups[Groups.IndexOf(old)] = group;
            }
            else
            {
                Groups.Add(group);
            }
            SelectedGroup = group;
            await SaveCoreAsync();
            ShowGroupDialog = false;
            Status = "Launch group saved.";
        });

    [RelayCommand(CanExecute = nameof(CanEditGroup))]
    private Task DeleteGroupAsync() =>
        RunAsync(async _ =>
        {
            if (SelectedGroup is not { } group)
            {
                return;
            }
            _savedGroups.RemoveAll(saved => saved.Id == group.Id);
            Groups.Remove(group);
            SelectedGroup = Groups.FirstOrDefault();
            await SaveCoreAsync();
            Status = "Launch group deleted. Its accounts are still available.";
        });

    [RelayCommand(CanExecute = nameof(CanLaunchGroup))]
    private Task LaunchGroupAsync() =>
        RunAsync(async cancellation =>
        {
            if (SelectedGroup is not { } group || !WorldReady || !ClientReady)
            {
                throw new InvalidOperationException("Choose a group, connect to its server, and verify your client first.");
            }
            GroupResults.Clear();
            string address = ApiAddress;
            string authority = CertificateAuthorityPath.Trim();
            string root = ClientRoot;
            var accounts = Accounts.ToArray();
            var members = group.AccountIds.Distinct().ToArray();
            int launched = 0;
            int skipped = 0;
            int failed = 0;
            bool cancelled = false;
            using var api = _apiFactory(address, authority);
            var server = await api.ServerAsync(cancellation);
            SetConnected(server);
            if (!WorldReady)
            {
                return;
            }
            foreach (long accountId in members)
            {
                if (cancellation.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }
                var account = accounts.FirstOrDefault(account => account.AccountId == accountId);
                string name = account?.UserName ?? $"Account {accountId}";
                GroupStatus = $"Launching {name}…";
                try
                {
                    if (_launcher.IsRunning(server.ServerId, accountId))
                    {
                        skipped++;
                        GroupResults.Add($"{name}: already running.");
                        continue;
                    }
                    if (account is null)
                    {
                        throw new InvalidOperationException("Account is no longer saved. Edit this group.");
                    }
                    _sessions.TryGetValue(address + "\n" + account.AccountId, out var cached);
                    var request = await _build.CreateLaunchRequestAsync(root, server, cancellation);
                    var result = await RequestLaunchAsync(api, address, account, cached, request, cancellation);
                    var (session, launch) =
                        result ?? throw new InvalidOperationException("Remembered sign-in expired or missing. Sign in to this account.");
                    launch = await ClientSkillArchive.DownloadForLaunchAsync(api, root, server, session, launch, cancellation);
                    await _launcher.LaunchAsync(root, server, session, launch, authority, cancellation);
                    RunningCount = _launcher.RunningCount;
                    launched++;
                    GroupResults.Add($"{name}: launched.");
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }
                catch (OperationCanceledException)
                {
                    failed++;
                    GroupResults.Add($"{name}: server timed out. Try again.");
                }
                catch (LauncherApiException error) when (error.Code is "world_setup_required" or "world_starting")
                {
                    throw;
                }
                catch (Exception error) when (IsOperationError(error))
                {
                    failed++;
                    string message = OperationErrorMessage(error);
                    GroupResults.Add($"{name}: {message}");
                }
            }
            IsError = failed > 0;
            GroupStatus = $"{launched} launched · {skipped} already running · {failed} need attention";
            if (cancelled)
            {
                GroupStatus += " · Cancelled; remaining accounts were not launched.";
            }
            Status = GroupStatus;
        });
}
