using System.Security.Cryptography;

namespace PerfSentinelHub.Configuration;

/// <summary>
///     A local account's password as a PHC string, <c>$pbkdf2-sha256$i=600000$&lt;salt&gt;$&lt;key&gt;</c>:
///     PBKDF2-HMAC-SHA256 over the UTF-8 password, untrimmed and unnormalized, a 16-byte salt
///     and a 32-byte key in unpadded base64. The count is fixed rather than a minimum, so
///     verifying an unknown name against the dummy hash costs exactly what a known one does.
/// </summary>
public static class LocalPassword
{
    public const int Iterations = 600_000, SaltBytes = 16, KeyBytes = 32, MinLength = 15, MaxLength = 1024;
    private const string Algorithm = "pbkdf2-sha256";
    private const string Cost = "i=600000";
    private const int SaltChars = 22, KeyChars = 43;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        return $"${Algorithm}${Cost}${Encode(salt)}${Encode(Derive(password, salt))}";
    }

    public static bool TryParse(string text, out byte[] salt, out byte[] key)
    {
        salt = key = [];
        return text.Trim().Split('$') is
                   ["", Algorithm, Cost, { Length: SaltChars } saltText, { Length: KeyChars } keyText] &&
               TryDecode(saltText + "==", SaltBytes, out salt) && TryDecode(keyText + "=", KeyBytes, out key);
    }

    public static bool Verify(string password, byte[] salt, byte[] key)
    {
        return CryptographicOperations.FixedTimeEquals(Derive(password, salt), key);
    }

    // What hash-password accepts.
    public static bool IsAcceptable(string password)
    {
        return password.Length is >= MinLength and <= MaxLength && !password.Any(char.IsControl);
    }

    private static byte[] Derive(string password, byte[] salt)
    {
        return Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeyBytes);
    }

    private static string Encode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=');
    }

    // Whitespace inside the text decodes to fewer bytes, so the length check refuses it too.
    private static bool TryDecode(string text, int length, out byte[] bytes)
    {
        var buffer = new byte[length + 3];
        var decoded = Convert.TryFromBase64String(text, buffer, out var written) && written == length;
        bytes = decoded ? buffer[..length] : [];
        return decoded;
    }
}
