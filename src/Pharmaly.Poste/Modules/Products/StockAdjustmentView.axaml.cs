using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace Pharmaly.Poste.Modules.Products;

public sealed partial class StockAdjustmentView : UserControl, IProductFormView
{
    public StockAdjustmentView()
    {
        InitializeComponent();
        CancelButton.Click += (_, _) => CancelAndRefocus();
        SubmitButton.Click += (_, _) => Dispatcher.UIThread.Post(Refocus);
    }

    private StockAdjustmentViewModel? ViewModel => DataContext as StockAdjustmentViewModel;

    /// <summary>Le curseur va sur l'emplacement : ↑ ↓ pour en changer, Entrée pour passer à la quantité.</summary>
    public void FocusDefault() => LocationBox.Focus();

    public async Task HandleKeyAsync(KeyEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelAndRefocus();
            return;
        }

        if (viewModel.IsConfirming || viewModel.IsBusy)
        {
            // Pendant la confirmation, le clavier ne sert qu'à y répondre.
            e.Handled = true;
            if (e.Key == Key.Enter)
            {
                await viewModel.SubmitAsync();
                Refocus();
            }

            return;
        }

        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        switch (e.Key)
        {
            case Key.F8:
                e.Handled = true;
                viewModel.IsCount = !viewModel.IsCount;
                QuantityBox.Focus();
                break;

            case Key.Enter when control || ReasonBox.IsFocused:
                e.Handled = true;
                await viewModel.SubmitAsync();
                Refocus();
                break;

            case Key.Enter when LocationBox.IsFocused || CountRadio.IsFocused || CorrectionRadio.IsFocused:
                e.Handled = true;
                QuantityBox.Focus();
                break;

            case Key.Enter when QuantityBox.IsFocused:
                e.Handled = true;
                ReasonBox.Focus();
                break;
        }
    }

    private void CancelAndRefocus()
    {
        ViewModel?.Cancel();
        Refocus();
    }

    private void Refocus()
    {
        if (ViewModel is { } viewModel)
        {
            FormFocus.Restore(this, viewModel, QuantityBox);
        }
    }
}
