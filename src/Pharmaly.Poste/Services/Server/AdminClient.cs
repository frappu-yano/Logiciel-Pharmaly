using Pharmaly.Contracts;

namespace Pharmaly.Poste.Services.Server;

/// <summary>Module « administration » : employés, droits et sites.</summary>
public interface IAdminClient
{
    Task<IReadOnlyList<EmployeeSummary>> ListEmployeesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<StoreSummary>> ListStoresAsync(CancellationToken cancellationToken);

    Task<EmployeeSummary> CreateEmployeeAsync(CreateEmployeeRequest request, CancellationToken cancellationToken);

    /// <summary>Remplace l'ensemble des droits de l'employé par la liste envoyée.</summary>
    Task SetPermissionsAsync(Guid employeeId, IReadOnlyList<PermissionGrant> permissions, CancellationToken cancellationToken);

    Task SetEmployeeStateAsync(Guid employeeId, bool isActive, CancellationToken cancellationToken);

    Task ResetSecretAsync(Guid employeeId, string secret, CancellationToken cancellationToken);
}

public sealed class AdminClient(IServerTransport transport) : IAdminClient
{
    public async Task<IReadOnlyList<EmployeeSummary>> ListEmployeesAsync(CancellationToken cancellationToken) =>
        await transport.SendAsync<List<EmployeeSummary>>(HttpMethod.Get, ApiRoutes.Employees, null, cancellationToken) ?? [];

    public async Task<IReadOnlyList<StoreSummary>> ListStoresAsync(CancellationToken cancellationToken) =>
        await transport.SendAsync<List<StoreSummary>>(HttpMethod.Get, ApiRoutes.Stores, null, cancellationToken) ?? [];

    public async Task<EmployeeSummary> CreateEmployeeAsync(CreateEmployeeRequest request, CancellationToken cancellationToken) =>
        (await transport.SendAsync<EmployeeSummary>(HttpMethod.Post, ApiRoutes.Employees, request, cancellationToken))!;

    public Task SetPermissionsAsync(Guid employeeId, IReadOnlyList<PermissionGrant> permissions, CancellationToken cancellationToken) =>
        transport.SendAsync(
            HttpMethod.Put, $"{ApiRoutes.Employees}/{employeeId}/permissions", new SetPermissionsRequest(permissions), cancellationToken);

    public Task SetEmployeeStateAsync(Guid employeeId, bool isActive, CancellationToken cancellationToken) =>
        transport.SendAsync(
            HttpMethod.Put, $"{ApiRoutes.Employees}/{employeeId}/etat", new SetEmployeeStateRequest(isActive), cancellationToken);

    public Task ResetSecretAsync(Guid employeeId, string secret, CancellationToken cancellationToken) =>
        transport.SendAsync(
            HttpMethod.Put, $"{ApiRoutes.Employees}/{employeeId}/code", new ResetSecretRequest(secret), cancellationToken);
}
