using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Pharmaly.Poste.Modules.Cash;

/// <summary>
/// Écran de caisse. Tout se fait au clavier et au lecteur : les touches arrivent ici avant les
/// contrôles, et le curseur est replacé au bon endroit à chaque changement d'onglet ou d'étape.
/// </summary>
public sealed partial class CashView : UserControl, IModuleView
{
    private CashViewModel? observed;

    public CashView()
    {
        InitializeComponent();

        // Un clic sur une ligne ou un bouton ne doit pas laisser le clavier sans destinataire.
        AddHandler(
            PointerReleasedEvent,
            (_, _) =>
            {
                if (!IsInFilterField())
                {
                    FocusAfterLayout();
                }
            },
            RoutingStrategies.Bubble,
            handledEventsToo: true);

        // Le « * » du clavier principal n'arrive pas toujours avec son symbole dans la touche
        // (X11, certaines dispositions) : il est alors reconnu au texte saisi, avant la recherche.
        AddHandler(TextInputEvent, OnTextInput, RoutingStrategies.Tunnel);
    }

    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (e.Text != CashShortcuts.CashLabel || ViewModel is not { IsReceivablesTab: true, IsSessionStepOpen: false } viewModel
            || !viewModel.Rights.CanViewReceivables)
        {
            return;
        }

        // Jamais écrit dans un champ : sur le tableau, il ouvre les espèces ; dans une étape, il est ignoré.
        e.Handled = true;
        viewModel.BeginCash();
    }

    private CashViewModel? ViewModel => DataContext as CashViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (observed is not null)
        {
            observed.PropertyChanged -= OnViewModelChanged;
        }

        observed = ViewModel;
        if (observed is not null)
        {
            observed.PropertyChanged += OnViewModelChanged;
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs change)
    {
        switch (change.PropertyName)
        {
            case nameof(CashViewModel.Tab) or nameof(CashViewModel.Step) or nameof(CashViewModel.SessionStep)
                or nameof(CashViewModel.IsConfirmingOpening) or nameof(CashViewModel.IsCashStateKnown)
                or nameof(CashViewModel.ClosingStep) or nameof(CashViewModel.ClosingMode):
                FocusAfterLayout();
                break;
            case nameof(CashViewModel.SelectedHistoryRow) when ViewModel?.SelectedHistoryRow is { } closure:
                HistoryList.ScrollIntoView(closure);
                break;
            case nameof(CashViewModel.OpenedClosure):
                ReportScroll.Offset = default;
                break;
            case nameof(CashViewModel.CurrentRow) when ViewModel?.CurrentRow is { } row:
                RowsList.ScrollIntoView(row);
                break;
            case nameof(CashViewModel.SelectedCustomerResult) when ViewModel?.SelectedCustomerResult is { } customer:
                CustomerList.ScrollIntoView(customer);
                break;
        }
    }

    private bool IsInFilterField() =>
        FromBox.IsKeyboardFocusWithin || ToBox.IsKeyboardFocusWithin || StatusBox.IsKeyboardFocusWithin;

    /// <summary>Le curseur va là où l'employé travaille : la recherche, ou le champ de l'étape ouverte.</summary>
    public void FocusDefault()
    {
        if (ViewModel is not { } viewModel)
        {
            Focus();
            return;
        }

        if (viewModel is { IsReceivablesTab: true, IsStepOpen: false } && IsInFilterField())
        {
            return;
        }

        var target = TargetFor(viewModel);
        if (target is TextBox { IsKeyboardFocusWithin: true })
        {
            return;
        }

        if (!target.Focus())
        {
            Focus();
        }

        if (target is TextBox box && !ReferenceEquals(box, SearchBox))
        {
            box.SelectAll();
        }
    }

    private Control TargetFor(CashViewModel viewModel)
    {
        if (viewModel.IsStepOpen)
        {
            return viewModel.Step == SettlementStep.CashAmount ? ReceivedBox : this;
        }

        if (viewModel.IsSessionStepOpen)
        {
            return viewModel.SessionStep switch
            {
                SessionStep.MovementInput => MovementReasonBox.IsFocused ? MovementReasonBox : MovementAmountBox,
                SessionStep.CloseCount => CountedCashBox,
                _ => this,
            };
        }

        if (viewModel.IsClosingStepOpen)
        {
            return viewModel.ClosingStep == ClosingStep.ForceReason ? ForceReasonBox : this;
        }

        return viewModel.Tab switch
        {
            CashTab.Receivables when viewModel.Rights.CanViewReceivables => SearchBox,
            CashTab.Session when viewModel is { CanShowOpening: true, IsConfirmingOpening: false } => OpeningFloatBox,
            _ => this,
        };
    }

    private void FocusAfterLayout() => Dispatcher.UIThread.Post(FocusDefault, DispatcherPriority.Loaded);

    public async Task HandleKeyAsync(KeyEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        if (viewModel.IsStepOpen)
        {
            await HandleSettlementStepKeyAsync(viewModel, e);
            return;
        }

        if (viewModel.IsSessionStepOpen)
        {
            await HandleSessionStepKeyAsync(viewModel, e);
            return;
        }

        if (viewModel.IsClosingStepOpen)
        {
            await HandleClosingStepKeyAsync(viewModel, e);
            return;
        }

        if (e.Key == Key.Tab && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            await viewModel.MoveTabAsync(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
            return;
        }

        if (CashShortcuts.IsControl(e, CashShortcuts.RefreshKey))
        {
            e.Handled = true;
            await viewModel.RefreshAsync();
            return;
        }

        switch (viewModel.Tab)
        {
            case CashTab.Receivables when viewModel.Rights.CanViewReceivables:
                await HandleReceivablesKeyAsync(viewModel, e);
                break;
            case CashTab.Session:
                await HandleSessionKeyAsync(viewModel, e);
                break;
            case CashTab.Closing when viewModel.Rights.CanViewReports:
                await HandleClosingKeyAsync(viewModel, e);
                break;
        }
    }

    private async Task HandleReceivablesKeyAsync(CashViewModel viewModel, KeyEventArgs e)
    {
        // Choisir le moyen n'est jamais confirmer : chaque touche ouvre une étape.
        if (CashShortcuts.IsCash(e))
        {
            e.Handled = true;
            viewModel.BeginCash();
            return;
        }

        if (CashShortcuts.IsCard(e))
        {
            e.Handled = true;
            viewModel.BeginCard();
            return;
        }

        var statusOpen = StatusBox.IsDropDownOpen;
        var searchIsEmpty = string.IsNullOrEmpty(viewModel.SearchText);
        switch (e.Key)
        {
            case CashShortcuts.StatusFilterKey when e.KeyModifiers == KeyModifiers.None:
                e.Handled = true;
                viewModel.CycleStatus();
                break;

            case CashShortcuts.PendingAnswerKey when e.KeyModifiers == KeyModifiers.Control:
                e.Handled = true;
                await viewModel.BeginPendingAnswerAsync();
                break;

            case Key.Enter when !statusOpen:
                e.Handled = true;
                await viewModel.SubmitSearchAsync();
                break;

            case Key.Down or Key.Up when !statusOpen && !StatusBox.IsKeyboardFocusWithin:
                e.Handled = true;
                viewModel.MoveCurrent(e.Key == Key.Down ? 1 : -1);
                break;

            case Key.Space when searchIsEmpty || !SearchBox.IsKeyboardFocusWithin:
                e.Handled = true;
                viewModel.ToggleCurrent();
                break;

            case Key.Delete when searchIsEmpty && SearchBox.IsKeyboardFocusWithin:
                e.Handled = true;
                viewModel.ClearSelection();
                break;

            case Key.Escape when !statusOpen:
                e.Handled = true;
                if (!searchIsEmpty)
                {
                    viewModel.SearchText = string.Empty;
                }
                else
                {
                    await viewModel.ClearCustomerFilterAsync();
                }

                SearchBox.Focus();
                break;
        }
    }

    private static async Task HandleSessionKeyAsync(CashViewModel viewModel, KeyEventArgs e)
    {
        if (CashShortcuts.IsAlone(e, CashShortcuts.CashInKey) && !viewModel.IsConfirmingOpening)
        {
            e.Handled = true;
            viewModel.BeginCashIn();
            return;
        }

        if (CashShortcuts.IsAlone(e, CashShortcuts.CashOutKey) && !viewModel.IsConfirmingOpening)
        {
            e.Handled = true;
            viewModel.BeginCashOut();
            return;
        }

        if (CashShortcuts.IsAlone(e, CashShortcuts.CloseKey) && !viewModel.IsConfirmingOpening)
        {
            e.Handled = true;
            viewModel.BeginCloseRegister();
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                if (viewModel.IsConfirmingOpening)
                {
                    await viewModel.ConfirmCashOpeningAsync();
                }
                else if (viewModel.CanShowOpening)
                {
                    viewModel.RequestCashOpening();
                }

                break;

            case Key.Escape:
                e.Handled = true;
                viewModel.CancelCashOpening();
                break;

            default:
                // Pendant la confirmation, aucune frappe ne modifie le montant affiché.
                e.Handled = viewModel.IsConfirmingOpening;
                break;
        }
    }

    /// <summary>
    /// Une étape de règlement est ouverte : le clavier ne sert qu'à elle. Seule la saisie du
    /// montant reçu laisse passer la frappe vers son champ.
    /// </summary>
    private static async Task HandleSettlementStepKeyAsync(CashViewModel viewModel, KeyEventArgs e)
    {
        var step = viewModel.Step;
        if (step != SettlementStep.CashAmount || e.Key is Key.Enter or Key.Escape or Key.Tab || CashShortcuts.IsCash(e))
        {
            e.Handled = true;
        }

        if (e.Key == Key.Escape)
        {
            await viewModel.CancelStepAsync();
            return;
        }

        switch (step)
        {
            case SettlementStep.ChooseCustomer when e.Key == Key.Enter:
                await viewModel.ChooseCustomerAsync();
                break;

            case SettlementStep.ChooseCustomer when e.Key is Key.Down or Key.Up:
                viewModel.MoveCustomerSelection(e.Key == Key.Down ? 1 : -1);
                break;

            case SettlementStep.CashAmount when e.Key == Key.Enter:
                viewModel.ValidateCashAmount();
                break;

            case SettlementStep.CashConfirm or SettlementStep.CardConfirm when e.Key == Key.Enter:
                await viewModel.ConfirmSettlementAsync();
                break;

            case SettlementStep.CashConfirm when e.Key is >= Key.D0 and <= Key.D9 or >= Key.NumPad0 and <= Key.NumPad9:
                // Des chiffres pendant la confirmation : retour à la saisie du montant, rien n'est confirmé.
                await viewModel.CancelStepAsync();
                break;

            // Réponse du TPE : une lettre par réponse, jamais Entrée.
            case SettlementStep.CardPending when e.Key == Sales.SalesShortcuts.CardAcceptedKey && e.KeyModifiers == KeyModifiers.None:
                await viewModel.AnswerCardAsync(true);
                break;

            case SettlementStep.CardPending when e.Key == Sales.SalesShortcuts.CardRefusedKey && e.KeyModifiers == KeyModifiers.None:
                await viewModel.AnswerCardAsync(false);
                break;

            case SettlementStep.Unconfirmed when e.Key == Key.Enter:
                await viewModel.RetrySettlementAsync();
                break;

            case SettlementStep.Done when e.Key == Key.Enter:
                await viewModel.FinishAsync();
                break;
        }
    }

    private async Task HandleSessionStepKeyAsync(CashViewModel viewModel, KeyEventArgs e)
    {
        var step = viewModel.SessionStep;
        if (step is not (SessionStep.MovementInput or SessionStep.CloseCount) || e.Key is Key.Enter or Key.Escape or Key.Tab)
        {
            e.Handled = true;
        }

        if (e.Key == Key.Escape)
        {
            if (step is SessionStep.CloseBlocked or SessionStep.CloseDone)
            {
                await viewModel.FinishCloseRegisterAsync();
            }
            else
            {
                viewModel.CancelSessionStep();
            }

            return;
        }

        switch (step)
        {
            case SessionStep.MovementInput when e.Key == Key.Tab:
                (MovementAmountBox.IsFocused ? MovementReasonBox : MovementAmountBox).Focus();
                break;

            case SessionStep.MovementInput when e.Key == Key.Enter && MovementAmountBox.IsFocused:
                MovementReasonBox.Focus();
                break;

            case SessionStep.MovementInput when e.Key == Key.Enter:
                viewModel.ValidateMovement();
                break;

            case SessionStep.MovementConfirm when e.Key == Key.Enter:
                await viewModel.ConfirmMovementAsync();
                break;

            case SessionStep.MovementUnconfirmed when e.Key == Key.Enter:
                await viewModel.RetryMovementAsync();
                break;

            case SessionStep.CloseCount when e.Key == Key.Enter:
                viewModel.ValidateCountedCash();
                break;

            case SessionStep.CloseConfirm when e.Key == Key.Enter:
                await viewModel.ConfirmCloseRegisterAsync();
                break;

            case SessionStep.CloseUnconfirmed when e.Key == Key.Enter:
                await viewModel.RetryCloseRegisterAsync();
                break;

            case SessionStep.CloseBlocked or SessionStep.CloseDone when e.Key == Key.Enter:
                await viewModel.FinishCloseRegisterAsync();
                break;
        }
    }

    private async Task HandleClosingKeyAsync(CashViewModel viewModel, KeyEventArgs e)
    {
        if (CashShortcuts.IsAlone(e, CashShortcuts.CloseKey))
        {
            e.Handled = true;
            viewModel.BeginCloseDay();
            return;
        }

        if (CashShortcuts.IsAlone(e, CashShortcuts.ForcedCloseKey))
        {
            e.Handled = true;
            viewModel.BeginForcedClose();
            return;
        }

        if (CashShortcuts.IsAlone(e, CashShortcuts.HistoryKey))
        {
            e.Handled = true;
            await viewModel.ToggleHistoryAsync();
            return;
        }

        if (viewModel.IsHistoryMode)
        {
            switch (e.Key)
            {
                case Key.Down or Key.Up:
                    e.Handled = true;
                    viewModel.MoveHistorySelection(e.Key == Key.Down ? 1 : -1);
                    break;
                case Key.Enter:
                    e.Handled = true;
                    await viewModel.OpenSelectedClosureAsync();
                    break;
                case Key.Escape:
                    e.Handled = true;
                    await viewModel.BackInClosingAsync();
                    break;
            }

            return;
        }

        if (e.Key == Key.Escape && viewModel.IsReportMode)
        {
            e.Handled = true;
            await viewModel.BackInClosingAsync();
            return;
        }

        // Le rapport est plus haut que l'écran : il défile au clavier.
        var scroll = viewModel.IsReportMode ? ReportScroll : DayScroll;
        switch (e.Key)
        {
            case Key.Down:
                e.Handled = true;
                scroll.LineDown();
                break;
            case Key.Up:
                e.Handled = true;
                scroll.LineUp();
                break;
            case Key.PageDown:
                e.Handled = true;
                scroll.PageDown();
                break;
            case Key.PageUp:
                e.Handled = true;
                scroll.PageUp();
                break;
            case Key.Home:
                e.Handled = true;
                scroll.ScrollToHome();
                break;
            case Key.End:
                e.Handled = true;
                scroll.ScrollToEnd();
                break;
        }
    }

    /// <summary>
    /// Une étape de clôture est ouverte : le clavier ne sert qu'à elle. La confirmation forte
    /// demande F10 : un appui machinal sur Entrée ne fige jamais une journée.
    /// </summary>
    private static async Task HandleClosingStepKeyAsync(CashViewModel viewModel, KeyEventArgs e)
    {
        var step = viewModel.ClosingStep;
        if (step != ClosingStep.ForceReason || e.Key is Key.Enter or Key.Escape or Key.Tab)
        {
            e.Handled = true;
        }

        if (e.Key == Key.Escape)
        {
            viewModel.CancelClosingStep();
            return;
        }

        switch (step)
        {
            case ClosingStep.ForceReason when e.Key == Key.Enter:
                viewModel.ValidateForceReason();
                break;

            case ClosingStep.Confirm when CashShortcuts.IsAlone(e, CashShortcuts.CloseKey):
                await viewModel.ConfirmCloseDayAsync();
                break;

            case ClosingStep.Unconfirmed when e.Key == Key.Enter:
                await viewModel.RetryCloseDayAsync();
                break;
        }
    }
}
