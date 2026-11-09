using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Eden.Launcher.Core;

public interface ILauncherApiClient : IDisposable
{
    Task<ServerReply> ServerAsync(CancellationToken cancellation = default);
    Task<AccountReply> RegisterAsync(AccountRequest request, CancellationToken cancellation = default);
    Task<SessionReply> SignInAsync(SignInRequest request, CancellationToken cancellation = default);
    Task<SessionReply> RefreshAsync(string refreshToken, CancellationToken cancellation = default);
    Task SignOutAsync(string accessToken, CancellationToken cancellation = default);
    Task<LaunchReply> LaunchAsync(string accessToken, LaunchRequest request, CancellationToken cancellation = default);
    Task<byte[]> SkillArchiveAsync(string accessToken, SkillClientPackage package, CancellationToken cancellation = default) =>
        Task.FromException<byte[]>(new InvalidOperationException("This launcher connection does not support skill packages."));
}

public sealed class LauncherApiException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class LauncherApiClient : ILauncherApiClient
{
    private readonly HttpClient _http;

    public LauncherApiClient(string address, HttpMessageHandler? handler = null)
    {
        var endpoint = new Uri(address.TrimEnd('/') + "/");
        ValidateEndpoint(endpoint);
        var defaultHandler = new HttpClientHandler { AllowAutoRedirect = false };
        if (TlsTrust.FromEnvironment() is { } trust)
        {
            defaultHandler.ServerCertificateCustomValidationCallback = (request, certificate, chain, errors) =>
                trust(request, certificate, chain, errors);
        }
        _http = new HttpClient(handler ?? defaultHandler) { BaseAddress = endpoint, Timeout = TimeSpan.FromSeconds(20) };
    }

    public static void ValidateEndpoint(Uri endpoint)
    {
        if (
            endpoint.UserInfo.Length != 0
            || endpoint.Query.Length != 0
            || endpoint.Fragment.Length != 0
            || (endpoint.Scheme != "https" && !(endpoint.Scheme == "http" && IsLoopback(endpoint.Host)))
        )
        {
            throw new InvalidDataException("Use HTTPS for remote servers. HTTP is allowed only on this computer.");
        }
    }

    public static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));

    public async Task<ServerReply> ServerAsync(CancellationToken cancellation = default)
    {
        var server = await SendAsync<ServerReply>(HttpMethod.Get, "server", null, null, cancellation);
        if (server.ApiVersion != 1 || server.ClientBuild != ClientBuild.Number)
        {
            throw new InvalidDataException("This server does not support this launcher or client build.");
        }
        ValidateEndpoint(new Uri(server.GatewayAddress));
        _ = GameEndpoint.Parse(server.GameAddress);
        return server;
    }

    public Task<AccountReply> RegisterAsync(AccountRequest request, CancellationToken cancellation = default) =>
        SendAsync<AccountReply>(HttpMethod.Post, "accounts", JsonContent.Create(request), null, cancellation);

    public Task<SessionReply> SignInAsync(SignInRequest request, CancellationToken cancellation = default) =>
        SendAsync<SessionReply>(HttpMethod.Post, "sessions", JsonContent.Create(request), null, cancellation);

    public Task<SessionReply> RefreshAsync(string refreshToken, CancellationToken cancellation = default) =>
        SendAsync<SessionReply>(
            HttpMethod.Post,
            "sessions/refresh",
            JsonContent.Create(new RefreshRequest(refreshToken)),
            null,
            cancellation
        );

    public async Task SignOutAsync(string accessToken, CancellationToken cancellation = default) =>
        _ = await SendAsync<object>(HttpMethod.Delete, "sessions/current", null, accessToken, cancellation);

    public Task<LaunchReply> LaunchAsync(string accessToken, LaunchRequest request, CancellationToken cancellation = default) =>
        SendAsync<LaunchReply>(HttpMethod.Post, "launch-tickets", JsonContent.Create(request), accessToken, cancellation);

    public async Task<byte[]> SkillArchiveAsync(string accessToken, SkillClientPackage package, CancellationToken cancellation = default)
    {
        ClientSkillArchive.Validate(package);
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/skill-packages/" + package.Id + "/code");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength != package.CodeLength)
        {
            throw new InvalidDataException(
                "The server skill package is unavailable or its size changed. Refresh the server and try again."
            );
        }
        byte[] bytes = new byte[checked((int)package.CodeLength)];
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
        await stream.ReadExactlyAsync(bytes, cancellation);
        if (stream.ReadByte() != -1)
        {
            throw new InvalidDataException("The skill package is larger than advertised.");
        }
        ClientSkillArchive.Verify(bytes, package);
        return bytes;
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, HttpContent? content, string? access, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(method, "api/v1/" + path) { Content = content };
        if (access is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        }
        using var response = await _http.SendAsync(request, cancellation);
        if (!response.IsSuccessStatusCode)
        {
            ApiError? error = null;
            try
            {
                error = await response.Content.ReadFromJsonAsync<ApiError>(cancellation);
            }
            catch (JsonException)
            {
                // Proxies may return HTML. Do not expose their body or request credentials.
            }
            throw new LauncherApiException(error?.Code ?? "server_error", error?.Message ?? $"Server returned {(int)response.StatusCode}.");
        }
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return default!;
        }
        return await response.Content.ReadFromJsonAsync<T>(cancellation)
            ?? throw new InvalidDataException("The server returned an empty reply.");
    }

    public void Dispose() => _http.Dispose();
}

public sealed record GameEndpoint(string Host, int Port)
{
    public static GameEndpoint Parse(string address)
    {
        if (
            !Uri.TryCreate("tcp://" + address, UriKind.Absolute, out var uri)
            || uri.Port is < 1 or > 65535
            || uri.UserInfo.Length > 0
            || uri.AbsolutePath != "/"
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0
        )
        {
            throw new InvalidDataException("The server advertised an invalid game address.");
        }
        return new(uri.Host, uri.Port);
    }
}
