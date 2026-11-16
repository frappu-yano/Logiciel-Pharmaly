using System.Globalization;
using Avalonia.Input;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Poste.ViewModels;

namespace Pharmaly.Poste.Modules.Sales;

/// <summary>
/// Touches du module Vente. Un seul endroit à modifier quand la direction fixe un raccourci.
/// </summary>
public static class SalesShortcuts
{
    /// <summary>Espèces : touche <c>*</c>, confirmée par la direction.</summary>
    public const string CashSymbol = "*";

    /// <summary>
    /// TPE : valeur PROVISOIRE (décision D7). La direction n'a pas encore choisi cette touche :
    /// c'est la seule constante à changer, l'écran et le clavier la lisent tous les deux ici.
    /// </summary>
    public const Key ProvisionalCardKey = Key.F8;

    public const Key UnpaidKey = Key.F9;
    public const Key DiscountKey = Key.F7;

    /// <summary>Réponse du TPE : deux lettres, jamais Entrée, pour qu'aucun appui machinal ne réponde.</summary>
    public const Key CardAcceptedKey = Key.A;
    public const Key CardRefusedKey = Key.R;

    /// <summary>Confirmation forte de l'annulation d'une vente : jamais Entrée.</summary>
    public const Key CancelSaleConfirmKey = Key.F9;

    /// <summary>Retours clients : Ctrl+R, depuis la vente, la caisse fermée ou le détail d'un ticket de l'historique.</summary>
    public const Key ReturnsKey = Key.R;

    public const string ReturnsLabel = "Ctrl+R";

    /// <summary>Depuis les retours : ouvrir le ticket affiché dans l'historique des ventes.</summary>
    public const Key OpenSaleInHistoryKey = Key.O;

    public const string OpenSaleInHistoryLabel = "Ctrl+O";

    public static bool IsReturns(KeyEventArgs e) => e.Key == ReturnsKey && e.KeyModifiers == KeyModifiers.Control;

    public static string CardLabel => ProvisionalCardKey.ToString();

    public static string UnpaidLabel => UnpaidKey.ToString();

    public static string DiscountLabel => DiscountKey.ToString();

    public static string CardAcceptedLabel => CardAcceptedKey.ToString();

    public static string CardRefusedLabel => CardRefusedKey.ToString();

    public static string CancelSaleConfirmLabel => CancelSaleConfirmKey.ToString();

    /// <summary>La touche <c>*</c> du pavé numérique, ou le caractère <c>*</c> du clavier principal.</summary>
    public static bool IsCash(KeyEventArgs e) => e.Key == Key.Multiply || e.KeySymbol == CashSymbol;

    public static bool IsCard(KeyEventArgs e) => e.Key == ProvisionalCardKey && e.KeyModifiers == KeyModifiers.None;
}

/// <summary>
/// Ce que les droits de l'employé permettent dans le module. Confort d'affichage seulement :
/// le serveur contrôle chaque demande.
/// </summary>
public sealed record SalesRights(
    bool CanSell,
    bool CanCollect,
    bool CanDiscount,
    bool CanViewHistory,
    bool CanCancel,
    bool CanOpenRegister,
    bool CanReturn)
{
    public static readonly SalesRights All = new(true, true, true, true, true, true, true);

    public static SalesRights From(SessionViewModel session) => From(session.HasPermission);

    public static SalesRights From(Func<string, bool> hasPermission) => new(
        hasPermission(Permissions.SalesCreate),
        hasPermission(Permissions.PaymentsCollect),
        hasPermission(Permissions.DiscountsApply),
        hasPermission(Permissions.SalesHistoryView),
        hasPermission(Permissions.SalesCancel),
        hasPermission(Permissions.RegisterOpen),
        hasPermission(Permissions.ReturnsCreate));
}

/// <summary>
/// Point d'accroche de l'impression : appelé une fois pour chaque vente et pour chaque retour
/// enregistrés, avec le détail renvoyé par le serveur. L'impression n'existe pas encore.
/// Une implémentation ne doit jamais lever d'exception : l'opération est déjà enregistrée.
/// </summary>
public interface ITicketPrinter
{
    void Print(SaleDetail ticket);

    /// <summary>Bon de retour, à remettre au client avec son remboursement.</summary>
    void PrintReturn(ReturnDetail receipt);
}

/// <summary>Aucune imprimante : rien n'est imprimé, rien n'est affiché.</summary>
public sealed class NoTicketPrinter : ITicketPrinter
{
    public void Print(SaleDetail ticket)
    {
    }

    public void PrintReturn(ReturnDetail receipt)
    {
    }
}

/// <summary>
/// Identifiant d'opération d'une commande d'écriture : le même tant que le contenu de la
/// demande est identique et que le serveur n'a pas répondu (retenter après une panne ne
/// crée donc jamais de doublon) ; un nouveau dès que le contenu change ou après une réponse.
/// </summary>
public sealed class OperationIdKeeper
{
    private string? signature;
    private Guid operationId;

    /// <param name="contentSignature">Tout ce que la demande contient, hors identifiant d'opération.</param>
    public Guid For(string contentSignature)
    {
        if (signature != contentSignature)
        {
            signature = contentSignature;
            operationId = Guid.NewGuid();
        }

        return operationId;
    }

    /// <summary>Le serveur a répondu (accepté ou refusé) : la prochaine demande aura un nouvel identifiant.</summary>
    public void Forget() => signature = null;
}

/// <summary>Lecture de ce que l'employé saisit : virgule ou point, espaces tolérés.</summary>
public static class SaleInput
{
    public const decimal MaxQuantity = 999_999.999m;

    /// <summary>Montant positif ou nul, avec au plus deux décimales.</summary>
    public static bool TryParseAmount(string? text, out decimal amount) =>
        TryParseNumber(text, out amount) && SaleMath.IsValidAmount(amount);

    /// <summary>Quantité strictement positive, avec au plus trois décimales.</summary>
    public static bool TryParseQuantity(string? text, out decimal quantity) =>
        TryParseNumber(text, out quantity) && quantity is > 0 and <= MaxQuantity && decimal.Round(quantity, 3) == quantity;

    /// <summary>
    /// Remise : « 500 » ou « 500,50 » est un montant, « 10 % » un pourcentage. Une saisie vide
    /// ou nulle retire la remise (<paramref name="kind"/> vaut alors <c>null</c>).
    /// </summary>
    public static bool TryParseDiscount(string? text, out string? kind, out decimal value)
    {
        kind = null;
        value = 0;
        var cleaned = (text ?? string.Empty).Trim();
        if (cleaned.Length == 0)
        {
            return true;
        }

        var isPercent = cleaned.EndsWith('%');
        if (isPercent)
        {
            cleaned = cleaned[..^1];
        }

        if (!TryParseNumber(cleaned, out value) || decimal.Round(value, 2) != value || (isPercent && value > 100))
        {
            return false;
        }

        kind = value == 0 ? null : isPercent ? DiscountKinds.Percent : DiscountKinds.Amount;
        return true;
    }

    private static bool TryParseNumber(string? text, out decimal value)
    {
        value = 0;
        var cleaned = new string((text ?? string.Empty).Where(character => !char.IsWhiteSpace(character)).ToArray());
        if (cleaned.EndsWith(Money.CurrencySymbol, StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned[..^Money.CurrencySymbol.Length];
        }

        // Un seul séparateur décimal, virgule ou point ; aucun signe, aucun séparateur de milliers.
        if (cleaned.Length == 0 || cleaned.Count(character => character is ',' or '.') > 1
            || !cleaned.All(character => char.IsAsciiDigit(character) || character is ',' or '.'))
        {
            return false;
        }

        return decimal.TryParse(
            cleaned.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }
}

/// <summary>Affichage des quantités, des dates et des mots du métier.</summary>
public static class SaleFormat
{
    private static readonly NumberFormatInfo QuantityFormat = new() { NumberDecimalSeparator = "," };

    /// <summary>3 devient « 3 », 0.25 devient « 0,25 ».</summary>
    public static string Quantity(decimal quantity) => quantity.ToString("0.###", QuantityFormat);

    public static string DateTime(DateTimeOffset moment) =>
        moment.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    public static string Percent(decimal percent) => $"{percent.ToString("0.##", QuantityFormat)} %";

    public static string PaymentMode(string mode) => mode switch
    {
        SalePaymentModes.Cash => "Espèces",
        SalePaymentModes.Card => "TPE",
        SalePaymentModes.Unpaid => "Impayé",
        _ => mode,
    };

    /// <summary>Où va la marchandise rendue, selon la destination portée par le motif.</summary>
    public static string StockDestination(string destination) => destination switch
    {
        ReturnStockDestinations.Sellable => "Remise en stock vendable",
        ReturnStockDestinations.SetAside => "Mise à l'écart",
        ReturnStockDestinations.None => "Pas de remise en stock",
        _ => destination,
    };

    /// <summary>Explication affichée sous la destination du motif choisi.</summary>
    public static string StockDestinationHint(string destination) => destination switch
    {
        ReturnStockDestinations.Sellable => "La marchandise rendue retourne dans le stock vendable du magasin.",
        ReturnStockDestinations.SetAside => "La marchandise rendue va dans l'emplacement « mis à l'écart » : elle ne peut pas être revendue.",
        ReturnStockDestinations.None => "La marchandise rendue sort du circuit : aucun stock n'est réintégré.",
        _ => string.Empty,
    };

    /// <summary>Comment le client est remboursé.</summary>
    public static string RefundMethod(string? method) => method switch
    {
        PaymentMethods.Cash => "En espèces — sortie de la caisse de ce poste",
        PaymentMethods.Card => "Par carte, sur le terminal — aucune sortie d'espèces",
        null => "Rien à rembourser",
        _ => method,
    };

    public static string Status(string status) => status switch
    {
        SaleStatuses.Completed => "Validée",
        SaleStatuses.CardPending => "En attente TPE",
        SaleStatuses.Cancelled => "Annulée",
        _ => status,
    };
}
