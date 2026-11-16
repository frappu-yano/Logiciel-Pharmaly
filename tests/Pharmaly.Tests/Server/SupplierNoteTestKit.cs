using System.Net;
using System.Net.Http.Json;
using Dapper;
using Pharmaly.Contracts;

namespace Pharmaly.Tests.Server;

/// <summary>Gestes communs aux tests des bons fournisseurs.</summary>
public sealed class SupplierNoteTestKit(ServerFixture server)
{
    /// <summary>Code personnel donné par <see cref="StockTestKit"/> aux employés qu'il crée.</summary>
    public const string EmployeeSecret = "482615";

    private static int nextSupplier;

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Réponse {(int)response.StatusCode} : {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    public async Task<SupplierSummary> CreateSupplierAsync(string? name = null) =>
        await ReadAsync<SupplierSummary>(await server.AdminClient.PostAsJsonAsync(
            SupplierNoteRoutes.Suppliers,
            new CreateSupplierRequest(Guid.NewGuid(), name ?? $"Fournisseur d'essai {Interlocked.Increment(ref nextSupplier)} {Guid.NewGuid():N}")));

    public static Task<HttpResponseMessage> PostNoteAsync(
        HttpClient client, Guid supplierId, Guid locationId, string reference = "BL-ESSAI", DateOnly? date = null, Guid? operationId = null) =>
        client.PostAsJsonAsync(
            SupplierNoteRoutes.Notes,
            new CreateSupplierNoteRequest(operationId ?? Guid.NewGuid(), supplierId, locationId, reference, date ?? new DateOnly(2026, 11, 16)));

    /// <summary>Nouveau bon en saisie, créé (et donc tenu) par ce client.</summary>
    public async Task<SupplierNoteDetail> CreateNoteAsync(HttpClient client, Guid locationId, Guid? supplierId = null, DateOnly? date = null) =>
        await ReadAsync<SupplierNoteDetail>(
            await PostNoteAsync(client, supplierId ?? (await CreateSupplierAsync()).Id, locationId, date: date));

    public static Task<HttpResponseMessage> AddLineAsync(
        HttpClient client, Guid noteId, Guid productId, decimal quantity, decimal price = 800m, Guid? operationId = null) =>
        client.PostAsJsonAsync(
            SupplierNoteRoutes.Lines(noteId), new AddSupplierNoteLineRequest(operationId ?? Guid.NewGuid(), productId, quantity, price));

    public static async Task<SupplierNoteLineResult> AddLineOkAsync(
        HttpClient client, Guid noteId, Guid productId, decimal quantity, decimal price = 800m) =>
        await ReadAsync<SupplierNoteLineResult>(await AddLineAsync(client, noteId, productId, quantity, price));

    public static Task<HttpResponseMessage> UpdateLineAsync(
        HttpClient client, Guid noteId, Guid lineId, int expectedVersion, decimal quantity, decimal price = 800m, Guid? operationId = null) =>
        client.PutAsJsonAsync(
            SupplierNoteRoutes.Line(noteId, lineId),
            new UpdateSupplierNoteLineRequest(operationId ?? Guid.NewGuid(), expectedVersion, quantity, price));

    public static Task<HttpResponseMessage> CancelLineAsync(
        HttpClient client, Guid noteId, Guid lineId, int expectedVersion, Guid? operationId = null) =>
        client.PostAsJsonAsync(
            SupplierNoteRoutes.LineCancellation(noteId, lineId),
            new CancelSupplierNoteLineRequest(operationId ?? Guid.NewGuid(), expectedVersion));

    public static Task<HttpResponseMessage> ValidateAsync(HttpClient client, Guid noteId, Guid? operationId = null) =>
        client.PostAsJsonAsync(SupplierNoteRoutes.Validation(noteId), new ValidateSupplierNoteRequest(operationId ?? Guid.NewGuid()));

    public static Task<HttpResponseMessage> CancelNoteAsync(HttpClient client, Guid noteId, Guid? operationId = null, string? reason = null) =>
        client.PostAsJsonAsync(SupplierNoteRoutes.Cancellation(noteId), new CancelSupplierNoteRequest(operationId ?? Guid.NewGuid(), reason));

    public static Task<HttpResponseMessage> AcquireAsync(HttpClient client, Guid noteId) =>
        client.PostAsync(SupplierNoteRoutes.Lock(noteId), null);

    public static Task<HttpResponseMessage> RenewAsync(HttpClient client, Guid noteId) =>
        client.PutAsync(SupplierNoteRoutes.Lock(noteId), null);

    public static Task<HttpResponseMessage> ReleaseAsync(HttpClient client, Guid noteId) =>
        client.DeleteAsync(SupplierNoteRoutes.Lock(noteId));

    public static Task<HttpResponseMessage> ForceReleaseAsync(HttpClient client, Guid noteId) =>
        client.PostAsync(SupplierNoteRoutes.ForcedRelease(noteId), null);

    public static async Task<SupplierNoteDetail> ReadNoteAsync(HttpClient client, Guid noteId) =>
        await ReadAsync<SupplierNoteDetail>(await client.GetAsync(SupplierNoteRoutes.Note(noteId)));

    public static async Task<NoteProductInfo> ReadProductAsync(HttpClient client, Guid productId) =>
        await ReadAsync<NoteProductInfo>(await client.GetAsync(SupplierNoteRoutes.ProductInfo(productId)));

    public async Task<T> ScalarAsync<T>(string sql, object? parameters = null)
    {
        await using var connection = await server.DataSource.OpenConnectionAsync();
        return (await connection.ExecuteScalarAsync<T>(sql, parameters))!;
    }

    public Task<int> CountNoteMovementsAsync(Guid noteId, string? type = null) =>
        ScalarAsync<int>(
            """
            SELECT count(*)::int FROM stock_movements
            WHERE source_type = 'BON' AND source_id = @source AND (@type::text IS NULL OR movement_type = @type)
            """,
            new { source = noteId.ToString(), type });

    public Task<int> CountPurchasesAsync(Guid productId) =>
        ScalarAsync<int>("SELECT count(*)::int FROM purchase_history WHERE product_id = @productId", new { productId });

    public Task<int> CountNoteAuditAsync(string action, Guid noteId) =>
        ScalarAsync<int>(
            "SELECT count(*)::int FROM audit_logs WHERE action = @action AND entity_type = 'supplier_note' AND entity_id = @id",
            new { action, id = noteId.ToString() });
}
