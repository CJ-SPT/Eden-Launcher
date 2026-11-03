using System.Net;
using System.Net.Security;
using System.Net.Sockets;

namespace Eden.Launcher.Core;

public interface IGatewayRelay : IAsyncDisposable
{
    string Address { get; }
}

// The pinned client's address override selects h2c. Forward its HTTP/2 bytes over validated TLS.
public sealed class GatewayRelay : IGatewayRelay
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Uri _upstream;
    private readonly Task _accept;
    private readonly List<Task> _connections = [];
    public string Address => ((IPEndPoint)_listener.LocalEndpoint).ToString();

    public GatewayRelay(Uri upstream)
    {
        LauncherApiClient.ValidateEndpoint(upstream);
        _upstream = upstream;
        _listener.Start();
        _accept = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_shutdown.Token);
                _connections.RemoveAll(task => task.IsCompleted);
                _connections.Add(ForwardAsync(client));
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (SocketException) when (_shutdown.IsCancellationRequested) { }
    }

    private async Task ForwardAsync(TcpClient client)
    {
        using (client)
        using (var server = new TcpClient())
        using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token))
        {
            try
            {
                await server.ConnectAsync(_upstream.Host, _upstream.Port, lifetime.Token);
                Stream outgoing = server.GetStream();
                if (_upstream.Scheme == "https")
                {
                    var tls = new SslStream(outgoing, leaveInnerStreamOpen: false, TlsTrust.FromEnvironment());
                    outgoing = tls;
                    await tls.AuthenticateAsClientAsync(
                        new SslClientAuthenticationOptions
                        {
                            TargetHost = _upstream.Host,
                            ApplicationProtocols = [SslApplicationProtocol.Http2],
                        },
                        lifetime.Token
                    );
                    if (tls.NegotiatedApplicationProtocol != SslApplicationProtocol.Http2)
                    {
                        throw new IOException("The remote gateway did not negotiate HTTP/2.");
                    }
                }
                await using (outgoing)
                {
                    Task send = client.GetStream().CopyToAsync(outgoing, lifetime.Token);
                    Task receive = outgoing.CopyToAsync(client.GetStream(), lifetime.Token);
                    await Task.WhenAny(send, receive);
                    await lifetime.CancelAsync();
                    await Task.WhenAll(send, receive);
                }
            }
            catch (Exception error)
                when (error
                        is IOException
                            or SocketException
                            or OperationCanceledException
                            or System.Security.Authentication.AuthenticationException
                )
            {
                // Closing the connection makes the stock client retry. No credential-bearing frames are logged.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        _listener.Stop();
        await _accept;
        await Task.WhenAll(_connections);
        _shutdown.Dispose();
    }
}
