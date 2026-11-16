using Avalonia.Controls;
using Avalonia.Input;

namespace Pharmaly.Poste.Modules.SupplierNotes;

public sealed partial class SupplierNotesView : UserControl, IModuleView
{
    public SupplierNotesView() => InitializeComponent();

    public void FocusDefault() => Focus();

    public Task HandleKeyAsync(KeyEventArgs e) => Task.CompletedTask;
}
