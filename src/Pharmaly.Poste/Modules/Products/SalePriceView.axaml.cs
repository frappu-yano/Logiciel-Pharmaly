using Avalonia.Controls;
using Avalonia.Input;

namespace Pharmaly.Poste.Modules.Products;

public sealed partial class SalePriceView : UserControl, IProductFormView
{
    public SalePriceView()
    {
        InitializeComponent();
        CancelButton.Click += (_, _) => CancelAndRefocus();
        SubmitButton.Click += (_, _) => RefocusLater();
    }

    private SalePriceViewModel? ViewModel => DataContext as SalePriceViewModel;

    public void FocusDefault() => NewPriceBox.Focus();

    public async Task HandleKeyAsync(KeyEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                CancelAndRefocus();
                break;

            case Key.Enter:
                e.Handled = true;
                await viewModel.SubmitAsync();
                FormFocus.Restore(this, viewModel, NewPriceBox);
                break;

            default:
                // Pendant la confirmation, le clavier ne sert qu'à y répondre.
                e.Handled = viewModel.IsConfirming;
                break;
        }
    }

    private void CancelAndRefocus()
    {
        if (ViewModel is { } viewModel)
        {
            viewModel.Cancel();
            FormFocus.Restore(this, viewModel, NewPriceBox);
        }
    }

    private void RefocusLater() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        if (ViewModel is { } viewModel)
        {
            FormFocus.Restore(this, viewModel, NewPriceBox);
        }
    });
}
