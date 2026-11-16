using Pharmaly.Contracts;
using Pharmaly.Poste.Services.Server;

namespace Pharmaly.Tests.Poste;

internal sealed partial class FakeServer : IAccessClient
{
    public Task<PosteSignInResponse> SignInPosteAsync(string login, string password, CancellationToken cancellationToken)
    {
        EnsureUp();
        if (login != RegisterLogin || password != RegisterPassword)
        {
            throw new ServerRefusedException(ApiErrorCodes.InvalidCredentials, "Identifiant ou code incorrect.");
        }

        HasPosteSession = true;
        return Task.FromResult(new PosteSignInResponse("jeton-poste", "Caisse 1", "Pharmaly 1 — Didouche"));
    }

    public Task<EmployeeSignInResponse> SignInEmployeeAsync(int employeeNumber, string secret, CancellationToken cancellationToken)
    {
        EnsureUp();
        if (!HasPosteSession)
        {
            throw new ServerRefusedException(ApiErrorCodes.PosteNotSignedIn, "Ce poste n'est pas connecté avec un compte de caisse.");
        }

        if (employeeNumber != EmployeeNumber || secret != EmployeeSecret)
        {
            throw new ServerRefusedException(ApiErrorCodes.InvalidCredentials, "Identifiant ou code incorrect.");
        }

        EmployeeIdentified = true;
        return Task.FromResult(new EmployeeSignInResponse("jeton-employe", EmployeeId, EmployeeName, EmployeePermissions));
    }

    public Task SignOutEmployeeAsync(CancellationToken cancellationToken)
    {
        EmployeeIdentified = false;
        EnsureUp();
        return Task.CompletedTask;
    }

    public void ForgetPosteSession()
    {
        HasPosteSession = false;
        EmployeeIdentified = false;
    }

    public Task<HealthResponse?> GetHealthAsync(CancellationToken cancellationToken) =>
        IsDown
            ? throw new HttpRequestException("serveur coupé")
            : Task.FromResult<HealthResponse?>(new HealthResponse("OK", "0.1.0", true, 1, DateTimeOffset.Now));
}
