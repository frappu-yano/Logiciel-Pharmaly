using Avalonia.Input;

namespace Pharmaly.Poste.Modules;

/// <summary>
/// Ce que la fenêtre principale attend de la vue d'un module, en plus d'être un contrôle.
/// </summary>
public interface IModuleView
{
    /// <summary>Place le curseur là où l'employé commence à travailler dans ce module.</summary>
    void FocusDefault();

    /// <summary>
    /// Reçoit chaque touche avant les contrôles, quel que soit l'élément qui a le focus (un
    /// lecteur de codes-barres est un clavier). Les touches de la fenêtre (F12, changement
    /// de module) sont déjà traitées. Mettre <c>e.Handled</c> à vrai pour une touche consommée.
    /// </summary>
    Task HandleKeyAsync(KeyEventArgs e);
}
