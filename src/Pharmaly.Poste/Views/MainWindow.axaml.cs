using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Pharmaly.Poste.Modules;
using Pharmaly.Poste.Modules.Diagnostic;
using Pharmaly.Poste.Services;
using Pharmaly.Poste.ViewModels;

namespace Pharmaly.Poste.Views;

/// <summary>
/// Fenêtre principale : identification, bandeau, barre de navigation et vue du module affiché.
/// Elle ne contient aucun écran métier : chaque module apporte sa vue (voir <see cref="ModuleCatalog"/>).
/// </summary>
public sealed partial class MainWindow : Window
{
    private static readonly TimeSpan ServerCheckInterval = TimeSpan.FromSeconds(15);

    private readonly ShellViewModel shell;
    private readonly SessionViewModel session;
    private readonly LaunchOptions options;
    private readonly DispatcherTimer serverTimer;

    public MainWindow(ShellViewModel shell, LaunchOptions options)
    {
        this.shell = shell;
        this.options = options;
        session = shell.Session;
        DataContext = shell;
        InitializeComponent();

        session.PropertyChanged += async (_, change) =>
        {
            if (change.PropertyName is nameof(SessionViewModel.Stage) or nameof(SessionViewModel.ServerApprovalNeeded))
            {
                FocusForStage();
            }

            if (change.PropertyName == nameof(SessionViewModel.ServerApprovalNeeded) && !session.ServerApprovalNeeded)
            {
                // Le serveur vient d'être approuvé : le bandeau ne doit pas rester sur l'ancien refus.
                await shell.Status.CheckAsync();
            }
        };
        shell.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(ShellViewModel.CurrentModule))
            {
                ShowCurrentModule();
            }
        };

        serverTimer = new DispatcherTimer(ServerCheckInterval, DispatcherPriority.Background, async (_, _) => await shell.Status.CheckAsync());
        serverTimer.Stop();

        // Les touches sont interceptées avant les contrôles : le scanner et le clavier
        // pilotent tout l'écran, quel que soit l'élément qui a le focus.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        // Toute frappe et tout geste de souris repoussent le verrouillage par inactivité.
        AddHandler(KeyDownEvent, (_, _) => shell.NotifyActivity(), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, (_, _) => shell.NotifyActivity(), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerPressedEvent, (_, _) => shell.NotifyActivity(), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerWheelChangedEvent, (_, _) => shell.NotifyActivity(), RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private IModuleView? CurrentView => ModuleHost.Content as IModuleView;

    /// <summary>
    /// Place dans la fenêtre la vue du module affiché. Au verrouillage il n'y a plus de module :
    /// la vue est retirée, rien de l'employé précédent ne reste à l'écran.
    /// </summary>
    private void ShowCurrentModule()
    {
        if (shell.CurrentModule is not { } module || shell.CurrentDefinition is not { } definition)
        {
            ModuleHost.Content = null;
            return;
        }

        var view = definition.CreateView();
        view.DataContext = module;
        ModuleHost.Content = view;
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(ModuleHost.Content, view) && session.IsWork)
            {
                ((IModuleView)view).FocusDefault();
            }
        });
    }

    /// <summary>Place le curseur sur l'écran d'identification affiché.</summary>
    private void FocusForStage() => Dispatcher.UIThread.Post(() =>
    {
        switch (session.Stage)
        {
            case SessionStage.PosteSignIn when session.ServerApprovalNeeded:
                // Approbation du serveur : aucun champ à saisir, les touches vont à la fenêtre.
                Focus();
                break;
            case SessionStage.PosteSignIn:
                RegisterLoginBox.Focus();
                break;
            case SessionStage.EmployeeSignIn:
                EmployeeNumberBox.Focus();
                break;
            case SessionStage.Work when shell.CurrentModule is null:
                // Aucun module visible : le focus revient à la fenêtre pour que F12 reste actif.
                Focus();
                break;
        }
    });

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        shell.StartupSeconds = Measurements.ReadTimeSinceProcessStart().TotalSeconds;
        FocusForStage();
        serverTimer.Start();
        await shell.Status.CheckAsync();
        await session.ReadServerFingerprintAsync();

        if (options.AutoBenchmark)
        {
            await RunAutoBenchmarkAsync();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
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

        // L'essai se déroule dans le module Diagnostic, quels que soient les droits du compte d'essai.
        var diagnostic = (DiagnosticViewModel)(await shell.OpenAsync(ModuleKeys.Diagnostic, evenIfHidden: true))!;
        var report = await diagnostic.ExecuteBenchmarkAsync();
        if (options.ReportPath is not null)
        {
            await File.WriteAllTextAsync(options.ReportPath, report.ToJson());
        }

        Close();
    }

    private async Task HandleSignInKeyAsync(KeyEventArgs e)
    {
        if (session.ServerApprovalNeeded)
        {
            // Entrée ne fait que relire l'empreinte : approuver demande une touche à part (F8),
            // pour qu'un appui machinal sur Entrée n'approuve jamais un serveur.
            if (e.Key == Key.F8)
            {
                e.Handled = true;
                session.ApproveServer();
            }
            else if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await session.ReadServerFingerprintAsync();
            }

            return;
        }

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

        if (e.Key == Key.F12)
        {
            e.Handled = true;
            await shell.LockAsync();
            return;
        }

        if (ModulePositionOf(e) is { } position)
        {
            e.Handled = true;
            await shell.OpenByShortcutAsync(position);
            return;
        }

        if (CurrentView is { } view)
        {
            await view.HandleKeyAsync(e);
        }
    }

    /// <summary>F1…F9 sans modificateur, ou Ctrl+1…Ctrl+9 : position du module demandé dans le catalogue.</summary>
    private static int? ModulePositionOf(KeyEventArgs e)
    {
        var count = ModuleCatalog.All.Count;
        if (e.KeyModifiers == KeyModifiers.None && e.Key >= Key.F1 && e.Key < Key.F1 + count)
        {
            return e.Key - Key.F1;
        }

        if (e.KeyModifiers == KeyModifiers.Control)
        {
            if (e.Key >= Key.D1 && e.Key < Key.D1 + count)
            {
                return e.Key - Key.D1;
            }

            if (e.Key >= Key.NumPad1 && e.Key < Key.NumPad1 + count)
            {
                return e.Key - Key.NumPad1;
            }
        }

        return null;
    }
}
