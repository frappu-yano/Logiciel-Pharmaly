using Avalonia.Controls;
using Avalonia.Input;

namespace Pharmaly.Poste.Modules.Products;

public sealed partial class ProductsView : UserControl, IModuleView
{
    public ProductsView() => InitializeComponent();

    public void FocusDefault() => Focus();

    public Task HandleKeyAsync(KeyEventArgs e) => Task.CompletedTask;
}
