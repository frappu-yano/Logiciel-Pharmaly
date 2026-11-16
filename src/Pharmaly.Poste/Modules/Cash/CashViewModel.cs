using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pharmaly.Contracts;
using Pharmaly.Poste.Services.Server;

namespace Pharmaly.Poste.Modules.Cash;

/// <summary>
/// Module Caisse : impayés (tableau, recherche, règlement intégral éventuellement groupé),
/// session de caisse du poste (état, ouverture, résumé, entrées et sorties d'espèces, fermeture
/// avec comptage des espèces) et clôture quotidienne du magasin (état de la journée, blocages,
/// aperçu des totaux, clôture éventuellement forcée, historique et rapports figés).
/// <para>
/// Le poste ne décide de rien : le serveur contrôle les droits, les montants et les
/// encaissements simultanés. Ce modèle de vue garantit seulement qu'un encaissement n'est
/// jamais envoyé deux fois et que l'employé sait toujours s'il est enregistré.
/// </para>
/// </summary>
public sealed partial class CashViewModel : ModuleViewModel
{
    public const string NoReceivablesViewRight = "Vos droits ne permettent pas de consulter les impayés.";

    /// <summary>
    /// Code que le transport donne à une réponse du serveur qui n'est pas un refus lisible
    /// (erreur interne, par exemple) : on ne sait alors pas si la commande est enregistrée.
    /// </summary>
    internal const string UnreadableAnswerCode = "ERREUR";

    private readonly IReceivablesClient receivables;
    private readonly ICustomersClient customers;
    private readonly ICashSessionClient cash;
    private readonly IClosuresClient closures;
    private readonly IClosureReportPrinter printer;
    private readonly Func<DateOnly> today;

    public CashViewModel(
        IReceivablesClient receivables,
        ICustomersClient customers,
        ICashSessionClient cash,
        IClosuresClient closures,
        CashRights rights,
        Func<DateOnly>? today = null,
        IClosureReportPrinter? printer = null)
    {
        this.receivables = receivables;
        this.customers = customers;
        this.cash = cash;
        this.closures = closures;
        this.printer = printer ?? new NoClosureReportPrinter();
        this.today = today ?? (() => DateOnly.FromDateTime(DateTime.Now));
        Rights = rights;
        selectedStatus = StatusChoices[0];
        tab = rights.CanViewReceivables ? CashTab.Receivables : CashTab.Session;
        Rows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRows));
        CustomerResults.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasCustomerResults));
    }

    public CashRights Rights { get; }

    // ---------- Onglets ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReceivablesTab), nameof(IsSessionTab), nameof(IsClosingTab))]
    private CashTab tab;

    public bool IsReceivablesTab => Tab == CashTab.Receivables;

    public bool IsSessionTab => Tab == CashTab.Session;

    public bool IsClosingTab => Tab == CashTab.Closing;

    /// <summary>Vrai tant qu'une étape (règlement, mouvement d'espèces, ouverture, fermeture, clôture) est ouverte : on ne change pas d'onglet.</summary>
    public bool IsAnyStepOpen => IsStepOpen || IsSessionStepOpen || IsConfirmingOpening || IsClosingStepOpen;

    /// <summary>Information utile à l'employé (règlement refusé par le TPE, caisse ouverte…).</summary>
    [ObservableProperty]
    private string infoMessage = string.Empty;

    /// <summary>Vrai pendant qu'une commande d'écriture attend la réponse du serveur : rien d'autre ne part.</summary>
    [ObservableProperty]
    private bool isSending;

    public override Task ActivateAsync()
    {
        // Une étape ouverte (encaissement en cours) n'est pas dérangée par un retour sur le module.
        if (IsAnyStepOpen || IsSending)
        {
            return Task.CompletedTask;
        }

        return LoadTabAsync();
    }

    /// <summary>Ctrl+Tab : onglet suivant (Ctrl+Maj+Tab : précédent). Sans effet pendant une étape.</summary>
    public Task MoveTabAsync(int delta)
    {
        var tabs = Enum.GetValues<CashTab>();
        var index = Array.IndexOf(tabs, Tab);
        return ShowTabAsync(tabs[(index + delta + tabs.Length) % tabs.Length]);
    }

    [RelayCommand]
    public Task ShowTabAsync(CashTab target)
    {
        if (IsAnyStepOpen || IsSending || target == Tab)
        {
            return Task.CompletedTask;
        }

        Tab = target;
        ClearMessages();
        return LoadTabAsync();
    }

    /// <summary>Ctrl+R : relit ce que l'onglet affiche.</summary>
    [RelayCommand]
    public Task RefreshAsync() => IsAnyStepOpen || IsSending ? Task.CompletedTask : LoadTabAsync();

    private async Task LoadTabAsync()
    {
        switch (Tab)
        {
            case CashTab.Receivables:
                await RefreshCashSessionStateAsync();
                if (Rights.CanViewReceivables)
                {
                    await ReloadAsync();
                }

                break;
            case CashTab.Session:
                await RefreshSessionTabAsync();
                break;
            case CashTab.Closing:
                await LoadClosingTabAsync();
                break;
        }
    }

    private void ClearMessages()
    {
        ErrorMessage = string.Empty;
        InfoMessage = string.Empty;
    }

    private static bool IsUnreachable(Exception exception) => exception is HttpRequestException or TaskCanceledException;

    /// <summary>Vrai quand on ne peut pas savoir si le serveur a enregistré la commande.</summary>
    private static bool IsUnknownOutcome(Exception exception) =>
        IsUnreachable(exception) || exception is ServerRefusedException { Code: UnreadableAnswerCode };
}
