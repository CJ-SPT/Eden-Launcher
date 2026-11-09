using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Eden.Launcher.Core;

public sealed record PreparationProgress(string FileName, long CopiedBytes, long TotalBytes)
{
    public double Percent => TotalBytes == 0 ? 0 : 100.0 * CopiedBytes / TotalBytes;
}

public interface IClientInstallation
{
    Task PrepareAsync(string source, string destination, IProgress<PreparationProgress>? progress, CancellationToken cancellation);
    Task AdoptAsync(string root, CancellationToken cancellation);
}

public sealed class ClientInstallation(ClientBuild build) : IClientInstallation
{
    private const string SourceMarker = ".eden-preparation-source";

    public async Task PrepareAsync(
        string source,
        string destination,
        IProgress<PreparationProgress>? progress,
        CancellationToken cancellation
    )
    {
        source = Canonical(source);
        destination = Canonical(destination);
        AssertSeparate(source, destination);
        AssertNoLinks(source);
        AssertNoLinks(destination);
        await build.VerifyAsync(source, cancellation);
        build.ReadPrefix(source);
        string? originalCode = null;
        string? originalManifest = null;
        string backup = Path.Combine(source, ClientSkillArchive.OriginalArchive);
        ClientInstallation.AssertNoLinks(backup);
        if (build.Files.FirstOrDefault(file => file.Path == "code.ccp") is { } fingerprint && File.Exists(backup))
        {
            await using var stream = File.OpenRead(backup);
            if (Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation)) == fingerprint.Sha256)
            {
                originalCode = backup;
                string manifestBackup = Path.Combine(source, ClientBuild.OriginalManifest);
                ClientInstallation.AssertNoLinks(manifestBackup);
                if (File.Exists(manifestBackup))
                {
                    byte[] bytes = await File.ReadAllBytesAsync(manifestBackup, cancellation);
                    _ = build.ReadPrefix(bytes);
                    if (Convert.ToHexString(bytes.AsSpan(ClientManifest.CodeHashOffset, SHA256.HashSizeInBytes)) == fingerprint.Sha256)
                    {
                        originalManifest = manifestBackup;
                    }
                }
            }
        }
        using var lease = InstallationLock.Acquire(destination);
        ClientProcesses.AssertNoExternalClient(destination, []);
        string marker = Path.Combine(destination, ClientBuild.BuildMarker);
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any() && !File.Exists(marker))
        {
            throw new InvalidDataException("Choose an empty destination or an existing Eden development copy.");
        }
        string sourceMarker = Path.Combine(destination, SourceMarker);
        if (File.Exists(sourceMarker) && await File.ReadAllTextAsync(sourceMarker, cancellation) != source)
        {
            throw new InvalidDataException("Resume preparation using the same source installation.");
        }
        var files = new[] { "tq", "ResFiles" }.SelectMany(name => EnumerateSafe(Path.Combine(source, name))).ToArray();
        long total = files.Sum(path => new FileInfo(Input(path)).Length);
        long remaining = files.Where(path => !IsCurrent(Input(path), Target(path))).Sum(path => new FileInfo(Input(path)).Length);
        string driveRoot = Path.GetPathRoot(destination)!;
        const long reserveBytes = 256L * 1024 * 1024;
        if (new DriveInfo(driveRoot).AvailableFreeSpace < remaining + reserveBytes)
        {
            throw new IOException("There is not enough free space to prepare this client.");
        }
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(marker, ClientBuild.Number.ToString(), cancellation);
        await File.WriteAllTextAsync(sourceMarker, source, cancellation);
        string ready = Path.Combine(destination, ClientBuild.ReadyMarker);
        File.Delete(ready);
        long copied = 0;
        byte[] buffer = new byte[1024 * 1024];
        var updates = Stopwatch.StartNew();
        foreach (string path in files)
        {
            cancellation.ThrowIfCancellationRequested();
            string target = Target(path);
            string inputPath = Input(path);
            AssertNoLinks(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!IsCurrent(inputPath, target))
            {
                string temporary = target + ".eden-copy.tmp";
                try
                {
                    await using (
                        var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)
                    )
                    await using (
                        var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true)
                    )
                    {
                        int count;
                        long partial = 0;
                        while ((count = await input.ReadAsync(buffer, cancellation)) > 0)
                        {
                            await output.WriteAsync(buffer.AsMemory(0, count), cancellation);
                            partial += count;
                            Report(path, copied + partial, partial == count && input.Length > buffer.Length);
                        }
                    }
                    File.Move(temporary, target, overwrite: true);
                    File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(inputPath));
                }
                finally
                {
                    File.Delete(temporary);
                }
            }
            copied += new FileInfo(inputPath).Length;
            Report(path, copied, copied == total);
        }
        await build.VerifyAsync(destination, cancellation);
        build.ReadPrefix(destination);
        PreserveManifest(destination);
        await AtomicFile.WriteAsync(ready, Encoding.UTF8.GetBytes(ClientBuild.Number.ToString()), cancellation);

        string Target(string path) => Path.Combine(destination, Path.GetRelativePath(source, path));

        string Input(string path) =>
            Path.GetRelativePath(source, path) switch
            {
                var relative when relative == Path.Combine("tq", "code.ccp") && originalCode is not null => originalCode,
                var relative when relative == Path.Combine("tq", "manifest.dat") && originalManifest is not null => originalManifest,
                _ => path,
            };

        void Report(string path, long bytes, bool force)
        {
            const int updateIntervalMilliseconds = 100;
            if (force || updates.ElapsedMilliseconds >= updateIntervalMilliseconds)
            {
                progress?.Report(new(Path.GetRelativePath(source, path), bytes, total));
                updates.Restart();
            }
        }
    }

    public async Task AdoptAsync(string root, CancellationToken cancellation)
    {
        AssertNoLinks(root);
        using var lease = InstallationLock.Acquire(root);
        ClientProcesses.AssertNoExternalClient(root, []);
        if (!File.Exists(Path.Combine(root, ClientBuild.ReadyMarker)))
        {
            throw new InvalidDataException("This folder is not a prepared Eden client. Use Prepare client first.");
        }
        await ClientSkillArchive.RecoverAsync(root, build, cancellation);
        await build.VerifyAsync(root, cancellation);
        build.ReadPrefix(root);
        PreserveManifest(root);
    }

    private static void PreserveManifest(string root)
    {
        string backup = Path.Combine(root, ClientBuild.OriginalManifest);
        if (!File.Exists(backup))
        {
            File.Copy(Path.Combine(root, "tq", "manifest.dat"), backup);
        }
    }

    public static string Canonical(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public static void AssertSeparate(string source, string destination)
    {
        source = Canonical(source);
        destination = Canonical(destination);
        if (
            source.Equals(destination, StringComparison.OrdinalIgnoreCase)
            || source.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || destination.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        )
        {
            throw new InvalidDataException("Use separate, non-overlapping source and destination folders.");
        }
    }

    internal static void AssertNoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Client preparation does not follow symbolic links or junctions.");
            }
        }
    }

    private static IEnumerable<string> EnumerateSafe(string directory)
    {
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            AssertNoLinks(path);
            if (Directory.Exists(path))
            {
                foreach (var file in EnumerateSafe(path))
                {
                    yield return file;
                }
            }
            else if (!path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            {
                yield return path;
            }
        }
    }

    private static bool IsCurrent(string source, string destination) =>
        File.Exists(destination)
        && new FileInfo(source).Length == new FileInfo(destination).Length
        && File.GetLastWriteTimeUtc(source) == File.GetLastWriteTimeUtc(destination);

    public static void ValidateSignedManifest(byte[] manifest, byte[] prefix)
    {
        if (manifest.Length > 32768 || !manifest.AsSpan(0, Math.Min(prefix.Length, manifest.Length)).SequenceEqual(prefix))
        {
            throw new InvalidDataException("The server returned an invalid client manifest.");
        }
        using var stream = new MemoryStream(manifest);
        using var reader = new BinaryReader(stream);
        stream.Position = prefix.Length;
        int keyLength = reader.ReadInt32();
        if (keyLength is < 256 or > 8192)
        {
            throw new InvalidDataException("Invalid manifest key length.");
        }
        string pem = Encoding.ASCII.GetString(reader.ReadBytes(keyLength));
        long signedLength = stream.Position;
        int signatureLength = reader.ReadInt32();
        if (signatureLength is < 128 or > 1024 || stream.Length - stream.Position != signatureLength)
        {
            throw new InvalidDataException("Invalid manifest signature length.");
        }
        using RSA key = RSA.Create();
        key.ImportFromPem(pem);
        if (
            !key.VerifyData(
                SHA256.HashData(manifest.AsSpan(0, (int)signedLength)),
                reader.ReadBytes(signatureLength),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1
            )
        )
        {
            throw new InvalidDataException("The server manifest signature is invalid.");
        }
    }
}

public sealed class InstallationLock : IDisposable
{
    private readonly FileStream _stream;

    private InstallationLock(FileStream stream) => _stream = stream;

    public static InstallationLock Acquire(string root)
    {
        string directory = Path.Combine(Path.GetTempPath(), "EdenLauncherLocks");
        Directory.CreateDirectory(directory);
        string name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ClientInstallation.Canonical(root).ToUpperInvariant())));
        try
        {
            return new(
                new FileStream(Path.Combine(directory, name + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
            );
        }
        catch (IOException)
        {
            throw new IOException("This client installation is already in use by an Eden launcher.");
        }
    }

    public void Dispose() => _stream.Dispose();
}
