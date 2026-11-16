using Pharmaly.Contracts;
using Pharmaly.Poste.Services.Server;

namespace Pharmaly.Tests.Poste;

/// <summary>La fermeture de caisse et la clôture n'ajoutent que leurs routes au transport.</summary>
public sealed class ClosuresClientTests
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
    public async Task La_fermeture_de_caisse_et_la_cloture_utilisent_les_routes_du_serveur()
    {
        var closures = new ClosuresClient(transport);
        var closureId = Guid.Parse("0b1c2d3e-4f50-4617-8293-a4b5c6d7e8f9");
        var closeSession = new CloseCashSessionRequest(Guid.NewGuid(), 6450m);
        var closeDay = new CloseDayRequest(Guid.NewGuid(), new DateOnly(2026, 11, 16));

        await new CashSessionClient(transport).CloseSessionAsync(closeSession, CancellationToken.None);
        await closures.GetCurrentDayAsync(CancellationToken.None);
        await closures.CloseDayAsync(closeDay, CancellationToken.None);
        var history = await closures.GetClosuresAsync(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 16), 60, CancellationToken.None);
        await closures.GetClosureAsync(closureId, CancellationToken.None);

        Assert.Empty(history);
        Assert.Equal(
            [
                (HttpMethod.Post, "/caisse/session/fermeture", closeSession, false),
                (HttpMethod.Get, "/cloture/journee", null, false),
                (HttpMethod.Post, "/cloture", closeDay, false),
                (HttpMethod.Get, "/cloture?du=2026-11-01&au=2026-11-16&limite=60", null, false),
                (HttpMethod.Get, $"/cloture/{closureId}", null, true),
            ],
            transport.Sent);
    }

    [Fact]
    public void L_historique_sans_filtre_n_a_aucun_parametre()
    {
        Assert.Equal(string.Empty, ClosuresClient.ToQueryString(null, null, null));
        Assert.Equal("?limite=10", ClosuresClient.ToQueryString(null, null, 10));
    }
}
