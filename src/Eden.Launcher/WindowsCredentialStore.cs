using System.Security.Cryptography;
using System.Text;
using Eden.Launcher.Core;

namespace Eden.Launcher;

public sealed class WindowsCredentialStore(string directory) : ICredentialStore
{
    public string? Read(string serverAddress, string userName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }
        string path = KeyPath(serverAddress, userName);
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public void Write(string serverAddress, string userName, string? refreshToken)
    {
        string path = KeyPath(serverAddress, userName);
        if (refreshToken is null)
        {
            File.Delete(path);
            return;
        }
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Remembered sign-in requires Windows.");
        }
        Directory.CreateDirectory(directory);
        byte[] encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(refreshToken), null, DataProtectionScope.CurrentUser);
        string temporary = path + ".tmp";
        File.WriteAllBytes(temporary, encrypted);
        File.Move(temporary, path, overwrite: true);
    }

    private string KeyPath(string serverAddress, string userName)
    {
        string identity = new Uri(serverAddress).AbsoluteUri.TrimEnd('/') + "\n" + userName.Trim().ToUpperInvariant();
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(directory, hash + ".credential");
    }
}
