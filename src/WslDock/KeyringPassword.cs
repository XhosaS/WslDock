using System.Security.Cryptography;
using System.Text;

namespace WslDock;

// Only ciphertext is persisted. DPAPI binds it to the current Windows user.
public static class KeyringPassword
{
    private static byte[] Entropy(string distro) => Encoding.UTF8.GetBytes("WslDock/keyring/" + distro);
    public static string Protect(string distro, string password)
    {
        var bytes = Encoding.UTF8.GetBytes(password);
        try { return Convert.ToBase64String(ProtectedData.Protect(bytes, Entropy(distro), DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public static string? Read(Preferences prefs, string distro)
    {
        if (!prefs.KeyringPasswords.TryGetValue(distro, out var encrypted)) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(encrypted), Entropy(distro), DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        { throw new InvalidOperationException("无法读取密钥环密码，请在设置中为该发行版重新保存密码。"); }
    }
}
