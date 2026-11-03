using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Eden.Launcher.Core;

public static class TlsTrust
{
    // An explicit process-local CA supports private servers without changing Windows trust or bypassing certificate checks.
    public static RemoteCertificateValidationCallback? FromEnvironment()
    {
        string? path = Environment.GetEnvironmentVariable("EDEN_LAUNCHER_CA_FILE");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        byte[] bytes = File.ReadAllBytes(path);
        return (_, certificate, _, errors) => Validate(bytes, certificate, errors);
    }

    public static bool Validate(byte[] authority, X509Certificate? certificate, SslPolicyErrors errors)
    {
        if (
            certificate is null
            || (errors & (SslPolicyErrors.RemoteCertificateNotAvailable | SslPolicyErrors.RemoteCertificateNameMismatch)) != 0
        )
        {
            return false;
        }
        using var root = X509CertificateLoader.LoadCertificate(authority);
        using var leaf = new X509Certificate2(certificate);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1"));
        return chain.Build(leaf);
    }
}
