namespace Pharmaly.Poste.Services.Server;

/// <summary>Le serveur a compris la demande et l'a refusée (droit manquant, code faux…).</summary>
public sealed class ServerRefusedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
