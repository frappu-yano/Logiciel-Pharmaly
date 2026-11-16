using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Poste.Services.Server;

namespace Pharmaly.Tests.Poste;

/// <summary>Administration : mêmes refus et mêmes messages que le vrai serveur.</summary>
internal sealed partial class FakeServer : IAdminClient
{
    public static readonly StoreSummary Didouche = new(Guid.NewGuid(), "D1", "Pharmaly 1 — Didouche", "MAGASIN");
    public static readonly StoreSummary Audin = new(Guid.NewGuid(), "A1", "Pharmaly 2 — Audin", "MAGASIN");
    public static readonly Guid KarimId = Guid.NewGuid();
    public const int KarimNumber = 7;
    public const string KarimName = "Karim Essai";

    private const int MinSecretLength = 4;

    /// <summary>Employés enregistrés ; Karim a déjà le droit de vendre, partout.</summary>
    public List<EmployeeSummary> Employees { get; } =
    [
        new(KarimId, KarimNumber, KarimName, true, [new PermissionGrant(Permissions.SalesCreate, null)]),
        new(EmployeeId, EmployeeNumber, EmployeeName, true, [new PermissionGrant(Permissions.UsersAdminister, null)]),
    ];

    public List<StoreSummary> Stores { get; } = [Didouche, Audin];

    /// <summary>Demandes d'écriture reçues, dans l'ordre.</summary>
    public List<CreateEmployeeRequest> CreateRequests { get; } = [];

    public List<(Guid EmployeeId, IReadOnlyList<PermissionGrant> Permissions)> PermissionRequests { get; } = [];

    public List<(Guid EmployeeId, bool IsActive)> StateRequests { get; } = [];

    public List<(Guid EmployeeId, string Secret)> SecretRequests { get; } = [];

    public Task<IReadOnlyList<EmployeeSummary>> ListEmployeesAsync(CancellationToken cancellationToken)
    {
        EnsurePermission(Permissions.UsersAdminister);
        return Task.FromResult<IReadOnlyList<EmployeeSummary>>(Employees.OrderBy(employee => employee.FullName).ToList());
    }

    public Task<IReadOnlyList<StoreSummary>> ListStoresAsync(CancellationToken cancellationToken)
    {
        EnsureEmployee();
        return Task.FromResult<IReadOnlyList<StoreSummary>>(Stores.ToList());
    }

    public Task<EmployeeSummary> CreateEmployeeAsync(CreateEmployeeRequest request, CancellationToken cancellationToken)
    {
        EnsurePermission(Permissions.UsersAdminister);
        CreateRequests.Add(request);
        if (request.EmployeeNumber <= 0 || request.FullName.Length == 0 || request.Secret.Length < MinSecretLength)
        {
            throw new ServerRefusedException(
                ApiErrorCodes.InvalidRequest, "Matricule, nom et code personnel (4 caractères minimum) sont obligatoires.");
        }

        if (Employees.Any(employee => employee.EmployeeNumber == request.EmployeeNumber))
        {
            throw new ServerRefusedException(ApiErrorCodes.Conflict, "Ce matricule est déjà attribué.");
        }

        var created = new EmployeeSummary(Guid.NewGuid(), request.EmployeeNumber, request.FullName, true, []);
        Employees.Add(created);
        return Task.FromResult(created);
    }

    public Task SetPermissionsAsync(Guid employeeId, IReadOnlyList<PermissionGrant> permissions, CancellationToken cancellationToken)
    {
        EnsurePermission(Permissions.UsersAdminister);
        PermissionRequests.Add((employeeId, permissions));
        if (employeeId == EmployeeId)
        {
            throw new ServerRefusedException(ApiErrorCodes.SelfModificationForbidden, "Vous ne pouvez pas modifier vos propres droits.");
        }

        Replace(employeeId, employee => employee with { Permissions = permissions.ToList() });
        return Task.CompletedTask;
    }

    public Task SetEmployeeStateAsync(Guid employeeId, bool isActive, CancellationToken cancellationToken)
    {
        EnsurePermission(Permissions.UsersAdminister);
        StateRequests.Add((employeeId, isActive));
        if (employeeId == EmployeeId)
        {
            throw new ServerRefusedException(
                ApiErrorCodes.SelfModificationForbidden, "Vous ne pouvez pas désactiver votre propre compte.");
        }

        Replace(employeeId, employee => employee with { IsActive = isActive });
        return Task.CompletedTask;
    }

    public Task ResetSecretAsync(Guid employeeId, string secret, CancellationToken cancellationToken)
    {
        EnsurePermission(Permissions.UsersAdminister);
        SecretRequests.Add((employeeId, secret));
        if (secret.Length < MinSecretLength)
        {
            throw new ServerRefusedException(
                ApiErrorCodes.InvalidRequest, "Le code personnel doit contenir au moins 4 caractères.");
        }

        Replace(employeeId, employee => employee);
        return Task.CompletedTask;
    }

    private void Replace(Guid employeeId, Func<EmployeeSummary, EmployeeSummary> change)
    {
        var index = Employees.FindIndex(employee => employee.Id == employeeId);
        if (index < 0)
        {
            throw new ServerRefusedException(ApiErrorCodes.NotFound, "Employé introuvable.");
        }

        Employees[index] = change(Employees[index]);
    }
}
