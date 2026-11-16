using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace Pharmaly.Poste.Modules.Diagnostic;

public sealed partial class DiagnosticView : UserControl, IModuleView
{
    private static readonly TimeSpan MemoryRefreshInterval = TimeSpan.FromSeconds(2);

    private readonly DispatcherTimer memoryTimer;

    public DiagnosticView()
    {
        InitializeComponent();
        memoryTimer = new DispatcherTimer(MemoryRefreshInterval, DispatcherPriority.Background, (_, _) => ViewModel?.RefreshMemory());
        memoryTimer.Stop();
    }

    private DiagnosticViewModel? ViewModel => DataContext as DiagnosticViewModel;

    public void FocusDefault() => SearchBox.Focus();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ViewModel?.RefreshMemory();
        memoryTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        memoryTimer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    public async Task HandleKeyAsync(KeyEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter when SearchBox.IsFocused:
                e.Handled = true;
                if (viewModel.Results.Count > 1 && viewModel.SelectedResult is not null && !LooksLikeBarcode(viewModel.Query))
                {
                    viewModel.AddSelectedResult();
                }
                else
                {
                    await viewModel.SubmitAsync();
                }

                SearchBox.Focus();
                break;

            case Key.Enter when ResultsList.IsKeyboardFocusWithin:
                e.Handled = true;
                viewModel.AddSelectedResult();
                SearchBox.Focus();
                break;

            case Key.Down when SearchBox.IsFocused && viewModel.Results.Count > 0:
                e.Handled = true;
                MoveResultSelection(viewModel, +1);
                break;

            case Key.Up when SearchBox.IsFocused && viewModel.Results.Count > 0:
                e.Handled = true;
                MoveResultSelection(viewModel, -1);
                break;

            case Key.Add or Key.OemPlus when string.IsNullOrEmpty(viewModel.Query):
                e.Handled = true;
                viewModel.IncreaseSelectedLine();
                break;

            case Key.Subtract or Key.OemMinus when string.IsNullOrEmpty(viewModel.Query):
                e.Handled = true;
                viewModel.DecreaseSelectedLine();
                break;

            case Key.Delete when string.IsNullOrEmpty(viewModel.Query):
                e.Handled = true;
                viewModel.RemoveSelectedLine();
                break;

            case Key.Escape:
                e.Handled = true;
                if (string.IsNullOrEmpty(viewModel.Query))
                {
                    viewModel.ClearLines();
                }
                else
                {
                    viewModel.Query = string.Empty;
                }

                SearchBox.Focus();
                break;

            case Key.F9 when !viewModel.IsBenchmarkRunning:
                e.Handled = true;
                await viewModel.ExecuteBenchmarkAsync();
                break;
        }
    }

    private void MoveResultSelection(DiagnosticViewModel viewModel, int delta)
    {
        var current = viewModel.SelectedResult is null ? -1 : viewModel.Results.IndexOf(viewModel.SelectedResult);
        var next = Math.Clamp(current + delta, 0, viewModel.Results.Count - 1);
        viewModel.SelectedResult = viewModel.Results[next];
        ResultsList.ScrollIntoView(next);
    }

    private static bool LooksLikeBarcode(string text) =>
        text.Trim() is { Length: >= 8 } trimmed && trimmed.All(char.IsAsciiDigit);
}
