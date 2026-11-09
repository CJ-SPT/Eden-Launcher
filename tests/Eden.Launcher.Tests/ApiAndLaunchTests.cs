using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Eden.Launcher.Core;

namespace Eden.Launcher.Tests;

public sealed class ApiAndLaunchTests
{
    [Theory]
    [InlineData("http://example.com:26002")]
    [InlineData("https://name:password@example.com")]
    [InlineData("file:///C:/secret")]
    public void RemoteCredentialsRequireAnHttpsOrigin(string address) =>
        Assert.Throws<InvalidDataException>(() => new LauncherApiClient(address));

    [Fact]
    public async Task NamedErrorsSurviveWithoutExposingTheResponseBody()
    {
        using var api = new LauncherApiClient(
            "http://127.0.0.1:26002",
            new Handler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = JsonContent.Create(new ApiError("invalid_credentials", "Invalid account name or password.")),
            })
        );
        var error = await Assert.ThrowsAsync<LauncherApiException>(() =>
            api.SignInAsync(new("user", "secret", false), TestContext.Current.CancellationToken)
        );
        Assert.Equal("invalid_credentials", error.Code);
        Assert.DoesNotContain("secret", error.Message);
    }

    [Fact]
    public async Task UnsupportedServerBuildIsRejected()
    {
        using var api = new LauncherApiClient(
            "http://localhost:26002",
            new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new ServerReply("test", "Test", 1, 1, "localhost:26000", "http://localhost:26001")),
            })
        );
        await Assert.ThrowsAsync<InvalidDataException>(() => api.ServerAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a URL")]
    [InlineData("/relative")]
    public async Task MalformedGatewayAddressesAreRejectedAsInvalidData(string? gateway)
    {
        using var api = new LauncherApiClient(
            "http://localhost:26002",
            new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new ServerReply("test", "Test", 1, ClientBuild.Number, "localhost:26000", gateway!)),
            })
        );
        await Assert.ThrowsAsync<InvalidDataException>(() => api.ServerAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void LaunchArgumentsPreservePathsAndIsolateAccountSettings()
    {
        string root = Path.Combine(Path.GetTempPath(), "client with spaces");
        var server = new ServerReply("test-server", "Test", 1, ClientBuild.Number, "127.0.0.1:26100", "http://127.0.0.1:26101");
        var account = new SessionReply(1000003, "Pilot", "secret", 1, null);
        var start = ClientProcesses.CreateStartInfo(root, server, account, "ticket", "127.0.0.1:12345");
        Assert.False(start.UseShellExecute);
        Assert.Contains("/remotefilecachefolder=" + Path.Combine(root, "ResFiles"), start.ArgumentList);
        Assert.Contains("/port:26100", start.ArgumentList);
        Assert.Contains("/ssoToken=ticket", start.ArgumentList);
        Assert.Equal("127.0.0.1:12345", start.Environment["EVE_PUBLIC_GATEWAY_ADDRESS"]);
        var other = ClientProcesses.CreateStartInfo(root, server, account with { AccountId = 1000004 }, "ticket", "localhost:12345");
        Assert.NotEqual(
            start.ArgumentList.Single(arg => arg.StartsWith("/settingsprofile=", StringComparison.Ordinal)),
            other.ArgumentList.Single(arg => arg.StartsWith("/settingsprofile=", StringComparison.Ordinal))
        );
    }

    [Fact]
    public async Task RelayForwardsBytesAndClosesItsClientsOnDisposal()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var relay = new GatewayRelay(new Uri("http://" + endpoint));
        using var client = new TcpClient();
        await client.ConnectAsync(IPEndPoint.Parse(relay.Address), timeout.Token);
        using var upstream = await listener.AcceptTcpClientAsync(timeout.Token);
        await client.GetStream().WriteAsync(new byte[] { 1, 2, 3 }, timeout.Token);
        byte[] received = new byte[3];
        await upstream.GetStream().ReadExactlyAsync(received, timeout.Token);
        Assert.Equal(new byte[] { 1, 2, 3 }, received);
        await upstream.GetStream().WriteAsync(new byte[] { 4 }, timeout.Token);
        byte[] reply = new byte[1];
        await client.GetStream().ReadExactlyAsync(reply, timeout.Token);
        Assert.Equal(4, reply[0]);
        await relay.DisposeAsync();
        Assert.Equal(0, await client.GetStream().ReadAsync(reply, timeout.Token));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation) =>
            Task.FromResult(respond(request));
    }
}
