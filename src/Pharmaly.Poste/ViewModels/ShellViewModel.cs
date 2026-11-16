using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pharmaly.Poste.Modules;
using Pharmaly.Poste.Services;
using Pharmaly.Poste.Services.Server;

namespace Pharmaly.Poste.ViewModels;

/// <summary>Bouton de la barre de navigation pour un module visible.</summary>
public sealed partial class ModuleEntryViewModel(ModuleDefinition definition, string shortcutLabel, Func<Task> open) : ObservableObject
{
    [ObservableProperty]
    private bool isCurrent;

    public ModuleDefinition Definition { get; } = definition;

    public string Title => Definition.Title;

    /// <summary>Touche affichée sur le bouton (F1, F2…).</summary>
    public string ShortcutLabel { get; } = shortcutLabel;

    [RelayCommand]
    private Task OpenAsync() => open();
}

/// <summary>
/// Structure du logiciel du poste : identification, modules visibles selon les droits,
/// module affiché, verrouillage manuel et par inactivité.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    /// <summary>Décision D2 : le poste se verrouille après ce délai sans frappe ni souris.</summary>
    public static readonly TimeSpan InactivityLockDelay = TimeSpan.FromMinutes(5);

    private readonly IReadOnlyList<ModuleDefinition> catalog;
    private readonly IInactivityTimer inactivity;
    private readonly ModuleContext context;
    private readonly Dictionary<string, ModuleViewModel> openedModules = [];

    public ShellViewModel(
        ServerClients clients, IInactivityTimer inactivity, TimeSpan searchDelay, IReadOnlyList<ModuleDefinition>? catalog = null)
    {
        this.catalog = catalog ?? ModuleCatalog.All;
        this.inactivity = inactivity;
        Session = new SessionViewModel(clients.Access);
        Status = new ServerStatusViewModel(clients.Access);
        context = new ModuleContext(clients, Session, Status, searchDelay);

        Session.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(SessionViewModel.Stage))
            {
                OnStageChanged();
            }
        };
        inactivity.Elapsed += async (_, _) => await LockAsync();
    }

    public SessionViewModel Session { get; }

    public ServerStatusViewModel Status { get; }

    /// <summary>Modules que les droits de l'employé rendent visibles, dans l'ordre du catalogue.</summary>
    public ObservableCollection<ModuleEntryViewModel> Modules { get; } = [];

    [ObservableProperty]
    private ModuleViewModel? currentModule;

    /// <summary>Description du module affiché ; la fenêtre s'en sert pour construire la vue.</summary>
    public ModuleDefinition? CurrentDefinition { get; private set; }

    /// <summary>Vrai quand l'employé identifié n'a de droit sur aucun module.</summary>
    [ObservableProperty]
    private bool hasNoModule;

    public double StartupSeconds
    {
        get => context.StartupSeconds;
        set => context.StartupSeconds = value;
    }

    /// <summary>Une touche ou la souris vient de servir : le délai d'inactivité repart de zéro.</summary>
    public void NotifyActivity()
    {
        if (Session.IsWork)
        {
            inactivity.Restart();
        }
    }

    /// <summary>Verrouille le poste (F12, bouton, inactivité) : l'employé suivant devra s'identifier.</summary>
    [RelayCommand]
    public Task LockAsync() => Session.IsWork ? Session.LockAsync() : Task.CompletedTask;

    /// <summary>Ouvre le module placé à cette position du catalogue (0 pour F1), s'il est visible.</summary>
    public Task OpenByShortcutAsync(int position) =>
        position >= 0 && position < catalog.Count ? OpenAsync(catalog[position].Key) : Task.CompletedTask;

    /// <summary>
    /// Affiche un module, en le créant à sa première ouverture. Un module que les droits de
    /// l'employé ne rendent pas visible n'est ouvert que sur demande explicite (essai automatique).
    /// </summary>
    public async Task<ModuleViewModel?> OpenAsync(string key, bool evenIfHidden = false)
    {
        var definition = evenIfHidden
            ? catalog.FirstOrDefault(module => module.Key == key)
            : Modules.FirstOrDefault(entry => entry.Definition.Key == key)?.Definition;
        if (definition is null)
        {
            return null;
        }

        if (!openedModules.TryGetValue(key, out var module))
        {
            module = definition.CreateViewModel(context);
            module.IdentificationRequired += (_, _) => Session.RequireEmployee();
            openedModules[key] = module;
        }

        foreach (var entry in Modules)
        {
            entry.IsCurrent = entry.Definition == definition;
        }

        CurrentDefinition = definition;
        CurrentModule = module;
        await module.ActivateAsync();
        return module;
    }

    private void OnStageChanged()
    {
        CloseModules();
        if (!Session.IsWork)
        {
            inactivity.Stop();
            return;
        }

        for (var position = 0; position < catalog.Count; position++)
        {
            var definition = catalog[position];
            if (definition.Permissions.Any(Session.HasPermission))
            {
                Modules.Add(new ModuleEntryViewModel(definition, $"F{position + 1}", () => OpenAsync(definition.Key)));
            }
        }

        HasNoModule = Modules.Count == 0;
        inactivity.Restart();
        if (Modules.Count > 0)
        {
            _ = OpenAsync(Modules[0].Definition.Key);
        }
    }

    /// <summary>Détruit tous les modules ouverts : rien de l'employé précédent ne reste en mémoire ni à l'écran.</summary>
    private void CloseModules()
    {
        foreach (var module in openedModules.Values)
        {
            module.Close();
        }

        openedModules.Clear();
        Modules.Clear();
        HasNoModule = false;
        CurrentDefinition = null;
        CurrentModule = null;
    }
}
