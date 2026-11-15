using Pharmaly.Contracts;

namespace Pharmaly.Server.Access;

public static class AccessEndpoints
{
    public static void MapAccessEndpoints(this WebApplication app)
    {
        app.MapPost(ApiRoutes.PosteSignIn, async (PosteSignInRequest request, AccessService access, CancellationToken cancellationToken) =>
        {
            var (response, failure) = await access.SignInPosteAsync(request, cancellationToken);
            return failure is null ? Results.Ok(response) : Results.Json(failure.Error, statusCode: failure.StatusCode);
        });

        app.MapPost(ApiRoutes.EmployeeSignIn, async (
            EmployeeSignInRequest request, HttpContext context, AccessService access, CancellationToken cancellationToken) =>
        {
            var (response, failure) = await access.SignInEmployeeAsync(context.GetIdentity(), request, cancellationToken);
            return failure is null ? Results.Ok(response) : Results.Json(failure.Error, statusCode: failure.StatusCode);
        }).RequirePoste();

        app.MapPost(ApiRoutes.EmployeeSignOut, async (HttpContext context, AccessService access) =>
        {
            await access.SignOutEmployeeAsync(context.GetIdentity(), context.Request.Headers[ApiHeaders.EmployeeToken].ToString());
            return Results.NoContent();
        }).RequireEmployee();

        app.MapGet(ApiRoutes.CurrentIdentity, async (HttpContext context, AccessService access) =>
        {
            var identity = context.GetIdentity();
            var permissions = await access.ReadEffectivePermissionsAsync(identity.RequireEmployeeId(), identity.StoreId);
            return Results.Ok(new CurrentIdentityResponse(
                identity.RegisterName, identity.StoreName, identity.RequireEmployeeId(), identity.EmployeeName!, permissions));
        }).RequireEmployee();
    }
}
