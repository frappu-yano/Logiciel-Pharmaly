using Avalonia.Controls;
using Avalonia.Input;

namespace Pharmaly.Poste.Modules.Cash;

public sealed partial class CashView : UserControl, IModuleView
{
    public CashView() => InitializeComponent();

    public void FocusDefault() => Focus();

    public Task HandleKeyAsync(KeyEventArgs e) => Task.CompletedTask;
}
