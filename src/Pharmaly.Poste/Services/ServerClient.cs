using System.Net;
using System.Net.Http.Json;
using Pharmaly.Contracts;

namespace Pharmaly.Poste.Services;

/// <summary>Le serveur a compris la demande et l'a refusée (droit manquant, code faux…).</summary>
public sealed class ServerRefusedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public interface IServerClient
{
    bool HasPosteSession { get; }

    Task<HealthResponse?> GetHealthAsync(CancellationToken cancellationToken);

    Task<PosteSignInResponse> SignInPosteAsync(string login, string password, CancellationToken cancellationToken);

    Task<EmployeeSignInResponse> SignInEmployeeAsync(int employeeNumber, string secret, CancellationToken cancellationToken);

    Task SignOutEmployeeAsync(CancellationToken cancellationToken);

    void ForgetPosteSession();

    Task<IReadOnlyList<ProductSummary>> SearchProductsAsync(string term, CancellationToken cancellationToken);

    Task<ProductSummary?> FindByBarcodeAsync(string barcode, CancellationToken cancellationToken);
}

/// <summary>Dialogue avec le serveur du site, sur le réseau interne uniquement.</summary>
public sealed class ServerClient : IServerClient, IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(4);
    private readonly HttpClient http;
    private readonly IPosteTokenStore tokenStore;
    private string? posteToken;
    private string? employeeToken;

    public ServerClient(PosteSettings settings, IPosteTokenStore tokenStore)
    {
        this.tokenStore = tokenStore;
        posteToken = tokenStore.Load();
        var handler = new SocketsHttpHandler
        {
            // Le poste ne passe jamais par un proxy : le serveur est sur le réseau du site.
            UseProxy = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(3),
        };
        http = new HttpClient(handler) { BaseAddress = settings.ServerUrl, Timeout = RequestTimeout };
    }

    public bool HasPosteSession => posteToken is not null;

    public async Task<HealthResponse?> GetHealthAsync(CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(ApiRoutes.Health.TrimStart('/'), cancellationToken);
        return await response.Content.ReadFromJsonAsync<HealthResponse>(cancellationToken);
    }

    public async Task<PosteSignInResponse> SignInPosteAsync(string login, string password, CancellationToken cancellationToken)
    {
        var signedIn = await SendAsync<PosteSignInResponse>(
            HttpMethod.Post, ApiRoutes.PosteSignIn, new PosteSignInRequest(login, password), cancellationToken);
        posteToken = signedIn!.Token;
        employeeToken = null;
        tokenStore.Save(posteToken);
        return signedIn;
    }

    public async Task<EmployeeSignInResponse> SignInEmployeeAsync(int employeeNumber, string secret, CancellationToken cancellationToken)
    {
        var signedIn = await SendAsync<EmployeeSignInResponse>(
            HttpMethod.Post, ApiRoutes.EmployeeSignIn, new EmployeeSignInRequest(employeeNumber, secret), cancellationToken);
        employeeToken = signedIn!.Token;
        return signedIn;
    }

    public async Task SignOutEmployeeAsync(CancellationToken cancellationToken)
    {
        if (employeeToken is null)
        {
            return;
        }

        try
        {
            await SendAsync<object>(HttpMethod.Post, ApiRoutes.EmployeeSignOut, null, cancellationToken);
        }
        finally
        {
            employeeToken = null;
        }
    }

    public void ForgetPosteSession()
    {
        posteToken = null;
        employeeToken = null;
        tokenStore.Clear();
    }

    public async Task<IReadOnlyList<ProductSummary>> SearchProductsAsync(string term, CancellationToken cancellationToken) =>
        await SendAsync<List<ProductSummary>>(
            HttpMethod.Get, $"{ApiRoutes.ProductSearch}?q={Uri.EscapeDataString(term)}", null, cancellationToken) ?? [];

    public async Task<ProductSummary?> FindByBarcodeAsync(string barcode, CancellationToken cancellationToken) =>
        await SendAsync<ProductSummary>(
            HttpMethod.Get, $"{ApiRoutes.ProductByBarcode}/{Uri.EscapeDataString(barcode)}", null, cancellationToken,
            notFoundIsNull: true);

    private async Task<T?> SendAsync<T>(
        HttpMethod method, string route, object? body, CancellationToken cancellationToken, bool notFoundIsNull = false)
        where T : class
    {
        using var request = new HttpRequestMessage(method, route.TrimStart('/'));
        if (posteToken is not null)
        {
            request.Headers.Add(ApiHeaders.PosteToken, posteToken);
        }

        if (employeeToken is not null)
        {
            request.Headers.Add(ApiHeaders.EmployeeToken, employeeToken);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        using var response = await http.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return response.StatusCode == HttpStatusCode.NoContent
                ? null
                : await response.Content.ReadFromJsonAsync<T>(cancellationToken);
        }

        if (notFoundIsNull && response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        var error = await ReadErrorAsync(response, cancellationToken);
        throw new ServerRefusedException(error.Code, error.Message);
    }

    private static async Task<ApiError> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<ApiError>(cancellationToken)
                ?? new ApiError("ERREUR", $"Le serveur a répondu {(int)response.StatusCode}.");
        }
        catch (System.Text.Json.JsonException)
        {
            return new ApiError("ERREUR", $"Le serveur a répondu {(int)response.StatusCode}.");
        }
    }

    public void Dispose() => http.Dispose();
}
