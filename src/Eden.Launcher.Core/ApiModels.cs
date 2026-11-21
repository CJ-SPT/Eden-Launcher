namespace Eden.Launcher.Core;

public sealed record AccountRequest(string UserName, string Password, bool Developer = false, string? DeveloperCode = null);

public sealed record SignInRequest(string UserName, string Password, bool RememberMe);

public sealed record RefreshRequest(string RefreshToken);

public sealed record LaunchRequest(
    int Build,
    string ManifestPrefix,
    string? ChatBundleSha256 = null,
    string? SkillPackageId = null,
    string? ModSetId = null
);

public sealed record AccountReply(long AccountId, string UserName, bool Developer = false);

public sealed record SessionReply(
    long AccountId,
    string UserName,
    string AccessToken,
    long ExpiresAt,
    string? RefreshToken,
    bool Developer = false
);

public sealed record LaunchReply(string Ticket, long ExpiresAt, string Manifest)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public byte[]? SkillArchive { get; init; }
}

public sealed record SkillClientPackage(string Id, long Generation, string CodeSha256, long CodeLength);

public sealed record ModDependency(string Id, string Version);

public sealed record ModSetPackage(string Id, long Generation, string CodeSha256, long CodeLength, IReadOnlyList<ModDependency> Mods)
{
    public SkillClientPackage AsArchive() => new(Id, Generation, CodeSha256, CodeLength);
}

public sealed record ApiError(string Code, string Message);

public static class WorldStatus
{
    public const string SetupRequired = "setup_required";
    public const string Starting = "starting";
    public const string Ready = "ready";
}

public sealed record ServerReply(
    string ServerId,
    string Name,
    int ApiVersion,
    int ClientBuild,
    string GameAddress,
    string GatewayAddress,
    string? ChatCertificatePem = null,
    SkillClientPackage? SkillPackage = null,
    ModSetPackage? ModSet = null,
    string WorldStatus = Eden.Launcher.Core.WorldStatus.Ready,
    string? WorldSetupUrl = null,
    string? AdministrationUrl = null
)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public SkillClientPackage? ClientPackage => ModSet?.AsArchive() ?? SkillPackage;

    public Uri? SetupUri(string apiAddress) =>
        WorldStatus == Eden.Launcher.Core.WorldStatus.SetupRequired ? BrowserUri(WorldSetupUrl, apiAddress) : null;

    public Uri? AdministrationUri(string apiAddress) => BrowserUri(AdministrationUrl, apiAddress);

    private static Uri? BrowserUri(string? address, string apiAddress)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }
        if (uri.IsLoopback && !LauncherApiClient.ParseEndpoint(apiAddress).IsLoopback)
        {
            return null;
        }
        return uri.Scheme == Uri.UriSchemeHttps || (uri.IsLoopback && uri.Scheme == Uri.UriSchemeHttp) ? uri : null;
    }
}
