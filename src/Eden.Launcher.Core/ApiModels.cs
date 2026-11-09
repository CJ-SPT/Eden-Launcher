namespace Eden.Launcher.Core;

public sealed record AccountRequest(string UserName, string Password, bool Developer = false, string? DeveloperCode = null);

public sealed record SignInRequest(string UserName, string Password, bool RememberMe);

public sealed record RefreshRequest(string RefreshToken);

public sealed record LaunchRequest(int Build, string ManifestPrefix, string? ChatBundleSha256 = null, string? SkillPackageId = null);

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

public sealed record ApiError(string Code, string Message);

public sealed record ServerReply(
    string ServerId,
    string Name,
    int ApiVersion,
    int ClientBuild,
    string GameAddress,
    string GatewayAddress,
    string? ChatCertificatePem = null,
    SkillClientPackage? SkillPackage = null
);
