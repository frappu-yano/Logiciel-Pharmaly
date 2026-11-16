using CommunityToolkit.Mvvm.ComponentModel;
using Pharmaly.Contracts;
using Pharmaly.Poste.Services.Server;

namespace Pharmaly.Poste.Modules.Products;

/// <summary>
/// Identifiant d'opération d'une intention de l'employé (une création, un changement de prix,
/// un transfert…). Il est créé une fois et renvoyé tel quel à chaque nouvelle tentative : si
/// le serveur avait déjà enregistré la première, il répond avec le même résultat sans rien
/// refaire. Un nouvel identifiant n'est tiré que pour une nouvelle intention.
/// </summary>
public sealed class OperationIntent
{
    public Guid Id { get; private set; } = Guid.NewGuid();

    /// <summary>L'intention est terminée (réussie ou abandonnée) : la suivante aura son propre identifiant.</summary>
    public void Renew() => Id = Guid.NewGuid();
}

/// <summary>
/// Base des formulaires d'écriture du module Produits (création, modification, prix,
/// ajustement, transfert). Chaque formulaire est autonome : il parle au serveur, affiche le
/// refus tel que le serveur le formule, et prévient par un rappel quand il a terminé.
/// </summary>
public abstract partial class ProductFormViewModel : ObservableObject
{
    public const string PreviousAttemptRecordedMessage =
        "Votre tentative précédente, elle, avait bien été enregistrée : vérifiez la fiche avant de recommencer.";

    private readonly Action cancelled;

    protected ProductFormViewModel(Action cancelled) => this.cancelled = cancelled;

    /// <summary>Dernier refus ou incident, affiché tel que le serveur l'a formulé.</summary>
    [ObservableProperty]
    private string errorMessage = string.Empty;

    /// <summary>Vrai pendant l'envoi : les boutons sont désactivés et Entrée ne renvoie rien.</summary>
    [ObservableProperty]
    private bool isBusy;

    /// <summary>Vrai quand la saisie est figée et que l'employé doit confirmer l'opération.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing))]
    private bool isConfirming;

    public bool IsEditing => !IsConfirming;

    /// <summary>Levé quand le serveur ne reconnaît plus l'employé : le module hôte doit redemander l'identification.</summary>
    public event EventHandler? IdentificationRequired;

    protected OperationIntent Operation { get; } = new();

    /// <summary>
    /// Vrai après une panne de réseau pendant une écriture : on ignore si le serveur l'a
    /// enregistrée. La tentative suivante repart avec le même identifiant d'opération.
    /// </summary>
    protected bool OutcomeUnknown { get; private set; }

    /// <summary>
    /// Échap : revient à la saisie si une confirmation est affichée, sinon abandonne le
    /// formulaire. Sans effet pendant un envoi, dont l'issue n'est pas encore connue.
    /// </summary>
    public void Cancel()
    {
        if (IsBusy)
        {
            return;
        }

        if (IsConfirming)
        {
            IsConfirming = false;
            ErrorMessage = string.Empty;
            return;
        }

        Operation.Renew();
        cancelled();
    }

    /// <summary>
    /// Exécute une écriture. Renvoie vrai si le serveur l'a acceptée. Après une panne de
    /// réseau l'identifiant d'opération est conservé : la nouvelle tentative ne crée pas de doublon.
    /// </summary>
    protected async Task<bool> RunWriteAsync(Func<Guid, Task> write)
    {
        if (IsBusy)
        {
            return false;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        OutcomeUnknown = false;
        try
        {
            await write(Operation.Id);
            Operation.Renew();
            return true;
        }
        catch (ServerRefusedException refused)
        {
            ErrorMessage = refused.Message;
            if (refused.Code == ApiErrorCodes.OperationMismatch)
            {
                // La saisie a changé après une tentative que le serveur avait bien reçue.
                ErrorMessage = $"{refused.Message} {PreviousAttemptRecordedMessage}";
                Operation.Renew();
            }

            RequireIdentificationIfLost(refused);
            return false;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            ErrorMessage = ModuleViewModel.ServerUnreachableMessage;
            OutcomeUnknown = true;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Exécute une lecture (recherche, fiche) : mêmes messages, sans identifiant d'opération.</summary>
    protected async Task<bool> RunReadAsync(Func<Task> read)
    {
        if (IsBusy)
        {
            return false;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            await read();
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
            ErrorMessage = ModuleViewModel.ServerUnreachableMessage;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Refus reçu par une recherche du formulaire : prévient le module si la session est perdue.</summary>
    protected void RelayRefusal(ServerRefusedException refused) => RequireIdentificationIfLost(refused);

    private void RequireIdentificationIfLost(ServerRefusedException refused)
    {
        if (refused.Code is ApiErrorCodes.EmployeeNotIdentified or ApiErrorCodes.PosteNotSignedIn)
        {
            IdentificationRequired?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>
/// Codes-barres d'un produit en cours de saisie : une liste, et le champ où le lecteur (ou
/// le clavier) en ajoute un par Entrée.
/// </summary>
public sealed partial class BarcodeListViewModel : ObservableObject
{
    public System.Collections.ObjectModel.ObservableCollection<string> Items { get; } = [];

    /// <summary>Code en cours de saisie, pas encore ajouté à la liste.</summary>
    [ObservableProperty]
    private string entry = string.Empty;

    public BarcodeListViewModel(IEnumerable<string> initial)
    {
        foreach (var barcode in initial)
        {
            Items.Add(barcode);
        }
    }

    /// <summary>Ajoute le code saisi à la liste. Renvoie faux si le champ était vide.</summary>
    public bool AddEntry()
    {
        var barcode = Entry.Trim();
        if (barcode.Length == 0)
        {
            return false;
        }

        if (!Items.Contains(barcode))
        {
            Items.Add(barcode);
        }

        Entry = string.Empty;
        return true;
    }

    public void Remove(string barcode) => Items.Remove(barcode);

    /// <summary>Retire le dernier code de la liste (touche Retour arrière dans un champ vide).</summary>
    public void RemoveLast()
    {
        if (Items.Count > 0)
        {
            Items.RemoveAt(Items.Count - 1);
        }
    }
}
