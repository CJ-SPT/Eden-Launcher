using System.Diagnostics;

namespace Eden.Launcher.Core;

public interface IClientLauncher
{
    int RunningCount { get; }
    event Action? Changed;
    bool IsRunning(string serverId, long accountId);
    Task LaunchAsync(
        string root,
        ServerReply server,
        SessionReply session,
        LaunchReply launch,
        string? certificateAuthorityPath,
        CancellationToken cancellation
    );
}

public sealed class ClientProcesses(ClientBuild build) : IClientLauncher
{
    private readonly SemaphoreSlim _gate = new(1);
    private readonly List<RunningClient> _clients = [];
    private readonly Dictionary<string, InstallationLock> _leases = new(StringComparer.OrdinalIgnoreCase);
    private RunningClient[] _running = [];
    public int RunningCount { get; private set; }
    public event Action? Changed;

    private sealed record RunningClient(Process Process, string Root, string ServerId, long AccountId, IGatewayRelay Relay);

    public bool IsRunning(string serverId, long accountId) =>
        Volatile.Read(ref _running).Any(client => client.ServerId == serverId && client.AccountId == accountId);

    private void NotifyChanged()
    {
        Volatile.Write(ref _running, _clients.ToArray());
        RunningCount = _clients.Count;
        Changed?.Invoke();
    }

    public async Task LaunchAsync(
        string root,
        ServerReply server,
        SessionReply session,
        LaunchReply launch,
        string? certificateAuthorityPath,
        CancellationToken cancellation
    )
    {
        root = ClientInstallation.Canonical(root);
        await _gate.WaitAsync(cancellation);
        try
        {
            if (_clients.Any(client => client.ServerId == server.ServerId && client.AccountId == session.AccountId))
            {
                throw new InvalidOperationException("This account already has a running game client.");
            }
            AssertNoExternalClient(root, _clients.Select(client => client.Process.Id).ToArray());
            bool newLease = !_leases.ContainsKey(root);
            if (newLease)
            {
                _leases.Add(root, InstallationLock.Acquire(root));
            }
            IGatewayRelay? relay = null;
            try
            {
                if (!File.Exists(Path.Combine(root, ClientBuild.ReadyMarker)))
                {
                    throw new InvalidDataException("Prepare or select a ready Eden client first.");
                }
                ClientInstallation.AssertNoLinks(root);
                await ClientSkillArchive.RecoverAsync(root, build, cancellation);
                await build.VerifyAsync(root, cancellation);
                byte[] prefix = build.ReadPrefix(root);
                var trust = await ChatTrust.PrepareAsync(root, server.ChatCertificatePem, cancellation, build.ChatBundleHash);
                prefix = ClientManifest.WithChatHash(prefix, trust?.Sha256);
                prefix = ClientManifest.WithCodeHash(prefix, server.ClientPackage?.CodeSha256);
                byte[] manifest = Convert.FromBase64String(launch.Manifest);
                ClientInstallation.ValidateSignedManifest(manifest, prefix);
                var skills = await ClientSkillArchive.PrepareAsync(root, build, server.ClientPackage, launch.SkillArchive, cancellation);
                string manifestPath = Path.Combine(root, "tq", "manifest.dat");
                byte[] currentManifest = await File.ReadAllBytesAsync(manifestPath, cancellation);
                bool manifestChanged = !manifest.AsSpan().SequenceEqual(currentManifest);
                if (
                    _clients.Any(client => client.Root.Equals(root, StringComparison.OrdinalIgnoreCase))
                    && (manifestChanged || trust?.Changed == true || skills?.Changed == true)
                )
                {
                    throw new InvalidOperationException(
                        "Close this installation's game clients before changing server keys, chat trust or skills."
                    );
                }
                if (launch.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                {
                    throw new InvalidDataException("The login ticket expired. Press Play again.");
                }
                try
                {
                    await ClientSkillArchive.BeginUpdateAsync(root, skills, currentManifest, cancellation);
                    await ClientSkillArchive.ApplyAsync(root, build, skills, cancellation);
                    if (manifestChanged)
                    {
                        await AtomicFile.WriteAsync(manifestPath, manifest, cancellation);
                    }
                    if (trust is not null)
                    {
                        await trust.ApplyAsync(cancellation);
                    }
                    ClientSkillArchive.CompleteUpdate(root);
                }
                catch
                {
                    if (skills?.Changed == true)
                    {
                        await AtomicFile.WriteAsync(Path.Combine(root, "tq", "code.ccp"), skills.Previous, CancellationToken.None);
                    }
                    await AtomicFile.WriteAsync(manifestPath, currentManifest, CancellationToken.None);
                    ClientSkillArchive.CompleteUpdate(root);
                    throw;
                }
                relay = new GatewayRelay(new Uri(server.GatewayAddress), certificateAuthorityPath);
                var process =
                    Process.Start(CreateStartInfo(root, server, session, launch.Ticket, relay.Address))
                    ?? throw new IOException("The game client could not start.");
                var running = new RunningClient(process, root, server.ServerId, session.AccountId, relay);
                _clients.Add(running);
                NotifyChanged();
                _ = MonitorAsync(running);
            }
            catch
            {
                if (relay is not null)
                {
                    await relay.DisposeAsync();
                }
                if (newLease)
                {
                    _leases[root].Dispose();
                    _leases.Remove(root);
                }
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public static ProcessStartInfo CreateStartInfo(string root, ServerReply server, SessionReply session, string ticket, string gateway)
    {
        var endpoint = GameEndpoint.Parse(server.GameAddress);
        string bin = Path.Combine(root, "tq", "bin64");
        var start = new ProcessStartInfo(Path.Combine(bin, "exefile.exe"))
        {
            WorkingDirectory = bin,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Normal,
        };
        // The pinned client accepts game port separately from the server hostname.
        foreach (
            string argument in new[]
            {
                "/noconsole",
                "/server:" + endpoint.Host,
                "/port:" + endpoint.Port,
                "/remotefilecachefolder=" + Path.Combine(root, "ResFiles"),
                $"/settingsprofile=Eden_{SafeProfile(server.ServerId)}_{session.AccountId}",
                "/triPlatform=dx11",
                "/ssoToken=" + ticket,
            }
        )
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment["EVE_PUBLIC_GATEWAY_ADDRESS"] = gateway;
        return start;
    }

    private static string SafeProfile(string value) => new(value.Where(char.IsAsciiLetterOrDigit).Take(32).ToArray());

    public static void AssertNoExternalClient(string root, IReadOnlyList<int> ownedIds)
    {
        string executable = Path.GetFullPath(Path.Combine(root, "tq", "bin64", "exefile.exe"));
        foreach (var process in Process.GetProcessesByName("exefile"))
        {
            using (process)
            {
                try
                {
                    if (
                        !ownedIds.Contains(process.Id)
                        && string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase)
                    )
                    {
                        throw new IOException("Close the game client already using this installation first.");
                    }
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    throw new IOException("Could not check a running game client. Close it before managing this installation.");
                }
                catch (InvalidOperationException)
                {
                    // The process exited while it was being inspected.
                }
            }
        }
    }

    private async Task MonitorAsync(RunningClient client)
    {
        await client.Process.WaitForExitAsync();
        await _gate.WaitAsync();
        try
        {
            await client.Relay.DisposeAsync();
            _clients.Remove(client);
            client.Process.Dispose();
            if (!_clients.Any(other => other.Root.Equals(client.Root, StringComparison.OrdinalIgnoreCase)))
            {
                _leases[client.Root].Dispose();
                _leases.Remove(client.Root);
            }
            NotifyChanged();
        }
        finally
        {
            _gate.Release();
        }
    }
}
