using System.Net;
using System.Net.Security;
using System.Security.Cryptography;

namespace Eden.Launcher.Core;

public sealed class ServerCertificateAuthority
{
    private const int MaximumCertificateBytes = 32 * 1024;
    private readonly byte[] _certificate;

    private ServerCertificateAuthority(Uri endpoint, byte[] certificate)
    {
        Endpoint = endpoint;
        _certificate = certificate;
        Fingerprint = Convert.ToHexString(SHA256.HashData(certificate));
    }

    public Uri Endpoint { get; }
    public string Fingerprint { get; }

    public static async Task<ServerCertificateAuthority> DownloadAsync(string address, CancellationToken cancellation)
    {
        var endpoint = LauncherApiClient.ParseEndpoint(address);
        if (endpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException("Enter the server's HTTPS address to download its CA certificate.");
        }
        var certificateAddress = new Uri(endpoint.AbsoluteUri.TrimEnd('/') + "/api/v1/ca-certificate");
        // This handler sends one anonymous request. Chain trust is established only after checking the operator's fingerprint.
        using var client = new HttpClient(
            new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                ServerCertificateCustomValidationCallback = (_, _, _, errors) =>
                    errors is SslPolicyErrors.None or SslPolicyErrors.RemoteCertificateChainErrors,
            }
        );
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await client.GetAsync(certificateAddress, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidDataException("This server does not publish a CA certificate. Ask the server operator to enable downloads.");
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidDataException($"The CA certificate download returned HTTP {(int)response.StatusCode}.");
        }
        if (
            response.Content.Headers.ContentType?.MediaType != "application/pkix-cert"
            || response.Content.Headers.ContentLength is > MaximumCertificateBytes
        )
        {
            throw new InvalidDataException("The server did not return a valid CA certificate.");
        }
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        byte[] bytes = new byte[MaximumCertificateBytes + 1];
        int count = await stream.ReadAtLeastAsync(bytes, bytes.Length, false, timeout.Token);
        if (count is 0 or > MaximumCertificateBytes)
        {
            throw new InvalidDataException("The server did not return a valid CA certificate.");
        }
        try
        {
            return new(endpoint, TlsTrust.ReadCertificateAuthority(bytes[..count]));
        }
        catch (Exception error) when (error is CryptographicException or InvalidDataException)
        {
            throw new InvalidDataException("The server did not return a valid CA certificate.", error);
        }
    }

    public async Task<(ServerReply Server, byte[] Certificate)> VerifyAsync(string expectedFingerprint, CancellationToken cancellation)
    {
        string expected = string.Concat(expectedFingerprint.Where(character => character != ':' && !char.IsWhiteSpace(character)));
        if (expected.Length != Fingerprint.Length || !expected.All(char.IsAsciiHexDigit))
        {
            throw new InvalidDataException("Enter the 64-digit SHA-256 fingerprint from your server operator.");
        }
        if (!expected.Equals(Fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The fingerprints do not match. Check the fingerprint with your server operator.");
        }
        using var api = new LauncherApiClient(
            Endpoint.AbsoluteUri,
            TlsTrust.CreateHttpHandler((_, certificate, _, errors) => TlsTrust.Validate(_certificate, certificate, errors))
        );
        return (await api.ServerAsync(cancellation), (byte[])_certificate.Clone());
    }
}
