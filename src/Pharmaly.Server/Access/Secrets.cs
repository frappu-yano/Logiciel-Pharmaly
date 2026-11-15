using System.Security.Cryptography;
using System.Text;

namespace Pharmaly.Server.Access;

/// <summary>Hachage des mots de passe et des codes personnels, et jetons de session.</summary>
public static class Secrets
{
    private const int Iterations = 100_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int TokenSize = 32;
    private const string Scheme = "pbkdf2-sha256";

    public static string Hash(string secret)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(secret, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return $"{Scheme}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string secret, string storedHash)
    {
        var parts = storedHash.Split('$');
        if (parts.Length != 4 || parts[0] != Scheme || !int.TryParse(parts[1], out var iterations))
        {
            return false;
        }

        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(secret, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Hachage factice : garde le même temps de réponse quand le compte n'existe pas.</summary>
    public static void BurnTime(string secret) => Verify(secret, DummyHash);

    private static readonly string DummyHash = Hash("compte-inexistant");

    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(TokenSize))
        .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public static string TokenFingerprint(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static string NewNumericCode(int digits) =>
        string.Concat(Enumerable.Range(0, digits).Select(_ => RandomNumberGenerator.GetInt32(10).ToString()));

    public static string NewPassword() => NewToken()[..14];
}
