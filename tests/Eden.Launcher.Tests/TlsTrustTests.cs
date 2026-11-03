using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Eden.Launcher.Core;

namespace Eden.Launcher.Tests;

public sealed class TlsTrustTests
{
    [Fact]
    public void ExplicitAuthorityStillRequiresTheCorrectHostAndCertificateChain()
    {
        using RSA rootKey = RSA.Create(2048);
        var request = new CertificateRequest("CN=Test Root", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var root = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using RSA leafKey = RSA.Create(2048);
        var leafRequest = new CertificateRequest("CN=localhost", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var leaf = leafRequest.Create(root, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1), [1, 2, 3, 4]);
        byte[] authority = root.Export(X509ContentType.Cert);
        Assert.True(TlsTrust.Validate(authority, leaf, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(TlsTrust.Validate(authority, leaf, SslPolicyErrors.RemoteCertificateNameMismatch));
        using RSA unrelatedKey = RSA.Create(2048);
        var unrelatedRequest = new CertificateRequest("CN=Unrelated", unrelatedKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var unrelated = unrelatedRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        Assert.False(TlsTrust.Validate(unrelated.Export(X509ContentType.Cert), leaf, SslPolicyErrors.RemoteCertificateChainErrors));
    }
}
