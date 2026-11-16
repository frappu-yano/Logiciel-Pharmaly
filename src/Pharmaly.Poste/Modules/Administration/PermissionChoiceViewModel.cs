using CommunityToolkit.Mvvm.ComponentModel;
using Pharmaly.Contracts;
using Pharmaly.Domain;

namespace Pharmaly.Poste.Modules.Administration;

/// <summary>Portée d'un droit : partout, ou limité à un site.</summary>
/// <param name="StoreId">Site auquel le droit est limité ; <c>null</c> pour « partout ».</param>
/// <param name="Label">Texte affiché dans la liste.</param>
/// <param name="KeepsExisting">
/// Vrai pour le choix qui renvoie tels quels des droits que cet écran ne sait pas représenter
/// (même droit limité à plusieurs sites, ou à un site qui n'est plus actif).
/// </param>
public sealed record ScopeChoice(Guid? StoreId, string Label, bool KeepsExisting = false)
{
    public static readonly ScopeChoice Everywhere = new(null, "Tous les sites");

    public override string ToString() => Label;
}

/// <summary>Une case à cocher : un droit, accordé ou non, avec sa portée.</summary>
public sealed partial class PermissionChoiceViewModel : ObservableObject
{
    private readonly IReadOnlyList<PermissionGrant> existing;
    private readonly Action changed;

    public PermissionChoiceViewModel(
        PermissionDefinition definition,
        IReadOnlyList<ScopeChoice> storeScopes,
        IReadOnlyList<PermissionGrant> existing,
        bool isEditable,
        Action changed)
    {
        Definition = definition;
        IsEditable = isEditable;
        this.existing = existing;
        this.changed = changed;

        var scopes = new List<ScopeChoice> { ScopeChoice.Everywhere };
        scopes.AddRange(storeScopes);
        isGranted = existing.Count > 0;
        scope = ScopeChoice.Everywhere;
        if (existing.Count > 0 && existing.All(grant => grant.StoreId is not null))
        {
            var known = existing.Count == 1 ? storeScopes.FirstOrDefault(choice => choice.StoreId == existing[0].StoreId) : null;
            if (known is null)
            {
                known = new ScopeChoice(null, "Limitation actuelle conservée", KeepsExisting: true);
                scopes.Add(known);
            }

            scope = known;
        }

        Scopes = scopes;
    }

    public PermissionDefinition Definition { get; }

    public string Label => Definition.Label;

    /// <summary>Faux quand l'employé affiché est celui qui est identifié : nul ne modifie ses propres droits.</summary>
    public bool IsEditable { get; }

    public IReadOnlyList<ScopeChoice> Scopes { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChooseScope))]
    private bool isGranted;

    [ObservableProperty]
    private ScopeChoice scope;

    public bool CanChooseScope => IsEditable && IsGranted;

    /// <summary>Ce qui sera envoyé au serveur pour ce droit.</summary>
    public IEnumerable<PermissionGrant> ToGrants()
    {
        if (!IsGranted)
        {
            return [];
        }

        return Scope.KeepsExisting ? existing : [new PermissionGrant(Definition.Code, Scope.StoreId)];
    }

    partial void OnIsGrantedChanged(bool value) => changed();

    partial void OnScopeChanged(ScopeChoice value) => changed();
}

public sealed record PermissionGroupViewModel(string Name, IReadOnlyList<PermissionChoiceViewModel> Permissions);
