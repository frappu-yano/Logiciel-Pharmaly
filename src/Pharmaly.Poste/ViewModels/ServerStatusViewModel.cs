using CommunityToolkit.Mvvm.ComponentModel;
using Pharmaly.Poste.Services.Server;

namespace Pharmaly.Poste.ViewModels;

/// <summary>État de la liaison avec le serveur, affiché dans le bandeau et partagé par les modules.</summary>
public sealed partial class ServerStatusViewModel(IAccessClient access) : ObservableObject
{
    public const string UnreachableText = "Serveur injoignable — vérifiez le réseau du magasin";

    [ObservableProperty]
    private string statusText = "Connexion au serveur…";

    [ObservableProperty]
    private bool isConnected;

    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var health = await access.GetHealthAsync(cancellationToken);
            IsConnected = health is { DatabaseReachable: true };
            StatusText = health switch
            {
                null => "Serveur injoignable",
                { DatabaseReachable: false } => "Serveur joint, base de données indisponible",
                _ => $"Connecté au serveur — version {health.Version}",
            };
        }
        catch (ServerRefusedException refused)
        {
            // Le poste a refusé de parler à ce serveur (non approuvé, ou échanges non chiffrés).
            IsConnected = false;
            StatusText = refused.Code == ServerTrust.ClearHttpCode
                ? "Échanges non chiffrés refusés"
                : "Serveur non approuvé — connexion refusée";
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            MarkUnreachable();
        }
    }

    /// <summary>Un module vient de constater que le serveur ne répond pas.</summary>
    public void MarkUnreachable()
    {
        IsConnected = false;
        StatusText = UnreachableText;
    }
}
