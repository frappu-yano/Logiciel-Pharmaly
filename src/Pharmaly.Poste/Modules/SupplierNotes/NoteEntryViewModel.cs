using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Poste.Services.Server;

namespace Pharmaly.Poste.Modules.SupplierNotes;

/// <summary>Endroit de l'écran de saisie où se trouve le curseur.</summary>
public enum EntryField
{
    Barcode,
    Quantity,
    Price,
    Lines,
}

/// <summary>
/// Écran de saisie d'un bon. Le poste tient le verrou du bon tant que l'écran est ouvert
/// (battement régulier) ; sans verrou, le bon est en lecture seule.
/// <para>
/// Boucle de saisie : code-barres → quantité → prix d'achat → Entrée enregistre la ligne.
/// Rien n'est envoyé avant cette dernière Entrée : une quantité en cours de frappe ne touche
/// jamais au stock.
/// </para>
/// <para>
/// Chaque écriture porte un identifiant d'opération créé une fois pour l'intention ; il est
/// réutilisé tel quel si l'envoi est relancé après une panne, pour que le serveur ne
/// l'enregistre jamais deux fois.
/// </para>
/// </summary>
public sealed partial class NoteEntryViewModel : ObservableObject
{
    public const string ReadOnlySuffix = "Le bon est en lecture seule ; votre saisie en cours est conservée à l'écran.";
    public const string CancelledNote = "Ce bon est annulé : il ne peut plus être modifié.";
    public const string ViewOnly = "Consultation seule : vous n'avez pas le droit de modifier les bons.";

    private static readonly string[] WriteRights =
        [Permissions.NotesEnter, Permissions.NotesEdit, Permissions.NotesValidate, Permissions.NotesCancel];

    private readonly ISupplierNotesClient notes;
    private readonly ICatalogClient catalog;
    private readonly Func<string, bool> hasPermission;
    private readonly ServerCaller call;
    private readonly INoteHeartbeat heartbeat;
    private readonly TimeSpan searchDelay;
    private readonly Func<string, Task> leave;
    private PendingWrite? pending;
    private CancellationTokenSource? searchCancellation;
    private string? searchedTerm;
    private bool settingBarcodeText;
    private bool resumeLock;
    private bool closed;
    private int stockRequest;

    public NoteEntryViewModel(
        ISupplierNotesClient notes, ICatalogClient catalog, Func<string, bool> hasPermission, ServerCaller call,
        INoteHeartbeat heartbeat, TimeSpan searchDelay, SupplierNoteDetail detail, bool holdsLock, string readOnlyMessage,
        Func<string, Task> leave)
    {
        this.notes = notes;
        this.catalog = catalog;
        this.hasPermission = hasPermission;
        this.call = call;
        this.heartbeat = heartbeat;
        this.searchDelay = searchDelay;
        this.leave = leave;
        note = detail.Note;
        Apply(detail);
        this.holdsLock = holdsLock;
        this.readOnlyMessage = readOnlyMessage;
        heartbeat.Elapsed += OnHeartbeat;
        if (holdsLock)
        {
            heartbeat.Start();
        }
    }

    /// <summary>Levé quand le curseur doit aller quelque part ; la vue n'en décide jamais seule.</summary>
    public event EventHandler<EntryField>? FocusRequested;

    public EntryField ActiveField { get; private set; } = EntryField.Barcode;

    // ---------- Le bon ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Details), nameof(StatusText), nameof(LineCountText), nameof(TotalText))]
    private SupplierNoteSummary note;

    public ObservableCollection<NoteLineRowViewModel> Lines { get; } = [];

    public ObservableCollection<NoteHistoryRowViewModel> History { get; } = [];

    [ObservableProperty]
    private NoteLineRowViewModel? selectedLine;

    [ObservableProperty]
    private bool isHistoryVisible;

    public string Title => $"Bon n° {Note.Number} — {Note.SupplierName}";

    public string Details =>
        $"Réf. {Note.SupplierReference} · {NoteInput.FormatDate(Note.NoteDate)} · {Note.LocationName} ({Note.StoreName})";

    public string StatusText => NoteStatusLabels.Of(Note.Status);

    public string LineCountText => Note.LineCount switch
    {
        0 => "Aucune ligne",
        1 => "1 ligne",
        var count => $"{count} lignes",
    };

    public string TotalText => Money.ToDisplay(Note.TotalAmount);

    // ---------- Verrou et droits ----------

    /// <summary>Vrai tant que l'employé tient le bon : sans verrou, rien ne s'écrit.</summary>
    [ObservableProperty]
    private bool holdsLock;

    /// <summary>Pourquoi le bon est en lecture seule (message du serveur quand il y en a un).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReadOnlyText))]
    private string readOnlyMessage;

    public bool IsCancelled => Note.Status == SupplierNoteStatuses.Cancelled;

    public bool IsInProgress => Note.Status == SupplierNoteStatuses.InProgress;

    public bool IsReadOnly => !HoldsLock;

    public string ReadOnlyText => IsCancelled ? CancelledNote : ReadOnlyMessage;

    /// <summary>L'employé a le droit d'écrire des lignes sur un bon dans cet état.</summary>
    public bool HasLineRight =>
        !IsCancelled && hasPermission(Note.Status == SupplierNoteStatuses.Validated ? Permissions.NotesEdit : Permissions.NotesEnter);

    public bool CanEnterLines => HoldsLock && HasLineRight;

    /// <summary>Les champs de saisie sont ouverts.</summary>
    public bool CanType => CanEnterLines && !HasUnconfirmedWrite;

    /// <summary>Le champ code-barres est ouvert : pas pendant la modification d'une ligne.</summary>
    public bool CanScan => CanType && !IsEditing;

    public bool CanValidate => HoldsLock && IsInProgress && hasPermission(Permissions.NotesValidate);

    public bool CanCancelNote => HoldsLock && !IsCancelled && hasPermission(Permissions.NotesCancel);

    /// <summary>Le bon est en lecture seule mais l'employé pourrait le reprendre.</summary>
    public bool CanRetake => !HoldsLock && !IsCancelled && WriteRights.Any(hasPermission);

    private void RefreshRights()
    {
        foreach (var name in (string[])
                 [
                     nameof(IsCancelled), nameof(IsInProgress), nameof(IsReadOnly), nameof(ReadOnlyText), nameof(HasLineRight),
                     nameof(CanEnterLines), nameof(CanType), nameof(CanScan), nameof(CanValidate), nameof(CanCancelNote), nameof(CanRetake),
                 ])
        {
            OnPropertyChanged(name);
        }
    }

    partial void OnHoldsLockChanged(bool value) => RefreshRights();

    partial void OnNoteChanged(SupplierNoteSummary value) => RefreshRights();

    // ---------- Messages ----------

    [ObservableProperty]
    private string errorMessage = string.Empty;

    [ObservableProperty]
    private string infoMessage = string.Empty;

    /// <summary>Effet de la dernière ligne enregistrée : stock avant → après, variation de marge.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLastOutcome))]
    private LineOutcomeViewModel? lastOutcome;

    public bool HasLastOutcome => LastOutcome is not null;

    // ---------- Ligne en cours de saisie ----------

    [ObservableProperty]
    private string barcodeText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProduct), nameof(ProductTitle), nameof(ProductDetails))]
    private NoteProductInfo? product;

    [ObservableProperty]
    private string quantityText = string.Empty;

    [ObservableProperty]
    private string priceText = string.Empty;

    /// <summary>Stock du produit à l'emplacement du bon, quand l'employé a le droit de le voir.</summary>
    [ObservableProperty]
    private string stockText = string.Empty;

    /// <summary>Ligne déjà enregistrée en cours de modification, ou <c>null</c> pour une nouvelle ligne.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing), nameof(EntryTitle), nameof(CanScan), nameof(ProductDetails))]
    private NoteLineRowViewModel? editedLine;

    [ObservableProperty]
    private bool isLookingUp;

    public bool HasProduct => Product is not null;

    public bool IsEditing => EditedLine is not null;

    public string EntryTitle => EditedLine is { } edited ? $"Modification de la ligne {edited.PositionText}" : "Nouvelle ligne";

    public string ProductTitle => Product is { } chosen ? chosen.Name : "Scannez un produit, ou tapez son nom";

    public string ProductDetails
    {
        get
        {
            if (Product is not { } chosen)
            {
                return string.Empty;
            }

            var sale = $"Vente {Money.ToDisplay(chosen.SalePrice)}";
            if (EditedLine is { } edited)
            {
                return $"{sale} · Enregistré : {edited.QuantityText} à {edited.PriceText}";
            }

            var last = "Jamais acheté";
            if (chosen.LastPurchasePrice is { } price)
            {
                string?[] origin = [chosen.LastSupplierName, chosen.LastPurchaseDate is { } date ? NoteInput.FormatDate(date) : null];
                var known = string.Join(", ", origin.Where(part => part is not null));
                last = $"Dernier achat {Money.ToDisplay(price)}" + (known.Length > 0 ? $" ({known})" : string.Empty);
            }

            var already = Lines.LastOrDefault(row => row.Line.ProductId == chosen.Id && !row.IsCancelled) is { } row
                ? $" · Déjà sur ce bon : ligne {row.PositionText} ({row.QuantityText})"
                : string.Empty;
            return $"{sale} · {last}{already}";
        }
    }

    /// <summary>Vrai dès qu'une ligne est commencée (texte tapé, produit choisi).</summary>
    public bool HasEntryInProgress => Product is not null || BarcodeText.Trim().Length > 0;

    // ---------- Recherche par nom ----------

    public ObservableCollection<ProductSummary> SearchResults { get; } = [];

    [ObservableProperty]
    private bool hasSearchResults;

    [ObservableProperty]
    private ProductSummary? selectedSearchResult;

    // ---------- Écriture en cours ----------

    /// <summary>Vrai pendant l'envoi : la validation est verrouillée.</summary>
    [ObservableProperty]
    private bool isSending;

    /// <summary>
    /// Une écriture est partie mais le serveur n'a pas répondu : on ne sait pas si elle est
    /// enregistrée. La saisie est bloquée jusqu'à ce que « Réessayer » ait tranché.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanType), nameof(CanScan))]
    private bool hasUnconfirmedWrite;

    [ObservableProperty]
    private string unconfirmedMessage = string.Empty;

    // ---------- Confirmation et création rapide ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirming), nameof(IsOverlayOpen))]
    private NoteConfirmationViewModel? confirmation;

    public bool IsConfirming => Confirmation is not null;

    /// <summary>Une fenêtre (confirmation, création rapide) est ouverte par-dessus le bon.</summary>
    public bool IsOverlayOpen => IsConfirming || IsCreatingProduct;

    /// <summary>Formulaire de création rapide, ouvert quand le code-barres scanné est inconnu.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCreatingProduct), nameof(IsOverlayOpen))]
    private QuickProductViewModel? quickProduct;

    public bool IsCreatingProduct => QuickProduct is not null;

    // =====================================================================================
    // Boucle de saisie
    // =====================================================================================

    partial void OnBarcodeTextChanged(string value)
    {
        if (settingBarcodeText)
        {
            return;
        }

        _ = SearchAfterDelayAsync(value.Trim());
    }

    private bool IsNameSearch(string term) =>
        term.Length >= 2 && !NoteInput.LooksLikeBarcode(term) && CanScan && hasPermission(Permissions.ProductsView);

    private async Task SearchAfterDelayAsync(string term)
    {
        searchCancellation?.Cancel();
        var cancellation = searchCancellation = new CancellationTokenSource();
        if (!IsNameSearch(term))
        {
            ShowSearchResults([], null);
            return;
        }

        try
        {
            await Task.Delay(searchDelay, cancellation.Token);
            if (await SearchAsync(term, cancellation.Token) is { } found)
            {
                ShowSearchResults(found, term);
            }
        }
        catch (OperationCanceledException)
        {
            // Une frappe plus récente a remplacé cette recherche.
        }
    }

    /// <summary>Renvoie les produits trouvés, ou <c>null</c> si le serveur n'a pas répondu ou a refusé.</summary>
    private async Task<IReadOnlyList<ProductSummary>?> SearchAsync(string term, CancellationToken cancellationToken)
    {
        IReadOnlyList<ProductSummary> found = [];
        var result = await call(async () => found = await catalog.SearchProductsAsync(term, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        return result.Succeeded ? found : null;
    }

    private void ShowSearchResults(IReadOnlyList<ProductSummary> found, string? term)
    {
        SearchResults.Clear();
        foreach (var candidate in found)
        {
            SearchResults.Add(candidate);
        }

        searchedTerm = term;
        SelectedSearchResult = SearchResults.FirstOrDefault();
        HasSearchResults = SearchResults.Count > 0;
    }

    /// <summary>Change le produit surligné dans les résultats de la recherche (↑ ↓).</summary>
    public void MoveSearchSelection(int delta)
    {
        if (SearchResults.Count == 0)
        {
            return;
        }

        var current = SelectedSearchResult is null ? -1 : SearchResults.IndexOf(SelectedSearchResult);
        SelectedSearchResult = SearchResults[Math.Clamp(current + delta, 0, SearchResults.Count - 1)];
    }

    /// <summary>
    /// Entrée dans le champ code-barres (le lecteur l'envoie après le code) : cherche le
    /// produit. Un code de chiffres est un code-barres ; tout autre texte est un nom.
    /// </summary>
    public async Task SubmitBarcodeAsync()
    {
        var text = BarcodeText.Trim();
        if (!CanScan || IsLookingUp || IsSending || text.Length == 0)
        {
            return;
        }

        searchCancellation?.Cancel();
        ErrorMessage = string.Empty;
        InfoMessage = string.Empty;
        IsHistoryVisible = false;
        IsLookingUp = true;
        try
        {
            if (NoteInput.LooksLikeBarcode(text))
            {
                await LookUpBarcodeAsync(text);
                return;
            }

            if (SearchResults.Count > 0 && searchedTerm == text && SelectedSearchResult is { } highlighted)
            {
                await SelectByIdAsync(highlighted.Id);
                return;
            }

            if (!hasPermission(Permissions.ProductsView))
            {
                Fail("La recherche par nom demande le droit de consulter les produits : scannez le code-barres du produit.", EntryField.Barcode);
                return;
            }

            var found = await SearchAsync(text, CancellationToken.None);
            if (found is null)
            {
                Fail($"{ModuleViewModel.ServerUnreachableMessage} Rien n'a été enregistré.", EntryField.Barcode);
            }
            else if (found.Count == 1)
            {
                await SelectByIdAsync(found[0].Id);
            }
            else if (found.Count > 1)
            {
                ShowSearchResults(found, text);
            }
            else if (text.All(char.IsAsciiDigit))
            {
                // Un code court que la recherche ne connaît pas : c'est peut-être un code-barres maison.
                await LookUpBarcodeAsync(text);
            }
            else
            {
                Fail($"Aucun produit ne correspond à « {text} ».", EntryField.Barcode);
            }
        }
        finally
        {
            IsLookingUp = false;
        }
    }

    private async Task LookUpBarcodeAsync(string barcode)
    {
        NoteProductInfo? found = null;
        var result = await call(async () => found = await notes.FindNoteProductByBarcodeAsync(barcode, CancellationToken.None));
        if (closed)
        {
            return;
        }

        if (result.Succeeded)
        {
            SelectIfActive(found!);
        }
        else if (result.IsRefusal(SupplierNoteErrorCodes.UnknownBarcode))
        {
            if (hasPermission(Permissions.ProductsCreate))
            {
                ShowSearchResults([], null);
                QuickProduct = new QuickProductViewModel(notes, call, barcode);
            }
            else
            {
                Fail(
                    $"{result.Refusal!.Message} Vous n'avez pas le droit de créer un produit : adressez-vous à un responsable.",
                    EntryField.Barcode);
            }
        }
        else
        {
            Fail(result.Refusal?.Message ?? $"{ModuleViewModel.ServerUnreachableMessage} Rien n'a été enregistré.", EntryField.Barcode);
        }
    }

    private async Task SelectByIdAsync(Guid productId)
    {
        NoteProductInfo? found = null;
        var result = await call(async () => found = await notes.ReadNoteProductAsync(productId, CancellationToken.None));
        if (closed)
        {
            return;
        }

        if (result.Succeeded)
        {
            SelectIfActive(found!);
        }
        else
        {
            Fail(result.Refusal?.Message ?? $"{ModuleViewModel.ServerUnreachableMessage} Rien n'a été enregistré.", EntryField.Barcode);
        }
    }

    private void SelectIfActive(NoteProductInfo found)
    {
        if (!found.IsActive)
        {
            ShowSearchResults([], null);
            Fail(
                $"« {found.Name} » est désactivé : il ne peut pas être reçu sur un bon. Réactivez-le d'abord dans le module Produits.",
                EntryField.Barcode);
            return;
        }

        SelectProduct(found, knownStock: null);
    }

    /// <summary>Le produit est trouvé : il s'affiche, son dernier prix d'achat est proposé, le curseur va sur la quantité.</summary>
    private void SelectProduct(NoteProductInfo chosen, decimal? knownStock)
    {
        ShowSearchResults([], null);
        EditedLine = null;
        Product = chosen;
        QuantityText = string.Empty;
        PriceText = chosen.LastPurchasePrice is { } price ? NoteInput.FormatPriceForEntry(price) : string.Empty;
        ErrorMessage = string.Empty;
        stockRequest++;
        if (knownStock is { } stock)
        {
            StockText = DescribeStock(stock);
        }
        else
        {
            StockText = string.Empty;
            _ = LoadStockAsync(chosen.Id);
        }

        Focus(EntryField.Quantity);
    }

    private string DescribeStock(decimal quantity) => $"En stock ({Note.LocationName}) : {NoteInput.FormatQuantity(quantity)}";

    /// <summary>Le stock s'affiche dès qu'il arrive, sans retarder la saisie de la quantité.</summary>
    private async Task LoadStockAsync(Guid productId)
    {
        if (!hasPermission(Permissions.ProductsView))
        {
            return;
        }

        var ticket = stockRequest;
        try
        {
            var stock = await notes.ReadNoteProductStockAsync(productId, CancellationToken.None);
            if (ticket == stockRequest && Product?.Id == productId)
            {
                StockText = DescribeStock(stock.FirstOrDefault(line => line.LocationId == Note.LocationId)?.Quantity ?? 0);
            }
        }
        catch (Exception exception) when (exception is ServerRefusedException or HttpRequestException or TaskCanceledException)
        {
            // Affichage de confort : sans réponse, le stock n'est simplement pas affiché.
        }
    }

    /// <summary>Entrée sur la quantité : rien n'est envoyé, le curseur passe au prix d'achat.</summary>
    public void SubmitQuantity()
    {
        if (!CanType || IsSending || Product is not { } chosen)
        {
            return;
        }

        if (NoteInput.ReadQuantity(QuantityText, chosen.SoldByUnit, chosen.Name, out _) is { } invalid)
        {
            Fail(invalid, EntryField.Quantity);
            return;
        }

        ErrorMessage = string.Empty;
        Focus(EntryField.Price);
    }

    /// <summary>
    /// Entrée sur le prix d'achat : enregistre la ligne. C'est le seul moment où une quantité
    /// part vers le serveur, donc vers le stock.
    /// </summary>
    public async Task SubmitPriceAsync()
    {
        if (IsSending)
        {
            return;
        }

        if (HasUnconfirmedWrite)
        {
            await RetryAsync();
            return;
        }

        if (!CanType || Product is not { } chosen)
        {
            return;
        }

        if (NoteInput.ReadQuantity(QuantityText, chosen.SoldByUnit, chosen.Name, out var quantity) is { } invalidQuantity)
        {
            Fail(invalidQuantity, EntryField.Quantity);
            return;
        }

        if (NoteInput.ReadPrice(PriceText, out var price) is { } invalidPrice)
        {
            Fail(invalidPrice, EntryField.Price);
            return;
        }

        SupplierNoteLineResult? saved = null;
        var noteId = Note.Id;
        if (EditedLine is { } edited)
        {
            if (quantity == edited.Line.Quantity && price == edited.Line.PurchasePrice)
            {
                ClearEntry();
                InfoMessage = $"Ligne {edited.PositionText} inchangée.";
                Focus(EntryField.Barcode);
                return;
            }

            // La version lue accompagne la modification : si la ligne a changé entre-temps, le serveur refuse.
            var version = edited.Line.Version;
            await RunWriteAsync(new PendingWrite(
                $"Modification de la ligne {edited.PositionText} « {edited.ProductName} »",
                async operationId => saved = await notes.UpdateNoteLineAsync(
                    noteId, edited.Id, new UpdateSupplierNoteLineRequest(operationId, version, quantity, price), CancellationToken.None),
                () => LineSaved(saved!, "modifiée")));
            return;
        }

        await RunWriteAsync(new PendingWrite(
            $"Ligne « {chosen.Name} » ({NoteInput.FormatQuantity(quantity)} à {Money.ToDisplay(price)})",
            async operationId => saved = await notes.AddNoteLineAsync(
                noteId, new AddSupplierNoteLineRequest(operationId, chosen.Id, quantity, price), CancellationToken.None),
            () => LineSaved(saved!, "enregistrée")));
    }

    private Task LineSaved(SupplierNoteLineResult result, string action)
    {
        ApplyLine(result, action);
        ClearEntry();
        Focus(EntryField.Barcode);
        return Task.CompletedTask;
    }

    private void ApplyLine(SupplierNoteLineResult result, string action)
    {
        var row = new NoteLineRowViewModel(result.Line);
        var index = IndexOfLine(result.Line.Id);
        if (index >= 0)
        {
            Lines[index] = row;
        }
        else
        {
            Lines.Add(row);
        }

        SelectedLine = row;
        Note = Note with { LineCount = result.NoteLineCount, TotalAmount = result.NoteTotalAmount };
        LastOutcome = new LineOutcomeViewModel(result, action);
    }

    private int IndexOfLine(Guid lineId)
    {
        for (var index = 0; index < Lines.Count; index++)
        {
            if (Lines[index].Id == lineId)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>Vide la ligne en cours de saisie. Rien n'a été envoyé : rien n'est à défaire.</summary>
    private void ClearEntry()
    {
        searchCancellation?.Cancel();
        stockRequest++;
        settingBarcodeText = true;
        BarcodeText = string.Empty;
        settingBarcodeText = false;
        ShowSearchResults([], null);
        Product = null;
        EditedLine = null;
        QuantityText = string.Empty;
        PriceText = string.Empty;
        StockText = string.Empty;
    }

    // =====================================================================================
    // Écritures : un identifiant par intention, réutilisé à chaque nouvel essai
    // =====================================================================================

    /// <summary>Une écriture décidée par l'employé, avec l'identifiant qu'elle garde jusqu'à sa réponse.</summary>
    private sealed class PendingWrite(string description, Func<Guid, Task> send, Func<Task> succeeded)
    {
        public Guid OperationId { get; } = Guid.NewGuid();

        public string Description { get; } = description;

        public Func<Guid, Task> Send { get; } = send;

        public Func<Task> Succeeded { get; } = succeeded;
    }

    private async Task RunWriteAsync(PendingWrite write)
    {
        if (IsSending)
        {
            return;
        }

        var wasUnconfirmed = HasUnconfirmedWrite;
        pending = write;
        IsSending = true;
        ErrorMessage = string.Empty;
        InfoMessage = string.Empty;
        var result = await call(() => write.Send(write.OperationId));
        IsSending = false;
        if (closed)
        {
            return;
        }

        if (result.Unreachable)
        {
            // Ni succès ni refus : l'écriture reste en attente, avec le même identifiant.
            HasUnconfirmedWrite = true;
            UnconfirmedMessage =
                $"NON CONFIRMÉ — {write.Description}. Le serveur n'a pas répondu : on ne sait pas encore si c'est enregistré. "
                + "Entrée : réessayer (rien ne sera enregistré deux fois).";
            return;
        }

        pending = null;
        HasUnconfirmedWrite = false;
        UnconfirmedMessage = string.Empty;
        if (result.Succeeded)
        {
            await write.Succeeded();
        }
        else
        {
            await HandleRefusalAsync(result.Refusal!, wasUnconfirmed);
        }
    }

    /// <summary>« Réessayer » : renvoie l'écriture non confirmée, avec le même identifiant d'opération.</summary>
    public Task RetryAsync() => HasUnconfirmedWrite && pending is { } write ? RunWriteAsync(write) : Task.CompletedTask;

    /// <summary>Un refus : rien n'a été enregistré. Le message du serveur est affiché tel quel.</summary>
    private async Task HandleRefusalAsync(ServerRefusedException refusal, bool reload)
    {
        switch (refusal.Code)
        {
            case SupplierNoteErrorCodes.LockedByOther or SupplierNoteErrorCodes.LockNotHeld:
                LoseLock(refusal.Message);
                break;

            case SupplierNoteErrorCodes.StaleVersion or SupplierNoteErrorCodes.LineCancelled or SupplierNoteErrorCodes.NoteCancelled:
                ClearEntry();
                var reloaded = await ReloadAsync();
                reload = false;
                ErrorMessage = reloaded
                    ? $"{refusal.Message} Le bon vient d'être rechargé : vérifiez-le avant de recommencer."
                    : refusal.Message;
                FocusDefault();
                break;

            default:
                // Stock déjà sorti, quantité refusée… : la ligne est inchangée, la saisie reste à corriger.
                Fail(refusal.Message, Product is null ? ActiveField : EntryField.Quantity);
                break;
        }

        if (reload)
        {
            // Après une écriture restée sans réponse, l'écran reprend ce que le serveur a réellement enregistré.
            await ReloadAsync();
        }
    }

    // =====================================================================================
    // Lignes enregistrées : modifier, annuler
    // =====================================================================================

    /// <summary>Le curseur va sur les lignes enregistrées (↑ depuis le champ code-barres).</summary>
    public void FocusLines()
    {
        if (Lines.Count == 0)
        {
            return;
        }

        SelectedLine ??= Lines[^1];
        Focus(EntryField.Lines);
    }

    /// <summary>Entrée sur une ligne enregistrée : ses valeurs reviennent dans les champs de saisie.</summary>
    public void BeginEditSelectedLine()
    {
        if (!CanType || IsSending || SelectedLine is not { } row)
        {
            return;
        }

        if (row.IsCancelled)
        {
            Fail("Cette ligne est annulée : elle ne peut plus être modifiée.", EntryField.Lines);
            return;
        }

        ClearEntry();
        ErrorMessage = string.Empty;
        InfoMessage = string.Empty;
        var line = row.Line;
        settingBarcodeText = true;
        BarcodeText = line.Barcode ?? string.Empty;
        settingBarcodeText = false;
        Product = new NoteProductInfo(
            line.ProductId, line.ProductCode, line.ProductName, line.Barcode, line.Margin.SalePrice, false, true,
            line.Margin.PreviousPurchasePrice, null, null);
        EditedLine = row;
        QuantityText = NoteInput.FormatQuantity(line.Quantity);
        PriceText = NoteInput.FormatPriceForEntry(line.PurchasePrice);
        Focus(EntryField.Quantity);
    }

    /// <summary>Suppr sur une ligne enregistrée : demande confirmation avant de la retirer du stock.</summary>
    public void RequestCancelSelectedLine()
    {
        if (!CanType || IsSending || SelectedLine is not { } row)
        {
            return;
        }

        if (row.IsCancelled)
        {
            Fail("Cette ligne est déjà annulée.", EntryField.Lines);
            return;
        }

        ErrorMessage = string.Empty;
        Confirmation = new NoteConfirmationViewModel(
            $"Annuler la ligne {row.PositionText} « {row.ProductName} » ? Sa quantité ({row.QuantityText}) sera retirée du stock.",
            "Annuler la ligne",
            asksReason: false,
            _ => CancelLineAsync(row));
    }

    private Task CancelLineAsync(NoteLineRowViewModel row)
    {
        SupplierNoteLineResult? cancelled = null;
        var noteId = Note.Id;
        var version = row.Line.Version;
        return RunWriteAsync(new PendingWrite(
            $"Annulation de la ligne {row.PositionText} « {row.ProductName} »",
            async operationId => cancelled = await notes.CancelNoteLineAsync(
                noteId, row.Id, new CancelSupplierNoteLineRequest(operationId, version), CancellationToken.None),
            () =>
            {
                if (EditedLine?.Id == row.Id)
                {
                    ClearEntry();
                }

                ApplyLine(cancelled!, "annulée");
                FocusDefault();
                return Task.CompletedTask;
            }));
    }

    // =====================================================================================
    // Validation et annulation du bon
    // =====================================================================================

    /// <summary>Demande confirmation avant de valider le bon.</summary>
    public void RequestValidate()
    {
        if (!CanValidate || IsSending || HasUnconfirmedWrite)
        {
            return;
        }

        if (HasEntryInProgress)
        {
            Fail(
                "Une ligne est en cours de saisie : enregistrez-la (Entrée) ou abandonnez-la (Échap) avant de valider le bon.",
                ActiveField);
            return;
        }

        ErrorMessage = string.Empty;
        Confirmation = new NoteConfirmationViewModel(
            $"Valider le bon n° {Note.Number} de {Note.SupplierName} ? {LineCountText}, total d'achat {TotalText}. "
            + "Les quantités sont déjà en stock : la validation ne les ajoute pas une deuxième fois.",
            "Valider le bon",
            asksReason: false,
            _ => ValidateAsync());
    }

    private Task ValidateAsync()
    {
        SupplierNoteSummary? validated = null;
        var noteId = Note.Id;
        return RunWriteAsync(new PendingWrite(
            $"Validation du bon n° {Note.Number}",
            async operationId => validated = await notes.ValidateNoteAsync(noteId, new ValidateSupplierNoteRequest(operationId), CancellationToken.None),
            () => LeaveAfterAsync($"Bon n° {validated!.Number} validé : {LineCountText}, total d'achat {Money.ToDisplay(validated.TotalAmount)}.")));
    }

    /// <summary>Demande confirmation, avec un motif, avant d'annuler le bon entier.</summary>
    public void RequestCancelNote()
    {
        if (!CanCancelNote || IsSending || HasUnconfirmedWrite)
        {
            return;
        }

        ErrorMessage = string.Empty;
        Confirmation = new NoteConfirmationViewModel(
            $"Annuler le bon n° {Note.Number} de {Note.SupplierName} ? Toutes ses lignes ({LineCountText}, {TotalText}) seront retirées du stock. "
            + "Un bon annulé ne peut plus être modifié.",
            "Annuler le bon",
            asksReason: true,
            CancelNoteAsync);
    }

    private Task CancelNoteAsync(string? reason)
    {
        SupplierNoteSummary? cancelled = null;
        var noteId = Note.Id;
        return RunWriteAsync(new PendingWrite(
            $"Annulation du bon n° {Note.Number}",
            async operationId => cancelled = await notes.CancelNoteAsync(noteId, new CancelSupplierNoteRequest(operationId, reason), CancellationToken.None),
            () => LeaveAfterAsync($"Bon n° {cancelled!.Number} annulé : ses lignes sont retirées du stock.")));
    }

    /// <summary>Le serveur a libéré le verrou en changeant l'état du bon : retour à la liste.</summary>
    private Task LeaveAfterAsync(string message)
    {
        HoldsLock = false;
        Detach();
        return leave(message);
    }

    /// <summary>Entrée dans la confirmation : exécute l'opération.</summary>
    public async Task ConfirmAsync()
    {
        if (Confirmation is not { } confirmed)
        {
            return;
        }

        Confirmation = null;
        await confirmed.Action(string.IsNullOrWhiteSpace(confirmed.Reason) ? null : confirmed.Reason.Trim());
    }

    public void CancelConfirmation()
    {
        Confirmation = null;
        FocusDefault();
    }

    // =====================================================================================
    // Création rapide d'un produit inconnu
    // =====================================================================================

    /// <summary>Crée le produit puis revient au bon : produit sélectionné, curseur sur la quantité.</summary>
    public async Task SubmitQuickProductAsync()
    {
        if (QuickProduct is not { } form || await form.CreateAsync() is not { } createdProduct || closed)
        {
            return;
        }

        QuickProduct = null;
        SelectProduct(createdProduct, knownStock: 0);
        InfoMessage = $"Produit « {createdProduct.Name} » créé (code {createdProduct.Code}).";
    }

    public void CancelQuickProduct()
    {
        QuickProduct = null;
        Focus(EntryField.Barcode);
    }

    // =====================================================================================
    // Verrou : battement, perte, reprise, libération
    // =====================================================================================

    private void OnHeartbeat(object? sender, EventArgs e) => _ = HeartbeatAsync();

    /// <summary>Battement : prolonge le bail. Si le bon a été repris par un autre, il passe en lecture seule.</summary>
    public async Task HeartbeatAsync()
    {
        if (!HoldsLock || closed)
        {
            return;
        }

        var noteId = Note.Id;
        var result = await call(() => notes.RenewNoteLockAsync(noteId, CancellationToken.None));
        if (result.IsRefusal(SupplierNoteErrorCodes.LockedByOther, SupplierNoteErrorCodes.LockNotHeld))
        {
            LoseLock(result.Refusal!.Message);
        }

        // Panne de réseau : le bail court encore, le battement suivant réessaiera.
    }

    /// <summary>Le bon n'est plus tenu : lecture seule, sans toucher à ce qui est en cours de frappe.</summary>
    private void LoseLock(string serverMessage)
    {
        if (closed)
        {
            return;
        }

        heartbeat.Stop();
        ReadOnlyMessage = $"{serverMessage} {ReadOnlySuffix}";
        HoldsLock = false;
    }

    /// <summary>« Reprendre le bon » : redemande le verrou ; la saisie en cours est conservée.</summary>
    public async Task RetakeAsync()
    {
        if (!CanRetake || IsSending)
        {
            return;
        }

        ErrorMessage = string.Empty;
        if (await AcquireAsync())
        {
            InfoMessage = "Vous tenez de nouveau ce bon.";
            FocusDefault();
        }
    }

    private async Task<bool> AcquireAsync()
    {
        SupplierNoteDetail? detail = null;
        var noteId = Note.Id;
        var result = await call(async () => detail = await notes.AcquireNoteLockAsync(noteId, CancellationToken.None));
        if (closed)
        {
            return false;
        }

        if (result.Succeeded)
        {
            Apply(detail!);
            ReadOnlyMessage = string.Empty;
            HoldsLock = true;
            heartbeat.Start();
            return true;
        }

        if (result.IsRefusal(SupplierNoteErrorCodes.LockedByOther))
        {
            ReadOnlyMessage = $"{result.Refusal!.Message} {ReadOnlySuffix}";
        }
        else if (result.Unreachable)
        {
            ReadOnlyMessage = $"{ModuleViewModel.ServerUnreachableMessage} Le bon n'a pas pu être repris. {ReadOnlySuffix}";
        }
        else
        {
            ReadOnlyMessage = $"{result.Refusal!.Message} {ReadOnlySuffix}";
            await ReloadAsync();
        }

        return false;
    }

    /// <summary>L'employé passe à un autre module : le bon est libéré pour ses collègues.</summary>
    public async Task SuspendAsync()
    {
        if (closed || !HoldsLock)
        {
            return;
        }

        resumeLock = true;
        await ReleaseAsync();
    }

    /// <summary>L'employé revient sur le module : le bon est repris, la saisie en cours est toujours là.</summary>
    public async Task ResumeAsync()
    {
        if (closed)
        {
            return;
        }

        if (resumeLock)
        {
            resumeLock = false;
            await AcquireAsync();
        }
        else if (!HasUnconfirmedWrite)
        {
            await ReloadAsync();
        }

        FocusDefault();
    }

    private async Task ReleaseAsync()
    {
        heartbeat.Stop();
        if (!HoldsLock)
        {
            return;
        }

        HoldsLock = false;
        try
        {
            await notes.ReleaseNoteLockAsync(Note.Id, CancellationToken.None);
        }
        catch (Exception exception) when (exception is ServerRefusedException or HttpRequestException or TaskCanceledException)
        {
            // Sans réponse du serveur, le bail expire de lui-même faute de battement.
        }
    }

    /// <summary>Quitte l'écran de saisie : le verrou est libéré, retour à la liste.</summary>
    public async Task LeaveAsync()
    {
        Detach();
        await ReleaseAsync();
        await leave(string.Empty);
    }

    /// <summary>Le module est détruit (verrouillage du poste) : plus de battement, verrou libéré.</summary>
    public void Close()
    {
        if (closed)
        {
            return;
        }

        Detach();
        _ = ReleaseAsync();
        closed = true;
    }

    private void Detach()
    {
        heartbeat.Stop();
        heartbeat.Elapsed -= OnHeartbeat;
        searchCancellation?.Cancel();
    }

    // =====================================================================================
    // Lecture du bon
    // =====================================================================================

    /// <summary>Relit le bon sur le serveur. Renvoie faux si le serveur n'a pas répondu.</summary>
    public async Task<bool> ReloadAsync()
    {
        SupplierNoteDetail? detail = null;
        var noteId = Note.Id;
        var result = await call(async () => detail = await notes.ReadNoteAsync(noteId, CancellationToken.None));
        if (closed || !result.Succeeded)
        {
            return false;
        }

        Apply(detail!);
        if (HoldsLock && detail!.Note.Lock is not { HeldByMe: true })
        {
            LoseLock(IsCancelled ? CancelledNote : "Vous ne tenez plus ce bon.");
        }

        return true;
    }

    private void Apply(SupplierNoteDetail detail)
    {
        var selectedId = SelectedLine?.Id;
        Note = detail.Note;
        Lines.Clear();
        foreach (var line in detail.Lines)
        {
            Lines.Add(new NoteLineRowViewModel(line));
        }

        History.Clear();
        foreach (var entry in detail.History.Reverse())
        {
            History.Add(new NoteHistoryRowViewModel(entry));
        }

        SelectedLine = Lines.FirstOrDefault(row => row.Id == selectedId);
        if (EditedLine is { } edited)
        {
            // La ligne en cours de modification a pu changer : on ne modifie jamais une version périmée.
            var current = Lines.FirstOrDefault(row => row.Id == edited.Id);
            if (current is null || current.IsCancelled || current.Line.Version != edited.Line.Version)
            {
                ClearEntry();
            }
            else
            {
                EditedLine = current;
            }
        }

        OnPropertyChanged(nameof(ProductDetails));
    }

    /// <summary>Affiche ou masque l'historique des états ; il est relu sur le serveur à l'ouverture.</summary>
    public async Task ToggleHistoryAsync()
    {
        IsHistoryVisible = !IsHistoryVisible;
        if (IsHistoryVisible && !HasUnconfirmedWrite)
        {
            await ReloadAsync();
        }

        if (!IsHistoryVisible)
        {
            FocusDefault();
        }
    }

    // =====================================================================================
    // Curseur et Échap
    // =====================================================================================

    /// <summary>
    /// Échap remonte d'un cran : ferme la fenêtre ouverte, abandonne la ligne en cours de
    /// frappe (rien n'a été envoyé), revient au champ code-barres, puis quitte le bon.
    /// </summary>
    public async Task EscapeAsync()
    {
        if (IsConfirming)
        {
            CancelConfirmation();
        }
        else if (IsCreatingProduct)
        {
            CancelQuickProduct();
        }
        else if (IsSending)
        {
            // L'envoi en cours ira à son terme : Échap ne l'interrompt pas.
        }
        else if (IsHistoryVisible)
        {
            await ToggleHistoryAsync();
        }
        else if (HasUnconfirmedWrite)
        {
            Confirmation = new NoteConfirmationViewModel(
                "Une écriture n'est pas confirmée par le serveur. Quitter le bon quand même ? "
                + "À sa réouverture, le bon affichera ce qui est réellement enregistré.",
                "Quitter le bon",
                asksReason: false,
                _ => LeaveAsync());
        }
        else if (CanType && HasEntryInProgress)
        {
            ClearEntry();
            ErrorMessage = string.Empty;
            Focus(EntryField.Barcode);
        }
        else if (CanType && ActiveField == EntryField.Lines)
        {
            Focus(EntryField.Barcode);
        }
        else
        {
            await LeaveAsync();
        }
    }

    /// <summary>Place le curseur là où la saisie reprend.</summary>
    public void FocusDefault() =>
        Focus(!CanType ? EntryField.Lines : Product is null ? EntryField.Barcode : EntryField.Quantity);

    public void Focus(EntryField field)
    {
        ActiveField = field;
        FocusRequested?.Invoke(this, field);
    }

    private void Fail(string message, EntryField field)
    {
        ErrorMessage = message;
        Focus(field);
    }
}
