namespace Pharmaly.Domain;

public static class Ean13
{
    private const int PayloadLength = 12;

    /// <summary>Complète 12 chiffres avec la clé de contrôle EAN-13.</summary>
    public static string WithCheckDigit(string payload)
    {
        if (payload.Length != PayloadLength || !payload.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("Un code EAN-13 se construit à partir de 12 chiffres.", nameof(payload));
        }

        return payload + ComputeCheckDigit(payload);
    }

    public static bool IsValid(string code) =>
        code.Length == PayloadLength + 1
        && code.All(char.IsAsciiDigit)
        && ComputeCheckDigit(code[..PayloadLength]) == code[PayloadLength] - '0';

    private static int ComputeCheckDigit(string payload)
    {
        var sum = 0;
        for (var index = 0; index < payload.Length; index++)
        {
            var digit = payload[index] - '0';
            sum += index % 2 == 0 ? digit : digit * 3;
        }

        return (10 - sum % 10) % 10;
    }
}
