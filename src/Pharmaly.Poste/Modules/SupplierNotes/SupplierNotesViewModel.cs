using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Poste.Services.Server;

namespace Pharmaly.Poste.Modules.SupplierNotes;

/// <summary>Écran affiché par le module.</summary>
public enum NotesScreen
{
    List,
    NewNote,
    Note,
}

/// <summary>
/// Module Bons fournisseurs : liste des bons, création d'un bon, saisie d'un bon. Le poste
/// n'applique aucune règle de stock : il envoie des commandes et affiche la réponse du serveur.
/// </summary>
public sealed partial class SupplierNotesViewModel : ModuleViewModel
{
    private static readonly string[] WriteRights =
        [Permissions.NotesEnter, Permissions.NotesEdit, Permissions.NotesValidate, Permissions.NotesCancel];

    private static readonly FilterChoice<SupplierSummary> AllSuppliers = new(null, "Tous les fournisseurs");

    private readonly ISupplierNotesClient notes;
    private readonly ICatalogClient catalog;
    private readonly Func<string, bool> hasPermission;
    private readonly INoteHeartbeat heartbeat;
    private readonly TimeSpan searchDelay;
    private readonly Func<DateOnly> today;
    private int listRequest;
    private bool settingFilters;
    private Guid? lastLocationId;

    public SupplierNotesViewModel(
        ISupplierNotesClient notes, ICatalogClient catalog, Func<string, bool> hasPermission, INoteHeartbeat heartbeat,
        TimeSpan searchDelay, Func<DateOnly>? today = null)
    {
        this.notes = notes;
        this.catalog = catalog;
        this.hasPermission = hasPermission;
        this.heartbeat = heartbeat;
        this.searchDelay = searchDelay;
        this.today = today ?? (() => DateOnly.FromDateTime(DateTime.Today));
        selectedStatus = StatusChoices[0];
        selectedSupplier = AllSuppliers;
        SupplierChoices.Add(AllSuppliers);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsList), nameof(IsNewNote), nameof(IsNote))]
    private NotesScreen screen = NotesScreen.List;

    public bool IsList => Screen == NotesScreen.List;

    public bool IsNewNote => Screen == NotesScreen.NewNote;

    public bool IsNote => Screen == NotesScreen.Note;

    /// <summary>Confirmation d'une opération réussie (bon validé, bon annulé).</summary>
    [ObservableProperty]
    private string infoMessage = string.Empty;

    [ObservableProperty]
    private NewNoteViewModel? newNote;

    /// <summary>Bon ouvert en saisie ou en lecture seule.</summary>
    [ObservableProperty]
    private NoteEntryViewModel? entry;

    /// <summary>« Nouveau bon » n'est proposé qu'à qui a le droit de saisir un bon.</summary>
    public bool CanCreateNote => hasPermission(Permissions.NotesEnter);

    // ---------- Liste et filtres ----------

    public IReadOnlyList<FilterChoice<string>> StatusChoices { get; } =
    [
        new(null, "Tous les états"),
        new(SupplierNoteStatuses.InProgress, "En saisie"),
        new(SupplierNoteStatuses.Validated, "Validés"),
        new(SupplierNoteStatuses.Cancelled, "Annulés"),
    ];

    public ObservableCollection<FilterChoice<SupplierSummary>> SupplierChoices { get; } = [];

    [ObservableProperty]
    private FilterChoice<string> selectedStatus;

    [ObservableProperty]
    private FilterChoice<SupplierSummary> selectedSupplier;

    /// <summary>Date de début (jj/mm/aaaa), vide = pas de limite.</summary>
    [ObservableProperty]
    private string fromText = string.Empty;

    [ObservableProperty]
    private string toText = string.Empty;

    public ObservableCollection<NoteRowViewModel> Notes { get; } = [];

    [ObservableProperty]
    private NoteRowViewModel? selectedNote;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageText))]
    private int page = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageText), nameof(HasSeveralPages))]
    private int pageCount = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText), nameof(IsEmpty))]
    private int total;

    public string PageText => $"Page {Page} / {PageCount}";

    public bool HasSeveralPages => PageCount > 1;

    public bool IsEmpty => Total == 0;

    public string CountText => Total switch
    {
        0 => "Aucun bon",
        1 => "1 bon",
        var count => $"{count} bons",
    };

    partial void OnSelectedStatusChanged(FilterChoice<string> value) => OnFilterChanged();

    partial void OnSelectedSupplierChanged(FilterChoice<SupplierSummary> value) => OnFilterChanged();

    private void OnFilterChanged()
    {
        if (!settingFilters)
        {
            _ = ApplyFiltersAsync();
        }
    }

    public override async Task ActivateAsync()
    {
        if (Entry is { } opened)
        {
            await opened.ResumeAsync();
        }
        else if (Screen == NotesScreen.List)
        {
            await ReloadAsync();
        }
    }

    /// <summary>L'employé passe à un autre module : le bon ouvert est libéré pour ses collègues.</summary>
    public Task DeactivateAsync() => Entry?.SuspendAsync() ?? Task.CompletedTask;

    /// <summary>Verrouillage du poste : le bon ouvert est libéré, rien ne reste de l'employé.</summary>
    public override void Close()
    {
        listRequest++;
        Entry?.Close();
        Entry = null;
        NewNote = null;
        Notes.Clear();
    }

    /// <summary>Exécute un appel au serveur pour un écran du module, sans jamais lever.</summary>
    private async Task<ServerCallResult> CallAsync(Func<Task> action)
    {
        try
        {
            await action();
            return ServerCallResult.Success;
        }
        catch (ServerRefusedException refused)
        {
            RequireIdentificationIfLost(refused);
            return new ServerCallResult(refused, false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return new ServerCallResult(null, true);
        }
    }

    /// <summary>Applique les filtres depuis la première page (Entrée dans un champ de date, choix d'un état…).</summary>
    public Task ApplyFiltersAsync()
    {
        Page = 1;
        return ReloadAsync();
    }

    public Task NextPageAsync() => Page < PageCount ? GoToPageAsync(Page + 1) : Task.CompletedTask;

    public Task PreviousPageAsync() => Page > 1 ? GoToPageAsync(Page - 1) : Task.CompletedTask;

    private Task GoToPageAsync(int target)
    {
        Page = target;
        return ReloadAsync();
    }

    /// <summary>Relit la liste des bons avec les filtres affichés.</summary>
    public async Task ReloadAsync()
    {
        if (!TryReadFilterDate(FromText, out var from) || !TryReadFilterDate(ToText, out var to))
        {
            ErrorMessage = NoteInput.InvalidDate;
            return;
        }

        var ticket = ++listRequest;
        var filter = new NoteListFilter(SelectedStatus.Value, SelectedSupplier.Value?.Id, from, to, Page);
        SupplierNotePage? found = null;
        IReadOnlyList<SupplierSummary> suppliers = [];
        IsBusy = true;
        var result = await CallAsync(async () =>
        {
            found = await notes.ListNotesAsync(filter, CancellationToken.None);
            suppliers = await notes.ListSuppliersAsync(null, CancellationToken.None);
        });
        if (ticket != listRequest)
        {
            // Une demande plus récente (autre filtre, verrouillage) a remplacé celle-ci.
            return;
        }

        IsBusy = false;
        if (!result.Succeeded)
        {
            ErrorMessage = result.Refusal?.Message ?? ServerUnreachableMessage;
            return;
        }

        ErrorMessage = string.Empty;
        var selectedId = SelectedNote?.Id;
        Notes.Clear();
        foreach (var note in found!.Items)
        {
            Notes.Add(new NoteRowViewModel(note));
        }

        Total = found.Total;
        PageCount = Math.Max(1, (int)Math.Ceiling(found.Total / (double)Math.Max(1, found.PageSize)));
        SelectedNote = Notes.FirstOrDefault(row => row.Id == selectedId) ?? Notes.FirstOrDefault();
        ShowSupplierChoices(suppliers);
    }

    private static bool TryReadFilterDate(string text, out DateOnly? date)
    {
        date = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (!NoteInput.TryReadDate(text, out var read))
        {
            return false;
        }

        date = read;
        return true;
    }

    private void ShowSupplierChoices(IReadOnlyList<SupplierSummary> suppliers)
    {
        if (SupplierChoices.Skip(1).Select(choice => choice.Value).SequenceEqual(suppliers))
        {
            return;
        }

        settingFilters = true;
        var selectedId = SelectedSupplier.Value?.Id;
        SupplierChoices.Clear();
        SupplierChoices.Add(AllSuppliers);
        foreach (var supplier in suppliers)
        {
            SupplierChoices.Add(new FilterChoice<SupplierSummary>(supplier, supplier.Name));
        }

        SelectedSupplier = SupplierChoices.FirstOrDefault(choice => choice.Value?.Id == selectedId && selectedId is not null) ?? AllSuppliers;
        settingFilters = false;
    }

    // ---------- Ouverture d'un bon ----------

    /// <summary>Entrée sur un bon de la liste.</summary>
    public Task OpenSelectedAsync() => SelectedNote is { } row ? OpenNoteAsync(row.Note) : Task.CompletedTask;

    /// <summary>
    /// Ouvre un bon : le poste prend le verrou. Si un autre employé tient le bon, le message
    /// du serveur (qui le nomme) est affiché et le bon s'ouvre en lecture seule.
    /// </summary>
    public async Task OpenNoteAsync(SupplierNoteSummary summary)
    {
        if (IsBusy || Entry is not null)
        {
            return;
        }

        IsBusy = true;
        InfoMessage = string.Empty;
        ErrorMessage = string.Empty;
        try
        {
            SupplierNoteDetail? detail = null;
            var holdsLock = false;
            var readOnly = string.Empty;
            var isCancelled = summary.Status == SupplierNoteStatuses.Cancelled;

            // Sans droit d'écriture, le verrou n'est pas demandé : la demande serait refusée.
            if (!isCancelled && WriteRights.Any(hasPermission))
            {
                var acquired = await CallAsync(async () => detail = await notes.AcquireNoteLockAsync(summary.Id, CancellationToken.None));
                if (acquired.Succeeded)
                {
                    holdsLock = true;
                }
                else if (acquired.IsRefusal(SupplierNoteErrorCodes.LockedByOther))
                {
                    readOnly = $"{acquired.Refusal!.Message} Le bon est ouvert en lecture seule.";
                }
                else if (!acquired.IsRefusal(SupplierNoteErrorCodes.NoteCancelled))
                {
                    ErrorMessage = acquired.Refusal?.Message ?? ServerUnreachableMessage;
                    return;
                }
            }
            else if (!isCancelled)
            {
                readOnly = NoteEntryViewModel.ViewOnly;
            }

            if (detail is null)
            {
                var read = await CallAsync(async () => detail = await notes.ReadNoteAsync(summary.Id, CancellationToken.None));
                if (!read.Succeeded)
                {
                    ErrorMessage = read.Refusal?.Message ?? ServerUnreachableMessage;
                    return;
                }
            }

            ShowEntry(detail!, holdsLock, readOnly);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ShowEntry(SupplierNoteDetail detail, bool holdsLock, string readOnlyMessage)
    {
        var opened = new NoteEntryViewModel(
            notes, catalog, hasPermission, CallAsync, heartbeat, searchDelay, detail, holdsLock, readOnlyMessage, CloseNoteAsync);
        NewNote = null;
        Entry = opened;
        Screen = NotesScreen.Note;
        opened.FocusDefault();
    }

    /// <summary>Retour à la liste après la fermeture d'un bon (le verrou est déjà libéré).</summary>
    private async Task CloseNoteAsync(string message)
    {
        Entry = null;
        Screen = NotesScreen.List;
        await ReloadAsync();
        InfoMessage = message;
    }

    // ---------- Nouveau bon ----------

    /// <summary>Ouvre le formulaire « Nouveau bon ».</summary>
    public async Task StartNewNoteAsync()
    {
        if (!CanCreateNote || Screen != NotesScreen.List)
        {
            return;
        }

        InfoMessage = string.Empty;
        ErrorMessage = string.Empty;
        var form = new NewNoteViewModel(notes, CallAsync, searchDelay, lastLocationId, today(), NoteCreatedAsync);
        NewNote = form;
        Screen = NotesScreen.NewNote;
        await form.LoadAsync();
        form.Focus(NewNoteField.Supplier);
    }

    private Task NoteCreatedAsync(SupplierNoteDetail detail)
    {
        // Le bon naît réservé à l'employé qui le crée : la saisie commence aussitôt.
        lastLocationId = detail.Note.LocationId;
        ShowEntry(detail, holdsLock: true, string.Empty);
        return Task.CompletedTask;
    }

    public void CancelNewNote()
    {
        NewNote = null;
        Screen = NotesScreen.List;
    }
}
