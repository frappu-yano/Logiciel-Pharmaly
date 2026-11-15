using System.Globalization;
using System.Text;

namespace Pharmaly.Domain;

public static class TextNormalizer
{
    /// <summary>
    /// Clé de recherche d'un libellé : minuscules, sans accents, espaces simples.
    /// « Crème  Hydratante » et « creme hydratante » donnent la même clé.
    /// </summary>
    public static string ToSearchKey(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var previousWasSpace = true;

        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                if (!previousWasSpace)
                {
                    builder.Append(' ');
                }

                previousWasSpace = true;
                continue;
            }

            builder.Append(char.ToLowerInvariant(character));
            previousWasSpace = false;
        }

        return builder.ToString().TrimEnd();
    }
}
