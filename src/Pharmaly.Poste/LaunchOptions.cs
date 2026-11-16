namespace Pharmaly.Poste;

/// <summary>
/// Options de lancement.
///   --serveur https://adresse:5443  adresse du serveur du site
///   --essai-auto                    lance l'essai de rapidité puis ferme le programme
///   --rapport chemin.json           fichier où écrire le résultat de l'essai
///   --oublier-serveur               oublie le serveur approuvé et la connexion du poste :
///                                   la prochaine connexion redemande de comparer l'empreinte
/// </summary>
public sealed record LaunchOptions(string? ServerUrl, bool AutoBenchmark, string? ReportPath)
{
    public const string ForgetServerArgument = "--oublier-serveur";

    /// <summary>Variable d'environnement lue par l'essai automatique : empreinte attendue du serveur.</summary>
    public const string TrialFingerprintVariable = "PHARMALY_ESSAI_EMPREINTE";

    /// <summary>
    /// Seule façon de remplacer le serveur approuvé : une action volontaire de l'installateur,
    /// jamais un bouton de l'écran.
    /// </summary>
    public bool ForgetServer { get; init; }

    public static LaunchOptions Parse(IReadOnlyList<string> args)
    {
        string? ValueAfter(string name)
        {
            for (var index = 0; index < args.Count - 1; index++)
            {
                if (args[index] == name)
                {
                    return args[index + 1];
                }
            }

            return null;
        }

        return new LaunchOptions(ValueAfter("--serveur"), args.Contains("--essai-auto"), ValueAfter("--rapport"))
        {
            ForgetServer = args.Contains(ForgetServerArgument),
        };
    }
}
