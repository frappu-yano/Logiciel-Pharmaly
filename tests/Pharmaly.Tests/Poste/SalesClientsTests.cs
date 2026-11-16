using Pharmaly.Contracts;
using Pharmaly.Poste.Services.Server;

namespace Pharmaly.Tests.Poste;

/// <summary>Les clients ventes, clients et session de caisse n'ajoutent que leurs routes au transport.</summary>
public sealed class SalesClientsTests
{
    private sealed class RecordingTransport : IServerTransport
    {
        public List<(HttpMethod Method, string Route, object? Body, bool NotFoundIsNull)> Sent { get; } = [];

        public bool HasPosteToken => true;

        public void SetPosteToken(string token)
        {
        }

        public void SetEmployeeToken(string? token)
        {
        }

        public void ForgetTokens()
        {
        }

        public Task<T?> SendAsync<T>(
            HttpMethod method, string route, object? body, CancellationToken cancellationToken, bool notFoundIsNull = false)
            where T : class
        {
            Sent.Add((method, route, body, notFoundIsNull));
            return Task.FromResult<T?>(null);
        }

        public Task SendAsync(HttpMethod method, string route, object? body, CancellationToken cancellationToken)
        {
            Sent.Add((method, route, body, false));
            return Task.CompletedTask;
        }

        public Task<T?> GetWhateverStatusAsync<T>(string route, CancellationToken cancellationToken)
            where T : class => Task.FromResult<T?>(null);
    }

    private readonly RecordingTransport transport = new();

    [Fact]
    public async Task Les_ventes_utilisent_les_routes_du_serveur()
    {
        var client = new SalesClient(transport);
        var saleId = Guid.Parse("6f0a3c0e-6f0e-4f43-9f6e-0d3b8c1d2a11");
        var create = new CreateSaleRequest(Guid.NewGuid(), [], SalePaymentModes.Cash);
        var card = new CardResultRequest(Guid.NewGuid(), true);
        var cancel = new CancelSaleRequest(Guid.NewGuid(), "Erreur");

        await client.CreateSaleAsync(create, CancellationToken.None);
        await client.ResolveCardAsync(saleId, card, CancellationToken.None);
        await client.CancelSaleAsync(saleId, cancel, CancellationToken.None);
        await client.GetSaleAsync(saleId, CancellationToken.None);
        await client.FindSaleByTicketAsync("D1-000012", CancellationToken.None);
        await client.ListSalesAsync(new SalesHistoryQuery(), CancellationToken.None);

        Assert.Equal(
            [
                (HttpMethod.Post, "/ventes", create, false),
                (HttpMethod.Post, $"/ventes/{saleId}/tpe", card, false),
                (HttpMethod.Post, $"/ventes/{saleId}/annulation", cancel, false),
                (HttpMethod.Get, $"/ventes/{saleId}", null, true),
                (HttpMethod.Get, "/ventes/ticket/D1-000012", null, true),
                (HttpMethod.Get, "/ventes", null, false),
            ],
            transport.Sent);
    }

    [Fact]
    public void Les_filtres_de_l_historique_deviennent_les_parametres_attendus_par_le_serveur()
    {
        var query = new SalesHistoryQuery(
            new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 16), SaleStatuses.CardPending, " tk 12 ", SalePaymentModes.Card, 50);

        Assert.Equal(
            "?du=2026-11-01&au=2026-11-16&statut=EN_ATTENTE_TPE&mode=TPE&numero=tk%2012&limite=50",
            SalesClient.ToQueryString(query));
        Assert.Equal(string.Empty, SalesClient.ToQueryString(new SalesHistoryQuery()));
    }

    [Fact]
    public async Task Les_clients_et_la_session_de_caisse_utilisent_les_routes_du_serveur()
    {
        var customers = new CustomersClient(transport);
        var cash = new CashSessionClient(transport);
        var customerId = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
        var create = new CreateCustomerRequest(Guid.NewGuid(), "Nadia Benali", "0550 12 34 56");
        var open = new OpenCashSessionRequest(Guid.NewGuid(), 5000m);

        await customers.SearchCustomersAsync("ben ali", CancellationToken.None);
        await customers.CreateCustomerAsync(create, CancellationToken.None);
        await customers.GetCustomerAsync(customerId, CancellationToken.None);
        Assert.Null(await cash.GetCurrentSessionAsync(CancellationToken.None));
        await cash.OpenSessionAsync(open, CancellationToken.None);

        Assert.Equal(
            [
                (HttpMethod.Get, "/clients/recherche?q=ben%20ali", null, false),
                (HttpMethod.Post, "/clients", create, false),
                (HttpMethod.Get, $"/clients/{customerId}", null, true),
                (HttpMethod.Get, "/caisse/session", null, false),
                (HttpMethod.Post, "/caisse/session/ouverture", open, false),
            ],
            transport.Sent);
    }
}
