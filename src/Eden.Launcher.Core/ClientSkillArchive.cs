using System.Security.Cryptography;

namespace Eden.Launcher.Core;

public sealed record SkillArchiveChange(byte[] Previous, byte[] Next, bool Changed);

public static class ClientSkillArchive
{
    public const string OriginalArchive = ".eden-original-code.ccp";
    private const string RollbackCode = ".eden-skill-update.code";
    private const string RollbackManifest = ".eden-skill-update.manifest";
    private const string RollbackReady = ".eden-skill-update.ready";
    private const long MaximumArchiveBytes = 256L * 1024 * 1024;

    public static void Validate(SkillClientPackage package)
    {
        if (
            string.IsNullOrWhiteSpace(package.Id)
            || package.Id.Length != 64
            || !package.Id.All(char.IsAsciiHexDigit)
            || string.IsNullOrWhiteSpace(package.CodeSha256)
            || package.CodeSha256.Length != 64
            || !package.CodeSha256.All(char.IsAsciiHexDigit)
            || package.CodeLength is < 1 or > MaximumArchiveBytes
            || package.Generation < 0
        )
        {
            throw new InvalidDataException("The server advertised an invalid skill package.");
        }
    }

    public static void Verify(byte[] bytes, SkillClientPackage package)
    {
        Validate(package);
        if (bytes.LongLength != package.CodeLength || Convert.ToHexString(SHA256.HashData(bytes)) != package.CodeSha256.ToUpperInvariant())
        {
            throw new InvalidDataException("The downloaded skill package failed verification. No client files were changed.");
        }
    }

    internal static async Task<bool> IsInstalledAsync(
        string root,
        string originalHash,
        string actualHash,
        ClientBuild build,
        CancellationToken cancellation
    )
    {
        string backup = Path.Combine(root, OriginalArchive);
        ClientInstallation.AssertNoLinks(backup);
        if (!File.Exists(backup) || build.ManifestLength != ClientBuild.ManifestPrefixLength)
        {
            return false;
        }
        await using var stream = File.OpenRead(backup);
        if (Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation)) != originalHash)
        {
            return false;
        }
        byte[] manifest = await File.ReadAllBytesAsync(Path.Combine(root, "tq", "manifest.dat"), cancellation);
        byte[] canonical = build.ReadPrefix(root);
        byte[] prefix = ClientManifest.WithCodeHash(canonical, actualHash);
        string chatHash = Convert.ToHexString(manifest.AsSpan(ClientManifest.ChatHashOffset, SHA256.HashSizeInBytes));
        prefix = ClientManifest.WithChatHash(prefix, chatHash);
        ClientInstallation.ValidateSignedManifest(manifest, prefix);
        return true;
    }

    public static async Task<LaunchReply> DownloadForLaunchAsync(
        ILauncherApiClient api,
        string root,
        ServerReply server,
        SessionReply session,
        LaunchReply launch,
        CancellationToken cancellation
    )
    {
        if (server.ClientPackage is not { } package)
        {
            return launch;
        }
        Validate(package);
        await using var stream = File.OpenRead(Path.Combine(root, "tq", "code.ccp"));
        if (
            !File.Exists(Path.Combine(root, RollbackReady))
            && Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation)) == package.CodeSha256.ToUpperInvariant()
        )
        {
            return launch;
        }
        return launch with
        {
            SkillArchive = server.ModSet is { } set
                ? await api.ModArchiveAsync(session.AccessToken, set, cancellation)
                : await api.SkillArchiveAsync(session.AccessToken, package, cancellation),
        };
    }

    public static async Task<SkillArchiveChange?> PrepareAsync(
        string root,
        ClientBuild build,
        SkillClientPackage? package,
        byte[]? download,
        CancellationToken cancellation
    )
    {
        var original = build.Files.FirstOrDefault(file => file.Path == "code.ccp");
        if (original is null)
        {
            return null;
        }
        byte[] previous = await File.ReadAllBytesAsync(Path.Combine(root, "tq", "code.ccp"), cancellation);
        string desired = package?.CodeSha256.ToUpperInvariant() ?? original.Sha256;
        if (package is not null)
        {
            Validate(package);
        }
        if (Convert.ToHexString(SHA256.HashData(previous)) == desired)
        {
            return new(previous, previous, false);
        }
        byte[] next;
        if (package is not null)
        {
            next = download ?? throw new InvalidDataException("Download the server's skill package before launching.");
            Verify(next, package);
        }
        else
        {
            string backup = Path.Combine(root, OriginalArchive);
            ClientInstallation.AssertNoLinks(backup);
            next = await File.ReadAllBytesAsync(backup, cancellation);
            if (Convert.ToHexString(SHA256.HashData(next)) != original.Sha256)
            {
                throw new InvalidDataException("The original client archive backup is damaged. Prepare a fresh client copy.");
            }
        }
        return new(previous, next, true);
    }

    public static async Task ApplyAsync(string root, ClientBuild build, SkillArchiveChange? change, CancellationToken cancellation)
    {
        if (change?.Changed != true)
        {
            return;
        }
        string backup = Path.Combine(root, OriginalArchive);
        ClientInstallation.AssertNoLinks(backup);
        if (!File.Exists(backup))
        {
            string original = build.Files.Single(file => file.Path == "code.ccp").Sha256;
            if (Convert.ToHexString(SHA256.HashData(change.Previous)) != original)
            {
                throw new InvalidDataException("The unchanged original archive is required before installing skills.");
            }
            await AtomicFile.WriteAsync(backup, change.Previous, cancellation);
        }
        else
        {
            await using var originalStream = File.OpenRead(backup);
            string expected = build.Files.Single(file => file.Path == "code.ccp").Sha256;
            if (Convert.ToHexString(await SHA256.HashDataAsync(originalStream, cancellation)) != expected)
            {
                throw new InvalidDataException("The original client archive backup is damaged. Prepare a fresh client copy.");
            }
        }
        await AtomicFile.WriteAsync(Path.Combine(root, "tq", "code.ccp"), change.Next, cancellation);
    }

    public static async Task BeginUpdateAsync(
        string root,
        SkillArchiveChange? change,
        byte[] previousManifest,
        CancellationToken cancellation
    )
    {
        if (change?.Changed != true)
        {
            return;
        }
        foreach (string file in new[] { RollbackCode, RollbackManifest, RollbackReady })
        {
            ClientInstallation.AssertNoLinks(Path.Combine(root, file));
        }
        await AtomicFile.WriteAsync(Path.Combine(root, RollbackCode), change.Previous, cancellation);
        await AtomicFile.WriteAsync(Path.Combine(root, RollbackManifest), previousManifest, cancellation);
        await AtomicFile.WriteAsync(Path.Combine(root, RollbackReady), [1], cancellation);
    }

    public static void CompleteUpdate(string root)
    {
        foreach (string file in new[] { RollbackReady, RollbackCode, RollbackManifest })
        {
            ClientInstallation.AssertNoLinks(Path.Combine(root, file));
            File.Delete(Path.Combine(root, file));
        }
    }

    public static async Task RecoverAsync(string root, ClientBuild build, CancellationToken cancellation)
    {
        ClientInstallation.AssertNoLinks(Path.Combine(root, RollbackReady));
        if (!File.Exists(Path.Combine(root, RollbackReady)))
        {
            return;
        }
        ClientProcesses.AssertNoExternalClient(root, []);
        foreach (string file in new[] { RollbackCode, RollbackManifest, OriginalArchive })
        {
            ClientInstallation.AssertNoLinks(Path.Combine(root, file));
        }
        byte[] code = await File.ReadAllBytesAsync(Path.Combine(root, RollbackCode), cancellation);
        byte[] manifest = await File.ReadAllBytesAsync(Path.Combine(root, RollbackManifest), cancellation);
        byte[] prefix = build.ReadPrefix(manifest);
        string sha = Convert.ToHexString(SHA256.HashData(code));
        string original = build.Files.Single(file => file.Path == "code.ccp").Sha256;
        if (sha != original)
        {
            byte[] backup = await File.ReadAllBytesAsync(Path.Combine(root, OriginalArchive), cancellation);
            if (Convert.ToHexString(SHA256.HashData(backup)) != original)
            {
                throw new InvalidDataException("The original client archive backup is damaged. Prepare a fresh client copy.");
            }
            prefix = ClientManifest.WithCodeHash(prefix, sha);
            prefix = ClientManifest.WithChatHash(
                prefix,
                Convert.ToHexString(manifest.AsSpan(ClientManifest.ChatHashOffset, SHA256.HashSizeInBytes))
            );
            ClientInstallation.ValidateSignedManifest(manifest, prefix);
        }
        await AtomicFile.WriteAsync(Path.Combine(root, "tq", "code.ccp"), code, cancellation);
        await AtomicFile.WriteAsync(Path.Combine(root, "tq", "manifest.dat"), manifest, cancellation);
        CompleteUpdate(root);
    }
}
