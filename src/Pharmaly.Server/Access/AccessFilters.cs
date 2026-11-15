namespace Pharmaly.Server.Access;

/// <summary>
/// Protection des routes. Les contrôles sont faits ici, par le serveur, à chaque demande :
/// ce que le poste affiche ou masque n'a aucune valeur de sécurité.
/// </summary>
public static class AccessFilters
{
    /// <summary>Exige un poste connecté avec un compte de caisse.</summary>
    public static RouteHandlerBuilder RequirePoste(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter((context, next) => CheckAsync(context, next, requireEmployee: false, permission: null));

    /// <summary>Exige en plus un employé identifié.</summary>
    public static RouteHandlerBuilder RequireEmployee(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter((context, next) => CheckAsync(context, next, requireEmployee: true, permission: null));

    /// <summary>Exige en plus que l'employé possède ce droit sur le site du poste.</summary>
    public static RouteHandlerBuilder RequirePermission(this RouteHandlerBuilder builder, string permission) =>
        builder.AddEndpointFilter((context, next) => CheckAsync(context, next, requireEmployee: true, permission));

    private static async ValueTask<object?> CheckAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next, bool requireEmployee, string? permission)
    {
        var access = context.HttpContext.RequestServices.GetRequiredService<AccessService>();
        var (identity, failure) = await access.AuthorizeAsync(context.HttpContext, requireEmployee, permission);
        if (failure is not null)
        {
            return Results.Json(failure.Error, statusCode: failure.StatusCode);
        }

        context.HttpContext.Items[RequestIdentity.HttpContextKey] = identity;
        return await next(context);
    }
}
