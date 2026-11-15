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
    private readonly SessionViewModel session;
    private readonly LaunchOptions options;
    private readonly DispatcherTimer memoryTimer;
    private readonly DispatcherTimer serverTimer;

    public MainWindow(MainViewModel viewModel, SessionViewModel session, LaunchOptions options)
    {
        this.viewModel = viewModel;
        this.session = session;
        this.options = options;
        DataContext = viewModel;
        InitializeComponent();

        PosteSignInPanel.DataContext = session;
        EmployeeSignInPanel.DataContext = session;
        LockButton.Click += async (_, _) => await session.LockAsync();
        session.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(SessionViewModel.Stage))
            {
                ApplyStage();
            }
        };
        viewModel.IdentificationRequired += (_, _) => session.RequireEmployee();

        memoryTimer = new DispatcherTimer(MemoryRefreshInterval, DispatcherPriority.Background, (_, _) => viewModel.RefreshMemory());
        serverTimer = new DispatcherTimer(ServerCheckInterval, DispatcherPriority.Background, async (_, _) => await viewModel.CheckServerAsync());

        // Les touches sont interceptées avant les contrôles : le scanner et le clavier
        // pilotent tout l'écran, quel que soit l'élément qui a le focus.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>Affiche l'écran qui correspond à l'étape d'identification et y place le curseur.</summary>
    private void ApplyStage()
    {
        WorkArea.IsVisible = session.IsWork;
        EmployeeLabel.Text = session.EmployeeName;
        if (!session.IsWork)
        {
            // Rien de l'employé précédent ne reste à l'écran.
            viewModel.Query = string.Empty;
            viewModel.ClearLines();
        }

        Dispatcher.UIThread.Post(() =>
        {
            var target = session.Stage switch
            {
                SessionStage.PosteSignIn => RegisterLoginBox,
                SessionStage.EmployeeSignIn => EmployeeNumberBox,
                _ => SearchBox,
            };
            target.Focus();
        });
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        viewModel.StartupSeconds = Measurements.ReadTimeSinceProcessStart().TotalSeconds;
        ApplyStage();
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
        // Essai automatique : les identifiants du compte d'essai viennent de l'environnement.
        string Read(string name) => Environment.GetEnvironmentVariable($"PHARMALY_ESSAI_{name}") ?? string.Empty;
        if (session.IsPosteSignIn)
        {
            session.RegisterLogin = Read("COMPTE");
            session.RegisterPassword = Read("MOT_DE_PASSE");
            await session.SignInPosteAsync();
        }

        if (session.IsEmployeeSignIn)
        {
            session.EmployeeNumber = Read("MATRICULE");
            session.EmployeeSecret = Read("CODE");
            await session.SignInEmployeeAsync();
        }

        var report = await viewModel.ExecuteBenchmarkAsync();
        if (options.ReportPath is not null)
        {
            await File.WriteAllTextAsync(options.ReportPath, report.ToJson());
        }

        Close();
    }

    private async Task HandleSignInKeyAsync(KeyEventArgs e)
    {
        if (e.Key != Key.Enter || session.IsBusy)
        {
            return;
        }

        e.Handled = true;
        if (session.IsPosteSignIn)
        {
            if (RegisterLoginBox.IsFocused)
            {
                RegisterPasswordBox.Focus();
            }
            else
            {
                await session.SignInPosteAsync();
            }
        }
        else if (EmployeeNumberBox.IsFocused)
        {
            EmployeeSecretBox.Focus();
        }
        else
        {
            await session.SignInEmployeeAsync();
            if (session.IsEmployeeSignIn)
            {
                EmployeeNumberBox.Focus();
            }
        }
    }

    private async void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (!session.IsWork)
        {
            await HandleSignInKeyAsync(e);
            return;
        }

        switch (e.Key)
        {
            case Key.F12:
                e.Handled = true;
                await session.LockAsync();
                break;

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
