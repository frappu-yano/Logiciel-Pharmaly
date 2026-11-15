using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Pharmaly.Poste.Services;
using Pharmaly.Poste.ViewModels;

namespace Pharmaly.Poste.Views;

public sealed partial class MainWindow : Window
{
    private static readonly TimeSpan MemoryRefreshInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ServerCheckInterval = TimeSpan.FromSeconds(15);

    private readonly MainViewModel viewModel;
    private readonly LaunchOptions options;
    private readonly DispatcherTimer memoryTimer;
    private readonly DispatcherTimer serverTimer;

    public MainWindow(MainViewModel viewModel, LaunchOptions options)
    {
        this.viewModel = viewModel;
        this.options = options;
        DataContext = viewModel;
        InitializeComponent();

        memoryTimer = new DispatcherTimer(MemoryRefreshInterval, DispatcherPriority.Background, (_, _) => viewModel.RefreshMemory());
        serverTimer = new DispatcherTimer(ServerCheckInterval, DispatcherPriority.Background, async (_, _) => await viewModel.CheckServerAsync());

        // Les touches sont interceptées avant les contrôles : le scanner et le clavier
        // pilotent tout l'écran, quel que soit l'élément qui a le focus.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        viewModel.StartupSeconds = Measurements.ReadTimeSinceProcessStart().TotalSeconds;
        SearchBox.Focus();
        viewModel.RefreshMemory();
        memoryTimer.Start();
        serverTimer.Start();
        await viewModel.CheckServerAsync();

        if (options.AutoBenchmark)
        {
            await RunAutoBenchmarkAsync();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        memoryTimer.Stop();
        serverTimer.Stop();
        base.OnClosed(e);
    }

    private async Task RunAutoBenchmarkAsync()
    {
        var report = await viewModel.ExecuteBenchmarkAsync();
        if (options.ReportPath is not null)
        {
            await File.WriteAllTextAsync(options.ReportPath, report.ToJson());
        }

        Close();
    }

    private async void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
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
                MoveResultSelection(+1);
                break;

            case Key.Up when SearchBox.IsFocused && viewModel.Results.Count > 0:
                e.Handled = true;
                MoveResultSelection(-1);
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

    private void MoveResultSelection(int delta)
    {
        var current = viewModel.SelectedResult is null ? -1 : viewModel.Results.IndexOf(viewModel.SelectedResult);
        var next = Math.Clamp(current + delta, 0, viewModel.Results.Count - 1);
        viewModel.SelectedResult = viewModel.Results[next];
        ResultsList.ScrollIntoView(next);
    }

    private static bool LooksLikeBarcode(string text) =>
        text.Trim() is { Length: >= 8 } trimmed && trimmed.All(char.IsAsciiDigit);
}
