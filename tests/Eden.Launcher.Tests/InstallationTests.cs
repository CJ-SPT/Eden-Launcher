using System.Security.Cryptography;
using System.Text;
using Eden.Launcher.Core;

namespace Eden.Launcher.Tests;

public sealed class InstallationTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("eden-launcher-").FullName;
    private string Source => Path.Combine(_directory, "original client");
    private string Destination => Path.Combine(_directory, "prepared client");

    private ClientBuild CreateSource()
    {
        Directory.CreateDirectory(Path.Combine(Source, "tq"));
        Directory.CreateDirectory(Path.Combine(Source, "ResFiles"));
        byte[] definition = Encoding.UTF8.GetBytes("fixture build definition");
        byte[] prefix = Encoding.UTF8.GetBytes("fixture manifest prefix");
        File.WriteAllBytes(Path.Combine(Source, "tq", "start.ini"), definition);
        File.WriteAllBytes(Path.Combine(Source, "tq", "manifest.dat"), prefix);
        File.WriteAllBytes(Path.Combine(Source, "ResFiles", "resource"), new byte[3 * 1024 * 1024]);
        return new(
            [new("start.ini", Convert.ToHexString(SHA256.HashData(definition)))],
            prefix.Length,
            Convert.ToHexString(SHA256.HashData(prefix))
        );
    }

    [Fact]
    public async Task PreparationPreservesSourceAndCanResumeAfterCancellation()
    {
        var build = CreateSource();
        var installation = new ClientInstallation(build);
        using var cancellation = new CancellationTokenSource();
        var progress = new ImmediateProgress(value =>
        {
            if (value.FileName.Contains("resource", StringComparison.Ordinal) && value.Percent < 100)
            {
                cancellation.Cancel();
            }
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            installation.PrepareAsync(Source, Destination, progress, cancellation.Token)
        );
        Assert.False(File.Exists(Path.Combine(Destination, ClientBuild.ReadyMarker)));
        Assert.Empty(Directory.EnumerateFiles(Destination, "*.tmp", SearchOption.AllDirectories));
        await installation.PrepareAsync(Source, Destination, null, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(Destination, ClientBuild.ReadyMarker)));
        Assert.Equal(
            await File.ReadAllBytesAsync(Path.Combine(Source, "tq", "manifest.dat"), TestContext.Current.CancellationToken),
            await File.ReadAllBytesAsync(Path.Combine(Destination, ClientBuild.OriginalManifest), TestContext.Current.CancellationToken)
        );
        await installation.AdoptAsync(Destination, CancellationToken.None);
        Assert.Equal(
            new FileInfo(Path.Combine(Source, "ResFiles", "resource")).Length,
            new FileInfo(Path.Combine(Destination, "ResFiles", "resource")).Length
        );
    }

    [Fact]
    public async Task InvalidBuildAndUnrelatedDestinationsAreRejected()
    {
        var build = CreateSource();
        var installation = new ClientInstallation(build);
        Directory.CreateDirectory(Destination);
        await File.WriteAllTextAsync(Path.Combine(Destination, "keep.txt"), "existing file", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => installation.PrepareAsync(Source, Destination, null, CancellationToken.None));
        Assert.Equal(
            "existing file",
            await File.ReadAllTextAsync(Path.Combine(Destination, "keep.txt"), TestContext.Current.CancellationToken)
        );
        await File.WriteAllTextAsync(Path.Combine(Source, "tq", "start.ini"), "modified", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => build.VerifyAsync(Source, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void NestedAndEqualPathsAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => ClientInstallation.AssertSeparate(Source, Source));
        Assert.Throws<InvalidDataException>(() => ClientInstallation.AssertSeparate(Source, Path.Combine(Source, "child")));
        Assert.Throws<InvalidDataException>(() => ClientInstallation.AssertSeparate(Path.Combine(Source, "child"), Source));
        ClientInstallation.AssertSeparate(Source, Destination);
    }

    [Fact]
    public async Task InterruptedAtomicWriteKeepsThePreviousFile()
    {
        string path = Path.Combine(_directory, "manifest.dat");
        await File.WriteAllTextAsync(path, "old", TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AtomicFile.WriteAsync(path, [1, 2, 3], cancellation.Token));
        Assert.Equal("old", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void InstallationLeasePreventsAnotherLauncherFromChangingTheSameCopy()
    {
        using var lease = InstallationLock.Acquire(Destination);
        Assert.Throws<IOException>(() => InstallationLock.Acquire(Destination));
        using var separate = InstallationLock.Acquire(Source);
    }

    [Fact]
    public void ManifestVerificationRejectsTampering()
    {
        byte[] prefix = Encoding.UTF8.GetBytes("fixture prefix");
        using RSA key = RSA.Create(2048);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(prefix);
        byte[] pem = Encoding.ASCII.GetBytes(key.ExportSubjectPublicKeyInfoPem());
        writer.Write(pem.Length);
        writer.Write(pem);
        byte[] signature = key.SignData(SHA256.HashData(stream.ToArray()), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        writer.Write(signature.Length);
        writer.Write(signature);
        byte[] manifest = stream.ToArray();
        ClientInstallation.ValidateSignedManifest(manifest, prefix);
        manifest[^1] ^= 1;
        Assert.Throws<InvalidDataException>(() => ClientInstallation.ValidateSignedManifest(manifest, prefix));
        Assert.Throws<InvalidDataException>(() => ClientInstallation.ValidateSignedManifest([1, 2, 3], prefix));
    }

    private sealed class ImmediateProgress(Action<PreparationProgress> report) : IProgress<PreparationProgress>
    {
        public void Report(PreparationProgress value) => report(value);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
