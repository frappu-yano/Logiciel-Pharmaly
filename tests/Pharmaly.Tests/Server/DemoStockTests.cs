using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Pharmaly.Server.Catalog;

namespace Pharmaly.Tests.Server;

public sealed class DemoStockTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    [Fact]
    public async Task Le_stock_fictif_passe_par_le_journal_et_ne_se_charge_que_sur_une_base_neuve()
    {
        await using (var connection = await server.DataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("TRUNCATE products CASCADE");
        }

        await server.Services.GetRequiredService<DemoDataSeeder>().SeedAsync(40);
        var seeder = server.Services.GetRequiredService<DemoStockSeeder>();

        Assert.Equal(40, await seeder.SeedAsync("D1", 25, ServerFixture.AdminNumber));
        await StockInvariant.AssertHoldsAsync(server.DataSource);
        await using (var connection = await server.DataSource.OpenConnectionAsync())
        {
            Assert.Equal(1000m, await connection.ExecuteScalarAsync<decimal>("SELECT sum(quantity) FROM stock_levels"));
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => seeder.SeedAsync("D1", 25, ServerFixture.AdminNumber));
    }
}
