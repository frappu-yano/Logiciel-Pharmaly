using Avalonia.Controls;
using Avalonia.Input;

namespace Pharmaly.Poste.Modules.Products;

public sealed partial class ProductEditView : UserControl, IProductFormView
{
    public ProductEditView()
    {
        InitializeComponent();
        CancelButton.Click += (_, _) => ViewModel?.Cancel();
    }

    private ProductEditViewModel? ViewModel => DataContext as ProductEditViewModel;

    public void FocusDefault()
    {
        NameBox.Focus();
        NameBox.CaretIndex = NameBox.Text?.Length ?? 0;
    }

    public async Task HandleKeyAsync(KeyEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        if (viewModel.IsBusy)
        {
            // Envoi en cours : ni second envoi, ni abandon tant que la réponse n'est pas connue.
            e.Handled = e.Key is Key.Enter or Key.Escape;
            return;
        }

        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                viewModel.Cancel();
                break;

            case Key.Enter when control || CategoryBox.IsFocused || ActiveBox.IsFocused:
                e.Handled = true;
                await viewModel.SubmitAsync();
                break;

            case Key.Enter when NameBox.IsFocused:
                e.Handled = true;
                BarcodeBox.Focus();
                break;

            case Key.Enter when BarcodeBox.IsFocused:
                e.Handled = true;
                if (!viewModel.Barcodes.AddEntry())
                {
                    BrandBox.Focus();
                }

                break;

            case Key.Back when BarcodeBox.IsFocused && string.IsNullOrEmpty(viewModel.Barcodes.Entry):
                e.Handled = true;
                viewModel.Barcodes.RemoveLast();
                break;

            case Key.Enter when BrandBox.IsFocused:
                e.Handled = true;
                CategoryBox.Focus();
                break;
        }
    }
}
