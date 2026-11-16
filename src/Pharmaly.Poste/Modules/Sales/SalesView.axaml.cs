using Avalonia.Controls;
using Avalonia.Input;

namespace Pharmaly.Poste.Modules.Sales;

public sealed partial class SalesView : UserControl, IModuleView
{
    public SalesView() => InitializeComponent();

    public void FocusDefault() => Focus();

    public Task HandleKeyAsync(KeyEventArgs e) => Task.CompletedTask;
}
