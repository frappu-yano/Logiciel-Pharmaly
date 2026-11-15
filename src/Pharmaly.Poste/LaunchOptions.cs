namespace Pharmaly.Poste;

/// <summary>
/// Options de lancement.
///   --serveur http://adresse:5080   adresse du serveur du site
///   --essai-auto                    lance l'essai de rapidité puis ferme le programme
///   --rapport chemin.json           fichier où écrire le résultat de l'essai
/// </summary>
public sealed record LaunchOptions(string? ServerUrl, bool AutoBenchmark, string? ReportPath)
{
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

        return new LaunchOptions(ValueAfter("--serveur"), args.Contains("--essai-auto"), ValueAfter("--rapport"));
    }
}
