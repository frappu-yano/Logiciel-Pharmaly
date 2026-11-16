using System.Text.Json;
using Dapper;
using Npgsql;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Server.Access;
using Pharmaly.Server.Operations;
using Pharmaly.Server.Stock;

namespace Pharmaly.Server.SupplierNotes;

/// <summary>
/// Écritures sur les bons fournisseurs. Chaque commande est UNE transaction (voir
/// <see cref="OperationRunner"/>) qui verrouille le bon, contrôle le verrou de saisie,
/// passe par le moteur de stock, puis écrit la ligne, l'historique des achats,
/// l'historique du bon et l'audit. Un échec à n'importe quelle étape annule tout.
/// </summary>
public sealed class SupplierNoteCommands(
    NpgsqlDataSource dataSource, OperationRunner runner, StockEngine engine, SupplierNoteLocks locks)
{
    public const string CreateSupplierOperation = "FOURNISSEUR_CREER";
    public const string CreateNoteOperation = "BON_CREER";
    public const string AddLineOperation = "BON_LIGNE_AJOUTER";
    public const string UpdateLineOperation = "BON_LIGNE_MODIFIER";
    public const string CancelLineOperation = "BON_LIGNE_ANNULER";
    public const string ValidateOperation = "BON_VALIDER";
    public const string CancelNoteOperation = "BON_ANNULER";

    /// <summary>Origine des mouvements de stock d'un bon ; l'identifiant d'origine est celui du bon.</summary>
    public const string StockSource = "BON";

    private const string PurchaseKind = "ACHAT";
    private const string CorrectionKind = "CORRECTION";
    private const string CancellationKind = "ANNULATION";

    private const int MaxSupplierNameLength = 200;
    private const int MaxReferenceLength = 100;
    private const int MaxReasonLength = 500;
    private const decimal MaxPrice = 9_999_999_999.99m;
    private const decimal MaxQuantity = 999_999_999.999m;
    private static readonly DateOnly EarliestNoteDate = new(2000, 1, 1);

    // ---------- Fournisseurs ----------

    public Task<IResult> CreateSupplierAsync(CreateSupplierRequest request, HttpContext context) =>
        runner.RunAsync(request.OperationId, CreateSupplierOperation, context.GetIdentity(), request, async operation =>
        {
            var name = (request.Name ?? string.Empty).Trim();
            if (name.Length is 0 or > MaxSupplierNameLength)
            {
                throw BusinessException.Invalid($"Le nom du fournisseur est obligatoire ({MaxSupplierNameLength} caractères au plus).");
            }

            // ON CONFLICT attend la fin d'une création simultanée du même nom, sans erreur de base.
            var id = await operation.Connection.ExecuteScalarAsync<Guid?>(
                """
                INSERT INTO suppliers (name, created_by) VALUES (@name, @EmployeeId)
                ON CONFLICT (lower(name)) DO NOTHING
                RETURNING id
                """,
                new { name, operation.EmployeeId },
                operation.Transaction)
                ?? throw BusinessException.Conflict($"Le fournisseur « {name} » existe déjà.");

            await operation.AuditAsync(SupplierNoteAuditActions.SupplierCreated, "supplier", id.ToString(), new { nom = name });
            return new SupplierSummary(id, name);
        });

    // ---------- Bon ----------

    public async Task<IResult> CreateNoteAsync(CreateSupplierNoteRequest request, HttpContext context)
    {
        var identity = context.GetIdentity();
        await using (var connection = await dataSource.OpenConnectionAsync())
        {
            // Le droit de saisir doit couvrir le site de l'emplacement où entre la marchandise.
            var storeId = await connection.ExecuteScalarAsync<Guid?>(
                "SELECT store_id FROM stock_locations WHERE id = @LocationId", new { request.LocationId });
            if (storeId is not null
                && await NoteAccess.RefuseUnlessAnyAsync(
                    connection, context, await NoteAccess.GrantedAsync(connection, identity, storeId), [Permissions.NotesEnter]) is { } refusal)
            {
                return refusal;
            }
        }

        return await runner.RunAsync(request.OperationId, CreateNoteOperation, identity, request, async operation =>
        {
            var reference = (request.SupplierReference ?? string.Empty).Trim();
            if (reference.Length is 0 or > MaxReferenceLength)
            {
                throw BusinessException.Invalid($"La référence du fournisseur est obligatoire ({MaxReferenceLength} caractères au plus).");
            }

            if (request.NoteDate < EarliestNoteDate)
            {
                throw BusinessException.Invalid("La date du bon est obligatoire.");
            }

            var supplierName = await operation.Connection.ExecuteScalarAsync<string?>(
                "SELECT name FROM suppliers WHERE id = @SupplierId AND is_active", new { request.SupplierId }, operation.Transaction)
                ?? throw BusinessException.NotFound("Fournisseur introuvable.");
            var locationKnown = await operation.Connection.ExecuteScalarAsync<bool>(
                """
                SELECT EXISTS (SELECT 1 FROM stock_locations l JOIN stores s ON s.id = l.store_id
                               WHERE l.id = @LocationId AND l.is_active AND s.is_active)
                """,
                new { request.LocationId },
                operation.Transaction);
            if (!locationKnown)
            {
                throw BusinessException.NotFound("Emplacement de stock introuvable.");
            }

            // Le bon naît réservé à l'employé qui le crée.
            var created = await operation.Connection.QuerySingleAsync<(Guid Id, long Number)>(
                """
                INSERT INTO supplier_notes
                    (supplier_id, location_id, supplier_reference, note_date, created_by,
                     lock_employee_id, lock_register_account_id, lock_acquired_at, lock_expires_at)
                VALUES (@SupplierId, @LocationId, @reference, @noteDate::date, @EmployeeId,
                        @EmployeeId, @RegisterAccountId, clock_timestamp(), clock_timestamp())
                RETURNING id, number
                """,
                new
                {
                    request.SupplierId,
                    request.LocationId,
                    reference,
                    noteDate = request.NoteDate.ToDateTime(TimeOnly.MinValue),
                    operation.EmployeeId,
                    operation.Identity.RegisterAccountId,
                },
                operation.Transaction);
            await locks.TakeAsync(
                operation.Connection, operation.Transaction,
                new HeldNote { Id = created.Id, LockEmployeeId = operation.EmployeeId }, operation.Identity);

            await RecordEventAsync(
                operation, created.Id, SupplierNoteEvents.Created, null, SupplierNoteStatuses.InProgress, null,
                $"Bon n° {created.Number} créé pour « {supplierName} », référence {reference}.",
                new { fournisseur = request.SupplierId, emplacement = request.LocationId, reference, date = request.NoteDate });
            await operation.AuditAsync(
                SupplierNoteAuditActions.NoteCreated, "supplier_note", created.Id.ToString(),
                new { numero = created.Number, fournisseur = supplierName, emplacement = request.LocationId, reference, date = request.NoteDate });
            return (await SupplierNoteQueries.ReadDetailAsync(operation.Connection, operation.Transaction, created.Id, operation.EmployeeId))!;
        });
    }

    // ---------- Lignes ----------

    public async Task<IResult> AddLineAsync(Guid noteId, AddSupplierNoteLineRequest request, HttpContext context)
    {
        var (gate, refusal) = await NoteAccess.OpenAsync(dataSource, context, noteId, NoteAccess.LineRights);
        if (refusal is not null)
        {
            return refusal;
        }

        return await runner.RunAsync(request.OperationId, AddLineOperation, context.GetIdentity(), new { noteId, request }, async operation =>
        {
            var note = await OpenForLinesAsync(operation, noteId, gate!);
            var product = await SupplierNoteQueries.ReadProductAsync(operation.Connection, operation.Transaction, request.ProductId)
                ?? throw BusinessException.NotFound("Produit introuvable.");
            if (!product.IsActive)
            {
                throw BusinessException.Invalid($"« {product.Name} » est désactivé : réactivez-le avant de le recevoir sur un bon.");
            }

            ValidateQuantity(product.Name, product.SoldByUnit, request.Quantity);
            ValidatePrice(request.PurchasePrice);

            var lineId = Guid.NewGuid();
            var position = await operation.Connection.ExecuteScalarAsync<int>(
                "SELECT COALESCE(max(position), 0) + 1 FROM supplier_note_lines WHERE note_id = @noteId", new { noteId }, operation.Transaction);

            // 1. La quantité entre en stock tout de suite, sans attendre la validation du bon.
            var applied = (await engine.ApplyAsync(
                operation.Connection, operation.Transaction, operation.EmployeeId, operation.OperationId,
                [StockChange.Move(request.ProductId, note.LocationId, request.Quantity, StockMovementTypes.NoteEntry)
                    with { SourceType = StockSource, SourceId = noteId.ToString(), Note = $"Bon n° {note.Number}, ligne {position}" }]))[0];

            // 2. La ligne, avec le dernier prix d'achat connu au moment de l'enregistrement.
            await operation.Connection.ExecuteAsync(
                """
                INSERT INTO supplier_note_lines
                    (id, note_id, position, product_id, quantity, purchase_price, previous_purchase_price, created_by)
                VALUES (@lineId, @noteId, @position, @ProductId, @Quantity, @PurchasePrice, @LastPurchasePrice, @EmployeeId)
                """,
                new { lineId, noteId, position, request.ProductId, request.Quantity, request.PurchasePrice, product.LastPurchasePrice, operation.EmployeeId },
                operation.Transaction);

            await RecordPurchaseAsync(operation, note, lineId, request.ProductId, PurchaseKind, request.PurchasePrice, request.Quantity);
            await RecordEventAsync(
                operation, noteId, SupplierNoteEvents.LineAdded, note.Status, note.Status, lineId,
                $"Ligne {position} « {product.Name} » : {StockEngine.Format(request.Quantity)} à {Money.ToDisplay(request.PurchasePrice)}.",
                new { produit = request.ProductId, quantite = request.Quantity, prixAchat = request.PurchasePrice });
            await operation.AuditAsync(
                SupplierNoteAuditActions.LineAdded, "supplier_note", noteId.ToString(),
                new
                {
                    ligne = lineId,
                    produit = request.ProductId,
                    quantite = request.Quantity,
                    prixAchat = request.PurchasePrice,
                    stockAvant = applied.QuantityBefore,
                    stockApres = applied.QuantityAfter,
                    etatDuBon = note.Status,
                });
            return await LineResultAsync(operation, noteId, lineId, applied.QuantityBefore, applied.Quantity, applied.QuantityAfter);
        });
    }

    public async Task<IResult> UpdateLineAsync(Guid noteId, Guid lineId, UpdateSupplierNoteLineRequest request, HttpContext context)
    {
        var (gate, refusal) = await NoteAccess.OpenAsync(dataSource, context, noteId, NoteAccess.LineRights);
        if (refusal is not null)
        {
            return refusal;
        }

        return await runner.RunAsync(
            request.OperationId, UpdateLineOperation, context.GetIdentity(), new { noteId, lineId, request }, async operation =>
            {
                var note = await OpenForLinesAsync(operation, noteId, gate!);
                var line = await ReadLiveLineAsync(operation, noteId, lineId, request.ExpectedVersion);
                ValidateQuantity(line.ProductName, line.SoldByUnit, request.Quantity);
                ValidatePrice(request.PurchasePrice);

                var difference = request.Quantity - line.Quantity;
                if (difference == 0 && request.PurchasePrice == line.PurchasePrice)
                {
                    // Rien n'a changé : ni mouvement, ni historique, ni nouvelle version.
                    var unchanged = await ReadStockAsync(operation, line.ProductId, note.LocationId);
                    return await LineResultAsync(operation, noteId, lineId, unchanged, 0, unchanged);
                }

                // Seule la différence entre en stock ou en sort.
                var (before, after) = difference == 0
                    ? await ReadUnchangedStockAsync(operation, line.ProductId, note.LocationId)
                    : await MoveAsync(
                        operation, note, line, difference, StockMovementTypes.NoteCorrection,
                        available => $"Impossible de ramener « {line.ProductName} » de {StockEngine.Format(line.Quantity)} à "
                                     + $"{StockEngine.Format(request.Quantity)} : il n'en reste que {StockEngine.Format(available)} en stock à cet "
                                     + "emplacement, le reste est déjà parti (vendu ou transféré). Quantité minimale possible pour "
                                     + $"cette ligne : {StockEngine.Format(line.Quantity - available)}. Au-delà, corrigez par un ajustement d'inventaire.");

                await operation.Connection.ExecuteAsync(
                    """
                    UPDATE supplier_note_lines
                    SET quantity = @Quantity, purchase_price = @PurchasePrice, version = version + 1,
                        updated_by = @EmployeeId, updated_at = now()
                    WHERE id = @lineId
                    """,
                    new { lineId, request.Quantity, request.PurchasePrice, operation.EmployeeId },
                    operation.Transaction);

                // Une correction AJOUTE une entrée à l'historique des achats ; l'ancienne reste telle quelle.
                await RecordPurchaseAsync(operation, note, lineId, line.ProductId, CorrectionKind, request.PurchasePrice, request.Quantity);
                var changes = new
                {
                    ligne = lineId,
                    produit = line.ProductId,
                    quantiteAvant = line.Quantity,
                    quantiteApres = request.Quantity,
                    prixAchatAvant = line.PurchasePrice,
                    prixAchatApres = request.PurchasePrice,
                    stockAvant = before,
                    stockApres = after,
                    etatDuBon = note.Status,
                };
                await RecordEventAsync(
                    operation, noteId, SupplierNoteEvents.LineUpdated, note.Status, note.Status, lineId,
                    $"Ligne {line.Position} « {line.ProductName} » : quantité {StockEngine.Format(line.Quantity)} → "
                    + $"{StockEngine.Format(request.Quantity)}, prix d'achat {Money.ToDisplay(line.PurchasePrice)} → {Money.ToDisplay(request.PurchasePrice)}.",
                    changes);
                await operation.AuditAsync(SupplierNoteAuditActions.LineUpdated, "supplier_note", noteId.ToString(), changes);
                return await LineResultAsync(operation, noteId, lineId, before, difference, after);
            });
    }

    public async Task<IResult> CancelLineAsync(Guid noteId, Guid lineId, CancelSupplierNoteLineRequest request, HttpContext context)
    {
        var (gate, refusal) = await NoteAccess.OpenAsync(dataSource, context, noteId, NoteAccess.LineRights);
        if (refusal is not null)
        {
            return refusal;
        }

        return await runner.RunAsync(
            request.OperationId, CancelLineOperation, context.GetIdentity(), new { noteId, lineId, request }, async operation =>
            {
                var note = await OpenForLinesAsync(operation, noteId, gate!);
                var line = await ReadLiveLineAsync(operation, noteId, lineId, request.ExpectedVersion);

                // Mouvement compensatoire : toute la quantité de la ligne ressort du stock.
                var (before, after) = await MoveAsync(
                    operation, note, line, -line.Quantity, StockMovementTypes.NoteCancellation,
                    available => $"Impossible d'annuler la ligne « {line.ProductName} » ({StockEngine.Format(line.Quantity)}) : il n'en reste que "
                                 + $"{StockEngine.Format(available)} en stock à cet emplacement, le reste est déjà parti (vendu ou transféré). "
                                 + "Corrigez par un ajustement d'inventaire.");

                await operation.Connection.ExecuteAsync(
                    """
                    UPDATE supplier_note_lines
                    SET is_cancelled = true, version = version + 1, updated_by = @EmployeeId, updated_at = now()
                    WHERE id = @lineId
                    """,
                    new { lineId, operation.EmployeeId },
                    operation.Transaction);
                await RecordPurchaseAsync(operation, note, lineId, line.ProductId, CancellationKind, line.PurchasePrice, 0);
                var details = new
                {
                    ligne = lineId,
                    produit = line.ProductId,
                    quantiteAnnulee = line.Quantity,
                    prixAchat = line.PurchasePrice,
                    stockAvant = before,
                    stockApres = after,
                    etatDuBon = note.Status,
                };
                await RecordEventAsync(
                    operation, noteId, SupplierNoteEvents.LineCancelled, note.Status, note.Status, lineId,
                    $"Ligne {line.Position} « {line.ProductName} » annulée : {StockEngine.Format(line.Quantity)} retiré du stock.",
                    details);
                await operation.AuditAsync(SupplierNoteAuditActions.LineCancelled, "supplier_note", noteId.ToString(), details);
                return await LineResultAsync(operation, noteId, lineId, before, -line.Quantity, after);
            });
    }

    // ---------- Validation et annulation du bon ----------

    public async Task<IResult> ValidateAsync(Guid noteId, ValidateSupplierNoteRequest request, HttpContext context)
    {
        var (_, refusal) = await NoteAccess.OpenAsync(dataSource, context, noteId, _ => [Permissions.NotesValidate]);
        if (refusal is not null)
        {
            return refusal;
        }

        return await runner.RunAsync(request.OperationId, ValidateOperation, context.GetIdentity(), new { noteId, request }, async operation =>
        {
            var note = await SupplierNoteLocks.ReadForUpdateAsync(operation.Connection, operation.Transaction, noteId);
            if (note.Status == SupplierNoteStatuses.Cancelled)
            {
                throw SupplierNoteLocks.NoteCancelled();
            }

            // Déjà validé : un second appel est sans effet.
            if (note.Status == SupplierNoteStatuses.InProgress)
            {
                await locks.RequireHeldAsync(operation, note);
                var lineCount = await operation.Connection.ExecuteScalarAsync<int>(
                    "SELECT count(*)::int FROM supplier_note_lines WHERE note_id = @noteId AND NOT is_cancelled",
                    new { noteId },
                    operation.Transaction);
                if (lineCount == 0)
                {
                    throw new BusinessException(
                        SupplierNoteErrorCodes.EmptyNote, "Ce bon n'a aucune ligne : il ne peut pas être validé.", StatusCodes.Status409Conflict);
                }

                // Changement d'état seulement : le stock est déjà entré, ligne par ligne.
                await operation.Connection.ExecuteAsync(
                    "UPDATE supplier_notes SET status = @status, validated_by = @EmployeeId, validated_at = now() WHERE id = @noteId",
                    new { noteId, status = SupplierNoteStatuses.Validated, operation.EmployeeId },
                    operation.Transaction);
                await SupplierNoteLocks.ReleaseAsync(operation.Connection, operation.Transaction, noteId);
                await RecordEventAsync(
                    operation, noteId, SupplierNoteEvents.Validated, note.Status, SupplierNoteStatuses.Validated, null,
                    $"Bon validé ({lineCount} ligne(s)).", new { lignes = lineCount });
                await operation.AuditAsync(
                    SupplierNoteAuditActions.NoteValidated, "supplier_note", noteId.ToString(), new { numero = note.Number, lignes = lineCount });
            }

            return (await SupplierNoteQueries.ReadSummaryAsync(operation.Connection, operation.Transaction, noteId, operation.EmployeeId))!;
        });
    }

    public async Task<IResult> CancelNoteAsync(Guid noteId, CancelSupplierNoteRequest request, HttpContext context)
    {
        var (_, refusal) = await NoteAccess.OpenAsync(dataSource, context, noteId, _ => [Permissions.NotesCancel]);
        if (refusal is not null)
        {
            return refusal;
        }

        return await runner.RunAsync(request.OperationId, CancelNoteOperation, context.GetIdentity(), new { noteId, request }, async operation =>
        {
            var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
            if (reason?.Length > MaxReasonLength)
            {
                throw BusinessException.Invalid($"Le motif a {MaxReasonLength} caractères au plus.");
            }

            var note = await SupplierNoteLocks.ReadForUpdateAsync(operation.Connection, operation.Transaction, noteId);

            // Déjà annulé : un second appel est sans effet.
            if (note.Status != SupplierNoteStatuses.Cancelled)
            {
                await locks.RequireHeldAsync(operation, note);
                var lines = (await operation.Connection.QueryAsync<LiveLine>(
                    LiveLineSelect + " WHERE x.note_id = @noteId AND NOT x.is_cancelled ORDER BY x.position",
                    new { noteId },
                    operation.Transaction)).AsList();

                // Toutes les lignes sont compensées dans un seul appel au moteur : tout ou rien.
                try
                {
                    await engine.ApplyAsync(
                        operation.Connection, operation.Transaction, operation.EmployeeId, operation.OperationId,
                        lines.Select(line =>
                            StockChange.Move(line.ProductId, note.LocationId, -line.Quantity, StockMovementTypes.NoteCancellation)
                                with { SourceType = StockSource, SourceId = noteId.ToString(), Note = $"Bon n° {note.Number} annulé, ligne {line.Position}" })
                            .ToList());
                }
                catch (InsufficientStockException)
                {
                    throw await AlreadyGoneAsync(operation, note);
                }

                foreach (var line in lines)
                {
                    await RecordPurchaseAsync(operation, note, line.Id, line.ProductId, CancellationKind, line.PurchasePrice, 0);
                }

                await operation.Connection.ExecuteAsync(
                    "UPDATE supplier_notes SET status = @status, cancelled_by = @EmployeeId, cancelled_at = now() WHERE id = @noteId",
                    new { noteId, status = SupplierNoteStatuses.Cancelled, operation.EmployeeId },
                    operation.Transaction);
                await SupplierNoteLocks.ReleaseAsync(operation.Connection, operation.Transaction, noteId);

                var details = new
                {
                    numero = note.Number,
                    etatAvant = note.Status,
                    motif = reason,
                    lignes = lines.Select(line => new { ligne = line.Id, produit = line.ProductId, quantite = line.Quantity }),
                };
                await RecordEventAsync(
                    operation, noteId, SupplierNoteEvents.Cancelled, note.Status, SupplierNoteStatuses.Cancelled, null,
                    $"Bon annulé : {lines.Count} ligne(s) retirée(s) du stock." + (reason is null ? string.Empty : $" Motif : {reason}"),
                    details);
                await operation.AuditAsync(SupplierNoteAuditActions.NoteCancelled, "supplier_note", noteId.ToString(), details);
            }

            return (await SupplierNoteQueries.ReadSummaryAsync(operation.Connection, operation.Transaction, noteId, operation.EmployeeId))!;
        });
    }

    // ---------- Étapes communes ----------

    private const string LiveLineSelect =
        """
        SELECT x.id AS Id, x.position AS Position, x.product_id AS ProductId, p.name AS ProductName,
               p.sold_by_unit AS SoldByUnit, x.quantity AS Quantity, x.purchase_price AS PurchasePrice,
               x.version AS Version, x.is_cancelled AS IsCancelled
        FROM supplier_note_lines x JOIN products p ON p.id = x.product_id
        """;

    /// <summary>
    /// Verrouille le bon, puis contrôle dans l'ordre : son état, le droit voulu pour cet
    /// état (saisie ordinaire ou correction d'un bon validé) et le verrou de saisie.
    /// </summary>
    private async Task<HeldNote> OpenForLinesAsync(OperationContext operation, Guid noteId, NoteGate gate)
    {
        var note = await SupplierNoteLocks.ReadForUpdateAsync(operation.Connection, operation.Transaction, noteId);
        if (note.Status == SupplierNoteStatuses.Cancelled)
        {
            throw SupplierNoteLocks.NoteCancelled();
        }

        // L'état a pu changer depuis le contrôle des droits fait avant la transaction.
        if (!NoteAccess.LineRights(note.Status).Any(gate.Granted.Contains))
        {
            throw NoteAccess.Denied();
        }

        await locks.RequireHeldAsync(operation, note);
        return note;
    }

    private static async Task<LiveLine> ReadLiveLineAsync(OperationContext operation, Guid noteId, Guid lineId, int expectedVersion)
    {
        var line = await operation.Connection.QuerySingleOrDefaultAsync<LiveLine>(
            LiveLineSelect + " WHERE x.id = @lineId AND x.note_id = @noteId FOR UPDATE OF x",
            new { lineId, noteId },
            operation.Transaction) ?? throw BusinessException.NotFound("Ligne introuvable sur ce bon.");
        if (line.Version != expectedVersion)
        {
            throw new BusinessException(
                SupplierNoteErrorCodes.StaleVersion,
                "Cette ligne a été modifiée depuis son affichage. Rechargez le bon avant de recommencer.",
                StatusCodes.Status409Conflict);
        }

        return line.IsCancelled
            ? throw new BusinessException(
                SupplierNoteErrorCodes.LineCancelled, "Cette ligne est annulée : elle ne peut plus être modifiée.", StatusCodes.Status409Conflict)
            : line;
    }

    /// <summary>
    /// Applique la variation de stock d'une ligne. Si la marchandise est déjà partie, le
    /// refus indique la quantité encore présente (décision D4) et rien n'est modifié.
    /// </summary>
    private async Task<(decimal Before, decimal After)> MoveAsync(
        OperationContext operation, HeldNote note, LiveLine line, decimal quantity, string movementType, Func<decimal, string> refusalMessage)
    {
        try
        {
            var applied = (await engine.ApplyAsync(
                operation.Connection, operation.Transaction, operation.EmployeeId, operation.OperationId,
                [StockChange.Move(line.ProductId, note.LocationId, quantity, movementType)
                    with { SourceType = StockSource, SourceId = note.Id.ToString(), Note = $"Bon n° {note.Number}, ligne {line.Position}" }]))[0];
            return (applied.QuantityBefore, applied.QuantityAfter);
        }
        catch (InsufficientStockException exception)
        {
            throw new BusinessException(
                SupplierNoteErrorCodes.StockAlreadyGone, refusalMessage(exception.Available), StatusCodes.Status409Conflict);
        }
    }

    /// <summary>Refus d'annuler un bon dont une partie du stock est déjà partie, avec la liste des produits.</summary>
    private static async Task<BusinessException> AlreadyGoneAsync(OperationContext operation, HeldNote note)
    {
        // Les soldes sont déjà verrouillés par le moteur : ces quantités sont exactes.
        var missing = await operation.Connection.QueryAsync<(string Name, decimal Needed, decimal Available)>(
            """
            SELECT p.name, t.needed, COALESCE(sl.quantity, 0)
            FROM (SELECT product_id, sum(quantity) AS needed FROM supplier_note_lines
                  WHERE note_id = @Id AND NOT is_cancelled GROUP BY product_id) t
            JOIN products p ON p.id = t.product_id
            LEFT JOIN stock_levels sl ON sl.product_id = t.product_id AND sl.location_id = @LocationId
            WHERE COALESCE(sl.quantity, 0) < t.needed
            ORDER BY p.name
            """,
            new { note.Id, note.LocationId },
            operation.Transaction);
        var products = string.Join(
            " ; ",
            missing.Select(product =>
                $"« {product.Name} » ({StockEngine.Format(product.Needed)} reçu, {StockEngine.Format(product.Available)} encore en stock)"));
        return new BusinessException(
            SupplierNoteErrorCodes.StockAlreadyGone,
            "Annulation impossible : une partie de la marchandise de ce bon a déjà quitté le stock (vendue ou transférée). "
            + $"Produits concernés : {products}. Rien n'a été modifié.",
            StatusCodes.Status409Conflict);
    }

    private static async Task<(decimal Before, decimal After)> ReadUnchangedStockAsync(OperationContext operation, Guid productId, Guid locationId)
    {
        var quantity = await ReadStockAsync(operation, productId, locationId);
        return (quantity, quantity);
    }

    private static async Task<decimal> ReadStockAsync(OperationContext operation, Guid productId, Guid locationId) =>
        await operation.Connection.ExecuteScalarAsync<decimal?>(
            "SELECT quantity FROM stock_levels WHERE product_id = @productId AND location_id = @locationId",
            new { productId, locationId },
            operation.Transaction) ?? 0m;

    private static Task RecordPurchaseAsync(
        OperationContext operation, HeldNote note, Guid lineId, Guid productId, string kind, decimal purchasePrice, decimal quantity) =>
        operation.Connection.ExecuteAsync(
            """
            INSERT INTO purchase_history
                (product_id, supplier_id, note_id, line_id, note_date, kind, purchase_price, quantity, employee_id, operation_id)
            VALUES (@productId, @SupplierId, @Id, @lineId, @NoteDate::date, @kind, @purchasePrice, @quantity, @EmployeeId, @OperationId)
            """,
            new { productId, note.SupplierId, note.Id, lineId, note.NoteDate, kind, purchasePrice, quantity, operation.EmployeeId, operation.OperationId },
            operation.Transaction);

    private static Task RecordEventAsync(
        OperationContext operation, Guid noteId, string @event, string? statusBefore, string statusAfter, Guid? lineId,
        string description, object details) =>
        operation.Connection.ExecuteAsync(
            """
            INSERT INTO supplier_note_history
                (note_id, event, status_before, status_after, line_id, description, details, employee_id, operation_id)
            VALUES (@noteId, @event, @statusBefore, @statusAfter, @lineId, @description, @details::jsonb, @EmployeeId, @OperationId)
            """,
            new
            {
                noteId,
                @event,
                statusBefore,
                statusAfter,
                lineId,
                description,
                details = JsonSerializer.Serialize(details),
                operation.EmployeeId,
                operation.OperationId,
            },
            operation.Transaction);

    private static async Task<SupplierNoteLineResult> LineResultAsync(
        OperationContext operation, Guid noteId, Guid lineId, decimal stockBefore, decimal stockChange, decimal stockAfter)
    {
        var line = await SupplierNoteQueries.ReadLineAsync(operation.Connection, operation.Transaction, lineId);
        var totals = await operation.Connection.QuerySingleAsync<(int Count, decimal Total)>(
            """
            SELECT count(*)::int, COALESCE(sum(round(quantity * purchase_price, 2)), 0)
            FROM supplier_note_lines WHERE note_id = @noteId AND NOT is_cancelled
            """,
            new { noteId },
            operation.Transaction);
        return new SupplierNoteLineResult(line, stockBefore, stockChange, stockAfter, totals.Count, totals.Total);
    }

    private static void ValidateQuantity(string productName, bool soldByUnit, decimal quantity)
    {
        if (quantity is <= 0 or > MaxQuantity)
        {
            throw BusinessException.Invalid("La quantité d'une ligne doit être supérieure à zéro.");
        }

        if (decimal.Round(quantity, 3) != quantity)
        {
            throw BusinessException.Invalid("Une quantité a au plus 3 décimales.");
        }

        if (soldByUnit && quantity != decimal.Truncate(quantity))
        {
            throw new BusinessException(
                ApiErrorCodes.FractionalQuantity,
                $"« {productName} » se vend à l'unité : la quantité {StockEngine.Format(quantity)} n'est pas entière.");
        }
    }

    private static void ValidatePrice(decimal price)
    {
        if (price is < 0 or > MaxPrice || decimal.Round(price, 2) != price)
        {
            throw BusinessException.Invalid("Le prix d'achat doit être positif ou nul, avec au plus deux décimales.");
        }
    }

    private sealed class LiveLine
    {
        public Guid Id { get; init; }
        public int Position { get; init; }
        public Guid ProductId { get; init; }
        public string ProductName { get; init; } = string.Empty;
        public bool SoldByUnit { get; init; }
        public decimal Quantity { get; init; }
        public decimal PurchasePrice { get; init; }
        public int Version { get; init; }
        public bool IsCancelled { get; init; }
    }
}
