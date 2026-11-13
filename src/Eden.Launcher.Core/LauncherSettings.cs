using System.Text.Json;

namespace Eden.Launcher.Core;

public sealed record ServerProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "Eden Local";
    public string ApiAddress { get; init; } = "http://127.0.0.1:26002";
    public string? CertificateAuthorityPath { get; init; }
    public string ClientRoot { get; init; } = "";
    public string UserName { get; init; } = "";

    public override string ToString() => Name;
}

public sealed record SavedAccount(string ServerProfileId, long AccountId, string UserName, bool Developer)
{
    public string Initial => UserName.Length == 0 ? "E" : UserName[..1].ToUpperInvariant();
    public string Caption => Developer ? "DEVELOPER ACCOUNT" : "EDEN ACCOUNT";
}

public sealed record LaunchGroup(string Id, string ServerProfileId, string Name, IReadOnlyList<long> AccountIds)
{
    public const int MaximumNameLength = 80;

    public override string ToString() => Name;
}

public sealed record LauncherSettings(
    IReadOnlyList<ServerProfile> Servers,
    string SelectedServerId,
    IReadOnlyList<SavedAccount>? Accounts = null,
    IReadOnlyList<LaunchGroup>? Groups = null
)
{
    public static LauncherSettings Default()
    {
        var profile = new ServerProfile();
        return new([profile], profile.Id);
    }
}

public interface ISettingsStore
{
    Task<LauncherSettings> LoadAsync();
    Task SaveAsync(LauncherSettings settings);
}

public interface ICredentialStore
{
    string? Read(string serverAddress, string userName);
    void Write(string serverAddress, string userName, string? refreshToken);
}

public sealed class SettingsStore(string directory) : ISettingsStore
{
    private readonly string _path = Path.Combine(directory, "settings.json");
    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };

    public async Task<LauncherSettings> LoadAsync()
    {
        if (!File.Exists(_path))
        {
            return LauncherSettings.Default();
        }
        try
        {
            var settings = JsonSerializer.Deserialize<LauncherSettings>(await File.ReadAllTextAsync(_path));
            if (settings is { Servers.Count: > 0 })
            {
                return settings;
            }
        }
        catch (JsonException)
        {
            // Preserve malformed settings for recovery; start with usable defaults.
        }
        return LauncherSettings.Default();
    }

    public async Task SaveAsync(LauncherSettings settings)
    {
        Directory.CreateDirectory(directory);
        await AtomicFile.WriteAsync(_path, JsonSerializer.SerializeToUtf8Bytes(settings, s_json));
    }
}

public static class AtomicFile
{
    public static async Task WriteAsync(string path, byte[] bytes, CancellationToken cancellation = default)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellation);
            cancellation.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
