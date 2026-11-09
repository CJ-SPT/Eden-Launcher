using System.Security.Cryptography;

namespace Eden.Launcher.Core;

public static class ClientManifest
{
    // Build 3662168's version-4 manifest, packages/certifi/cacert.pem only.
    // The other bin64/cacert.pem entry must retain its original fingerprint.
    public const int ChatHashOffset = 1479;
    public const string OriginalChatHash = "54ABC073292AB1B3EA95361D7E9FBC0FDF3C2B74777F0A4E2B6EC26670F0A453";
    public const int CodeHashOffset = 34;
    public const string OriginalCodeHash = "37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368";

    public static byte[] WithCodeHash(byte[] prefix, string? hash)
    {
        byte[] result = prefix.ToArray();
        if (hash is not null)
        {
            if (
                prefix.Length != ClientBuild.ManifestPrefixLength
                || hash.Length != SHA256.HashSizeInBytes * 2
                || !hash.All(char.IsAsciiHexDigit)
            )
            {
                throw new InvalidDataException("Invalid skill archive manifest hash.");
            }
            Convert.FromHexString(hash).CopyTo(result, CodeHashOffset);
        }
        return result;
    }

    public static byte[] WithChatHash(byte[] prefix, string? hash)
    {
        byte[] result = prefix.ToArray();
        if (hash is null)
        {
            return result;
        }
        if (hash.Length != SHA256.HashSizeInBytes * 2 || !hash.All(char.IsAsciiHexDigit))
        {
            throw new InvalidDataException("Invalid chat certificate bundle hash.");
        }
        if (prefix.Length != ClientBuild.ManifestPrefixLength)
        {
            throw new InvalidDataException("Chat requires the build 3662168 manifest.");
        }
        Convert.FromHexString(hash).CopyTo(result, ChatHashOffset);
        return result;
    }
}
