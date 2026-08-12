using System.Security.Cryptography;
using System.Text;

namespace NksUsbShare.Core.Security;

/// <summary>
/// Pairing tokens. A token is what a client presents after the TLS handshake to prove it is
/// allowed to talk to this host at all; per-device permissions are decided separately.
/// </summary>
public static class AccessToken
{
    /// <summary>No I, O, 0 or 1, so a token read off a screen cannot be mistyped.</summary>
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>Five groups of five characters: 125 bits of entropy, still readable aloud.</summary>
    public static string Generate()
    {
        var characters = new char[29];
        var index = 0;
        for (var group = 0; group < 5; group++)
        {
            if (group > 0) characters[index++] = '-';
            for (var i = 0; i < 5; i++)
                characters[index++] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }
        return new string(characters);
    }

    /// <summary>
    /// Constant-time comparison. Tokens are secrets, so a length-dependent early exit would leak
    /// how much of a guess was correct.
    /// </summary>
    public static bool Verify(string? presented, string? expected)
    {
        if (string.IsNullOrEmpty(expected)) return false;
        if (string.IsNullOrEmpty(presented)) return false;

        // Hashing first makes the comparison independent of length while staying constant time.
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(Normalise(presented)));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(Normalise(expected)));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static string Normalise(string token) => token.Replace("-", "").Replace(" ", "").ToUpperInvariant();
}
