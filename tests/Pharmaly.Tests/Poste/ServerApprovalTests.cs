using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Pharmaly.Contracts;
using Pharmaly.Poste;
using Pharmaly.Poste.Services.Server;
using Pharmaly.Poste.ViewModels;
using Pharmaly.Poste.Views;

namespace Pharmaly.Tests.Poste;

/// <summary>
/// Première connexion d'un poste : l'installateur compare l'empreinte du serveur et
/// l'approuve avant que le compte de caisse soit demandé.
/// </summary>
public sealed class ServerApprovalTests
{
    private const string Fingerprint = "9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A08";
    private const string LaterFingerprint = "00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF";

    private readonly FakeServer server = new();
    private readonly FakeTrust trust = new() { ApprovalNeeded = true, PresentedFingerprint = Fingerprint };

    [Fact]
    public async Task Tant_que_le_serveur_n_est_pas_approuve_le_compte_de_caisse_n_est_ni_demande_ni_envoye()
    {
        server.HasPosteSession = true;
        var session = new SessionViewModel(server, trust) { RegisterLogin = FakeServer.RegisterLogin, RegisterPassword = FakeServer.RegisterPassword };
        server.HasPosteSession = false;

        Assert.Equal(SessionStage.PosteSignIn, session.Stage);
        Assert.True(session.ServerApprovalNeeded);
        Assert.False(session.IsRegisterAccountAsked);

        await session.SignInPosteAsync();

        Assert.False(server.HasPosteSession);
        Assert.Equal(SessionStage.PosteSignIn, session.Stage);
    }

    [Fact]
    public async Task L_empreinte_recue_est_affichee_en_groupes_et_rien_n_est_approuve_sans_geste_de_l_installateur()
    {
        var session = new SessionViewModel(server, trust);
        Assert.False(session.HasServerFingerprint);

        await session.ReadServerFingerprintAsync();

        Assert.Equal(CertificateFingerprint.Format(Fingerprint), session.ServerFingerprint);
        Assert.True(session.ServerApprovalNeeded);
        Assert.Null(trust.Approved);
    }

    [Fact]
    public async Task Approuver_enregistre_l_empreinte_affichee_et_pas_une_autre_arrivee_entre_temps()
    {
        var session = new SessionViewModel(server, trust);
        await session.ReadServerFingerprintAsync();
        // Un autre serveur répond entre l'affichage et l'approbation : ce n'est pas lui que l'installateur a comparé.
        trust.PresentedFingerprint = LaterFingerprint;

        session.ApproveServer();

        Assert.Equal(Fingerprint, trust.Approved);
        Assert.False(session.ServerApprovalNeeded);
        Assert.True(session.IsRegisterAccountAsked);
        Assert.Equal(SessionStage.PosteSignIn, session.Stage);
    }

    [Fact]
    public void Sans_empreinte_affichee_il_n_y_a_rien_a_approuver()
    {
        var session = new SessionViewModel(server, trust);

        session.ApproveServer();

        Assert.Null(trust.Approved);
        Assert.True(session.ServerApprovalNeeded);
    }

    [Fact]
    public async Task Serveur_injoignable_aucune_empreinte_n_est_proposee()
    {
        var session = new SessionViewModel(server, trust);
        await session.ReadServerFingerprintAsync();
        server.IsDown = true;

        await session.ReadServerFingerprintAsync();
        session.ApproveServer();

        Assert.False(session.HasServerFingerprint);
        Assert.Contains("injoignable", session.ErrorMessage);
        Assert.Null(trust.Approved);
    }

    [Fact]
    public async Task Un_poste_deja_connecte_passe_a_l_identification_une_fois_le_serveur_approuve()
    {
        server.HasPosteSession = true;
        var session = new SessionViewModel(server, trust);
        await session.ReadServerFingerprintAsync();

        session.ApproveServer();

        Assert.Equal(SessionStage.EmployeeSignIn, session.Stage);
    }

    [Fact]
    public void Un_poste_dont_le_serveur_est_deja_approuve_ne_redemande_rien()
    {
        trust.ApprovalNeeded = false;

        var session = new SessionViewModel(server, trust);

        Assert.False(session.ServerApprovalNeeded);
        Assert.True(session.IsRegisterAccountAsked);
    }

    [Fact]
    public async Task Un_serveur_qui_n_est_pas_celui_approuve_est_signale_dans_le_bandeau()
    {
        var status = new ServerStatusViewModel(new RefusingAccess(ServerTrust.NotApprovedCode, ServerTrust.NotApprovedMessage));

        await status.CheckAsync();

        Assert.False(status.IsConnected);
        Assert.Equal("Serveur non approuvé — connexion refusée", status.StatusText);
    }

    [Fact]
    public void L_option_oublier_serveur_est_la_seule_qui_efface_le_serveur_approuve()
    {
        Assert.True(LaunchOptions.Parse(["--oublier-serveur"]).ForgetServer);
        Assert.False(LaunchOptions.Parse(["--serveur", "https://192.168.1.10:5443", "--essai-auto"]).ForgetServer);
        Assert.False(LaunchOptions.Parse([]).ForgetServer);
    }

    [AvaloniaFact]
    public async Task A_l_ecran_entree_n_approuve_pas_le_serveur_il_faut_la_touche_f8()
    {
        var shell = new ShellViewModel(server.Clients, new ManualInactivityTimer(), TimeSpan.Zero, trust: trust);
        var window = new MainWindow(shell, new LaunchOptions(null, false, null));
        window.Show();
        await SettleAsync();

        Assert.True(window.FindControl<Control>("ServerApprovalPanel")!.IsVisible);
        Assert.False(window.FindControl<Control>("RegisterAccountPanel")!.IsVisible);
        Assert.Equal(CertificateFingerprint.Format(Fingerprint), window.FindControl<TextBlock>("ServerFingerprintLabel")!.Text);

        window.KeyTextInput(FakeServer.RegisterLogin);
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await SettleAsync();

        Assert.Null(trust.Approved);
        Assert.True(shell.Session.ServerApprovalNeeded);
        Assert.Equal(string.Empty, shell.Session.RegisterLogin);

        window.KeyPressQwerty(PhysicalKey.F8, RawInputModifiers.None);
        await SettleAsync();

        Assert.Equal(Fingerprint, trust.Approved);
        Assert.False(window.FindControl<Control>("ServerApprovalPanel")!.IsVisible);
        Assert.True(window.FindControl<Control>("RegisterAccountPanel")!.IsVisible);
        Assert.True(window.FindControl<TextBox>("RegisterLoginBox")!.IsFocused);

        window.KeyTextInput(FakeServer.RegisterLogin);
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        window.KeyTextInput(FakeServer.RegisterPassword);
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await SettleAsync();

        Assert.Equal(SessionStage.EmployeeSignIn, shell.Session.Stage);
        window.Close();
    }

    private static async Task SettleAsync()
    {
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class FakeTrust : IServerTrust
    {
        public bool ApprovalNeeded { get; set; }

        public string? PresentedFingerprint { get; set; }

        public string? Approved { get; private set; }

        public void Approve(string fingerprint)
        {
            Approved = fingerprint;
            ApprovalNeeded = false;
        }
    }

    /// <summary>Le transport a refusé de parler au serveur : chaque appel lève ce refus.</summary>
    private sealed class RefusingAccess(string code, string message) : IAccessClient
    {
        public bool HasPosteSession => false;

        public Task<HealthResponse?> GetHealthAsync(CancellationToken cancellationToken) => throw Refusal();

        public Task<PosteSignInResponse> SignInPosteAsync(string login, string password, CancellationToken cancellationToken) => throw Refusal();

        public Task<EmployeeSignInResponse> SignInEmployeeAsync(int employeeNumber, string secret, CancellationToken cancellationToken) => throw Refusal();

        public Task SignOutEmployeeAsync(CancellationToken cancellationToken) => throw Refusal();

        public void ForgetPosteSession()
        {
        }

        private ServerRefusedException Refusal() => new(code, message);
    }
}
