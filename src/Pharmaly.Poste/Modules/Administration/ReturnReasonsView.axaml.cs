using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace Pharmaly.Poste.Modules.Administration;

/// <summary>Onglet « motifs de retour » de l'administration, piloté au clavier.</summary>
public sealed partial class ReturnReasonsView : UserControl
{
    private bool listAwaitsFocus;
    private ReturnReasonsViewModel? observed;

    public ReturnReasonsView() => InitializeComponent();

    private ReturnReasonsViewModel? ViewModel => DataContext as ReturnReasonsViewModel;

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

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs change)
    {
        // La liste arrive du serveur après l'ouverture de l'onglet : le curseur passe sur le motif sélectionné.
        if (change.PropertyName == nameof(ReturnReasonsViewModel.SelectedReason) && listAwaitsFocus)
        {
            FocusAfterLayout();
        }
    }

    private void FocusAfterLayout() => Dispatcher.UIThread.Post(FocusDefault, DispatcherPriority.Loaded);

    /// <summary>Le curseur va sur la liste des motifs.</summary>
    public void FocusDefault()
    {
        ReasonsList.UpdateLayout();
        if (ReasonsList.SelectedIndex >= 0 && ReasonsList.ContainerFromIndex(ReasonsList.SelectedIndex) is { } container)
        {
            listAwaitsFocus = false;
            container.Focus();
        }
        else
        {
            listAwaitsFocus = true;
            ReasonsList.Focus();
        }
    }

    /// <summary>↑ ↓ sur une liste déroulante fermée : choix précédent ou suivant, sans l'ouvrir.</summary>
    private static bool MoveChoice(ComboBox box, KeyEventArgs e)
    {
        if (!box.IsKeyboardFocusWithin || box.IsDropDownOpen || e.Key is not (Key.Up or Key.Down) || box.ItemCount == 0)
        {
            return false;
        }

        box.SelectedIndex = Math.Clamp(box.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, box.ItemCount - 1);
        e.Handled = true;
        return true;
    }

    public async Task HandleKeyAsync(KeyEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        if (viewModel.IsConfirming)
        {
            // Tant que la confirmation est affichée, le clavier ne sert qu'à y répondre.
            e.Handled = true;
            if (e.Key == Key.Enter)
            {
                await viewModel.ConfirmAsync();
                FocusAfterLayout();
            }
            else if (e.Key == Key.Escape)
            {
                viewModel.CancelConfirmation();
                FocusAfterLayout();
            }

            return;
        }

        if (MoveChoice(EditDestinationBox, e) || MoveChoice(NewDestinationBox, e))
        {
            return;
        }

        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        switch (e.Key)
        {
            case Key.N when control:
                e.Handled = true;
                NewReasonLabelBox.Focus();
                break;

            case Key.S when control:
                e.Handled = true;
                await viewModel.SaveAsync();
                break;

            case Key.D when control:
                e.Handled = true;
                await viewModel.ToggleStateAsync();
                break;

            case Key.Escape:
                e.Handled = true;
                FocusDefault();
                break;

            case Key.Up or Key.Down when ReasonsList.IsKeyboardFocusWithin:
                e.Handled = true;
                viewModel.MoveSelection(e.Key == Key.Down ? 1 : -1);
                FocusAfterLayout();
                break;

            case Key.Enter when ReasonsList.IsKeyboardFocusWithin:
                e.Handled = true;
                EditLabelBox.Focus();
                EditLabelBox.SelectAll();
                break;

            case Key.Enter when EditLabelBox.IsFocused:
                e.Handled = true;
                EditDestinationBox.Focus();
                break;

            case Key.Enter when EditDestinationBox.IsKeyboardFocusWithin && !EditDestinationBox.IsDropDownOpen:
                e.Handled = true;
                await viewModel.SaveAsync();
                FocusAfterLayout();
                break;

            case Key.Enter when NewReasonLabelBox.IsFocused:
                e.Handled = true;
                NewDestinationBox.Focus();
                break;

            case Key.Enter when NewDestinationBox.IsKeyboardFocusWithin && !NewDestinationBox.IsDropDownOpen:
                e.Handled = true;
                await viewModel.CreateAsync();
                if (viewModel.ErrorMessage.Length == 0)
                {
                    FocusAfterLayout();
                }
                else
                {
                    NewReasonLabelBox.Focus();
                }

                break;
        }
    }
}
