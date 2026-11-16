using Pharmaly.Contracts;

namespace Pharmaly.Poste.Services.Server;

/// <summary>Module « clients » : recherche, création rapide et fiche avec ses créances.</summary>
public interface ICustomersClient
{
    /// <summary>Clients dont le nom contient chaque mot saisi (ou dont le téléphone contient les chiffres saisis).</summary>
    Task<IReadOnlyList<CustomerSummary>> SearchCustomersAsync(string term, CancellationToken cancellationToken);

    /// <summary>Création rapide : nom obligatoire, téléphone facultatif.</summary>
    Task<CustomerSummary> CreateCustomerAsync(CreateCustomerRequest request, CancellationToken cancellationToken);

    /// <summary>Fiche d'un client avec ses créances ; <c>null</c> s'il n'existe pas. Demande le droit de consulter les impayés.</summary>
    Task<CustomerDetail?> GetCustomerAsync(Guid customerId, CancellationToken cancellationToken);
}

public sealed class CustomersClient(IServerTransport transport) : ICustomersClient
{
    public async Task<IReadOnlyList<CustomerSummary>> SearchCustomersAsync(string term, CancellationToken cancellationToken) =>
        await transport.SendAsync<List<CustomerSummary>>(
            HttpMethod.Get, $"{CustomerRoutes.CustomerSearch}?q={Uri.EscapeDataString(term)}", null, cancellationToken) ?? [];

    public async Task<CustomerSummary> CreateCustomerAsync(CreateCustomerRequest request, CancellationToken cancellationToken) =>
        (await transport.SendAsync<CustomerSummary>(HttpMethod.Post, CustomerRoutes.Customers, request, cancellationToken))!;

    public Task<CustomerDetail?> GetCustomerAsync(Guid customerId, CancellationToken cancellationToken) =>
        transport.SendAsync<CustomerDetail>(
            HttpMethod.Get, CustomerRoutes.Customer(customerId), null, cancellationToken, notFoundIsNull: true);
}
