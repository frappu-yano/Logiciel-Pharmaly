using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Poste.Services.Server;

namespace Pharmaly.Tests.Poste;

/// <summary>Clients : recherche et création rapide, comme le vrai serveur.</summary>
internal sealed partial class FakeServer : ICustomersClient
{
    public static readonly CustomerSummary Benali = new(Guid.NewGuid(), "Nadia Benali", "0550 12 34 56");
    public static readonly CustomerSummary Haddad = new(Guid.NewGuid(), "Omar Haddad", null);

    public List<CustomerSummary> Customers { get; } = [Benali, Haddad];

    public List<CreateCustomerRequest> CustomerRequests { get; } = [];

    /// <summary>Pannes de réseau (demande perdue) pour les prochaines créations de client.</summary>
    public int FailNextCustomerWrites { get; set; }

    public Task<IReadOnlyList<CustomerSummary>> SearchCustomersAsync(string term, CancellationToken cancellationToken)
    {
        EnsureEmployee();
        if (!EmployeePermissions.Contains(Permissions.SalesCreate) && !EmployeePermissions.Contains(Permissions.ReceivablesView)
            && !EmployeePermissions.Contains(Permissions.ReceivablesSettle))
        {
            EnsurePermission(Permissions.SalesCreate);
        }

        return Task.FromResult<IReadOnlyList<CustomerSummary>>(
            Customers.Where(customer => customer.Name.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList());
    }

    public Task<CustomerSummary> CreateCustomerAsync(CreateCustomerRequest request, CancellationToken cancellationToken)
    {
        CustomerRequests.Add(request);
        if (FailNextCustomerWrites > 0)
        {
            FailNextCustomerWrites--;
            throw new HttpRequestException("réseau coupé");
        }

        EnsurePermission(Permissions.SalesCreate);
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ServerRefusedException(ApiErrorCodes.InvalidRequest, "Le nom du client est obligatoire (200 caractères au plus).");
        }

        var created = new CustomerSummary(Guid.NewGuid(), request.Name.Trim(), request.Phone);
        Customers.Add(created);
        return Task.FromResult(created);
    }

    public Task<CustomerDetail?> GetCustomerAsync(Guid customerId, CancellationToken cancellationToken)
    {
        EnsurePermission(Permissions.ReceivablesView);
        return Task.FromResult(Customers.Where(customer => customer.Id == customerId)
            .Select(customer => new CustomerDetail(customer.Id, customer.Name, customer.Phone, DateTimeOffset.Now, 0, 0, []))
            .FirstOrDefault());
    }
}
