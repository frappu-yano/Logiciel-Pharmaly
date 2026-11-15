using System.Net;
using System.Net.Http.Json;
using Pharmaly.Contracts;

namespace Pharmaly.Poste.Services;

public interface IServerClient
{
    Task<HealthResponse?> GetHealthAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ProductSummary>> SearchProductsAsync(string term, CancellationToken cancellationToken);

    Task<ProductSummary?> FindByBarcodeAsync(string barcode, CancellationToken cancellationToken);
}

/// <summary>Dialogue avec le serveur du site, sur le réseau interne uniquement.</summary>
public sealed class ServerClient : IServerClient, IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(4);
    private readonly HttpClient http;

    public ServerClient(PosteSettings settings)
    {
        var handler = new SocketsHttpHandler
        {
            // Le poste ne passe jamais par un proxy : le serveur est sur le réseau du site.
            UseProxy = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(2),
        };
        http = new HttpClient(handler) { BaseAddress = settings.ServerUrl, Timeout = RequestTimeout };
    }

    public async Task<HealthResponse?> GetHealthAsync(CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(ApiRoutes.Health.TrimStart('/'), cancellationToken);
        return await response.Content.ReadFromJsonAsync<HealthResponse>(cancellationToken);
    }

    public async Task<IReadOnlyList<ProductSummary>> SearchProductsAsync(string term, CancellationToken cancellationToken)
    {
        var route = $"{ApiRoutes.ProductSearch.TrimStart('/')}?q={Uri.EscapeDataString(term)}";
        return await http.GetFromJsonAsync<List<ProductSummary>>(route, cancellationToken) ?? [];
    }

    public async Task<ProductSummary?> FindByBarcodeAsync(string barcode, CancellationToken cancellationToken)
    {
        var route = $"{ApiRoutes.ProductByBarcode.TrimStart('/')}/{Uri.EscapeDataString(barcode)}";
        using var response = await http.GetAsync(route, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ProductSummary>(cancellationToken);
    }

    public void Dispose() => http.Dispose();
}
