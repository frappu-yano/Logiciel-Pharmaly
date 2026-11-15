using Pharmaly.Poste.ViewModels;

namespace Pharmaly.Tests.Poste;

public sealed class SessionViewModelTests
{
    private readonly FakeServer server = new();

    [Fact]
    public void Un_poste_jamais_connecte_demande_le_compte_de_caisse() =>
        Assert.Equal(SessionStage.PosteSignIn, new SessionViewModel(server).Stage);

    [Fact]
    public void Un_poste_deja_connecte_demande_directement_l_employe()
    {
        server.HasPosteSession = true;

        Assert.Equal(SessionStage.EmployeeSignIn, new SessionViewModel(server).Stage);
    }

    [Fact]
    public async Task Les_deux_niveaux_menent_a_l_ecran_de_travail_et_les_secrets_sont_effaces()
    {
        var session = new SessionViewModel(server) { RegisterLogin = FakeServer.RegisterLogin, RegisterPassword = FakeServer.RegisterPassword };

        await session.SignInPosteAsync();
        Assert.Equal(SessionStage.EmployeeSignIn, session.Stage);
        Assert.Equal(string.Empty, session.RegisterPassword);

        session.EmployeeNumber = FakeServer.EmployeeNumber.ToString();
        session.EmployeeSecret = FakeServer.EmployeeSecret;
        await session.SignInEmployeeAsync();

        Assert.Equal(SessionStage.Work, session.Stage);
        Assert.Equal("Samia Test", session.EmployeeName);
        Assert.Equal(string.Empty, session.EmployeeSecret);
    }

    [Fact]
    public async Task Un_code_faux_laisse_sur_l_identification_avec_un_message()
    {
        server.HasPosteSession = true;
        var session = new SessionViewModel(server) { EmployeeNumber = "12", EmployeeSecret = "000000" };

        await session.SignInEmployeeAsync();

        Assert.Equal(SessionStage.EmployeeSignIn, session.Stage);
        Assert.Contains("incorrect", session.ErrorMessage);
        Assert.Equal(string.Empty, session.EmployeeSecret);
        Assert.Equal(string.Empty, session.EmployeeNumber);
    }

    [Fact]
    public async Task Une_connexion_de_poste_expiree_renvoie_au_compte_de_caisse()
    {
        server.HasPosteSession = true;
        var session = new SessionViewModel(server) { EmployeeNumber = "12", EmployeeSecret = FakeServer.EmployeeSecret };
        server.HasPosteSession = false;

        await session.SignInEmployeeAsync();

        Assert.Equal(SessionStage.PosteSignIn, session.Stage);
    }

    [Fact]
    public async Task Verrouiller_ferme_la_session_meme_si_le_serveur_est_coupe()
    {
        server.HasPosteSession = true;
        var session = new SessionViewModel(server) { EmployeeNumber = "12", EmployeeSecret = FakeServer.EmployeeSecret };
        await session.SignInEmployeeAsync();
        server.IsDown = true;

        await session.LockAsync();

        Assert.Equal(SessionStage.EmployeeSignIn, session.Stage);
        Assert.Equal(string.Empty, session.EmployeeName);
    }

    [Fact]
    public async Task Serveur_coupe_l_identification_l_explique()
    {
        server.HasPosteSession = true;
        server.IsDown = true;
        var session = new SessionViewModel(server) { EmployeeNumber = "12", EmployeeSecret = FakeServer.EmployeeSecret };

        await session.SignInEmployeeAsync();

        Assert.Contains("injoignable", session.ErrorMessage);
    }
}
