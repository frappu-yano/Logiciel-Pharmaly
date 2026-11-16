using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace Pharmaly.Poste.Modules.Sales;

/// <summary>Historique des ventes : sous-écran du module Vente, piloté au clavier comme lui.</summary>
public sealed partial class SalesHistoryView : UserControl
{
    private SalesHistoryViewModel? observed;

    public SalesHistoryView() => InitializeComponent();

    private SalesHistoryViewModel? ViewModel => DataContext as SalesHistoryViewModel;

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
            case nameof(SalesHistoryViewModel.Step):
                Dispatcher.UIThread.Post(FocusDefault, DispatcherPriority.Loaded);
                break;
            case nameof(SalesHistoryViewModel.SelectedSale) when ViewModel?.SelectedSale is { } sale:
                SalesList.ScrollIntoView(sale);
                break;
        }
    }

    /// <summary>Le curseur va dans la recherche de ticket (le lecteur y lit le code-barres), ou dans le champ de l'étape ouverte.</summary>
    public void FocusDefault()
    {
        if (!IsEffectivelyVisible)
        {
            return;
        }

        switch (ViewModel?.Step)
        {
            case HistoryStep.CancelReason:
                ReasonBox.Focus();
                break;
            case HistoryStep.CancelConfirm or HistoryStep.CardAnswer:
                Focus();
                break;
            default:
                TicketBox.Focus();
                TicketBox.SelectAll();
                break;
        }
    }

    public async Task HandleKeyAsync(KeyEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        switch (viewModel.Step)
        {
            case HistoryStep.None:
                switch (e.Key)
                {
                    case Key.Enter when !StatusBox.IsDropDownOpen:
                        e.Handled = true;
                        await viewModel.SearchAsync();
                        break;

                    case Key.Down or Key.Up when !StatusBox.IsDropDownOpen && !StatusBox.IsKeyboardFocusWithin:
                        e.Handled = true;
                        viewModel.MoveSelection(e.Key == Key.Down ? 1 : -1);
                        break;

                    case Key.Delete when control:
                        e.Handled = true;
                        viewModel.BeginCancel();
                        break;

                    case Key.T when control:
                        e.Handled = true;
                        viewModel.BeginCardAnswer();
                        break;

                    case SalesShortcuts.ReturnsKey when control:
                        e.Handled = true;
                        await viewModel.BeginReturnAsync();
                        break;
                }

                break;

            case HistoryStep.CancelReason:
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    viewModel.ValidateCancelReason();
                }
                else if (e.Key is Key.Escape or Key.Tab)
                {
                    e.Handled = true;
                    if (e.Key == Key.Escape)
                    {
                        viewModel.CloseStep();
                    }
                }

                break;

            case HistoryStep.CancelConfirm:
                // Confirmation forte : une touche dédiée, jamais Entrée.
                e.Handled = true;
                if (e.Key == SalesShortcuts.CancelSaleConfirmKey && e.KeyModifiers == KeyModifiers.None)
                {
                    await viewModel.ConfirmCancelAsync();
                }
                else if (e.Key == Key.Escape)
                {
                    viewModel.CloseStep();
                }

                break;

            case HistoryStep.CardAnswer:
                e.Handled = true;
                if (e.Key == SalesShortcuts.CardAcceptedKey && e.KeyModifiers == KeyModifiers.None)
                {
                    await viewModel.AnswerCardAsync(true);
                }
                else if (e.Key == SalesShortcuts.CardRefusedKey && e.KeyModifiers == KeyModifiers.None)
                {
                    await viewModel.AnswerCardAsync(false);
                }
                else if (e.Key == Key.Escape)
                {
                    viewModel.CloseStep();
                }

                break;
        }
    }
}
