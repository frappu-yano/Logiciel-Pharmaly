using Avalonia.Controls;

namespace Pharmaly.Poste.Modules.Cash;

/// <summary>
/// Les trois blocs d'un rapport de journée (chiffre d'affaires, trésorerie, créances) et ses
/// caisses. Le même affichage sert à l'aperçu non figé et au rapport figé d'une clôture.
/// </summary>
public sealed partial class ClosureReportPanel : UserControl
{
    public ClosureReportPanel()
    {
        InitializeComponent();
    }
}
