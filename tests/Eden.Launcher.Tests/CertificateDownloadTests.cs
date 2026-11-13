using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Eden.Launcher.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Eden.Launcher.Tests;

public sealed class CertificateDownloadTests
{
    [AvaloniaFact]
    public async Task DownloadRequiresFingerprintBeforeConnectingAndPersistsVerifiedCaForRestart()
    {
        string directory = Directory.CreateTempSubdirectory("eden-launcher-download-ca-").FullName;
        try
        {
            using var caKey = RSA.Create(2048);
            var caRequest = new CertificateRequest("CN=Test CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            var now = DateTimeOffset.UtcNow;
            using var authority = caRequest.CreateSelfSigned(now.AddDays(-2), now.AddDays(2));
            using var serverKey = RSA.Create(2048);
            var serverRequest = new CertificateRequest("CN=localhost", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost");
            serverRequest.CertificateExtensions.Add(names.Build());
            using var serverLeaf = serverRequest.Create(authority, now.AddDays(-1), now.AddDays(1), [1]);
            using var serverCertificate = serverLeaf.CopyWithPrivateKey(serverKey);
            string serverPath = Path.Combine(directory, "server.pfx");
            await File.WriteAllBytesAsync(serverPath, serverCertificate.Export(X509ContentType.Pfx), TestContext.Current.CancellationToken);

            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, endpoint => endpoint.UseHttps(serverPath)));
            await using var app = builder.Build();
            int metadataRequests = 0;
            bool redirectDownload = true;
            byte[] publishedCertificate = authority.RawData;
            app.MapGet(
                "/api/v1/ca-certificate",
                (HttpContext context) =>
                {
                    Assert.False(context.Request.Headers.ContainsKey("Authorization"));
                    Assert.False(context.Request.Headers.ContainsKey("Cookie"));
                    return redirectDownload
                        ? Results.Redirect("/api/v1/server")
                        : Results.Bytes(publishedCertificate, "application/pkix-cert");
                }
            );
            app.MapGet(
                "/api/v1/server",
                () =>
                {
                    Interlocked.Increment(ref metadataRequests);
                    return new ServerReply("remote", "Remote", 2, ClientBuild.Number, "localhost:26000", "https://localhost:26001");
                }
            );
            var cancellation = TestContext.Current.CancellationToken;
            await app.StartAsync(cancellation);
            string address = new UriBuilder(Assert.Single(app.Urls)) { Host = "localhost" }.Uri.AbsoluteUri.TrimEnd('/');
            var redirectError = await Assert.ThrowsAsync<InvalidDataException>(() =>
                ServerCertificateAuthority.DownloadAsync(address, cancellation)
            );
            Assert.Contains("HTTP 302", redirectError.Message);
            var remote = new ServerProfile { ApiAddress = address };
            var local = new ServerProfile();
            var settings = new SettingsStore(directory);
            await settings.SaveAsync(new([remote, local], remote.Id));
            var model = new MainViewModel(settings, new NoCredentials(), new ClientInstallation(new ClientBuild()), new NoGames());
            await model.InitializeAsync();
            Assert.False(model.IsConnected);
            model.ShowSettings = true;
            var window = new MainWindow { DataContext = model };
            window.Show();
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(), button => button.Command == model.DownloadCertificateCommand);
            Assert.Equal(0, Volatile.Read(ref metadataRequests));

            redirectDownload = false;
            await model.DownloadCertificateCommand.ExecuteAsync(null);
            string fingerprint = authority.GetCertHashString(HashAlgorithmName.SHA256);
            Assert.Equal(fingerprint, model.DownloadedFingerprint);
            model.ExpectedFingerprint = new string('0', fingerprint.Length);
            await model.VerifyCertificateCommand.ExecuteAsync(null);
            Assert.Contains("fingerprints do not match", model.Status);
            Assert.False(model.IsConnected);
            Assert.Equal(0, Volatile.Read(ref metadataRequests));
            Assert.False(Directory.Exists(Path.Combine(directory, "certificates")));

            model.SelectedServer = model.Servers[1];
            Assert.False(model.HasCertificateDownload);
            Assert.Empty(model.ExpectedFingerprint);
            model.SelectedServer = model.Servers[0];

            using var unrelatedKey = RSA.Create(2048);
            var unrelatedRequest = new CertificateRequest("CN=Other CA", unrelatedKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            unrelatedRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            using var unrelated = unrelatedRequest.CreateSelfSigned(now.AddDays(-2), now.AddDays(2));
            publishedCertificate = unrelated.RawData;
            await model.DownloadCertificateCommand.ExecuteAsync(null);
            model.ExpectedFingerprint = unrelated.GetCertHashString(HashAlgorithmName.SHA256);
            await model.VerifyCertificateCommand.ExecuteAsync(null);
            Assert.True(model.IsError);
            Assert.False(model.IsConnected);
            Assert.Equal(0, Volatile.Read(ref metadataRequests));
            Assert.False(Directory.Exists(Path.Combine(directory, "certificates")));

            publishedCertificate = authority.RawData;
            await model.DownloadCertificateCommand.ExecuteAsync(null);
            model.ExpectedFingerprint = string.Join(':', fingerprint.ToLowerInvariant().Chunk(2).Select(pair => new string(pair)));
            await model.VerifyCertificateCommand.ExecuteAsync(null);
            Assert.True(model.IsConnected, model.Status);
            Assert.Equal(1, Volatile.Read(ref metadataRequests));
            Assert.False(model.HasCertificateDownload);
            var saved = await settings.LoadAsync();
            Assert.Null(saved.Servers[1].CertificateAuthorityPath);
            Assert.Equal(authority.RawData, File.ReadAllBytes(Assert.IsType<string>(saved.Servers[0].CertificateAuthorityPath)));

            var reopened = new MainViewModel(settings, new NoCredentials(), new ClientInstallation(new ClientBuild()), new NoGames());
            await reopened.InitializeAsync();
            Assert.True(reopened.IsConnected, reopened.Status);
            Assert.Equal(2, Volatile.Read(ref metadataRequests));
            window.Close();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class NoCredentials : ICredentialStore
    {
        public string? Read(string serverAddress, string userName) => null;

        public void Write(string serverAddress, string userName, string? refreshToken) => throw new NotSupportedException();
    }

    private sealed class NoGames : IClientLauncher
    {
        public int RunningCount => 0;

        public event Action? Changed
        {
            add { }
            remove { }
        }

        public bool IsRunning(string serverId, long accountId) => false;

        public Task LaunchAsync(
            string root,
            ServerReply server,
            SessionReply session,
            LaunchReply launch,
            string? certificateAuthorityPath,
            CancellationToken cancellation
        ) => throw new NotSupportedException();
    }
}
