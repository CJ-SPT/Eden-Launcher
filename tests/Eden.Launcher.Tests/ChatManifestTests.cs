using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Eden.Launcher.Core;

namespace Eden.Launcher.Tests;

public sealed class ChatManifestTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("eden-chat-manifest-").FullName;
    private string ManifestPath => Path.Combine(_root, "tq", "manifest.dat");
    private string BundlePath => Path.Combine(_root, "tq", "bin64", "packages", "certifi", "cacert.pem");

    [Fact]
    public async Task SignedBundleFingerprintMatchesNativeVerificationAndIsStableForTheSecondPilot()
    {
        var build = Prepare();
        var server = Server();
        byte[] originalManifest = File.ReadAllBytes(ManifestPath);
        byte[] originalBundle = File.ReadAllBytes(BundlePath);
        var request = await build.CreateLaunchRequestAsync(_root, server, TestContext.Current.CancellationToken);
        Assert.Equal(originalManifest, File.ReadAllBytes(ManifestPath));
        Assert.Equal(originalBundle, File.ReadAllBytes(BundlePath));
        var trust = (await ChatTrust.PrepareAsync(_root, server.ChatCertificatePem, TestContext.Current.CancellationToken))!;
        Assert.Equal(Convert.ToHexString(SHA256.HashData(trust.Contents)), request.ChatBundleSha256);
        byte[] canonical = Convert.FromBase64String(request.ManifestPrefix);
        byte[] expected = ClientManifest.WithChatHash(canonical, request.ChatBundleSha256);
        byte[] signed = Sign(expected);
        ClientInstallation.ValidateSignedManifest(signed, expected);
        // Native Carbon verifies the SHA-256 bytes in each version-4 file entry.
        using var reader = new BinaryReader(new MemoryStream(signed));
        Assert.Equal(4, reader.ReadInt32());
        Assert.Equal(2, reader.ReadInt32());
        Assert.Equal(1, reader.ReadInt32());
        reader.ReadBytes(reader.ReadInt32());
        Assert.Equal(0, reader.ReadInt32());
        Assert.Equal("root:/bin64/packages/certifi\\cacert.pem", Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadInt32())));
        Assert.Equal(SHA256.HashSizeInBytes, reader.ReadInt32());
        Assert.Equal(SHA256.HashData(trust.Contents), reader.ReadBytes(SHA256.HashSizeInBytes));
        Assert.Equal(canonical[..ClientManifest.ChatHashOffset], expected[..ClientManifest.ChatHashOffset]);
        int afterHash = ClientManifest.ChatHashOffset + SHA256.HashSizeInBytes;
        Assert.Equal(canonical[afterHash..], expected[afterHash..]);
        await trust.ApplyAsync(TestContext.Current.CancellationToken);
        await AtomicFile.WriteAsync(ManifestPath, signed, TestContext.Current.CancellationToken);
        // Group launches perform this same request for each account, with no file mutation.
        var second = await build.CreateLaunchRequestAsync(_root, server, TestContext.Current.CancellationToken);
        Assert.Equal(request, second);
        Assert.Equal(signed, File.ReadAllBytes(ManifestPath));
        Assert.False((await ChatTrust.PrepareAsync(_root, server.ChatCertificatePem, TestContext.Current.CancellationToken))!.Changed);
        signed[20] ^= 1;
        await File.WriteAllBytesAsync(ManifestPath, signed, TestContext.Current.CancellationToken);
        Assert.Throws<InvalidDataException>(() => build.ReadPrefix(_root));
    }

    [Fact]
    public async Task ServerThatDoesNotSignTheBundleHashIsRejectedBeforeChangingFilesOrStartingAClient()
    {
        var build = Prepare();
        var server = Server();
        byte[] originalManifest = File.ReadAllBytes(ManifestPath);
        byte[] originalBundle = File.ReadAllBytes(BundlePath);
        var clients = new ClientProcesses(build);
        var reply = new LaunchReply("unused ticket", long.MaxValue, Convert.ToBase64String(Sign(build.ReadPrefix(_root))));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            clients.LaunchAsync(
                _root,
                server,
                new(1000001, "Pilot", "unused", long.MaxValue, null),
                reply,
                TestContext.Current.CancellationToken
            )
        );
        Assert.Equal(originalManifest, File.ReadAllBytes(ManifestPath));
        Assert.Equal(originalBundle, File.ReadAllBytes(BundlePath));
        Assert.Equal(0, clients.RunningCount);
    }

    private ClientBuild Prepare()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(BundlePath)!);
        Directory.CreateDirectory(Path.Combine(_root, "ResFiles"));
        File.WriteAllText(Path.Combine(_root, ClientBuild.ReadyMarker), ClientBuild.Number.ToString());
        File.WriteAllText(BundlePath, "original roots\r\n");
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        byte[] name = Encoding.UTF8.GetBytes("root:/bin64/packages/certifi\\cacert.pem");
        const int headerBytes = 8;
        const int directiveHeaderBytes = 8;
        const int fileEntryHeaderBytes = 12;
        int padding = ClientManifest.ChatHashOffset - headerBytes - directiveHeaderBytes - fileEntryHeaderBytes - name.Length;
        writer.Write(4); // Carbon manifest version.
        writer.Write(2); // One library directive and one file entry.
        writer.Write(1);
        writer.Write(padding);
        writer.Write(new byte[padding]);
        writer.Write(0);
        writer.Write(name.Length);
        writer.Write(name);
        writer.Write(SHA256.HashSizeInBytes);
        Assert.Equal(ClientManifest.ChatHashOffset, stream.Position);
        writer.Write(Convert.FromHexString(ClientManifest.OriginalChatHash));
        int timestampLength = ClientBuild.ManifestPrefixLength - (int)stream.Position - sizeof(int) * 2;
        writer.Write(timestampLength);
        writer.Write(new byte[timestampLength]);
        writer.Write(0); // Public key flag.
        byte[] prefix = stream.ToArray();
        File.WriteAllBytes(ManifestPath, Sign(prefix));
        return new([], prefix.Length, Convert.ToHexString(SHA256.HashData(prefix)));
    }

    private static byte[] Sign(byte[] prefix)
    {
        using RSA key = RSA.Create(2048);
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write(prefix);
        byte[] pem = Encoding.ASCII.GetBytes(key.ExportSubjectPublicKeyInfoPem());
        writer.Write(pem.Length);
        writer.Write(pem);
        byte[] signature = key.SignData(SHA256.HashData(stream.ToArray()), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        writer.Write(signature.Length);
        writer.Write(signature);
        return stream.ToArray();
    }

    private static ServerReply Server()
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new("CN=Chat manifest test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return new("test", "Test", 1, ClientBuild.Number, "localhost:26000", "http://localhost:26001", certificate.ExportCertificatePem());
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
