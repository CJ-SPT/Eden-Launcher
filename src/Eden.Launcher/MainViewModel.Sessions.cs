using Eden.Launcher.Core;

namespace Eden.Launcher;

public partial class MainViewModel
{
    private async Task RestoreRememberedSessionAsync(CancellationToken cancellation)
    {
        if (IsSignedIn)
        {
            return;
        }
        string address = ApiAddress;
        string name = UserName;
        var account = Accounts.FirstOrDefault(saved => string.Equals(saved.UserName, name, StringComparison.OrdinalIgnoreCase));
        using var api = _apiFactory(address, CertificateAuthorityPath.Trim());
        try
        {
            var session = await RefreshRememberedSessionAsync(api, address, name, account?.AccountId, cancellation);
            if (session is not null && ApiAddress == address && UserName == name)
            {
                await AcceptSessionAsync(session);
                Status = "Welcome back. Your account is ready.";
            }
        }
        catch (LauncherApiException error) when (error.Code == "unauthorized" && ApiAddress == address && UserName == name)
        {
            ShowSignIn(name);
            Status = "Your remembered sign-in expired. Sign in again.";
        }
    }

    private void ShowSignIn(string name)
    {
        _session = null;
        _sessionAddress = null;
        IsSignedIn = false;
        UserName = name;
        CreateAccount = false;
        ShowAccountDialog = true;
        AccountStatus = "Sign in to " + name;
    }

    private void SaveSession(string address, SessionReply session)
    {
        _credentials.Write(address, session.UserName, session.RefreshToken);
        _sessions[address + "\n" + session.AccountId] = session;
        if (_sessionAddress == address && _session?.AccountId == session.AccountId)
        {
            _session = session;
        }
    }

    private async Task<SessionReply?> RefreshRememberedSessionAsync(
        ILauncherApiClient api,
        string address,
        string name,
        long? accountId,
        CancellationToken cancellation
    )
    {
        if (_credentials.Read(address, name) is not { } refresh)
        {
            return null;
        }
        SessionReply session;
        try
        {
            session = await api.RefreshAsync(refresh, cancellation);
        }
        catch (LauncherApiException error) when (error.Code == "unauthorized")
        {
            _credentials.Write(address, name, null);
            if (accountId is { } id)
            {
                _sessions.Remove(address + "\n" + id);
            }
            throw;
        }
        if (accountId is { } expected && session.AccountId != expected)
        {
            throw new InvalidDataException("Remembered sign-in belongs to another account. Sign in again.");
        }
        SaveSession(address, session);
        return session;
    }

    private async Task<(SessionReply Session, LaunchReply Launch)?> RequestLaunchAsync(
        ILauncherApiClient api,
        string address,
        SavedAccount account,
        SessionReply? session,
        LaunchRequest request,
        CancellationToken cancellation
    )
    {
        if (session is not null && session.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds() + SessionRefreshLeadSeconds)
        {
            try
            {
                return (session, await api.LaunchAsync(session.AccessToken, request, cancellation));
            }
            catch (LauncherApiException error) when (error.Code == "unauthorized")
            {
                _sessions.Remove(address + "\n" + account.AccountId);
            }
        }
        SessionReply? renewed;
        try
        {
            renewed = await RefreshRememberedSessionAsync(api, address, account.UserName, account.AccountId, cancellation);
        }
        catch (LauncherApiException error) when (error.Code == "unauthorized")
        {
            return null;
        }
        if (renewed is null)
        {
            return null;
        }
        return (renewed, await api.LaunchAsync(renewed.AccessToken, request, cancellation));
    }
}
