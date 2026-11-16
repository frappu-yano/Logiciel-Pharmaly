using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Pharmaly.Poste.Modules.Sales;

/// <summary>
/// Retours clients : sous-écran du module Vente. Le clavier suit les zones du modèle de vue
/// (ticket, quantités, motif, note), et le curseur est replacé à chaque changement.
/// </summary>
public sealed partial class ReturnsView : UserControl
{
    private ReturnsViewModel? observed;

    public ReturnsView()
    {
        InitializeComponent();

        // Un clic dans la quantité d'une ligne sélectionne cette ligne.
        ReturnLinesList.AddHandler(
            GotFocusEvent,
            (_, e) =>
            {
                if (e.Source is TextBox { DataContext: ReturnLineViewModel line } && ViewModel is { IsChoosing: true } viewModel)
                {
                    viewModel.SelectedLine = line;
                    viewModel.Field = ReturnField.Lines;
                }
            },
            RoutingStrategies.Bubble);
        NoteBox.GotFocus += (_, _) =>
        {
            if (ViewModel is { IsChoosing: true } viewModel)
            {
                viewModel.Field = ReturnField.Note;
            }
        };
    }

    private ReturnsViewModel? ViewModel => DataContext as ReturnsViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (observed is not null)
        {
            observed.PropertyChanged -= OnViewModelChanged;
            observed.History.PropertyChanged -= OnHistoryChanged;
        }

        observed = ViewModel;
        if (observed is not null)
        {
            observed.PropertyChanged += OnViewModelChanged;
            observed.History.PropertyChanged += OnHistoryChanged;
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs change)
    {
        switch (change.PropertyName)
        {
            case nameof(ReturnsViewModel.Step) or nameof(ReturnsViewModel.Field) or nameof(ReturnsViewModel.IsHistoryShown):
                FocusAfterLayout();
                break;
            case nameof(ReturnsViewModel.SelectedLine) when ViewModel?.SelectedLine is { } line:
                ReturnLinesList.ScrollIntoView(line);
                if (ViewModel.Field == ReturnField.Lines)
                {
                    FocusAfterLayout();
                }

                break;
            case nameof(ReturnsViewModel.SelectedReason) when ViewModel?.SelectedReason is { } reason:
                ReasonList.ScrollIntoView(reason);
                break;
        }
    }

    private void OnHistoryChanged(object? sender, PropertyChangedEventArgs change)
    {
        if (change.PropertyName == nameof(ReturnsHistoryViewModel.SelectedReturn) && ViewModel?.History.SelectedReturn is { } row)
        {
            ReturnsList.ScrollIntoView(row);
        }
    }

    private void FocusAfterLayout() => Dispatcher.UIThread.Post(FocusDefault, DispatcherPriority.Loaded);

    /// <summary>Le curseur va dans le champ de la zone active : ticket, quantité de la ligne, motif ou note.</summary>
    public void FocusDefault()
    {
        if (!IsEffectivelyVisible || ViewModel is not { } viewModel)
        {
            return;
        }

        if (viewModel.IsHistoryShown)
        {
            if (!ReturnsFromBox.IsKeyboardFocusWithin && !ReturnsToBox.IsKeyboardFocusWithin)
            {
                ReturnsTicketBox.Focus();
            }

            return;
        }

        switch (viewModel.Step)
        {
            case ReturnStep.Search:
                TicketBox.Focus();
                TicketBox.SelectAll();
                break;
            case ReturnStep.Choose when viewModel.Field == ReturnField.Lines:
                FocusSelectedQuantity(viewModel);
                break;
            case ReturnStep.Choose when viewModel.Field == ReturnField.Reason:
                ReasonList.Focus();
                break;
            case ReturnStep.Choose:
                NoteBox.Focus();
                break;
            default:
                Focus();
                break;
        }
    }

    private void FocusSelectedQuantity(ReturnsViewModel viewModel)
    {
        ReturnLinesList.UpdateLayout();
        if (viewModel.SelectedLine is { } line
            && ReturnLinesList.ContainerFromItem(line) is { } container
            && container.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() is { } box)
        {
            if (!box.IsFocused)
            {
                box.Focus();
            }

            box.SelectAll();
        }
        else
        {
            Focus();
        }
    }

    private static bool IsControl(KeyEventArgs e) => e.KeyModifiers.HasFlag(KeyModifiers.Control);

    /// <summary>Chiffre de 1 à 9 tapé sans modificateur, au clavier principal ou au pavé numérique.</summary>
    private static int? DigitOf(KeyEventArgs e) => e.Key switch
    {
        >= Key.D1 and <= Key.D9 when e.KeyModifiers == KeyModifiers.None => e.Key - Key.D0,
        >= Key.NumPad1 and <= Key.NumPad9 when e.KeyModifiers == KeyModifiers.None => e.Key - Key.NumPad0,
        _ => null,
    };

    /// <summary>
    /// Touches de l'écran des retours. Renvoie faux pour Échap quand rien n'est en cours : le
    /// module revient alors à la vente.
    /// </summary>
    public async Task<bool> HandleKeyAsync(KeyEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return false;
        }

        if (viewModel.IsHistoryShown)
        {
            await HandleHistoryKeyAsync(viewModel, e);
            return true;
        }

        switch (viewModel.Step)
        {
            case ReturnStep.Search:
                return await HandleSearchKeyAsync(viewModel, e);

            case ReturnStep.Choose:
                await HandleChooseKeyAsync(viewModel, e);
                return true;

            case ReturnStep.Confirm:
                e.Handled = true;
                if (e.Key == Key.Enter)
                {
                    await viewModel.ConfirmAsync();
                }
                else if (e.Key == Key.Escape)
                {
                    viewModel.Back();
                }

                return true;

            case ReturnStep.Unconfirmed:
                // Aucune sortie tant que le serveur n'a pas répondu : seul « Réessayer » est possible.
                e.Handled = true;
                if (e.Key == Key.Enter)
                {
                    await viewModel.RetryAsync();
                }

                return true;

            case ReturnStep.Done:
                e.Handled = true;
                if (e.Key is Key.Enter or Key.Escape)
                {
                    viewModel.StartNextReturn();
                }

                return true;
        }

        return true;
    }

    private static async Task<bool> HandleSearchKeyAsync(ReturnsViewModel viewModel, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                await viewModel.FindTicketAsync();
                return true;

            case Key.H when IsControl(e):
                e.Handled = true;
                await viewModel.ShowHistoryAsync();
                return true;

            case SalesShortcuts.OpenSaleInHistoryKey when IsControl(e):
                e.Handled = true;
                await viewModel.OpenSaleInHistoryAsync();
                return true;

            case Key.Escape when viewModel.HasTicket || viewModel.TicketQuery.Length > 0:
                e.Handled = true;
                viewModel.Back();
                return true;

            case Key.Escape:
                return false;

            case Key.Tab:
                e.Handled = true;
                return true;
        }

        return true;
    }

    private async Task HandleChooseKeyAsync(ReturnsViewModel viewModel, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            viewModel.Back();
            return;
        }

        if (e.Key == Key.Tab)
        {
            e.Handled = true;
            viewModel.NextField();
            return;
        }

        switch (viewModel.Field)
        {
            case ReturnField.Lines:
                switch (e.Key)
                {
                    case Key.Enter:
                        e.Handled = true;
                        await viewModel.NextFromLineAsync();
                        break;
                    case Key.Up or Key.Down:
                        e.Handled = true;
                        viewModel.MoveLine(e.Key == Key.Down ? 1 : -1);
                        break;
                }

                break;

            case ReturnField.Reason:
                e.Handled = true;
                if (e.Key == Key.Enter)
                {
                    viewModel.ValidateReason();
                }
                else if (e.Key is Key.Up or Key.Down)
                {
                    viewModel.MoveReason(e.Key == Key.Down ? 1 : -1);
                }
                else if (DigitOf(e) is { } number)
                {
                    viewModel.ChooseReasonNumber(number);
                }

                break;

            case ReturnField.Note:
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    await viewModel.RequestPreviewAsync();
                    if (viewModel.IsChoosing)
                    {
                        FocusAfterLayout();
                    }
                }

                break;
        }
    }

    private async Task HandleHistoryKeyAsync(ReturnsViewModel viewModel, KeyEventArgs e)
    {
        var history = viewModel.History;
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                viewModel.HideHistory();
                break;
            case Key.Enter:
                e.Handled = true;
                await history.SearchAsync();
                break;
            case Key.Up or Key.Down:
                e.Handled = true;
                history.MoveSelection(e.Key == Key.Down ? 1 : -1);
                break;
        }
    }
}
