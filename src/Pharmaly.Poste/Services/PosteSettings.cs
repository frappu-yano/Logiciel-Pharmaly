using System.Text.Json;

namespace Pharmaly.Poste.Services;

/// <summary>Réglages du poste, lus dans « poste.json » à côté du programme.</summary>
public sealed record PosteSettings(Uri ServerUrl)
{
    private const string FileName = "poste.json";
    private const string LocalFileName = "poste.local.json";
    // Adresse numérique et non « localhost » : sous Windows, « localhost » essaie d'abord
    // l'IPv6 et peut faire perdre deux secondes à chaque connexion.
    private const string DefaultServerUrl = "http://127.0.0.1:5080";

    public static PosteSettings Load(string? serverUrlOverride = null)
    {
        var url = serverUrlOverride ?? ReadServerUrl(LocalFileName) ?? ReadServerUrl(FileName) ?? DefaultServerUrl;
        return new PosteSettings(new Uri(url.TrimEnd('/') + "/"));
    }

    private static string? ReadServerUrl(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        if (!File.Exists(path))
        {
            return null;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.TryGetProperty("serverUrl", out var value) ? value.GetString() : null;
    }
}
