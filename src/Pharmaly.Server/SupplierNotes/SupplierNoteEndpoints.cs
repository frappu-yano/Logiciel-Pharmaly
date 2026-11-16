using Npgsql;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Server.Access;

namespace Pharmaly.Server.SupplierNotes;

/// <summary>
/// Bons fournisseurs : fournisseurs, bons, verrou de saisie, lignes, validation, annulation.
/// <para>
/// Les routes dont le droit dépend du bon (son site, son état) portent « RequireEmployee »
/// et font leur contrôle avec <see cref="NoteAccess"/> : le droit doit couvrir le site du
/// poste et celui de l'emplacement du bon, et tout refus est audité.
/// </para>
/// </summary>
public static class SupplierNoteEndpoints
{
    public static void MapSupplierNoteEndpoints(this WebApplication app)
    {
        const string Note = SupplierNoteRoutes.Notes + "/{id:guid}";
        const string Line = Note + "/" + SupplierNoteRoutes.LinesSuffix + "/{lineId:guid}";

        // ---------- Fournisseurs ----------

        app.MapGet(SupplierNoteRoutes.Suppliers, async (string? q, HttpContext context, NpgsqlDataSource dataSource, SupplierNoteQueries queries) =>
            await NoteAccess.RefuseUnlessAnyAsync(dataSource, context, NoteAccess.ReadRights)
            ?? Results.Ok(await queries.ListSuppliersAsync(q)))
            .RequireEmployee();

        // Créer un fournisseur à la volée pendant la saisie d'un bon : droit de saisir.
        app.MapPost(SupplierNoteRoutes.Suppliers, (CreateSupplierRequest request, HttpContext context, SupplierNoteCommands commands) =>
            commands.CreateSupplierAsync(request, context))
            .RequirePermission(Permissions.NotesEnter);

        // ---------- Produit proposé à la saisie, avec son dernier prix d'achat ----------

        app.MapGet(SupplierNoteRoutes.ProductByBarcode + "/{barcode}", async (
            string barcode, HttpContext context, NpgsqlDataSource dataSource, SupplierNoteQueries queries) =>
            await NoteAccess.RefuseUnlessAnyAsync(dataSource, context, NoteAccess.ReadRights)
            ?? (await queries.FindProductByBarcodeAsync(barcode) is { } product
                ? Results.Ok(product)
                : Results.Json(
                    new ApiError(SupplierNoteErrorCodes.UnknownBarcode, $"Aucun produit ne porte le code-barres {barcode.Trim()}."),
                    statusCode: StatusCodes.Status404NotFound)))
            .RequireEmployee();

        app.MapGet(SupplierNoteRoutes.Products + "/{productId:guid}", async (
            Guid productId, HttpContext context, NpgsqlDataSource dataSource, SupplierNoteQueries queries) =>
            await NoteAccess.RefuseUnlessAnyAsync(dataSource, context, NoteAccess.ReadRights)
            ?? (await queries.FindProductAsync(productId) is { } product
                ? Results.Ok(product)
                : Results.Json(new ApiError(ApiErrorCodes.NotFound, "Produit introuvable."), statusCode: StatusCodes.Status404NotFound)))
            .RequireEmployee();

        // ---------- Bons : lecture ----------

        app.MapGet(SupplierNoteRoutes.Notes, async (
            string? etat, Guid? fournisseur, DateOnly? du, DateOnly? au, int? page, int? taille,
            HttpContext context, NpgsqlDataSource dataSource, SupplierNoteQueries queries) =>
        {
            if (await NoteAccess.RefuseUnlessAnyAsync(dataSource, context, NoteAccess.ReadRights) is { } refusal)
            {
                return refusal;
            }

            var status = string.IsNullOrWhiteSpace(etat) ? null : etat.Trim().ToUpperInvariant();
            if (status is not null && !SupplierNoteStatuses.IsKnown(status))
            {
                return Results.Json(
                    new ApiError(ApiErrorCodes.InvalidRequest, $"État de bon inconnu : {etat}."), statusCode: StatusCodes.Status400BadRequest);
            }

            return Results.Ok(await queries.ListAsync(
                context.GetIdentity().RequireEmployeeId(), status, fournisseur, du, au,
                page ?? 1, taille ?? SupplierNoteQueries.DefaultPageSize));
        }).RequireEmployee();

        app.MapGet(Note, async (Guid id, HttpContext context, NpgsqlDataSource dataSource, SupplierNoteQueries queries) =>
            (await NoteAccess.OpenAsync(dataSource, context, id, _ => NoteAccess.ReadRights)).Refusal
            ?? Results.Ok(await queries.ReadDetailAsync(id, context.GetIdentity().RequireEmployeeId())))
            .RequireEmployee();

        // ---------- Bons : écriture ----------

        app.MapPost(SupplierNoteRoutes.Notes, (CreateSupplierNoteRequest request, HttpContext context, SupplierNoteCommands commands) =>
            commands.CreateNoteAsync(request, context))
            .RequirePermission(Permissions.NotesEnter);

        app.MapPost(Note + "/" + SupplierNoteRoutes.LinesSuffix, (
            Guid id, AddSupplierNoteLineRequest request, HttpContext context, SupplierNoteCommands commands) =>
            commands.AddLineAsync(id, request, context))
            .RequireEmployee();

        app.MapPut(Line, (
            Guid id, Guid lineId, UpdateSupplierNoteLineRequest request, HttpContext context, SupplierNoteCommands commands) =>
            commands.UpdateLineAsync(id, lineId, request, context))
            .RequireEmployee();

        app.MapPost(Line + "/" + SupplierNoteRoutes.CancellationSuffix, (
            Guid id, Guid lineId, CancelSupplierNoteLineRequest request, HttpContext context, SupplierNoteCommands commands) =>
            commands.CancelLineAsync(id, lineId, request, context))
            .RequireEmployee();

        app.MapPost(Note + "/" + SupplierNoteRoutes.ValidationSuffix, (
            Guid id, ValidateSupplierNoteRequest request, HttpContext context, SupplierNoteCommands commands) =>
            commands.ValidateAsync(id, request, context))
            .RequireEmployee();

        app.MapPost(Note + "/" + SupplierNoteRoutes.CancellationSuffix, (
            Guid id, CancelSupplierNoteRequest request, HttpContext context, SupplierNoteCommands commands) =>
            commands.CancelNoteAsync(id, request, context))
            .RequireEmployee();

        // ---------- Verrou de saisie ----------
        // Prendre, renouveler ou libérer : n'importe quel droit d'écriture sur les bons.

        app.MapPost(Note + "/" + SupplierNoteRoutes.LockSuffix, async (
            Guid id, HttpContext context, NpgsqlDataSource dataSource, SupplierNoteLocks locks) =>
            (await NoteAccess.OpenAsync(dataSource, context, id, _ => NoteAccess.WriteRights)).Refusal
            ?? await locks.AcquireAsync(id, context.GetIdentity()))
            .RequireEmployee();

        app.MapPut(Note + "/" + SupplierNoteRoutes.LockSuffix, async (
            Guid id, HttpContext context, NpgsqlDataSource dataSource, SupplierNoteLocks locks) =>
            (await NoteAccess.OpenAsync(dataSource, context, id, _ => NoteAccess.WriteRights)).Refusal
            ?? await locks.RenewAsync(id, context.GetIdentity()))
            .RequireEmployee();

        app.MapDelete(Note + "/" + SupplierNoteRoutes.LockSuffix, async (
            Guid id, HttpContext context, NpgsqlDataSource dataSource, SupplierNoteLocks locks) =>
            (await NoteAccess.OpenAsync(dataSource, context, id, _ => NoteAccess.WriteRights)).Refusal
            ?? await locks.ReleaseAsync(id, context.GetIdentity()))
            .RequireEmployee();

        app.MapPost(Note + "/" + SupplierNoteRoutes.ForcedReleaseSuffix, async (
            Guid id, HttpContext context, NpgsqlDataSource dataSource, SupplierNoteLocks locks) =>
            (await NoteAccess.OpenAsync(dataSource, context, id, _ => [Permissions.NotesEdit])).Refusal
            ?? await locks.ForceReleaseAsync(id, context.GetIdentity()))
            .RequireEmployee();
    }
}
