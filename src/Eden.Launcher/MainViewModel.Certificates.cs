using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Eden.Launcher.Core;

namespace Eden.Launcher;

public partial class MainViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCertificateDownload))]
    [NotifyPropertyChangedFor(nameof(DownloadedFingerprint))]
    private ServerCertificateAuthority? _certificateDownload;

    [ObservableProperty]
    private string _expectedFingerprint = "";

    public bool HasCertificateDownload => CertificateDownload is not null;
    public string DownloadedFingerprint => CertificateDownload?.Fingerprint ?? "";

    partial void OnShowSettingsChanged(bool value)
    {
        if (!value)
        {
            ClearCertificateDownload();
        }
    }

    [RelayCommand]
    private Task DownloadCertificateAsync() =>
        RunAsync(async cancellation =>
        {
            ClearCertificateDownload();
            string address = ApiAddress;
            string? profileId = SelectedServer?.Id;
            var certificate = await ServerCertificateAuthority.DownloadAsync(address, cancellation);
            if (ShowSettings && ApiAddress == address && SelectedServer?.Id == profileId)
            {
                CertificateDownload = certificate;
                Status = "CA certificate downloaded. Verify its fingerprint before trusting it.";
            }
        });

    [RelayCommand]
    private Task VerifyCertificateAsync() =>
        RunAsync(async cancellation =>
        {
            if (CertificateDownload is not { } candidate || SelectedServer is null)
            {
                return;
            }
            var (server, certificate) = await candidate.VerifyAsync(ExpectedFingerprint, cancellation);
            if (CertificateDownload != candidate)
            {
                return;
            }
            string path = await _settings.SaveCertificateAuthorityAsync(certificate, cancellation);
            await SaveCoreAsync(path);
            CertificateAuthorityPath = path;
            ClearCertificateDownload();
            SetConnected(server);
            if (WorldReady)
            {
                Status = "Verified the CA fingerprint and connected. The certificate was saved to this server profile.";
            }
        });

    [RelayCommand]
    private void ClearCertificateDownload()
    {
        CertificateDownload = null;
        ExpectedFingerprint = "";
    }
}
