using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Poste.Services.Server;

namespace Pharmaly.Tests.Poste;

/// <summary>
/// Bons fournisseurs : mêmes règles, mêmes refus et mêmes messages que le vrai serveur
/// (verrou par employé, droits selon l'état du bon, idempotence par identifiant d'opération,
/// version des lignes, stock déjà sorti).
/// </summary>
internal sealed partial class FakeServer : ISupplierNotesClient
{
    public static readonly StockLocationSummary Depot = new(Guid.NewGuid(), "DEPOT", "Dépôt", "RESERVE", Didouche.Id, Didouche.Name);
    public static readonly StockLocationSummary Shop = new(Guid.NewGuid(), "MAG", "Magasin", "VENDABLE", Didouche.Id, Didouche.Name);
    public static readonly SupplierSummary Biocos = new(Guid.NewGuid(), "Biocos Distribution");
    public static readonly SupplierSummary Dermalab = new(Guid.NewGuid(), "Dermalab");

    /// <summary>Produit vendu au poids : les quantités fractionnées sont permises.</summary>
    public static readonly ProductSummary Henna = new(Guid.NewGuid(), 100003, "Henné en vrac", 900m, "2000000000039");

    /// <summary>Produit désactivé : il ne peut pas être reçu sur un bon.</summary>
    public static readonly ProductSummary Retired = new(Guid.NewGuid(), 100004, "Ancien shampooing", 650m, "2000000000046");

    public const string UnknownBarcode = "2000000000077";
    public const string KarimRegister = "Caisse 2";

    private sealed class FakeNote
    {
        public required SupplierNoteSummary Summary { get; set; }

        public List<SupplierNoteLine> Lines { get; } = [];

        public List<SupplierNoteHistoryEntry> History { get; } = [];

        public Guid? Holder { get; set; }
    }

    private sealed record FakeNoteProduct(ProductSummary Product, bool SoldByUnit, bool IsActive);

    private readonly Dictionary<Guid, FakeNote> fakeNotes = [];
    private readonly Dictionary<Guid, object> noteOperations = [];
    private readonly List<FakeNoteProduct> noteProducts =
    [
        new(Creme, true, true), new(Serum, true, true), new(Henna, false, true), new(Retired, true, false),
    ];

    private long nextNoteNumber = 1;
    private long nextProductCode = 100010;

    public List<SupplierSummary> NoteSuppliers { get; } = [Biocos, Dermalab];

    /// <summary>Stock à l'emplacement des bons, par produit.</summary>
    public Dictionary<Guid, decimal> NoteStock { get; } = new() { [Creme.Id] = 10m };

    /// <summary>Dernier prix d'achat connu, par produit.</summary>
    public Dictionary<Guid, decimal> LastPurchasePrices { get; } = new() { [Creme.Id] = 1200m };

    // ---------- Demandes reçues, dans l'ordre (y compris celles restées sans réponse) ----------

    public List<AddSupplierNoteLineRequest> NoteLineRequests { get; } = [];

    public List<UpdateSupplierNoteLineRequest> NoteLineUpdateRequests { get; } = [];

    public List<CancelSupplierNoteLineRequest> NoteLineCancelRequests { get; } = [];

    public List<ValidateSupplierNoteRequest> NoteValidateRequests { get; } = [];

    public List<CancelSupplierNoteRequest> NoteCancelRequests { get; } = [];

    public List<CreateSupplierNoteRequest> NoteCreateRequests { get; } = [];

    public List<CreateSupplierRequest> SupplierCreateRequests { get; } = [];

    public List<CreateProductRequest> QuickProductRequests { get; } = [];

    public List<NoteListFilter> NoteListRequests { get; } = [];

    /// <summary>Appels au verrou : « prendre », « battre », « libérer », suivis du numéro du bon.</summary>
    public List<string> NoteLockCalls { get; } = [];

    /// <summary>La prochaine écriture est enregistrée, mais sa réponse se perd (coupure du réseau).</summary>
    public bool LoseNextNoteResponse { get; set; }

    /// <summary>La prochaine écriture n'arrive pas au serveur (coupure du réseau).</summary>
    public bool DropNextNoteRequest { get; set; }

    /// <summary>Quand il est posé, l'ajout d'une ligne attend ce signal : la demande est « en cours d'envoi ».</summary>
    public TaskCompletionSource? NoteLineGate { get; set; }

    // ---------- Préparation des essais ----------

    /// <summary>Crée un bon directement dans le faux serveur, tenu par personne.</summary>
    public Guid SeedNote(string status = SupplierNoteStatuses.InProgress, SupplierSummary? supplier = null, DateOnly? date = null)
    {
        supplier ??= Biocos;
        var id = Guid.NewGuid();
        var number = nextNoteNumber++;
        fakeNotes[id] = new FakeNote
        {
            Summary = new SupplierNoteSummary(
                id, number, supplier.Id, supplier.Name, Depot.Id, Depot.Name, Depot.StoreId, Depot.StoreName, $"BL-{number:000}",
                date ?? new DateOnly(2026, 11, 16), status, 0, 0m, DateTimeOffset.Now, KarimName, null),
        };
        return id;
    }

    /// <summary>Ajoute une ligne déjà enregistrée (et son stock) à un bon du faux serveur.</summary>
    public SupplierNoteLine SeedNoteLine(Guid noteId, ProductSummary product, decimal quantity, decimal price)
    {
        var note = fakeNotes[noteId];
        var line = NewLine(note, product, quantity, price);
        note.Lines.Add(line);
        NoteStock[product.Id] = NoteStock.GetValueOrDefault(product.Id) + quantity;
        Recount(note);
        return line;
    }

    /// <summary>Un collègue (Karim) prend le bon : le verrou n'est plus à l'employé de test.</summary>
    public void LetKarimTakeNote(Guid noteId) => fakeNotes[noteId].Holder = KarimId;

    /// <summary>Le verrou est libéré de force : plus personne ne tient le bon.</summary>
    public void ForceReleaseNote(Guid noteId) => fakeNotes[noteId].Holder = null;

    public Guid? NoteHolder(Guid noteId) => fakeNotes[noteId].Holder;

    public IReadOnlyList<SupplierNoteLine> NoteLines(Guid noteId) => fakeNotes[noteId].Lines;

    public string NoteStatus(Guid noteId) => fakeNotes[noteId].Summary.Status;

    /// <summary>Un autre poste a modifié la ligne : sa version a changé.</summary>
    public void ChangeNoteLineElsewhere(Guid noteId, Guid lineId, decimal quantity)
    {
        var note = fakeNotes[noteId];
        var index = note.Lines.FindIndex(line => line.Id == lineId);
        var line = note.Lines[index];
        NoteStock[line.ProductId] += quantity - line.Quantity;
        note.Lines[index] = line with { Quantity = quantity, LineTotal = quantity * line.PurchasePrice, Version = line.Version + 1 };
        Recount(note);
    }

    // ---------- Fournisseurs et emplacements ----------

    public Task<IReadOnlyList<SupplierSummary>> ListSuppliersAsync(string? term, CancellationToken cancellationToken)
    {
        EnsureNoteRight(NoteReadRights);
        var key = TextNormalizer.ToSearchKey(term ?? string.Empty);
        return Task.FromResult<IReadOnlyList<SupplierSummary>>(
            NoteSuppliers.Where(supplier => TextNormalizer.ToSearchKey(supplier.Name).Contains(key)).OrderBy(supplier => supplier.Name).ToList());
    }

    public Task<SupplierSummary> CreateSupplierAsync(CreateSupplierRequest request, CancellationToken cancellationToken)
    {
        EnsurePermission(Permissions.NotesEnter);
        SupplierCreateRequests.Add(request);
        return Task.FromResult(RunNoteOperation(request.OperationId, () =>
        {
            var name = request.Name.Trim();
            if (name.Length == 0)
            {
                throw new ServerRefusedException(ApiErrorCodes.InvalidRequest, "Le nom du fournisseur est obligatoire (200 caractères au plus).");
            }

            if (NoteSuppliers.Any(supplier => string.Equals(supplier.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ServerRefusedException(ApiErrorCodes.Conflict, $"Le fournisseur « {name} » existe déjà.");
            }

            var created = new SupplierSummary(Guid.NewGuid(), name);
            NoteSuppliers.Add(created);
            return created;
        }));
    }

    public Task<IReadOnlyList<StockLocationSummary>> ListNoteLocationsAsync(CancellationToken cancellationToken)
    {
        EnsureEmployee();
        return Task.FromResult<IReadOnlyList<StockLocationSummary>>([Depot, Shop]);
    }

    // ---------- Bons : lecture ----------

    public Task<SupplierNotePage> ListNotesAsync(NoteListFilter filter, CancellationToken cancellationToken)
    {
        EnsureNoteRight(NoteReadRights);
        NoteListRequests.Add(filter);
        var found = fakeNotes.Values
            .Select(note => Summarize(note))
            .Where(note => filter.Status is null || note.Status == filter.Status)
            .Where(note => filter.SupplierId is null || note.SupplierId == filter.SupplierId)
            .Where(note => filter.From is null || note.NoteDate >= filter.From)
            .Where(note => filter.To is null || note.NoteDate <= filter.To)
            .OrderByDescending(note => note.NoteDate).ThenByDescending(note => note.Number)
            .ToList();
        return Task.FromResult(new SupplierNotePage(found, found.Count, filter.Page, 50));
    }

    public Task<SupplierNoteDetail> ReadNoteAsync(Guid noteId, CancellationToken cancellationToken)
    {
        EnsureNoteRight(NoteReadRights);
        return Task.FromResult(Detail(FindNote(noteId)));
    }

    // ---------- Verrou ----------

    public Task<SupplierNoteDetail> AcquireNoteLockAsync(Guid noteId, CancellationToken cancellationToken)
    {
        EnsureNoteRight(NoteWriteRights);
        var note = FindNote(noteId);
        NoteLockCalls.Add($"prendre {note.Summary.Number}");
        if (note.Summary.Status == SupplierNoteStatuses.Cancelled)
        {
            throw NoteIsCancelled();
        }

        if (note.Holder is { } holder && holder != EmployeeId)
        {
            throw LockedByOther();
        }

        note.Holder = EmployeeId;
        return Task.FromResult(Detail(note));
    }

    public Task RenewNoteLockAsync(Guid noteId, CancellationToken cancellationToken)
    {
        EnsureNoteRight(NoteWriteRights);
        var note = FindNote(noteId);
        NoteLockCalls.Add($"battre {note.Summary.Number}");
        RequireHolder(note);
        return Task.CompletedTask;
    }

    public Task ReleaseNoteLockAsync(Guid noteId, CancellationToken cancellationToken)
    {
        EnsureNoteRight(NoteWriteRights);
        var note = FindNote(noteId);
        NoteLockCalls.Add($"libérer {note.Summary.Number}");
        if (note.Holder == EmployeeId)
        {
            note.Holder = null;
        }
        else if (note.Holder is not null)
        {
            throw LockedByOther();
        }

        return Task.CompletedTask;
    }

    // ---------- Produits proposés à la saisie ----------

    public Task<NoteProductInfo> FindNoteProductByBarcodeAsync(string barcode, CancellationToken cancellationToken)
    {
        EnsureNoteRight(NoteReadRights);
        var found = noteProducts.FirstOrDefault(candidate => candidate.Product.Barcode == barcode.Trim())
            ?? throw new ServerRefusedException(SupplierNoteErrorCodes.UnknownBarcode, $"Aucun produit ne porte le code-barres {barcode.Trim()}.");
        return Task.FromResult(Info(found));
    }

    public Task<NoteProductInfo> ReadNoteProductAsync(Guid productId, CancellationToken cancellationToken)
    {
        EnsureNoteRight(NoteReadRights);
        return Task.FromResult(Info(FindNoteProduct(productId)));
    }

    public Task<IReadOnlyList<ProductStockLine>> ReadNoteProductStockAsync(Guid productId, CancellationToken cancellationToken)
    {
        EnsurePermission(Permissions.ProductsView);
        return Task.FromResult<IReadOnlyList<ProductStockLine>>(
        [
            new(Depot.Id, Depot.Code, Depot.Name, Depot.Kind, Depot.StoreId, Depot.StoreName, NoteStock.GetValueOrDefault(productId)),
            new(Shop.Id, Shop.Code, Shop.Name, Shop.Kind, Shop.StoreId, Shop.StoreName, 0m),
        ]);
    }

    public Task<ProductSummary> CreateQuickProductAsync(CreateProductRequest request, CancellationToken cancellationToken)
    {
        EnsurePermission(Permissions.ProductsCreate);
        QuickProductRequests.Add(request);
        return Task.FromResult(RunNoteOperation(request.OperationId, () =>
        {
            if (request.Name.Trim().Length == 0)
            {
                throw new ServerRefusedException(ApiErrorCodes.InvalidRequest, "La désignation est obligatoire (200 caractères au plus).");
            }

            if (noteProducts.FirstOrDefault(known => request.Barcodes.Contains(known.Product.Barcode)) is { } used)
            {
                throw new ServerRefusedException(
                    ApiErrorCodes.Conflict, $"Le code-barres {used.Product.Barcode} est déjà attribué à « {used.Product.Name} ».");
            }

            var created = new ProductSummary(Guid.NewGuid(), nextProductCode++, request.Name.Trim(), request.SalePrice, request.Barcodes[0]);
            noteProducts.Add(new FakeNoteProduct(created, request.SoldByUnit, true));
            return created;
        }));
    }

    // ---------- Bons : écriture ----------

    public Task<SupplierNoteDetail> CreateNoteAsync(CreateSupplierNoteRequest request, CancellationToken cancellationToken)
    {
        EnsurePermission(Permissions.NotesEnter);
        NoteCreateRequests.Add(request);
        return Task.FromResult(RunNoteOperation(request.OperationId, () =>
        {
            if (request.SupplierReference.Trim().Length == 0)
            {
                throw new ServerRefusedException(
                    ApiErrorCodes.InvalidRequest, "La référence du fournisseur est obligatoire (100 caractères au plus).");
            }

            var supplier = NoteSuppliers.FirstOrDefault(known => known.Id == request.SupplierId)
                ?? throw new ServerRefusedException(ApiErrorCodes.NotFound, "Fournisseur introuvable.");
            var location = new[] { Depot, Shop }.FirstOrDefault(known => known.Id == request.LocationId)
                ?? throw new ServerRefusedException(ApiErrorCodes.NotFound, "Emplacement de stock introuvable.");
            var id = Guid.NewGuid();
            var number = nextNoteNumber++;
            var note = new FakeNote
            {
                Summary = new SupplierNoteSummary(
                    id, number, supplier.Id, supplier.Name, location.Id, location.Name, location.StoreId, location.StoreName,
                    request.SupplierReference.Trim(), request.NoteDate, SupplierNoteStatuses.InProgress, 0, 0m, DateTimeOffset.Now,
                    EmployeeName, null),
                Holder = EmployeeId,
            };
            fakeNotes[id] = note;
            Record(note, SupplierNoteEvents.Created, null, null, $"Bon n° {number} créé pour « {supplier.Name} », référence {request.SupplierReference.Trim()}.");
            return Detail(note);
        }));
    }

    public async Task<SupplierNoteLineResult> AddNoteLineAsync(Guid noteId, AddSupplierNoteLineRequest request, CancellationToken cancellationToken)
    {
        var note = OpenForLines(noteId);
        NoteLineRequests.Add(request);
        if (NoteLineGate is { } gate)
        {
            await gate.Task;
        }

        return RunNoteOperation(request.OperationId, () =>
        {
            RequireHolder(note);
            var product = FindNoteProduct(request.ProductId);
            if (!product.IsActive)
            {
                throw new ServerRefusedException(
                    ApiErrorCodes.InvalidRequest, $"« {product.Product.Name} » est désactivé : réactivez-le avant de le recevoir sur un bon.");
            }

            ValidateLine(product, request.Quantity, request.PurchasePrice);
            var line = NewLine(note, product.Product, request.Quantity, request.PurchasePrice);
            note.Lines.Add(line);
            var before = NoteStock.GetValueOrDefault(line.ProductId);
            NoteStock[line.ProductId] = before + line.Quantity;
            LastPurchasePrices[line.ProductId] = line.PurchasePrice;
            Record(note, SupplierNoteEvents.LineAdded, note.Summary.Status, line.Id,
                $"Ligne {line.Position} « {line.ProductName} » : {line.Quantity:0.###} à {Money.ToDisplay(line.PurchasePrice)}.");
            return Result(note, line, before, line.Quantity);
        });
    }

    public Task<SupplierNoteLineResult> UpdateNoteLineAsync(
        Guid noteId, Guid lineId, UpdateSupplierNoteLineRequest request, CancellationToken cancellationToken)
    {
        var note = OpenForLines(noteId);
        NoteLineUpdateRequests.Add(request);
        return Task.FromResult(RunNoteOperation(request.OperationId, () =>
        {
            RequireHolder(note);
            var (index, line) = FindLiveLine(note, lineId, request.ExpectedVersion);
            ValidateLine(FindNoteProduct(line.ProductId), request.Quantity, request.PurchasePrice);
            var before = NoteStock.GetValueOrDefault(line.ProductId);
            var difference = request.Quantity - line.Quantity;
            if (before + difference < 0)
            {
                throw new ServerRefusedException(
                    SupplierNoteErrorCodes.StockAlreadyGone,
                    $"Impossible de ramener « {line.ProductName} » de {line.Quantity:0.###} à {request.Quantity:0.###} : il n'en reste que "
                    + $"{before:0.###} en stock à cet emplacement, le reste est déjà parti (vendu ou transféré). Quantité minimale possible pour "
                    + $"cette ligne : {line.Quantity - before:0.###}. Au-delà, corrigez par un ajustement d'inventaire.");
            }

            var updated = line with
            {
                Quantity = request.Quantity,
                PurchasePrice = request.PurchasePrice,
                LineTotal = request.Quantity * request.PurchasePrice,
                Version = line.Version + 1,
                Margin = Margin(line.Margin.SalePrice, line.Margin.PreviousPurchasePrice, request.PurchasePrice),
            };
            note.Lines[index] = updated;
            NoteStock[line.ProductId] = before + difference;
            Record(note, SupplierNoteEvents.LineUpdated, note.Summary.Status, lineId, $"Ligne {line.Position} « {line.ProductName} » modifiée.");
            return Result(note, updated, before, difference);
        }));
    }

    public Task<SupplierNoteLineResult> CancelNoteLineAsync(
        Guid noteId, Guid lineId, CancelSupplierNoteLineRequest request, CancellationToken cancellationToken)
    {
        var note = OpenForLines(noteId);
        NoteLineCancelRequests.Add(request);
        return Task.FromResult(RunNoteOperation(request.OperationId, () =>
        {
            RequireHolder(note);
            var (index, line) = FindLiveLine(note, lineId, request.ExpectedVersion);
            var before = NoteStock.GetValueOrDefault(line.ProductId);
            if (before < line.Quantity)
            {
                throw new ServerRefusedException(
                    SupplierNoteErrorCodes.StockAlreadyGone,
                    $"Impossible d'annuler la ligne « {line.ProductName} » ({line.Quantity:0.###}) : il n'en reste que {before:0.###} en stock "
                    + "à cet emplacement, le reste est déjà parti (vendu ou transféré). Corrigez par un ajustement d'inventaire.");
            }

            var cancelled = line with { IsCancelled = true, Version = line.Version + 1 };
            note.Lines[index] = cancelled;
            NoteStock[line.ProductId] = before - line.Quantity;
            Record(note, SupplierNoteEvents.LineCancelled, note.Summary.Status, lineId, $"Ligne {line.Position} « {line.ProductName} » annulée.");
            return Result(note, cancelled, before, -line.Quantity);
        }));
    }

    public Task<SupplierNoteSummary> ValidateNoteAsync(Guid noteId, ValidateSupplierNoteRequest request, CancellationToken cancellationToken)
    {
        EnsurePermission(Permissions.NotesValidate);
        var note = FindNote(noteId);
        NoteValidateRequests.Add(request);
        return Task.FromResult(RunNoteOperation(request.OperationId, () =>
        {
            if (note.Summary.Status == SupplierNoteStatuses.Cancelled)
            {
                throw NoteIsCancelled();
            }

            if (note.Summary.Status == SupplierNoteStatuses.InProgress)
            {
                RequireHolder(note);
                if (note.Lines.All(line => line.IsCancelled))
                {
                    throw new ServerRefusedException(SupplierNoteErrorCodes.EmptyNote, "Ce bon n'a aucune ligne : il ne peut pas être validé.");
                }

                note.Summary = note.Summary with { Status = SupplierNoteStatuses.Validated };
                note.Holder = null;
                Record(note, SupplierNoteEvents.Validated, SupplierNoteStatuses.InProgress, null, $"Bon validé ({note.Summary.LineCount} ligne(s)).");
            }

            return Summarize(note);
        }));
    }

    public Task<SupplierNoteSummary> CancelNoteAsync(Guid noteId, CancelSupplierNoteRequest request, CancellationToken cancellationToken)
    {
        EnsurePermission(Permissions.NotesCancel);
        var note = FindNote(noteId);
        NoteCancelRequests.Add(request);
        return Task.FromResult(RunNoteOperation(request.OperationId, () =>
        {
            if (note.Summary.Status != SupplierNoteStatuses.Cancelled)
            {
                RequireHolder(note);
                var live = note.Lines.Where(line => !line.IsCancelled).ToList();
                var missing = live.GroupBy(line => line.ProductId)
                    .Where(group => NoteStock.GetValueOrDefault(group.Key) < group.Sum(line => line.Quantity))
                    .Select(group =>
                        $"« {group.First().ProductName} » ({group.Sum(line => line.Quantity):0.###} reçu, {NoteStock.GetValueOrDefault(group.Key):0.###} encore en stock)")
                    .ToList();
                if (missing.Count > 0)
                {
                    throw new ServerRefusedException(
                        SupplierNoteErrorCodes.StockAlreadyGone,
                        "Annulation impossible : une partie de la marchandise de ce bon a déjà quitté le stock (vendue ou transférée). "
                        + $"Produits concernés : {string.Join(" ; ", missing)}. Rien n'a été modifié.");
                }

                foreach (var line in live)
                {
                    NoteStock[line.ProductId] -= line.Quantity;
                }

                var before = note.Summary.Status;
                note.Summary = note.Summary with { Status = SupplierNoteStatuses.Cancelled };
                note.Holder = null;
                Record(note, SupplierNoteEvents.Cancelled, before, null,
                    $"Bon annulé : {live.Count} ligne(s) retirée(s) du stock." + (request.Reason is null ? string.Empty : $" Motif : {request.Reason}"));
            }

            return Summarize(note);
        }));
    }

    // ---------- Règles communes ----------

    private static readonly string[] NoteWriteRights =
        [Permissions.NotesEnter, Permissions.NotesEdit, Permissions.NotesValidate, Permissions.NotesCancel];

    private static readonly string[] NoteReadRights = [Permissions.NotesView, .. NoteWriteRights];

    private void EnsureNoteRight(string[] rights)
    {
        EnsureEmployee();
        if (!rights.Any(EmployeePermissions.Contains))
        {
            throw new ServerRefusedException(ApiErrorCodes.PermissionDenied, "Vous n'avez pas le droit d'effectuer cette action.");
        }
    }

    /// <summary>
    /// Idempotence et pannes simulées : un identifiant déjà traité renvoie le résultat
    /// enregistré sans rien refaire ; un refus n'enregistre rien.
    /// </summary>
    private T RunNoteOperation<T>(Guid operationId, Func<T> action)
        where T : class
    {
        if (DropNextNoteRequest)
        {
            DropNextNoteRequest = false;
            throw new HttpRequestException("demande perdue");
        }

        if (operationId == Guid.Empty)
        {
            throw new ServerRefusedException(ApiErrorCodes.InvalidRequest, "Identifiant d'opération manquant.");
        }

        if (!noteOperations.TryGetValue(operationId, out var result))
        {
            result = action();
            noteOperations[operationId] = result;
        }

        if (LoseNextNoteResponse)
        {
            LoseNextNoteResponse = false;
            throw new TaskCanceledException("réponse perdue");
        }

        return result as T
            ?? throw new ServerRefusedException(
                ApiErrorCodes.OperationMismatch, "Cet identifiant d'opération a déjà servi pour une autre demande. Rien n'a été enregistré.");
    }

    private FakeNote FindNote(Guid noteId) =>
        fakeNotes.TryGetValue(noteId, out var note) ? note : throw new ServerRefusedException(ApiErrorCodes.NotFound, "Bon introuvable.");

    private FakeNoteProduct FindNoteProduct(Guid productId) =>
        noteProducts.FirstOrDefault(known => known.Product.Id == productId)
        ?? throw new ServerRefusedException(ApiErrorCodes.NotFound, "Produit introuvable.");

    /// <summary>Saisie ordinaire tant que le bon est en saisie ; ensuite, c'est une correction.</summary>
    private FakeNote OpenForLines(Guid noteId)
    {
        EnsureEmployee();
        var note = FindNote(noteId);
        EnsurePermission(note.Summary.Status == SupplierNoteStatuses.Validated ? Permissions.NotesEdit : Permissions.NotesEnter);
        return note.Summary.Status == SupplierNoteStatuses.Cancelled ? throw NoteIsCancelled() : note;
    }

    private void RequireHolder(FakeNote note)
    {
        if (note.Holder == EmployeeId)
        {
            return;
        }

        throw note.Holder is not null
            ? LockedByOther()
            : new ServerRefusedException(SupplierNoteErrorCodes.LockNotHeld, "Vous ne tenez pas ce bon. Reprenez-le avant de le modifier.");
    }

    private static ServerRefusedException LockedByOther() =>
        new(SupplierNoteErrorCodes.LockedByOther, $"Ce bon est en cours de saisie par {KarimName} (poste « {KarimRegister} »).");

    private static ServerRefusedException NoteIsCancelled() =>
        new(SupplierNoteErrorCodes.NoteCancelled, "Ce bon est annulé : il ne peut plus être modifié.");

    private static (int Index, SupplierNoteLine Line) FindLiveLine(FakeNote note, Guid lineId, int expectedVersion)
    {
        var index = note.Lines.FindIndex(line => line.Id == lineId);
        if (index < 0)
        {
            throw new ServerRefusedException(ApiErrorCodes.NotFound, "Ligne introuvable sur ce bon.");
        }

        var line = note.Lines[index];
        if (line.Version != expectedVersion)
        {
            throw new ServerRefusedException(
                SupplierNoteErrorCodes.StaleVersion, "Cette ligne a été modifiée depuis son affichage. Rechargez le bon avant de recommencer.");
        }

        return line.IsCancelled
            ? throw new ServerRefusedException(SupplierNoteErrorCodes.LineCancelled, "Cette ligne est annulée : elle ne peut plus être modifiée.")
            : (index, line);
    }

    private static void ValidateLine(FakeNoteProduct product, decimal quantity, decimal price)
    {
        if (quantity <= 0)
        {
            throw new ServerRefusedException(ApiErrorCodes.InvalidRequest, "La quantité d'une ligne doit être supérieure à zéro.");
        }

        if (product.SoldByUnit && quantity != decimal.Truncate(quantity))
        {
            throw new ServerRefusedException(
                ApiErrorCodes.FractionalQuantity, $"« {product.Product.Name} » se vend à l'unité : la quantité {quantity:0.###} n'est pas entière.");
        }

        if (price < 0 || decimal.Round(price, 2) != price)
        {
            throw new ServerRefusedException(
                ApiErrorCodes.InvalidRequest, "Le prix d'achat doit être positif ou nul, avec au plus deux décimales.");
        }
    }

    private SupplierNoteLine NewLine(FakeNote note, ProductSummary product, decimal quantity, decimal price) =>
        new(
            Guid.NewGuid(), note.Lines.Count + 1, product.Id, product.Code, product.Name, product.Barcode, quantity, price,
            quantity * price, 1, false,
            Margin(product.SalePrice, LastPurchasePrices.TryGetValue(product.Id, out var previous) ? previous : null, price));

    private static MarginVariation Margin(decimal salePrice, decimal? previousPrice, decimal newPrice)
    {
        decimal? Percent(decimal margin) => salePrice == 0 ? null : decimal.Round(margin / salePrice * 100, 2);
        decimal? before = previousPrice is { } previous ? salePrice - previous : null;
        var after = salePrice - newPrice;
        return new MarginVariation(salePrice, previousPrice, newPrice, before, before is { } known ? Percent(known) : null, after, Percent(after));
    }

    private NoteProductInfo Info(FakeNoteProduct known) =>
        new(
            known.Product.Id, known.Product.Code, known.Product.Name, known.Product.Barcode, known.Product.SalePrice, known.SoldByUnit,
            known.IsActive,
            LastPurchasePrices.TryGetValue(known.Product.Id, out var price) ? price : null,
            LastPurchasePrices.ContainsKey(known.Product.Id) ? Biocos.Name : null,
            LastPurchasePrices.ContainsKey(known.Product.Id) ? new DateOnly(2026, 10, 12) : null);

    private static void Recount(FakeNote note)
    {
        var live = note.Lines.Where(line => !line.IsCancelled).ToList();
        note.Summary = note.Summary with { LineCount = live.Count, TotalAmount = live.Sum(line => line.LineTotal) };
    }

    private SupplierNoteLineResult Result(FakeNote note, SupplierNoteLine line, decimal before, decimal change)
    {
        Recount(note);
        return new SupplierNoteLineResult(line, before, change, before + change, note.Summary.LineCount, note.Summary.TotalAmount);
    }

    private void Record(FakeNote note, string @event, string? statusBefore, Guid? lineId, string description) =>
        note.History.Add(new SupplierNoteHistoryEntry(
            DateTimeOffset.Now, @event, statusBefore, note.Summary.Status, EmployeeName, lineId, description));

    private SupplierNoteSummary Summarize(FakeNote note)
    {
        Recount(note);
        var holder = note.Holder is { } id
            ? new SupplierNoteLock(
                id, id == EmployeeId ? EmployeeName : KarimName, id == EmployeeId ? "Caisse 1" : KarimRegister, DateTimeOffset.Now,
                DateTimeOffset.Now.AddMinutes(2), id == EmployeeId, false)
            : null;
        return note.Summary with { Lock = holder };
    }

    private SupplierNoteDetail Detail(FakeNote note) => new(Summarize(note), note.Lines.ToList(), note.History.ToList());
}
