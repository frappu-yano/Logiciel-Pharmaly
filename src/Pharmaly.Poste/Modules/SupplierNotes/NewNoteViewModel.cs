using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Poste.Services.Server;

namespace Pharmaly.Poste.Modules.SupplierNotes;

public enum NewNoteField
{
    Supplier,
    Location,
    Reference,
    Date,
}

public sealed record LocationChoice(StockLocationSummary Location)
{
    public override string ToString() => $"{Location.Name} — {Location.StoreName}";
}

/// <summary>
/// Formulaire « Nouveau bon » : fournisseur (cherché par son nom, ou créé à la volée),
/// emplacement où entre la marchandise, référence du fournisseur et date. Entrée passe d'un
/// champ au suivant ; la dernière crée le bon, aussitôt réservé à l'employé.
/// </summary>
public sealed partial class NewNoteViewModel : ObservableObject
{
    private readonly ISupplierNotesClient notes;
    private readonly ServerCaller call;
    private readonly TimeSpan searchDelay;
    private readonly Guid? preferredLocationId;
    private readonly Func<SupplierNoteDetail, Task> created;
    private CancellationTokenSource? searchCancellation;
    private string? suggestionsTerm;
    private bool settingSupplierText;
    private CreateSupplierRequest? lastSupplierRequest;
    private CreateSupplierNoteRequest? lastNoteRequest;

    public NewNoteViewModel(
        ISupplierNotesClient notes, ServerCaller call, TimeSpan searchDelay, Guid? preferredLocationId, DateOnly today,
        Func<SupplierNoteDetail, Task> created)
    {
        this.notes = notes;
        this.call = call;
        this.searchDelay = searchDelay;
        this.preferredLocationId = preferredLocationId;
        this.created = created;
        dateText = NoteInput.FormatDate(today);
    }

    /// <summary>Levé quand le curseur doit aller sur un champ.</summary>
    public event EventHandler<NewNoteField>? FocusRequested;

    public NewNoteField ActiveField { get; private set; } = NewNoteField.Supplier;

    [ObservableProperty]
    private string supplierText = string.Empty;

    /// <summary>Fournisseurs dont le nom correspond à la frappe.</summary>
    public ObservableCollection<SupplierSummary> Suggestions { get; } = [];

    [ObservableProperty]
    private bool hasSuggestions;

    [ObservableProperty]
    private SupplierSummary? selectedSuggestion;

    /// <summary>Fournisseur retenu pour le bon.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SupplierHint))]
    private SupplierSummary? supplier;

    /// <summary>Aucun fournisseur ne porte ce nom : Entrée le crée.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SupplierHint))]
    private bool isCreationProposed;

    public ObservableCollection<LocationChoice> Locations { get; } = [];

    [ObservableProperty]
    private LocationChoice? selectedLocation;

    [ObservableProperty]
    private string reference = string.Empty;

    [ObservableProperty]
    private string dateText;

    [ObservableProperty]
    private string errorMessage = string.Empty;

    [ObservableProperty]
    private bool isBusy;

    public string SupplierHint => IsCreationProposed
        ? $"Aucun fournisseur « {SupplierText.Trim()} ». Entrée : le créer · Échap : corriger le nom"
        : Supplier is not null
            ? "Fournisseur retenu"
            : "↑ ↓ pour choisir · Entrée : retenir · Ctrl+Entrée : créer ce nom comme nouveau fournisseur";

    /// <summary>Charge les emplacements et les premiers fournisseurs.</summary>
    public async Task LoadAsync()
    {
        IReadOnlyList<StockLocationSummary> locations = [];
        var result = await call(async () => locations = await notes.ListNoteLocationsAsync(CancellationToken.None));
        if (!result.Succeeded)
        {
            ErrorMessage = result.Refusal?.Message ?? ModuleViewModel.ServerUnreachableMessage;
            return;
        }

        Locations.Clear();
        foreach (var location in locations)
        {
            Locations.Add(new LocationChoice(location));
        }

        SelectedLocation = Locations.FirstOrDefault(choice => choice.Location.Id == preferredLocationId) ?? Locations.FirstOrDefault();
        await SearchSuppliersAsync(string.Empty, CancellationToken.None);
    }

    partial void OnSupplierTextChanged(string value)
    {
        OnPropertyChanged(nameof(SupplierHint));
        if (settingSupplierText)
        {
            return;
        }

        if (Supplier is { } kept && kept.Name != value)
        {
            Supplier = null;
        }

        IsCreationProposed = false;
        ErrorMessage = string.Empty;
        _ = SearchSuppliersAfterDelayAsync(value.Trim());
    }

    private async Task SearchSuppliersAfterDelayAsync(string term)
    {
        searchCancellation?.Cancel();
        var cancellation = searchCancellation = new CancellationTokenSource();
        try
        {
            await Task.Delay(searchDelay, cancellation.Token);
            await SearchSuppliersAsync(term, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // Une frappe plus récente a remplacé cette recherche.
        }
    }

    private async Task<bool> SearchSuppliersAsync(string term, CancellationToken cancellationToken)
    {
        IReadOnlyList<SupplierSummary> found = [];
        var result = await call(async () => found = await notes.ListSuppliersAsync(term, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        if (!result.Succeeded)
        {
            ErrorMessage = result.Refusal?.Message ?? ModuleViewModel.ServerUnreachableMessage;
            return false;
        }

        Suggestions.Clear();
        foreach (var supplier in found)
        {
            Suggestions.Add(supplier);
        }

        suggestionsTerm = term;
        HasSuggestions = Suggestions.Count > 0 && Supplier is null;
        SelectedSuggestion = Suggestions.FirstOrDefault();
        return true;
    }

    /// <summary>Change le fournisseur surligné dans les propositions (↑ ↓).</summary>
    public void MoveSuggestion(int delta)
    {
        if (Suggestions.Count == 0)
        {
            return;
        }

        var current = SelectedSuggestion is null ? -1 : Suggestions.IndexOf(SelectedSuggestion);
        SelectedSuggestion = Suggestions[Math.Clamp(current + delta, 0, Suggestions.Count - 1)];
    }

    /// <summary>
    /// Entrée dans le champ fournisseur : retient le fournisseur de ce nom, sinon celui qui
    /// est surligné ; s'il n'y en a aucun, propose de le créer (une seconde Entrée le crée).
    /// </summary>
    public async Task SubmitSupplierAsync(bool createNew = false)
    {
        if (IsBusy)
        {
            return;
        }

        var text = SupplierText.Trim();
        if (Supplier is { } kept && kept.Name == text)
        {
            Focus(NewNoteField.Location);
            return;
        }

        if (text.Length == 0)
        {
            Fail("Saisissez le nom du fournisseur.", NewNoteField.Supplier);
            return;
        }

        if (IsCreationProposed || createNew)
        {
            await CreateSupplierAsync(text);
            return;
        }

        searchCancellation?.Cancel();
        IsBusy = true;
        try
        {
            if (suggestionsTerm != text && !await SearchSuppliersAsync(text, CancellationToken.None))
            {
                return;
            }
        }
        finally
        {
            IsBusy = false;
        }

        var key = TextNormalizer.ToSearchKey(text);
        var chosen = Suggestions.FirstOrDefault(candidate => TextNormalizer.ToSearchKey(candidate.Name) == key) ?? SelectedSuggestion;
        if (chosen is not null)
        {
            Choose(chosen);
        }
        else
        {
            IsCreationProposed = true;
        }
    }

    /// <summary>Échap : renonce à créer le fournisseur proposé. Renvoie faux s'il n'y avait rien à annuler.</summary>
    public bool CancelCreationProposal()
    {
        if (!IsCreationProposed)
        {
            return false;
        }

        IsCreationProposed = false;
        Focus(NewNoteField.Supplier);
        return true;
    }

    private async Task CreateSupplierAsync(string name)
    {
        // Même nom relancé après une panne : même identifiant, le fournisseur n'est créé qu'une fois.
        var request = lastSupplierRequest is { } sent && sent.Name == name ? sent : new CreateSupplierRequest(Guid.NewGuid(), name);
        lastSupplierRequest = request;

        IsBusy = true;
        ErrorMessage = string.Empty;
        SupplierSummary? supplier = null;
        var result = await call(async () => supplier = await notes.CreateSupplierAsync(request, CancellationToken.None));
        IsBusy = false;

        if (result.Succeeded)
        {
            Choose(supplier!);
        }
        else if (result.Unreachable)
        {
            Fail($"{ModuleViewModel.ServerUnreachableMessage} Appuyez sur Entrée pour réessayer.", NewNoteField.Supplier);
        }
        else
        {
            IsCreationProposed = false;
            Fail(result.Refusal!.Message, NewNoteField.Supplier);
        }
    }

    private void Choose(SupplierSummary chosen)
    {
        searchCancellation?.Cancel();
        settingSupplierText = true;
        SupplierText = chosen.Name;
        settingSupplierText = false;
        Supplier = chosen;
        IsCreationProposed = false;
        HasSuggestions = false;
        ErrorMessage = string.Empty;
        Focus(NewNoteField.Location);
    }

    public void SubmitLocation()
    {
        if (SelectedLocation is null)
        {
            Fail("Choisissez l'emplacement où entre la marchandise.", NewNoteField.Location);
            return;
        }

        ErrorMessage = string.Empty;
        Focus(NewNoteField.Reference);
    }

    public void SubmitReference()
    {
        if (string.IsNullOrWhiteSpace(Reference))
        {
            Fail("La référence du fournisseur est obligatoire (numéro de son bon de livraison ou de sa facture).", NewNoteField.Reference);
            return;
        }

        ErrorMessage = string.Empty;
        Focus(NewNoteField.Date);
    }

    /// <summary>Dernière Entrée : crée le bon et ouvre sa saisie.</summary>
    public async Task SubmitAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (Supplier is not { } chosenSupplier)
        {
            Fail("Choisissez d'abord le fournisseur (Entrée dans le champ Fournisseur).", NewNoteField.Supplier);
            return;
        }

        if (SelectedLocation is not { } location)
        {
            Fail("Choisissez l'emplacement où entre la marchandise.", NewNoteField.Location);
            return;
        }

        if (string.IsNullOrWhiteSpace(Reference))
        {
            Fail("La référence du fournisseur est obligatoire (numéro de son bon de livraison ou de sa facture).", NewNoteField.Reference);
            return;
        }

        if (!NoteInput.TryReadDate(DateText, out var date))
        {
            Fail(NoteInput.InvalidDate, NewNoteField.Date);
            return;
        }

        // Même demande relancée après une panne : même identifiant, le bon n'est créé qu'une fois.
        var request = new CreateSupplierNoteRequest(Guid.NewGuid(), chosenSupplier.Id, location.Location.Id, Reference.Trim(), date);
        if (lastNoteRequest is { } sent && sent with { OperationId = request.OperationId } == request)
        {
            request = sent;
        }

        lastNoteRequest = request;
        IsBusy = true;
        ErrorMessage = string.Empty;
        SupplierNoteDetail? detail = null;
        var result = await call(async () => detail = await notes.CreateNoteAsync(request, CancellationToken.None));
        IsBusy = false;

        if (result.Succeeded)
        {
            await created(detail!);
        }
        else if (result.Unreachable)
        {
            Fail(
                $"{ModuleViewModel.ServerUnreachableMessage} Le bon n'est peut-être pas créé : appuyez sur Entrée pour réessayer, il ne sera pas créé deux fois.",
                NewNoteField.Date);
        }
        else
        {
            Fail(result.Refusal!.Message, NewNoteField.Date);
        }
    }

    public void Focus(NewNoteField field)
    {
        ActiveField = field;
        FocusRequested?.Invoke(this, field);
    }

    private void Fail(string message, NewNoteField field)
    {
        ErrorMessage = message;
        Focus(field);
    }
}
