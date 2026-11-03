using System.Security.Cryptography;

namespace Eden.Launcher.Core;

public sealed record FileFingerprint(string Path, string Sha256);

public sealed class ClientBuild
{
    public const int Number = 3662168;
    public const int ManifestPrefixLength = 17769;
    public const string ManifestPrefixHash = "04EB4A00671E7289BF14AAFBEE6849D490678A291DAD0DF333E519913921C631";
    public const string StockManifestHash = "895B2827174D7BDDC54870763E9D5AEA056066C01E3D56F0DDF76927DCD3AA26";
    public const string ReadyMarker = ".eden-client-ready";
    public const string BuildMarker = ".eden-build-3662168";
    public const string OriginalManifest = ".eden-original-manifest.dat";
    public IReadOnlyList<FileFingerprint> Files { get; }
    public int ManifestLength { get; }
    public string ManifestHash { get; }

    public ClientBuild(
        IReadOnlyList<FileFingerprint>? files = null,
        int manifestLength = ManifestPrefixLength,
        string manifestHash = ManifestPrefixHash
    )
    {
        ManifestLength = manifestLength;
        ManifestHash = manifestHash;
        Files =
            files
            ??
            [
                new("start.ini", "4F8216EF3873B779ACDCF414D6A061C61545FB2A489BB3288EF0CFD95B9965F0"),
                new("code.ccp", "37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368"),
                new("bin64/exefile.exe", "4C33AD1B4D95C80D2D314849FF6B577A27B6D2420D3CA52641EC3A354EA3D787"),
                new("bin64/blue.dll", "BFC3C167D25A5BD2CFAF0EA8D60E25028E9F034380885746BCCCFB73645FC94A"),
            ];
    }

    public async Task VerifyAsync(string root, CancellationToken cancellation = default)
    {
        foreach (var fingerprint in Files)
        {
            string path = Path.Combine(root, "tq", fingerprint.Path);
            await using var stream = File.OpenRead(path);
            if (Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation)) != fingerprint.Sha256)
            {
                throw new InvalidDataException($"Wrong client build or modified file: {fingerprint.Path}");
            }
        }
        if (!Directory.Exists(Path.Combine(root, "ResFiles")))
        {
            throw new InvalidDataException("Choose a client folder containing both tq and ResFiles.");
        }
    }

    public static byte[] ReadManifestPrefix(string root) => new ClientBuild().ReadPrefix(root);

    public byte[] ReadPrefix(string root)
    {
        using var stream = File.OpenRead(Path.Combine(root, "tq", "manifest.dat"));
        byte[] prefix = new byte[ManifestLength];
        stream.ReadExactly(prefix);
        if (Convert.ToHexString(SHA256.HashData(prefix)) != ManifestHash)
        {
            throw new InvalidDataException("Unrecognized client manifest. Restore the original build 3662168 files.");
        }
        return prefix;
    }
}
