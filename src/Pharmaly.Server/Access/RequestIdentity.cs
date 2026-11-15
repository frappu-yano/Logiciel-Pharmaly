namespace Pharmaly.Server.Access;

/// <summary>Qui fait la demande : le compte de caisse du poste et, s'il est identifié, l'employé.</summary>
public sealed record RequestIdentity(
    string PosteTokenHash,
    Guid RegisterAccountId,
    string RegisterName,
    Guid StoreId,
    string StoreName,
    Guid? EmployeeId,
    string? EmployeeName)
{
    public const string HttpContextKey = "pharmaly.identity";

    public Guid RequireEmployeeId() =>
        EmployeeId ?? throw new InvalidOperationException("Aucun employé identifié sur cette demande.");
}

public static class HttpContextIdentityExtensions
{
    public static RequestIdentity GetIdentity(this HttpContext context) =>
        (RequestIdentity)context.Items[RequestIdentity.HttpContextKey]!;
}
