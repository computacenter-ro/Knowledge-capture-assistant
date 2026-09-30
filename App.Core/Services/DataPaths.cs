using System.Security.Cryptography;
using System.Text;

namespace KnowledgeCapture.Core.Services;

public static class DataPaths
{
    /// <summary>%LOCALAPPDATA%\KnowledgeCapture, or KC_DATA_DIR (used by the self-test for an isolated run).</summary>
    public static string Root
    {
        get
        {
            var overrideDir = Environment.GetEnvironmentVariable("KC_DATA_DIR");
            var dir = !string.IsNullOrWhiteSpace(overrideDir)
                ? overrideDir
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KnowledgeCapture");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string Database => Path.Combine(Root, "knowledge.db");
    public static string Logs => Path.Combine(Root, "logs");
    private static string SaltFile => Path.Combine(Root, "identity.salt");

    /// <summary>
    /// Salted SHA-256 of the Windows user name. The salt is random per install and DPAPI-protected (CurrentUser),
    /// kept outside the database, so the stored hash cannot be brute-forced from the database alone.
    /// </summary>
    public static string EmployeeHash()
    {
        var salt = LoadOrCreateSalt();
        var data = Encoding.UTF8.GetBytes(Environment.UserDomainName + "\\" + Environment.UserName);
        return Convert.ToHexString(SHA256.HashData([.. salt, .. data])).ToLowerInvariant();
    }

    private static byte[] LoadOrCreateSalt()
    {
        try
        {
            if (File.Exists(SaltFile))
                return ProtectedData.Unprotect(File.ReadAllBytes(SaltFile), null, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException) { /* unreadable (other user/machine): make a new one */ }
        var salt = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(SaltFile, ProtectedData.Protect(salt, null, DataProtectionScope.CurrentUser));
        return salt;
    }
}
