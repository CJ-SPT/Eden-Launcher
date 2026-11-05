using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Eden.Launcher.Core;

public static class ChatTrust
{
    public static async Task<ChatTrustUpdate?> PrepareAsync(string root, string? certificatePem, CancellationToken cancellation)
    {
        if (certificatePem is null)
        {
            return null;
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
        const int maximumBundleBytes = 4 * 1024 * 1024;
        if (new FileInfo(bundle).Length > maximumBundleBytes)
        {
            throw new InvalidDataException("The client certificate bundle is too large.");
        }
        byte[] original = await File.ReadAllBytesAsync(bundle, cancellation);
        bool changed = !Encoding.UTF8.GetString(original).Contains(pem, StringComparison.Ordinal);
        byte[] contents = changed ? [.. original, .. Encoding.UTF8.GetBytes("\n" + pem + "\n")] : original;
        return new(bundle, contents, changed);
    }
}

public sealed record ChatTrustUpdate(string Path, byte[] Contents, bool Changed)
{
    public string Sha256 => Convert.ToHexString(SHA256.HashData(Contents));

    public Task ApplyAsync(CancellationToken cancellation) =>
        Changed ? AtomicFile.WriteAsync(Path, Contents, cancellation) : Task.CompletedTask;
}
