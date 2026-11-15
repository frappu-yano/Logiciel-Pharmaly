using Pharmaly.Poste.ViewModels;

namespace Pharmaly.Tests.Poste;

public sealed class MainViewModelTests
{
    private readonly FakeServer server = new() { HasPosteSession = true, EmployeeIdentified = true };

    private MainViewModel CreateViewModel() => new(server, TimeSpan.Zero);

    private static async Task ScanAsync(MainViewModel viewModel, string text)
    {
        viewModel.Query = text;
        await viewModel.SubmitAsync();
    }

    [Fact]
    public async Task Scanner_un_code_barres_ajoute_une_ligne_et_vide_la_saisie()
    {
        var viewModel = CreateViewModel();

        await ScanAsync(viewModel, FakeServer.Creme.Barcode!);

        var line = Assert.Single(viewModel.Lines);
        Assert.Equal(1, line.Quantity);
        Assert.Equal(string.Empty, viewModel.Query);
        Assert.Equal("1 850,10 DA", viewModel.TotalText);
    }

    [Fact]
    public async Task Scanner_deux_fois_le_meme_produit_augmente_la_quantite()
    {
        var viewModel = CreateViewModel();

        await ScanAsync(viewModel, FakeServer.Creme.Barcode!);
        await ScanAsync(viewModel, FakeServer.Creme.Barcode!);

        Assert.Equal(2, Assert.Single(viewModel.Lines).Quantity);
        Assert.Equal("2 articles", viewModel.ArticleCountText);
    }

    [Fact]
    public async Task Le_total_est_exact_au_centime()
    {
        var viewModel = CreateViewModel();

        for (var index = 0; index < 3; index++)
        {
            await ScanAsync(viewModel, FakeServer.Creme.Barcode!);
        }

        await ScanAsync(viewModel, FakeServer.Serum.Barcode!);

        Assert.Equal(3 * 1850.10m + 4200.20m, viewModel.Total);
        Assert.Equal("9 750,50 DA", viewModel.TotalText);
    }

    [Fact]
    public async Task Une_recherche_a_resultat_unique_ajoute_le_produit()
    {
        var viewModel = CreateViewModel();

        await ScanAsync(viewModel, "sérum");

        Assert.Equal(FakeServer.Serum.Id, Assert.Single(viewModel.Lines).Product.Id);
    }

    [Fact]
    public async Task Un_produit_inconnu_affiche_un_message_et_n_ajoute_rien()
    {
        var viewModel = CreateViewModel();

        await ScanAsync(viewModel, "0000000000000");

        Assert.Empty(viewModel.Lines);
        Assert.Contains("Aucun produit", viewModel.Notice);
    }

    [Fact]
    public async Task Diminuer_jusqu_a_zero_retire_la_ligne()
    {
        var viewModel = CreateViewModel();
        await ScanAsync(viewModel, FakeServer.Creme.Barcode!);

        viewModel.IncreaseSelectedLine();
        Assert.Equal(2, viewModel.Lines[0].Quantity);

        viewModel.DecreaseSelectedLine();
        viewModel.DecreaseSelectedLine();

        Assert.Empty(viewModel.Lines);
        Assert.Equal("0,00 DA", viewModel.TotalText);
    }

    [Fact]
    public async Task Serveur_coupe_le_poste_le_signale_sans_planter()
    {
        var viewModel = CreateViewModel();
        server.IsDown = true;

        await viewModel.CheckServerAsync();
        await ScanAsync(viewModel, FakeServer.Creme.Barcode!);

        Assert.False(viewModel.IsConnected);
        Assert.Contains("injoignable", viewModel.StatusText);
        Assert.Empty(viewModel.Lines);
    }

    [Fact]
    public async Task Un_refus_du_serveur_s_affiche_et_n_ajoute_rien()
    {
        var viewModel = CreateViewModel();
        server.DenyProducts = true;

        await ScanAsync(viewModel, FakeServer.Creme.Barcode!);

        Assert.Empty(viewModel.Lines);
        Assert.Contains("pas le droit", viewModel.Notice);
    }

    [Fact]
    public async Task Une_session_perdue_redemande_l_identification()
    {
        var viewModel = CreateViewModel();
        var asked = false;
        viewModel.IdentificationRequired += (_, _) => asked = true;
        server.EmployeeIdentified = false;

        await ScanAsync(viewModel, FakeServer.Creme.Barcode!);

        Assert.True(asked);
        Assert.Empty(viewModel.Lines);
    }

    [Fact]
    public async Task L_essai_de_rapidite_signale_un_serveur_absent()
    {
        var viewModel = CreateViewModel();
        server.IsDown = true;

        var report = await viewModel.ExecuteBenchmarkAsync();

        Assert.False(report.ServerReachable);
        Assert.Contains("ne répond pas", viewModel.BenchmarkText);
    }
}
