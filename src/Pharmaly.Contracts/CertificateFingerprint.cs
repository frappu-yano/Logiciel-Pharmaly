using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Pharmaly.Contracts;

/// <summary>
/// Empreinte SHA-256 du certificat du serveur : 64 caractères hexadécimaux. C'est elle que
/// l'installateur compare entre le serveur (<c>--empreinte</c>) et le poste, et c'est la
/// seule chose que le poste vérifie pour reconnaître son serveur.
/// </summary>
public static class CertificateFingerprint
{
    private const int HexLength = 64;
    private const int GroupLength = 4;
    private const int GroupsPerLine = 8;

    public static string Of(X509Certificate certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));

    /// <summary>
    /// Lit une empreinte saisie ou copiée : la casse et les séparateurs (espaces, deux-points,
    /// tirets, points, retours à la ligne) sont ignorés. Renvoie <c>null</c> si ce n'est pas
    /// une empreinte.
    /// </summary>
    public static string? Parse(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var hex = new StringBuilder(HexLength);
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character) || character is ':' or '-' or '.')
            {
                continue;
            }

            if (!char.IsAsciiHexDigit(character) || hex.Length == HexLength)
            {
                return null;
            }

            hex.Append(char.ToUpperInvariant(character));
        }

        return hex.Length == HexLength ? hex.ToString() : null;
    }

    /// <summary>Vrai si les deux textes désignent la même empreinte valide.</summary>
    public static bool Same(string? first, string? second) =>
        Parse(first) is { } left && Parse(second) is { } right && left == right;

    /// <summary>
    /// Présentation à comparer à l'œil : groupes de quatre caractères, huit groupes par ligne,
    /// identique sur le serveur et sur le poste.
    /// </summary>
    public static string Format(string fingerprint)
    {
        var hex = Parse(fingerprint) ?? throw new FormatException("Ce texte n'est pas une empreinte de certificat.");
        var text = new StringBuilder();
        for (var group = 0; group < HexLength / GroupLength; group++)
        {
            if (group > 0)
            {
                text.Append(group % GroupsPerLine == 0 ? '\n' : ' ');
            }

            text.Append(hex, group * GroupLength, GroupLength);
        }

        return text.ToString();
    }
}
