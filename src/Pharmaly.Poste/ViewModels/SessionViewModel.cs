using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pharmaly.Contracts;
using Pharmaly.Poste.Services.Server;

namespace Pharmaly.Poste.ViewModels;

public enum SessionStage
{
    /// <summary>Niveau 1 : le poste doit être connecté avec un compte de caisse.</summary>
    PosteSignIn,

    /// <summary>Niveau 2 : un employé doit s'identifier.</summary>
    EmployeeSignIn,

    /// <summary>Un employé est identifié : l'écran de travail est accessible.</summary>
    Work,
}

/// <summary>Identification à deux niveaux : compte de caisse du poste, puis employé.</summary>
public sealed partial class SessionViewModel : ObservableObject
{
    private const string ServerUnreachable = "Serveur injoignable — vérifiez le réseau du magasin.";
    private readonly IAccessClient server;

    public SessionViewModel(IAccessClient server)
    {
        this.server = server;
        stage = server.HasPosteSession ? SessionStage.EmployeeSignIn : SessionStage.PosteSignIn;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPosteSignIn), nameof(IsEmployeeSignIn), nameof(IsWork))]
    private SessionStage stage;

    [ObservableProperty]
    private string registerLogin = string.Empty;

    [ObservableProperty]
    private string registerPassword = string.Empty;

    [ObservableProperty]
    private string employeeNumber = string.Empty;

    [ObservableProperty]
    private string employeeSecret = string.Empty;

    [ObservableProperty]
    private string errorMessage = string.Empty;

    [ObservableProperty]
    private string registerLabel = string.Empty;

    [ObservableProperty]
    private string employeeName = string.Empty;

    [ObservableProperty]
    private bool isBusy;

    public bool IsPosteSignIn => Stage == SessionStage.PosteSignIn;

    public bool IsEmployeeSignIn => Stage == SessionStage.EmployeeSignIn;

    public bool IsWork => Stage == SessionStage.Work;

    /// <summary>
    /// Droits effectifs de l'employé identifié. Ils servent seulement à masquer ce qui serait
    /// refusé : le serveur contrôle chaque demande.
    /// </summary>
    public IReadOnlyList<string> Permissions { get; private set; } = [];

    /// <summary>Identifiant de l'employé identifié, ou <see cref="Guid.Empty"/> quand le poste est verrouillé.</summary>
    public Guid EmployeeId { get; private set; }

    public bool HasPermission(string code) => Permissions.Contains(code);

    [RelayCommand]
    public async Task SignInPosteAsync()
    {
        if (string.IsNullOrWhiteSpace(RegisterLogin) || RegisterPassword.Length == 0)
        {
            ErrorMessage = "Saisissez le compte de caisse et son mot de passe.";
            return;
        }

        await RunAsync(async () =>
        {
            var signedIn = await server.SignInPosteAsync(RegisterLogin.Trim(), RegisterPassword, CancellationToken.None);
            RegisterLabel = $"{signedIn.RegisterName} — {signedIn.StoreName}";
            RegisterPassword = string.Empty;
            Stage = SessionStage.EmployeeSignIn;
        });
    }

    [RelayCommand]
    public async Task SignInEmployeeAsync()
    {
        if (!int.TryParse(EmployeeNumber.Trim(), out var number) || EmployeeSecret.Length == 0)
        {
            ErrorMessage = "Saisissez votre matricule et votre code personnel.";
            return;
        }

        await RunAsync(async () =>
        {
            var signedIn = await server.SignInEmployeeAsync(number, EmployeeSecret, CancellationToken.None);
            EmployeeName = signedIn.EmployeeName;
            EmployeeId = signedIn.EmployeeId;
            Permissions = signedIn.Permissions;
            EmployeeNumber = string.Empty;
            EmployeeSecret = string.Empty;
            Stage = SessionStage.Work;
        });
    }

    /// <summary>Verrouille le poste : l'employé suivant devra s'identifier.</summary>
    [RelayCommand]
    public async Task LockAsync()
    {
        try
        {
            await server.SignOutEmployeeAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or ServerRefusedException)
        {
            // Le poste se verrouille même si le serveur ne répond pas.
        }

        RequireEmployee();
    }

    /// <summary>Le serveur ne reconnaît plus l'employé (session expirée, compte désactivé).</summary>
    public void RequireEmployee()
    {
        EmployeeName = string.Empty;
        EmployeeId = Guid.Empty;
        Permissions = [];
        EmployeeNumber = string.Empty;
        EmployeeSecret = string.Empty;
        ErrorMessage = string.Empty;
        Stage = SessionStage.EmployeeSignIn;
    }

    private async Task RunAsync(Func<Task> action)
    {
        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            await action();
        }
        catch (ServerRefusedException refused) when (refused.Code == ApiErrorCodes.PosteNotSignedIn)
        {
            // Le compte de caisse mémorisé n'est plus valable : retour au niveau 1.
            server.ForgetPosteSession();
            Stage = SessionStage.PosteSignIn;
            ErrorMessage = "La connexion de ce poste a expiré. Reconnectez le compte de caisse.";
        }
        catch (ServerRefusedException refused)
        {
            // Après un refus, la saisie repart de zéro : rien ne reste dans les champs.
            ErrorMessage = refused.Message;
            EmployeeNumber = string.Empty;
            EmployeeSecret = string.Empty;
            RegisterPassword = string.Empty;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            ErrorMessage = ServerUnreachable;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
