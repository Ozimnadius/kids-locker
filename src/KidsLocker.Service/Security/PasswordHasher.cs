using System.Security.Cryptography;

namespace KidsLocker.Service.Security;

/// <summary>Хэширование PBKDF2 (SHA-256) для мастер-пароля административного пульта.</summary>
public static class PasswordHasher
{
    private const int SaltSize = 16;
    private const int KeySize = 32;
    private const int Iterations = 100_000;

    public static (string hash, string salt) Create(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
        return (Convert.ToBase64String(key), Convert.ToBase64String(salt));
    }

    public static bool Verify(string password, string? hashB64, string? saltB64)
    {
        if (string.IsNullOrEmpty(hashB64) || string.IsNullOrEmpty(saltB64)) return false;
        try
        {
            var salt = Convert.FromBase64String(saltB64);
            var expected = Convert.FromBase64String(hashB64);
            var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
            return CryptographicOperations.FixedTimeEquals(key, expected);
        }
        catch
        {
            return false;
        }
    }
}
