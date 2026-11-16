using Pharmaly.Contracts;

namespace Pharmaly.Poste.Services.Server;

/// <summary>Module « accès » : état du serveur et identification à deux niveaux.</summary>
public interface IAccessClient
{
    bool HasPosteSession { get; }

    Task<HealthResponse?> GetHealthAsync(CancellationToken cancellationToken);

    Task<PosteSignInResponse> SignInPosteAsync(string login, string password, CancellationToken cancellationToken);

    Task<EmployeeSignInResponse> SignInEmployeeAsync(int employeeNumber, string secret, CancellationToken cancellationToken);

    Task SignOutEmployeeAsync(CancellationToken cancellationToken);

    void ForgetPosteSession();
}

public sealed class AccessClient(IServerTransport transport) : IAccessClient
{
    private bool employeeSignedIn;

    public bool HasPosteSession => transport.HasPosteToken;

    public Task<HealthResponse?> GetHealthAsync(CancellationToken cancellationToken) =>
        transport.GetWhateverStatusAsync<HealthResponse>(ApiRoutes.Health, cancellationToken);

    public async Task<PosteSignInResponse> SignInPosteAsync(string login, string password, CancellationToken cancellationToken)
    {
        var signedIn = (await transport.SendAsync<PosteSignInResponse>(
            HttpMethod.Post, ApiRoutes.PosteSignIn, new PosteSignInRequest(login, password), cancellationToken))!;
        transport.SetPosteToken(signedIn.Token);
        employeeSignedIn = false;
        return signedIn;
    }

    public async Task<EmployeeSignInResponse> SignInEmployeeAsync(int employeeNumber, string secret, CancellationToken cancellationToken)
    {
        var signedIn = (await transport.SendAsync<EmployeeSignInResponse>(
            HttpMethod.Post, ApiRoutes.EmployeeSignIn, new EmployeeSignInRequest(employeeNumber, secret), cancellationToken))!;
        transport.SetEmployeeToken(signedIn.Token);
        employeeSignedIn = true;
        return signedIn;
    }

    public async Task SignOutEmployeeAsync(CancellationToken cancellationToken)
    {
        if (!employeeSignedIn)
        {
            return;
        }

        try
        {
            await transport.SendAsync(HttpMethod.Post, ApiRoutes.EmployeeSignOut, null, cancellationToken);
        }
        finally
        {
            // Le jeton est oublié même si le serveur ne répond pas : le poste se verrouille toujours.
            employeeSignedIn = false;
            transport.SetEmployeeToken(null);
        }
    }

    public void ForgetPosteSession()
    {
        employeeSignedIn = false;
        transport.ForgetTokens();
    }
}
