using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Poste.Services.Server;

namespace Pharmaly.Poste.Modules.Administration;

/// <summary>Ligne de la liste des employés.</summary>
public sealed class EmployeeRowViewModel(EmployeeSummary employee, bool isSelf)
{
    public EmployeeSummary Employee { get; } = employee;

    public Guid Id => Employee.Id;

    public string NumberText => Employee.EmployeeNumber.ToString();

    public string FullName => Employee.FullName;

    public bool IsActive => Employee.IsActive;

    public string StateText => Employee.IsActive ? "Actif" : "Inactif";

    /// <summary>Vrai pour l'employé identifié sur ce poste.</summary>
    public bool IsSelf => isSelf;
}

/// <summary>Opération critique en attente de confirmation.</summary>
public sealed record ConfirmationViewModel(string Message, string ConfirmLabel, Func<Task> Action);

/// <summary>
/// Module Administration : employés et droits. Le poste n'applique aucune règle : il envoie
/// la demande et affiche la réponse du serveur.
/// </summary>
public sealed partial class AdministrationViewModel(IAdminClient admin, Guid currentEmployeeId) : ModuleViewModel
{
    public const string SelfExplanation =
        "Vous ne pouvez ni modifier vos propres droits ni désactiver votre compte : demandez à un autre administrateur.";

    private IReadOnlyList<ScopeChoice> storeScopes = [];

    public ObservableCollection<EmployeeRowViewModel> Employees { get; } = [];

    /// <summary>Droits de l'employé sélectionné, groupés comme dans <see cref="Permissions.All"/>.</summary>
    public ObservableCollection<PermissionGroupViewModel> PermissionGroups { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(IsSelfSelected), nameof(CanEditSelection), nameof(SelectedTitle),
        nameof(SelectedStateText), nameof(ToggleStateLabel))]
    [NotifyCanExecuteChangedFor(nameof(SavePermissionsCommand), nameof(ToggleStateCommand), nameof(RequestSecretResetCommand))]
    private EmployeeRowViewModel? selectedEmployee;

    [ObservableProperty]
    private bool hasUnsavedChanges;

    /// <summary>Confirmation d'une opération réussie.</summary>
    [ObservableProperty]
    private string infoMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirming))]
    private ConfirmationViewModel? confirmation;

    [ObservableProperty]
    private string newEmployeeNumber = string.Empty;

    [ObservableProperty]
    private string newEmployeeName = string.Empty;

    [ObservableProperty]
    private string newEmployeeSecret = string.Empty;

    /// <summary>Nouveau code personnel à donner à l'employé sélectionné.</summary>
    [ObservableProperty]
    private string newSecret = string.Empty;

    public bool HasSelection => SelectedEmployee is not null;

    public bool IsSelfSelected => SelectedEmployee?.IsSelf == true;

    /// <summary>Les droits et l'état de l'employé affiché peuvent être modifiés (ce n'est pas soi-même).</summary>
    public bool CanEditSelection => SelectedEmployee is { IsSelf: false };

    public bool IsConfirming => Confirmation is not null;

    public string SelectedTitle => SelectedEmployee is { } row ? $"{row.Employee.FullName} — matricule {row.NumberText}" : string.Empty;

    public string SelectedStateText => SelectedEmployee?.StateText ?? string.Empty;

    public string ToggleStateLabel => SelectedEmployee is { IsActive: false } ? "Réactiver l'employé" : "Désactiver l'employé";

    public override Task ActivateAsync() => ReloadAsync(SelectedEmployee?.Id);

    partial void OnSelectedEmployeeChanged(EmployeeRowViewModel? value)
    {
        ErrorMessage = string.Empty;
        InfoMessage = string.Empty;
        NewSecret = string.Empty;
        BuildPermissions(value);
    }

    private void BuildPermissions(EmployeeRowViewModel? row)
    {
        PermissionGroups.Clear();
        HasUnsavedChanges = false;
        if (row is null)
        {
            return;
        }

        var grants = row.Employee.Permissions.ToLookup(grant => grant.Code);
        foreach (var group in Permissions.All.GroupBy(permission => permission.Group))
        {
            var choices = group
                .Select(definition => new PermissionChoiceViewModel(
                    definition, storeScopes, grants[definition.Code].ToList(), !row.IsSelf, () => HasUnsavedChanges = true))
                .ToList();
            PermissionGroups.Add(new PermissionGroupViewModel(group.Key, choices));
        }
    }

    private async Task<bool> ReloadAsync(Guid? selectId)
    {
        var loaded = await RunServerAsync(async () =>
        {
            var stores = await admin.ListStoresAsync(CancellationToken.None);
            var employees = await admin.ListEmployeesAsync(CancellationToken.None);
            storeScopes = stores.Select(store => new ScopeChoice(store.Id, store.Name)).ToList();

            var rows = employees.Select(employee => new EmployeeRowViewModel(employee, employee.Id == currentEmployeeId)).ToList();
            Employees.Clear();
            foreach (var row in rows)
            {
                Employees.Add(row);
            }

            SelectedEmployee = rows.FirstOrDefault(row => row.Id == selectId) ?? rows.FirstOrDefault();
        });
        return loaded;
    }

    /// <summary>Recharge la liste après une écriture réussie, puis affiche la confirmation.</summary>
    private async Task ReloadAfterWriteAsync(Guid selectId, string message)
    {
        if (await ReloadAsync(selectId))
        {
            InfoMessage = message;
        }
    }

    [RelayCommand]
    public async Task CreateEmployeeAsync()
    {
        InfoMessage = string.Empty;
        if (!int.TryParse(NewEmployeeNumber.Trim(), out var number))
        {
            ErrorMessage = "Le matricule doit être un nombre.";
            return;
        }

        EmployeeSummary? created = null;
        var request = new CreateEmployeeRequest(number, NewEmployeeName.Trim(), NewEmployeeSecret);
        if (!await RunServerAsync(async () => created = await admin.CreateEmployeeAsync(request, CancellationToken.None)))
        {
            return;
        }

        NewEmployeeNumber = string.Empty;
        NewEmployeeName = string.Empty;
        NewEmployeeSecret = string.Empty;
        await ReloadAfterWriteAsync(
            created!.Id, $"{created.FullName} est créé sans aucun droit : cochez ses droits puis enregistrez.");
    }

    /// <summary>Envoie l'ensemble des droits cochés : il remplace les droits actuels de l'employé.</summary>
    [RelayCommand(CanExecute = nameof(CanEditSelection))]
    public async Task SavePermissionsAsync()
    {
        InfoMessage = string.Empty;
        if (SelectedEmployee is not { } row)
        {
            return;
        }

        if (row.IsSelf)
        {
            ErrorMessage = SelfExplanation;
            return;
        }

        var grants = PermissionGroups
            .SelectMany(group => group.Permissions)
            .SelectMany(choice => choice.ToGrants())
            .ToList();
        if (await RunServerAsync(() => admin.SetPermissionsAsync(row.Id, grants, CancellationToken.None)))
        {
            await ReloadAfterWriteAsync(row.Id, $"Droits de {row.Employee.FullName} enregistrés.");
        }
    }

    /// <summary>Désactive (après confirmation) ou réactive l'employé sélectionné.</summary>
    [RelayCommand(CanExecute = nameof(CanEditSelection))]
    public async Task ToggleStateAsync()
    {
        InfoMessage = string.Empty;
        if (SelectedEmployee is not { } row)
        {
            return;
        }

        if (row.IsSelf)
        {
            ErrorMessage = SelfExplanation;
            return;
        }

        if (!row.IsActive)
        {
            await SetStateAsync(row, true);
            return;
        }

        ErrorMessage = string.Empty;
        Confirmation = new ConfirmationViewModel(
            $"Désactiver {row.Employee.FullName} (matricule {row.NumberText}) ? Il ne pourra plus s'identifier.",
            "Désactiver",
            () => SetStateAsync(row, false));
    }

    private async Task SetStateAsync(EmployeeRowViewModel row, bool isActive)
    {
        if (await RunServerAsync(() => admin.SetEmployeeStateAsync(row.Id, isActive, CancellationToken.None)))
        {
            await ReloadAfterWriteAsync(
                row.Id, isActive ? $"{row.Employee.FullName} est réactivé." : $"{row.Employee.FullName} est désactivé.");
        }
    }

    /// <summary>Demande confirmation avant de remplacer le code personnel de l'employé sélectionné.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    public void RequestSecretReset()
    {
        InfoMessage = string.Empty;
        if (SelectedEmployee is not { } row)
        {
            return;
        }

        if (NewSecret.Length == 0)
        {
            ErrorMessage = "Saisissez le nouveau code personnel.";
            return;
        }

        ErrorMessage = string.Empty;
        var secret = NewSecret;
        Confirmation = new ConfirmationViewModel(
            $"Remplacer le code personnel de {row.Employee.FullName} (matricule {row.NumberText}) ? L'ancien code ne fonctionnera plus.",
            "Remplacer",
            () => ResetSecretAsync(row, secret));
    }

    private async Task ResetSecretAsync(EmployeeRowViewModel row, string secret)
    {
        if (!await RunServerAsync(() => admin.ResetSecretAsync(row.Id, secret, CancellationToken.None)))
        {
            return;
        }

        NewSecret = string.Empty;
        InfoMessage = $"Le code personnel de {row.Employee.FullName} est remplacé.";
        if (row.IsSelf)
        {
            // Le serveur ferme les sessions ouvertes avec l'ancien code, donc celle-ci.
            RequireIdentification();
        }
    }

    /// <summary>Exécute l'opération en attente de confirmation.</summary>
    [RelayCommand]
    public async Task ConfirmAsync()
    {
        if (Confirmation is not { } pending)
        {
            return;
        }

        Confirmation = null;
        await pending.Action();
    }

    [RelayCommand]
    public void CancelConfirmation() => Confirmation = null;
}
