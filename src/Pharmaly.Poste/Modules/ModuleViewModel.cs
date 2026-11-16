using CommunityToolkit.Mvvm.ComponentModel;
using Pharmaly.Contracts;
using Pharmaly.Poste.Services.Server;

namespace Pharmaly.Poste.Modules;

/// <summary>
/// Base du modèle de vue d'un module. Un module est créé à sa première ouverture et détruit
/// au verrouillage du poste : il ne garde donc jamais rien d'un employé pour le suivant.
/// </summary>
public abstract partial class ModuleViewModel : ObservableObject
{
    public const string ServerUnreachableMessage = "Serveur injoignable — vérifiez le réseau du magasin.";

    /// <summary>Dernier refus ou incident, affiché tel que le serveur l'a formulé.</summary>
    [ObservableProperty]
    private string errorMessage = string.Empty;

    [ObservableProperty]
    private bool isBusy;

    /// <summary>Levé quand le serveur ne reconnaît plus l'employé : le poste doit redemander l'identification.</summary>
    public event EventHandler? IdentificationRequired;

    /// <summary>Appelé à chaque fois que l'employé arrive sur le module : charger ce qu'il affiche.</summary>
    public virtual Task ActivateAsync() => Task.CompletedTask;

    /// <summary>Appelé au verrouillage, juste avant que le module soit détruit : arrêter ce qui tourne.</summary>
    public virtual void Close()
    {
    }

    protected void RequireIdentification() => IdentificationRequired?.Invoke(this, EventArgs.Empty);

    /// <summary>Redemande l'identification si le refus signifie que la session n'est plus reconnue.</summary>
    protected bool RequireIdentificationIfLost(ServerRefusedException refused)
    {
        if (refused.Code is not (ApiErrorCodes.EmployeeNotIdentified or ApiErrorCodes.PosteNotSignedIn))
        {
            return false;
        }

        RequireIdentification();
        return true;
    }

    /// <summary>
    /// Exécute un appel au serveur. Un refus est affiché avec le message du serveur ; une
    /// panne de réseau avec <see cref="ServerUnreachableMessage"/>. Renvoie vrai si tout a réussi.
    /// </summary>
    protected async Task<bool> RunServerAsync(Func<Task> action)
    {
        if (IsBusy)
        {
            return false;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            await action();
            return true;
        }
        catch (ServerRefusedException refused)
        {
            ErrorMessage = refused.Message;
            RequireIdentificationIfLost(refused);
            return false;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            ErrorMessage = ServerUnreachableMessage;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
