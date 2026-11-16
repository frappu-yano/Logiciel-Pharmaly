using Avalonia.Controls;
using Avalonia.Input;

namespace Pharmaly.Poste.Modules.Products;

/// <summary>
/// Vue du formulaire de création. L'écran qui l'accueille lui transmet les touches par
/// <see cref="HandleKeyAsync"/> et place le curseur avec <see cref="FocusDefault"/>.
/// </summary>
public sealed partial class ProductCreateView : UserControl, IProductFormView
{
    public ProductCreateView()
    {
        InitializeComponent();
        CancelButton.Click += (_, _) => ViewModel?.Cancel();
    }

    private ProductCreateViewModel? ViewModel => DataContext as ProductCreateViewModel;

    /// <summary>Le curseur va sur la désignation : le code-barres scanné, s'il y en a un, est déjà inscrit.</summary>
    public void FocusDefault() => NameBox.Focus();

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

            case Key.Enter when control || PriceBox.IsFocused:
                e.Handled = true;
                await viewModel.SubmitAsync();
                break;

            case Key.Enter when NameBox.IsFocused:
                e.Handled = true;
                BarcodeBox.Focus();
                break;

            case Key.Enter when BarcodeBox.IsFocused:
                // Un lecteur termine par Entrée : le code rejoint la liste et le champ attend le suivant.
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

            case Key.Enter when CategoryBox.IsFocused || UnitBox.IsFocused:
                e.Handled = true;
                PriceBox.Focus();
                break;
        }
    }
}
