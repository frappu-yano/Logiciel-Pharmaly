using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Poste.Modules.Sales;
using Pharmaly.Poste.ViewModels;

namespace Pharmaly.Poste.Modules.Cash;

/// <summary>
/// Touches du module Caisse. Espèces et TPE sont celles du module Vente (une seule constante
/// pour le TPE provisoire, D7) ; les autres sont propres au module.
/// </summary>
public static class CashShortcuts
{
    /// <summary>Impayés : change le filtre de statut (ouverts, soldés, tous).</summary>
    public const Key StatusFilterKey = Key.F7;

    /// <summary>Impayés : réponse du TPE pour un règlement resté en attente (avec Ctrl).</summary>
    public const Key PendingAnswerKey = Key.T;

    /// <summary>Session de caisse : entrée d'espèces.</summary>
    public const Key CashInKey = Key.F7;

    /// <summary>Session de caisse : sortie d'espèces.</summary>
    public const Key CashOutKey = Key.F9;

    /// <summary>Relire l'onglet affiché (avec Ctrl).</summary>
    public const Key RefreshKey = Key.R;

    public static string CashLabel => SalesShortcuts.CashSymbol;

    public static string CardLabel => SalesShortcuts.CardLabel;

    public static bool IsCash(KeyEventArgs e) => SalesShortcuts.IsCash(e);

    public static bool IsCard(KeyEventArgs e) => SalesShortcuts.IsCard(e);

    public static bool IsControl(KeyEventArgs e, Key key) => e.Key == key && e.KeyModifiers == KeyModifiers.Control;

    public static bool IsAlone(KeyEventArgs e, Key key) => e.Key == key && e.KeyModifiers == KeyModifiers.None;
}

/// <summary>
/// Ce que les droits de l'employé permettent dans le module. Confort d'affichage seulement :
/// le serveur contrôle chaque demande.
/// </summary>
public sealed record CashRights(
    bool CanViewReceivables,
    bool CanSettle,
    bool CanOpenRegister,
    bool CanViewReports,
    bool CanMoveCash)
{
    public static readonly CashRights All = new(true, true, true, true, true);

    public static CashRights From(SessionViewModel session) => From(session.HasPermission);

    public static CashRights From(Func<string, bool> hasPermission) => new(
        hasPermission(Permissions.ReceivablesView),
        hasPermission(Permissions.ReceivablesSettle),
        hasPermission(Permissions.RegisterOpen),
        hasPermission(Permissions.RegisterReportsView),
        hasPermission(Permissions.PaymentsCollect));
}

/// <summary>Onglets du module.</summary>
public enum CashTab
{
    Receivables,
    Session,
    Closing,
}

/// <summary>Choix du filtre « statut » du tableau des impayés.</summary>
/// <param name="Code">Un état de <see cref="ReceivableStatuses"/>, ou <c>null</c> pour tous.</param>
public sealed record ReceivableStatusChoice(string? Code, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Affichage des mots du métier des impayés.</summary>
public static class ReceivableFormat
{
    public static string Status(ReceivableLine line) => line.Status switch
    {
        ReceivableStatuses.Open when line.PendingSettlementId is not null => "TPE en attente",
        ReceivableStatuses.Open => "Ouvert",
        ReceivableStatuses.Settled => "Soldé",
        ReceivableStatuses.Cancelled => "Annulé",
        _ => line.Status,
    };

    public static string Method(string? method) => method switch
    {
        PaymentMethods.Cash => "Espèces",
        PaymentMethods.Card => "TPE",
        null => string.Empty,
        _ => method,
    };

    public static string Tickets(int count) => count == 1 ? "1 ticket" : $"{count} tickets";
}

/// <summary>Ligne du tableau des impayés.</summary>
public sealed partial class ReceivableRowViewModel(ReceivableLine line) : ObservableObject
{
    public ReceivableLine Line { get; } = line;

    public Guid Id => Line.Id;

    public string TicketNumber => Line.TicketNumber;

    public string DateText => SaleFormat.DateTime(Line.CreatedAt);

    public string CustomerName => Line.CustomerName;

    public string AmountText => Money.ToDisplay(Line.Amount);

    public string StatusText => ReceivableFormat.Status(Line);

    public string EmployeeName => Line.EmployeeName;

    public string SettledAtText => Line.SettledAt is { } settledAt ? SaleFormat.DateTime(settledAt) : string.Empty;

    public string MethodText => ReceivableFormat.Method(Line.SettlementMethod);

    public bool IsOpen => Line.Status == ReceivableStatuses.Open;

    public bool IsSettled => Line.Status == ReceivableStatuses.Settled;

    public bool IsCardPending => Line.Status == ReceivableStatuses.Open && Line.PendingSettlementId is not null;

    /// <summary>Le ticket fait partie de la sélection à régler.</summary>
    [ObservableProperty]
    private bool isChecked;
}

/// <summary>Une ligne du résumé de session : libellé et montant.</summary>
public sealed record SummaryLine(string Label, string Amount, bool IsTotal = false, string Note = "");
