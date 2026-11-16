using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pharmaly.Contracts;
using Pharmaly.Poste.Modules.Sales;
using Pharmaly.Poste.Services.Server;

namespace Pharmaly.Poste.Modules.Administration;

/// <summary>Destination du stock proposée pour un motif.</summary>
/// <param name="Code">Une valeur de <see cref="ReturnStockDestinations"/>.</param>
public sealed record DestinationChoice(string Code, string Label)
{
    public static readonly IReadOnlyList<DestinationChoice> All =
    [
        new(ReturnStockDestinations.Sellable, SaleFormat.StockDestination(ReturnStockDestinations.Sellable)),
        new(ReturnStockDestinations.SetAside, SaleFormat.StockDestination(ReturnStockDestinations.SetAside)),
        new(ReturnStockDestinations.None, SaleFormat.StockDestination(ReturnStockDestinations.None)),
    ];

    public static DestinationChoice For(string code) => All.FirstOrDefault(choice => choice.Code == code) ?? All[0];

    public override string ToString() => Label;
}

/// <summary>Ligne de la liste des motifs.</summary>
public sealed class ReasonRowViewModel(ReturnReason reason)
{
    public ReturnReason Reason { get; } = reason;

    public Guid Id => Reason.Id;

    public string Label => Reason.Label;

    public string DestinationText => SaleFormat.StockDestination(Reason.StockDestination);

    public bool IsActive => Reason.IsActive;

    public string StateText => Reason.IsActive ? "Actif" : "Inactif";
}

/// <summary>
/// Administration des motifs de retour (D36 : droit « administrer les utilisateurs ») : liste
/// des motifs actifs et inactifs, création, modification, désactivation après confirmation.
/// Le serveur fait foi : chaque écriture est suivie d'une relecture de la liste.
/// </summary>
public sealed partial class ReturnReasonsViewModel(IReturnsClient returns, Func<ServerRefusedException, bool> requireIdentificationIfLost)
    : ObservableObject
{
    public const string UnconfirmedMessage =
        "Le serveur n'a pas répondu : on ne sait pas si la modification est enregistrée. Recommencez : la même demande est renvoyée, sans doublon.";

    private readonly OperationIdKeeper createOperationIds = new();
    private readonly OperationIdKeeper updateOperationIds = new();

    public ObservableCollection<ReasonRowViewModel> Reasons { get; } = [];

    public IReadOnlyList<DestinationChoice> Destinations => DestinationChoice.All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(ToggleStateLabel), nameof(SelectedTitle))]
    private ReasonRowViewModel? selectedReason;

    /// <summary>Libellé du motif sélectionné, tel que modifié.</summary>
    [ObservableProperty]
    private string editLabel = string.Empty;

    [ObservableProperty]
    private DestinationChoice editDestination = DestinationChoice.All[0];

    [ObservableProperty]
    private string newLabel = string.Empty;

    [ObservableProperty]
    private DestinationChoice newDestination = DestinationChoice.All[0];

    [ObservableProperty]
    private string errorMessage = string.Empty;

    [ObservableProperty]
    private string infoMessage = string.Empty;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirming))]
    private ConfirmationViewModel? confirmation;

    public bool IsConfirming => Confirmation is not null;

    public bool HasSelection => SelectedReason is not null;

    public string SelectedTitle => SelectedReason is { } row ? $"{row.Label} — {row.StateText}" : string.Empty;

    public string ToggleStateLabel => SelectedReason is { IsActive: false } ? "Réactiver le motif" : "Désactiver le motif";

    partial void OnSelectedReasonChanged(ReasonRowViewModel? value)
    {
        EditLabel = value?.Label ?? string.Empty;
        EditDestination = DestinationChoice.For(value?.Reason.StockDestination ?? ReturnStockDestinations.Sellable);
    }

    /// <summary>Relit tous les motifs, actifs et inactifs.</summary>
    public async Task<bool> LoadAsync(Guid? selectId = null)
    {
        selectId ??= SelectedReason?.Id;
        IReadOnlyList<ReturnReason> all = [];
        if (!await RunAsync(async () => all = await returns.ListReasonsAsync(true, CancellationToken.None)))
        {
            return false;
        }

        Reasons.Clear();
        foreach (var reason in all)
        {
            Reasons.Add(new ReasonRowViewModel(reason));
        }

        SelectedReason = Reasons.FirstOrDefault(row => row.Id == selectId) ?? Reasons.FirstOrDefault();
        return true;
    }

    public void MoveSelection(int delta)
    {
        if (Reasons.Count == 0)
        {
            return;
        }

        var current = SelectedReason is null ? -1 : Reasons.IndexOf(SelectedReason);
        SelectedReason = Reasons[Math.Clamp(current + delta, 0, Reasons.Count - 1)];
    }

    /// <summary>Crée un motif (libellé et destination du stock).</summary>
    [RelayCommand]
    public async Task CreateAsync()
    {
        InfoMessage = string.Empty;
        var label = NewLabel.Trim();
        if (label.Length == 0)
        {
            ErrorMessage = "Le libellé du motif est obligatoire.";
            return;
        }

        var destination = NewDestination.Code;
        var request = new CreateReturnReasonRequest(createOperationIds.For($"{label}|{destination}"), label, destination);
        ReturnReason? created = null;
        if (!await WriteAsync(createOperationIds, async () => created = await returns.CreateReasonAsync(request, CancellationToken.None)))
        {
            return;
        }

        NewLabel = string.Empty;
        NewDestination = DestinationChoice.All[0];
        if (await LoadAsync(created!.Id))
        {
            InfoMessage = $"Motif « {created.Label} » créé : {SaleFormat.StockDestination(created.StockDestination).ToLowerInvariant()}.";
        }
    }

    /// <summary>Enregistre le libellé et la destination du motif sélectionné ; son état ne change pas.</summary>
    [RelayCommand]
    public async Task SaveAsync()
    {
        InfoMessage = string.Empty;
        if (SelectedReason is not { } row)
        {
            return;
        }

        var label = EditLabel.Trim();
        if (label.Length == 0)
        {
            ErrorMessage = "Le libellé du motif est obligatoire.";
            return;
        }

        if (await UpdateAsync(row, label, EditDestination.Code, row.IsActive))
        {
            InfoMessage = $"Motif « {label} » enregistré. Les retours déjà faits gardent le motif de leur époque.";
        }
    }

    /// <summary>Désactive le motif sélectionné (après confirmation) ou le réactive.</summary>
    [RelayCommand]
    public async Task ToggleStateAsync()
    {
        InfoMessage = string.Empty;
        if (SelectedReason is not { } row)
        {
            return;
        }

        if (!row.IsActive)
        {
            if (await UpdateAsync(row, row.Label, row.Reason.StockDestination, true))
            {
                InfoMessage = $"Motif « {row.Label} » réactivé : il est de nouveau proposé aux retours.";
            }

            return;
        }

        ErrorMessage = string.Empty;
        Confirmation = new ConfirmationViewModel(
            $"Désactiver le motif « {row.Label} » ? Il ne sera plus proposé pour les nouveaux retours ; les retours déjà faits le gardent.",
            "Désactiver",
            async () =>
            {
                if (await UpdateAsync(row, row.Label, row.Reason.StockDestination, false))
                {
                    InfoMessage = $"Motif « {row.Label} » désactivé.";
                }
            });
    }

    private async Task<bool> UpdateAsync(ReasonRowViewModel row, string label, string destination, bool isActive)
    {
        var request = new UpdateReturnReasonRequest(
            updateOperationIds.For($"{row.Id}|{label}|{destination}|{isActive}"), label, destination, isActive);
        return await WriteAsync(updateOperationIds, () => returns.UpdateReasonAsync(row.Id, request, CancellationToken.None))
            && await LoadAsync(row.Id);
    }

    [RelayCommand]
    public async Task ConfirmAsync()
    {
        if (Confirmation is not { } pending)
        {
            return;
        }

        Confirmation = null;
        await pending.Action();
    }

    [RelayCommand]
    public void CancelConfirmation() => Confirmation = null;

    /// <summary>
    /// Écriture : un refus clôt la demande et s'affiche tel quel ; sans réponse, l'identifiant
    /// d'opération est gardé pour que recommencer ne crée pas de doublon.
    /// </summary>
    private async Task<bool> WriteAsync(OperationIdKeeper operationIds, Func<Task> write)
    {
        var unknown = false;
        var done = await RunAsync(async () =>
        {
            try
            {
                await write();
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                                  or ServerRefusedException { Code: SalesViewModel.UnreadableAnswerCode })
            {
                unknown = true;
                throw new HttpRequestException(UnconfirmedMessage, exception);
            }
        });
        if (!unknown)
        {
            operationIds.Forget();
        }
        else
        {
            ErrorMessage = UnconfirmedMessage;
        }

        return done;
    }

    private async Task<bool> RunAsync(Func<Task> action)
    {
        if (IsBusy)
        {
            return false;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            await action();
            return true;
        }
        catch (ServerRefusedException refused)
        {
            ErrorMessage = refused.Message;
            requireIdentificationIfLost(refused);
            return false;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            ErrorMessage = ModuleViewModel.ServerUnreachableMessage;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
