using System.Security.Cryptography.X509Certificates;

namespace Eden.Launcher.Core;

public static class ChatTrust
{
    public static async Task InstallAsync(string root, string? certificatePem, CancellationToken cancellation)
    {
        if (certificatePem is null)
        {
            return;
        }
        if (certificatePem.Length > 16384 || !File.Exists(Path.Combine(root, ClientBuild.ReadyMarker)))
        {
            throw new InvalidDataException("Chat trust requires a valid server certificate and prepared Eden client.");
        }
        using var certificate = X509Certificate2.CreateFromPem(certificatePem);
        if (
            certificate.HasPrivateKey
            || certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow
            || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow
            || !certificate.Extensions.OfType<X509BasicConstraintsExtension>().Any(extension => extension.CertificateAuthority)
        )
        {
            throw new InvalidDataException("The server chat certificate is invalid or expired.");
        }
        string pem = certificate.ExportCertificatePem();
        string bundle = Path.Combine(root, "tq", "bin64", "packages", "certifi", "cacert.pem");
        ClientInstallation.AssertNoLinks(bundle);
        string original = await File.ReadAllTextAsync(bundle, cancellation);
        if (original.Length > 4 * 1024 * 1024)
        {
            throw new InvalidDataException("The client certificate bundle is too large.");
        }
        if (!original.Contains(pem, StringComparison.Ordinal))
        {
            await AtomicFile.WriteAsync(bundle, System.Text.Encoding.UTF8.GetBytes(original + "\n" + pem + "\n"), cancellation);
        }
    }
}
