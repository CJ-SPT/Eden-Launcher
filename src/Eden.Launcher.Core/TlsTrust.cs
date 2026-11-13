using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Eden.Launcher.Core;

public static class TlsTrust
{
    public static RemoteCertificateValidationCallback? ForServer(string? certificateAuthorityPath)
    {
        if (!string.IsNullOrWhiteSpace(certificateAuthorityPath))
        {
            return FromCertificateFile(certificateAuthorityPath);
        }
        string? path = Environment.GetEnvironmentVariable("EDEN_LAUNCHER_CA_FILE");
        return string.IsNullOrWhiteSpace(path) ? null : FromCertificateFile(path);
    }

    public static RemoteCertificateValidationCallback FromCertificateFile(string path)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            _ = ReadCertificateAuthority(bytes);
            return (_, certificate, _, errors) => Validate(bytes, certificate, errors);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
        {
            throw new InvalidDataException("Could not read the server CA certificate. Check the certificate file in Settings.", error);
        }
    }

    internal static byte[] ReadCertificateAuthority(byte[] bytes)
    {
        using var authority = X509CertificateLoader.LoadCertificate(bytes);
        if (!authority.Extensions.OfType<X509BasicConstraintsExtension>().Any(extension => extension.CertificateAuthority))
        {
            throw new InvalidDataException("Choose the server's CA certificate.");
        }
        return authority.RawData;
    }

    internal static HttpClientHandler CreateHttpHandler(RemoteCertificateValidationCallback? trust)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        if (trust is not null)
        {
            handler.ServerCertificateCustomValidationCallback = (request, certificate, chain, errors) =>
                trust(request, certificate, chain, errors);
        }
        return handler;
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
