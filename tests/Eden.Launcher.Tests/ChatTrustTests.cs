using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Eden.Launcher.Core;

namespace Eden.Launcher.Tests;

public sealed class ChatTrustTests
{
    [Fact]
    public async Task ChatCertificateIsAddedOnlyToPreparedClientAndRepeatedLaunchesPreserveTheOriginalBundle()
    {
        string root = Path.Combine(Path.GetTempPath(), "eden-chat-trust-" + Guid.NewGuid().ToString("N"));
        string bundle = Path.Combine(root, "tq", "bin64", "packages", "certifi", "cacert.pem");
        Directory.CreateDirectory(Path.GetDirectoryName(bundle)!);
        try
        {
            await File.WriteAllTextAsync(bundle, "original public roots\n", TestContext.Current.CancellationToken);
            using var certificate = Certificate(true);
            string pem = certificate.ExportCertificatePem();
            await Assert.ThrowsAsync<InvalidDataException>(() => ChatTrust.PrepareAsync(root, pem, TestContext.Current.CancellationToken));
            Assert.Equal("original public roots\n", await File.ReadAllTextAsync(bundle, TestContext.Current.CancellationToken));
            await File.WriteAllTextAsync(
                Path.Combine(root, ClientBuild.ReadyMarker),
                ClientBuild.Number.ToString(),
                TestContext.Current.CancellationToken
            );
            var update = await ChatTrust.PrepareAsync(root, pem, TestContext.Current.CancellationToken);
            Assert.Equal("original public roots\n", await File.ReadAllTextAsync(bundle, TestContext.Current.CancellationToken));
            await update!.ApplyAsync(TestContext.Current.CancellationToken);
            string first = await File.ReadAllTextAsync(bundle, TestContext.Current.CancellationToken);
            Assert.StartsWith("original public roots\n", first);
            Assert.Contains(pem, first);
            var repeated = await ChatTrust.PrepareAsync(root, pem, TestContext.Current.CancellationToken);
            Assert.False(repeated!.Changed);
            Assert.Equal(update.Sha256, repeated.Sha256);
            await repeated.ApplyAsync(TestContext.Current.CancellationToken);
            Assert.Equal(first, await File.ReadAllTextAsync(bundle, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task OlderServersDoNotChangeClientTrust()
    {
        await ChatTrust.PrepareAsync("not-a-client-path", null, TestContext.Current.CancellationToken);
        var server = System.Text.Json.JsonSerializer.Deserialize<ServerReply>(
            """
            {"ServerId":"old","Name":"Old","ApiVersion":1,"ClientBuild":3662168,
             "GameAddress":"localhost:26000","GatewayAddress":"http://localhost:26001"}
            """
        );
        Assert.Null(server!.ChatCertificatePem);
    }

    [Fact]
    public async Task NonAuthorityCertificatesCannotBeInstalled()
    {
        string root = Path.Combine(Path.GetTempPath(), "eden-chat-trust-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, ClientBuild.ReadyMarker), "3662168", TestContext.Current.CancellationToken);
            using var leaf = Certificate(false);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                ChatTrust.PrepareAsync(root, leaf.ExportCertificatePem(), TestContext.Current.CancellationToken)
            );
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static X509Certificate2 Certificate(bool authority)
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new("CN=Chat test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(authority, false, 0, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }
}
