using System.Net;
using System.Security.Cryptography;
using System.Text;
using Eden.Launcher.Core;

namespace Eden.Launcher.Tests;

public sealed class SkillPackageTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("eden-skill-client-").FullName;
    private readonly byte[] _original = [1, 2, 3, 4, 5];
    private string Archive => Path.Combine(_root, "tq", "code.ccp");
    private string Manifest => Path.Combine(_root, "tq", "manifest.dat");

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private ClientBuild Prepare()
    {
        Directory.CreateDirectory(Path.Combine(_root, "tq"));
        Directory.CreateDirectory(Path.Combine(_root, "ResFiles"));
        File.WriteAllBytes(Archive, _original);
        byte[] prefix = new byte[ClientBuild.ManifestPrefixLength];
        prefix = ClientManifest.WithCodeHash(prefix, Hash(_original));
        prefix = ClientManifest.WithChatHash(prefix, ClientManifest.OriginalChatHash);
        File.WriteAllBytes(Manifest, Sign(prefix));
        return new([new("code.ccp", Hash(_original))], prefix.Length, Hash(prefix));
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

    [Fact]
    public async Task InstallingSwitchingAndRestoringVerifyArchivesAndKeepThePinnedOriginal()
    {
        var build = Prepare();
        await build.VerifyAsync(_root, TestContext.Current.CancellationToken);
        byte[] edited = [10, 20, 30];
        SkillClientPackage package = new(new string('A', 64), 1, Hash(edited), edited.Length);
        var change = await ClientSkillArchive.PrepareAsync(_root, build, package, edited, TestContext.Current.CancellationToken);
        Assert.True(change!.Changed);
        Assert.Equal(_original, File.ReadAllBytes(Archive));
        await ClientSkillArchive.ApplyAsync(_root, build, change, TestContext.Current.CancellationToken);
        Assert.Equal(_original, File.ReadAllBytes(Path.Combine(_root, ClientSkillArchive.OriginalArchive)));
        Assert.Equal(edited, File.ReadAllBytes(Archive));
        File.WriteAllBytes(Manifest, Sign(ClientManifest.WithCodeHash(build.ReadPrefix(_root), package.CodeSha256)));
        await build.VerifyAsync(_root, TestContext.Current.CancellationToken);
        Assert.False((await ClientSkillArchive.PrepareAsync(_root, build, package, null, TestContext.Current.CancellationToken))!.Changed);
        byte[] other = [40, 50, 60, 70];
        SkillClientPackage next = new(new string('B', 64), 2, Hash(other), other.Length);
        var switchServer = await ClientSkillArchive.PrepareAsync(_root, build, next, other, TestContext.Current.CancellationToken);
        await ClientSkillArchive.ApplyAsync(_root, build, switchServer, TestContext.Current.CancellationToken);
        File.WriteAllBytes(Manifest, Sign(ClientManifest.WithCodeHash(build.ReadPrefix(_root), next.CodeSha256)));
        await build.VerifyAsync(_root, TestContext.Current.CancellationToken);
        Assert.Equal(_original, File.ReadAllBytes(Path.Combine(_root, ClientSkillArchive.OriginalArchive)));
        var restore = await ClientSkillArchive.PrepareAsync(_root, build, null, null, TestContext.Current.CancellationToken);
        await ClientSkillArchive.ApplyAsync(_root, build, restore, TestContext.Current.CancellationToken);
        File.WriteAllBytes(Manifest, Sign(build.ReadPrefix(_root)));
        await build.VerifyAsync(_root, TestContext.Current.CancellationToken);
        Assert.Equal(_original, File.ReadAllBytes(Archive));
    }

    [Fact]
    public async Task InterruptedUpdatesRecoverThePreviousVerifiedArchiveAndManifest()
    {
        var build = Prepare();
        byte[] first = [10, 20, 30];
        SkillClientPackage package = new(new string('A', 64), 1, Hash(first), first.Length);
        byte[] originalManifest = File.ReadAllBytes(Manifest);
        var change = await ClientSkillArchive.PrepareAsync(_root, build, package, first, TestContext.Current.CancellationToken);
        await ClientSkillArchive.BeginUpdateAsync(_root, change, originalManifest, TestContext.Current.CancellationToken);
        await ClientSkillArchive.ApplyAsync(_root, build, change, TestContext.Current.CancellationToken);
        await ClientSkillArchive.RecoverAsync(_root, build, TestContext.Current.CancellationToken);
        Assert.Equal(_original, File.ReadAllBytes(Archive));
        Assert.Equal(originalManifest, File.ReadAllBytes(Manifest));
        await ClientSkillArchive.ApplyAsync(_root, build, change, TestContext.Current.CancellationToken);
        byte[] firstManifest = Sign(ClientManifest.WithCodeHash(build.ReadPrefix(_root), package.CodeSha256));
        File.WriteAllBytes(Manifest, firstManifest);
        byte[] second = [40, 50, 60];
        SkillClientPackage next = new(new string('B', 64), 2, Hash(second), second.Length);
        var update = await ClientSkillArchive.PrepareAsync(_root, build, next, second, TestContext.Current.CancellationToken);
        await ClientSkillArchive.BeginUpdateAsync(_root, update, firstManifest, TestContext.Current.CancellationToken);
        await ClientSkillArchive.ApplyAsync(_root, build, update, TestContext.Current.CancellationToken);
        await ClientSkillArchive.RecoverAsync(_root, build, TestContext.Current.CancellationToken);
        Assert.Equal(first, File.ReadAllBytes(Archive));
        Assert.Equal(firstManifest, File.ReadAllBytes(Manifest));
        await build.VerifyAsync(_root, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PreparingFromAnUpdatedClientCopiesItsVerifiedPinnedOriginal()
    {
        var build = Prepare();
        File.WriteAllBytes(Path.Combine(_root, ClientBuild.OriginalManifest), File.ReadAllBytes(Manifest));
        File.WriteAllBytes(Path.Combine(_root, "ResFiles", "resource"), [1]);
        byte[] edited = [10, 20, 30];
        SkillClientPackage package = new(new string('A', 64), 1, Hash(edited), edited.Length);
        var update = await ClientSkillArchive.PrepareAsync(_root, build, package, edited, TestContext.Current.CancellationToken);
        await ClientSkillArchive.ApplyAsync(_root, build, update, TestContext.Current.CancellationToken);
        File.WriteAllBytes(Manifest, Sign(ClientManifest.WithCodeHash(build.ReadPrefix(_root), package.CodeSha256)));
        string destination = Path.Combine(Path.GetDirectoryName(_root)!, "eden-prepared-skill-" + Guid.NewGuid().ToString("N"));
        try
        {
            await new ClientInstallation(build).PrepareAsync(_root, destination, null, TestContext.Current.CancellationToken);
            Assert.Equal(_original, File.ReadAllBytes(Path.Combine(destination, "tq", "code.ccp")));
            Assert.Equal(edited, File.ReadAllBytes(Archive));
            await build.VerifyAsync(destination, TestContext.Current.CancellationToken);
        }
        finally
        {
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }
        }
    }

    [Fact]
    public async Task InvalidDownloadsMissingBackupsAndUnsignedArchivesAreRejectedWithoutWriting()
    {
        var build = Prepare();
        byte[] edited = [10, 20, 30];
        SkillClientPackage package = new(new string('A', 64), 1, Hash(edited), edited.Length);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ClientSkillArchive.PrepareAsync(_root, build, package, [0, 0, 0], TestContext.Current.CancellationToken)
        );
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ClientSkillArchive.PrepareAsync(_root, build, package, null, TestContext.Current.CancellationToken)
        );
        Assert.Equal(_original, File.ReadAllBytes(Archive));
        Assert.False(File.Exists(Path.Combine(_root, ClientSkillArchive.OriginalArchive)));
        File.WriteAllBytes(Archive, edited);
        await Assert.ThrowsAsync<InvalidDataException>(() => build.VerifyAsync(_root, TestContext.Current.CancellationToken));
        File.WriteAllBytes(Path.Combine(_root, ClientSkillArchive.OriginalArchive), _original);
        await Assert.ThrowsAsync<InvalidDataException>(() => build.VerifyAsync(_root, TestContext.Current.CancellationToken));
        Assert.Equal(edited, File.ReadAllBytes(Archive));
    }

    [Theory]
    [InlineData("../bad", 3)]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", 0)]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", 268435457)]
    public void InvalidPackageIdentitiesAndSizesAreRejected(string id, long size) =>
        Assert.Throws<InvalidDataException>(() => ClientSkillArchive.Validate(new(id, 1, new string('A', 64), size)));

    [Fact]
    public async Task DownloadUsesTheAuthenticatedServerOriginAndChecksItsExactContents()
    {
        byte[] bytes = [10, 20, 30];
        SkillClientPackage package = new(new string('A', 64), 1, Hash(bytes), bytes.Length);
        using var api = new LauncherApiClient(
            "https://server.example",
            new Handler(request =>
            {
                Assert.Equal("https://server.example/api/v1/skill-packages/" + package.Id + "/code", request.RequestUri!.AbsoluteUri);
                Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
                Assert.Equal("access", request.Headers.Authorization.Parameter);
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            })
        );
        Assert.Equal(bytes, await api.SkillArchiveAsync("access", package, TestContext.Current.CancellationToken));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handle(request));
    }

    [Fact]
    public async Task ModSetsUseAuthenticatedV2DownloadsAndTheExistingVerifiedInstallationPipeline()
    {
        var build = Prepare();
        byte[] edited = [8, 7, 6, 5];
        var set = new ModSetPackage(new string('C', 64), 3, Hash(edited), edited.Length, [new("example.hello", "1.0.0")]);
        var server = new ServerReply("test", "Test", 2, ClientBuild.Number, "127.0.0.1:26000", "https://server.example", ModSet: set);
        using var api = new LauncherApiClient(
            "https://server.example",
            new Handler(request =>
            {
                Assert.Equal("https://server.example/api/v2/mod-sets/" + set.Id + "/code", request.RequestUri!.AbsoluteUri);
                Assert.Equal("access", request.Headers.Authorization!.Parameter);
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(edited) };
            })
        );
        var request = await build.CreateLaunchRequestAsync(_root, server, TestContext.Current.CancellationToken);
        Assert.Equal(set.Id, request.ModSetId);
        Assert.Null(request.SkillPackageId);
        var session = new SessionReply(1, "Pilot", "access", long.MaxValue, null);
        var launch = await ClientSkillArchive.DownloadForLaunchAsync(
            api,
            _root,
            server,
            session,
            new("ticket", long.MaxValue, ""),
            TestContext.Current.CancellationToken
        );
        Assert.Equal(edited, launch.SkillArchive);
        Assert.Equal(set.AsArchive(), server.ClientPackage);
        var change = await ClientSkillArchive.PrepareAsync(
            _root,
            build,
            server.ClientPackage,
            launch.SkillArchive,
            TestContext.Current.CancellationToken
        );
        await ClientSkillArchive.ApplyAsync(_root, build, change, TestContext.Current.CancellationToken);
        Assert.Equal(edited, File.ReadAllBytes(Archive));
        Assert.Equal(_original, File.ReadAllBytes(Path.Combine(_root, ClientSkillArchive.OriginalArchive)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ServerDiscoverySupportsBothProtocolVersions(int version)
    {
        var server = new ServerReply("test", "Test", version, ClientBuild.Number, "127.0.0.1:26000", "https://server.example");
        using var api = new LauncherApiClient(
            "https://server.example",
            new Handler(_ => new(HttpStatusCode.OK) { Content = System.Net.Http.Json.JsonContent.Create(server) })
        );
        Assert.Equal(version, (await api.ServerAsync(TestContext.Current.CancellationToken)).ApiVersion);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
