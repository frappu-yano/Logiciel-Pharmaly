using Pharmaly.Contracts;

namespace Pharmaly.Server.Operations;

/// <summary>
/// Refus métier : la commande est annulée en entier et l'employé reçoit ce message.
/// Ce n'est pas une panne ; le code est stable et le message est en français.
/// </summary>
public class BusinessException(string code, string message, int statusCode = StatusCodes.Status400BadRequest)
    : Exception(message)
{
    public string Code { get; } = code;

    public int StatusCode { get; } = statusCode;

    public ApiError ToApiError() => new(Code, Message);

    /// <summary>
    /// Corps de la réponse de refus : toujours un code et un message ; un refus peut y ajouter
    /// des précisions (la liste de ce qui bloque une clôture, par exemple).
    /// </summary>
    public virtual object ToErrorBody() => ToApiError();

    public static BusinessException Invalid(string message) => new(ApiErrorCodes.InvalidRequest, message);

    public static BusinessException NotFound(string message) =>
        new(ApiErrorCodes.NotFound, message, StatusCodes.Status404NotFound);

    public static BusinessException Conflict(string message) =>
        new(ApiErrorCodes.Conflict, message, StatusCodes.Status409Conflict);
}
