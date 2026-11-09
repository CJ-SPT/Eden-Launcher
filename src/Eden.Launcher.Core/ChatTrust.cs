using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Eden.Launcher.Core;

public static class ChatTrust
{
    public static async Task<ChatTrustUpdate?> PrepareAsync(
        string root,
        string? certificatePem,
        CancellationToken cancellation,
        string originalBundleHash = ClientManifest.OriginalChatHash
    )
    {
        if (certificatePem is null)
        {
            if (!File.Exists(Path.Combine(root, ClientBuild.ReadyMarker)))
            {
                return null;
            }
            string path = BundlePath(root);
            byte[] current = await ReadBundleAsync(path, cancellation);
            byte[] restored = FindOriginalBundle(current, originalBundleHash, cancellation);
            return new(path, restored, restored.Length != current.Length);
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
        string bundle = BundlePath(root);
        byte[] original = await ReadBundleAsync(bundle, cancellation);
        bool changed = !Encoding.UTF8.GetString(original).Contains(pem, StringComparison.Ordinal);
        byte[] contents = changed ? [.. original, .. Encoding.UTF8.GetBytes("\n" + pem + "\n")] : original;
        return new(bundle, contents, changed);
    }

    private static string BundlePath(string root) => Path.Combine(root, "tq", "bin64", "packages", "certifi", "cacert.pem");

    private static async Task<byte[]> ReadBundleAsync(string path, CancellationToken cancellation)
    {
        ClientInstallation.AssertNoLinks(path);
        const int maximumBundleBytes = 4 * 1024 * 1024;
        if (new FileInfo(path).Length > maximumBundleBytes)
        {
            throw new InvalidDataException("The client certificate bundle is too large.");
        }
        return await File.ReadAllBytesAsync(path, cancellation);
    }

    private static byte[] FindOriginalBundle(byte[] current, string expectedHash, CancellationToken cancellation)
    {
        int length = current.Length;
        while (length >= 0)
        {
            cancellation.ThrowIfCancellationRequested();
            if (Convert.ToHexString(SHA256.HashData(current.AsSpan(0, length))) == expectedHash)
            {
                return current[..length];
            }
            // Earlier launchers appended exactly this separator and PEM. Recover only a hash-verified original prefix.
            length = current.AsSpan(0, length).LastIndexOf("\n-----BEGIN CERTIFICATE-----"u8);
        }
        throw new InvalidDataException("The original client certificate bundle is damaged. Prepare a fresh client copy.");
    }
}

public sealed record ChatTrustUpdate(string Path, byte[] Contents, bool Changed)
{
    public string Sha256 => Convert.ToHexString(SHA256.HashData(Contents));

    public Task ApplyAsync(CancellationToken cancellation) =>
        Changed ? AtomicFile.WriteAsync(Path, Contents, cancellation) : Task.CompletedTask;
}
