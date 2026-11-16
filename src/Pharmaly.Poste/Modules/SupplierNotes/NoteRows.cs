using CommunityToolkit.Mvvm.ComponentModel;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Poste.Services.Server;

namespace Pharmaly.Poste.Modules.SupplierNotes;

/// <summary>Résultat d'un appel au serveur fait par un écran du module.</summary>
/// <param name="Refusal">Le serveur a refusé : rien n'a été enregistré.</param>
/// <param name="Unreachable">Pas de réponse (panne de réseau, délai dépassé) : on ne sait pas si la demande est arrivée.</param>
public sealed record ServerCallResult(ServerRefusedException? Refusal, bool Unreachable)
{
    public static readonly ServerCallResult Success = new(null, false);

    public bool Succeeded => Refusal is null && !Unreachable;

    public bool IsRefusal(params string[] codes) => Refusal is not null && codes.Contains(Refusal.Code);
}

/// <summary>Exécute un appel au serveur sans jamais lever : le résultat dit ce qui s'est passé.</summary>
public delegate Task<ServerCallResult> ServerCaller(Func<Task> action);

public static class NoteStatusLabels
{
    public static string Of(string status) => status switch
    {
        SupplierNoteStatuses.InProgress => "En saisie",
        SupplierNoteStatuses.Validated => "Validé",
        SupplierNoteStatuses.Cancelled => "Annulé",
        _ => status,
    };
}

/// <summary>Choix d'un filtre de la liste ; <c>Value</c> nul = tout afficher.</summary>
public sealed record FilterChoice<T>(T? Value, string Label)
    where T : class
{
    public override string ToString() => Label;
}

/// <summary>Ligne de la liste des bons.</summary>
public sealed class NoteRowViewModel(SupplierNoteSummary note)
{
    public SupplierNoteSummary Note { get; } = note;

    public Guid Id => Note.Id;

    public string NumberText => Note.Number.ToString();

    public string DateText => NoteInput.FormatDate(Note.NoteDate);

    public string SupplierName => Note.SupplierName;

    public string Reference => Note.SupplierReference;

    public string LocationName => Note.LocationName;

    public string StatusText => NoteStatusLabels.Of(Note.Status);

    public bool IsInProgress => Note.Status == SupplierNoteStatuses.InProgress;

    public bool IsCancelled => Note.Status == SupplierNoteStatuses.Cancelled;

    /// <summary>Qui saisit le bon en ce moment, s'il est tenu.</summary>
    public string LockText => Note.Lock switch
    {
        { IsExpired: false, HeldByMe: true } => "Tenu par vous",
        { IsExpired: false } held => $"Saisie : {held.EmployeeName}",
        _ => string.Empty,
    };

    public bool IsHeld => LockText.Length > 0;

    public string LineCountText => Note.LineCount.ToString();

    public string TotalText => Money.ToDisplay(Note.TotalAmount);
}

/// <summary>Ligne d'un bon, telle que le serveur l'a enregistrée.</summary>
public sealed class NoteLineRowViewModel(SupplierNoteLine line)
{
    public SupplierNoteLine Line { get; } = line;

    public Guid Id => Line.Id;

    public string PositionText => Line.Position.ToString();

    public string ProductName => Line.ProductName;

    public string Barcode => Line.Barcode ?? string.Empty;

    public string QuantityText => NoteInput.FormatQuantity(Line.Quantity);

    public string PriceText => Money.ToDisplay(Line.PurchasePrice);

    public string TotalText => Line.IsCancelled ? "Annulée" : Money.ToDisplay(Line.LineTotal);

    public bool IsCancelled => Line.IsCancelled;

    /// <summary>Marge de la ligne au prix de vente actuel.</summary>
    public string MarginText => Line.Margin.MarginAfterPercent is { } percent
        ? $"{Money.ToDisplay(Line.Margin.MarginAfter)} · {NoteInput.FormatPercent(percent)}"
        : Money.ToDisplay(Line.Margin.MarginAfter);

    /// <summary>La marge a baissé par rapport au dernier achat, ou le produit est vendu à perte.</summary>
    public bool MarginDropped => !Line.IsCancelled && LineOutcomeViewModel.HasDropped(Line.Margin);
}

public sealed class NoteHistoryRowViewModel(SupplierNoteHistoryEntry entry)
{
    public string MomentText => NoteInput.FormatMoment(entry.OccurredAt);

    public string EmployeeName => entry.EmployeeName;

    public string Description => entry.Description;

    /// <summary>Changement d'état, s'il y en a un : « En saisie → Validé ».</summary>
    public string StatusText => entry.StatusBefore is null
        ? NoteStatusLabels.Of(entry.StatusAfter)
        : entry.StatusBefore == entry.StatusAfter
            ? string.Empty
            : $"{NoteStatusLabels.Of(entry.StatusBefore)} → {NoteStatusLabels.Of(entry.StatusAfter)}";
}

/// <summary>
/// Ce que la dernière écriture d'une ligne a produit : stock avant → après, et variation de
/// marge. Affiché sans gêner la saisie suivante ; le prix de vente n'y est jamais modifiable.
/// </summary>
public sealed class LineOutcomeViewModel
{
    public LineOutcomeViewModel(SupplierNoteLineResult result, string action)
    {
        var line = result.Line;
        var margin = line.Margin;
        Title = $"Ligne {line.Position} {action} — {line.ProductName}";
        var change = result.StockChange == 0
            ? "inchangé"
            : $"{(result.StockChange > 0 ? "+" : "−")}{NoteInput.FormatQuantity(Math.Abs(result.StockChange))}";
        StockText = $"Stock : {NoteInput.FormatQuantity(result.StockBefore)} → {NoteInput.FormatQuantity(result.StockAfter)} ({change})";
        HasMargin = !line.IsCancelled;
        MarginDropped = HasMargin && HasDropped(margin);

        var purchase = margin.PreviousPurchasePrice is { } previous && previous != margin.NewPurchasePrice
            ? $"achat {Money.ToDisplay(previous)} → {Money.ToDisplay(margin.NewPurchasePrice)}"
            : $"achat {Money.ToDisplay(margin.NewPurchasePrice)}";
        PriceText = $"Vente {Money.ToDisplay(margin.SalePrice)} · {purchase}";

        var after = Describe(margin.MarginAfter, margin.MarginAfterPercent);
        MarginText = margin.MarginBefore is { } before
            ? before == margin.MarginAfter
                ? $"Marge inchangée : {after}"
                : $"Marge : {Describe(before, margin.MarginBeforePercent)} → {after}"
            : $"Marge (premier achat connu) : {after}";
        if (MarginDropped)
        {
            MarginText += margin.MarginAfter < 0 ? " — vente à perte" : " — la marge baisse";
        }
    }

    public string Title { get; }

    public string StockText { get; }

    public bool HasMargin { get; }

    public string PriceText { get; }

    public string MarginText { get; }

    /// <summary>À mettre en évidence : la marge baisse, ou devient négative.</summary>
    public bool MarginDropped { get; }

    public static bool HasDropped(MarginVariation margin) =>
        margin.MarginAfter < 0 || (margin.MarginBefore is { } before && margin.MarginAfter < before);

    private static string Describe(decimal margin, decimal? percent) =>
        percent is { } value ? $"{Money.ToDisplay(margin)} ({NoteInput.FormatPercent(value)})" : Money.ToDisplay(margin);
}

/// <summary>Opération critique en attente de confirmation, avec un motif quand elle en demande un.</summary>
public sealed partial class NoteConfirmationViewModel(string message, string confirmLabel, bool asksReason, Func<string?, Task> action)
    : ObservableObject
{
    public string Message { get; } = message;

    public string ConfirmLabel { get; } = confirmLabel;

    public bool AsksReason { get; } = asksReason;

    public Func<string?, Task> Action { get; } = action;

    [ObservableProperty]
    private string reason = string.Empty;
}
