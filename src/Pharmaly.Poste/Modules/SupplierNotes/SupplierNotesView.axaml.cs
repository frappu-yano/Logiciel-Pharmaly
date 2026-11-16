using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Pharmaly.Poste.Modules.SupplierNotes;

/// <summary>
/// Vue du module Bons fournisseurs. Elle ne décide de rien : elle traduit les touches en
/// demandes au modèle de vue et place le curseur là où il le demande.
/// </summary>
public sealed partial class SupplierNotesView : UserControl, IModuleView
{
    private SupplierNotesViewModel? hookedModule;
    private NoteEntryViewModel? hookedEntry;
    private NewNoteViewModel? hookedForm;

    /// <summary>Vrai quand le curseur attend sur la liste des bons, pas encore arrivée du serveur.</summary>
    private bool listAwaitsFocus;

    public SupplierNotesView() => InitializeComponent();

    private SupplierNotesViewModel? ViewModel => DataContext as SupplierNotesViewModel;

    // ---------- Liaison avec les modèles de vue ----------

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (hookedModule is not null)
        {
            hookedModule.PropertyChanged -= OnModulePropertyChanged;
        }

        hookedModule = ViewModel;
        if (hookedModule is not null)
        {
            hookedModule.PropertyChanged += OnModulePropertyChanged;
        }

        HookEntry(hookedModule?.Entry);
        HookForm(hookedModule?.NewNote);
    }

    private void OnModulePropertyChanged(object? sender, PropertyChangedEventArgs change)
    {
        if (hookedModule is not { } module)
        {
            return;
        }

        switch (change.PropertyName)
        {
            case nameof(SupplierNotesViewModel.Entry):
                HookEntry(module.Entry);
                break;

            case nameof(SupplierNotesViewModel.NewNote):
                HookForm(module.NewNote);
                break;

            case nameof(SupplierNotesViewModel.Screen):
                FocusDefault();
                break;

            // La liste arrive du serveur après l'affichage : le curseur qui l'attendait va sur le bon sélectionné.
            case nameof(SupplierNotesViewModel.SelectedNote) when listAwaitsFocus && module.IsList:
                Dispatcher.UIThread.Post(FocusList, DispatcherPriority.Loaded);
                break;
        }
    }

    private void HookEntry(NoteEntryViewModel? entry)
    {
        if (ReferenceEquals(hookedEntry, entry))
        {
            return;
        }

        if (hookedEntry is not null)
        {
            hookedEntry.FocusRequested -= OnEntryFocusRequested;
            hookedEntry.PropertyChanged -= OnEntryPropertyChanged;
        }

        hookedEntry = entry;
        if (entry is not null)
        {
            entry.FocusRequested += OnEntryFocusRequested;
            entry.PropertyChanged += OnEntryPropertyChanged;
        }
    }

    private void HookForm(NewNoteViewModel? form)
    {
        if (ReferenceEquals(hookedForm, form))
        {
            return;
        }

        if (hookedForm is not null)
        {
            hookedForm.FocusRequested -= OnFormFocusRequested;
            hookedForm.PropertyChanged -= OnFormPropertyChanged;
        }

        hookedForm = form;
        if (form is not null)
        {
            form.FocusRequested += OnFormFocusRequested;
            form.PropertyChanged += OnFormPropertyChanged;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // L'employé change de module (ou ferme le logiciel) : le bon ouvert est libéré.
        _ = ViewModel?.DeactivateAsync();
        base.OnDetachedFromVisualTree(e);
    }

    // ---------- Curseur ----------

    public void FocusDefault()
    {
        if (ViewModel is not { } module)
        {
            Focus();
            return;
        }

        switch (module.Screen)
        {
            case NotesScreen.Note when module.Entry is { } entry:
                FocusEntry(entry.IsCreatingProduct || entry.IsConfirming ? null : entry.ActiveField);
                break;

            case NotesScreen.NewNote when module.NewNote is { } form:
                FocusForm(form.ActiveField);
                break;

            default:
                FocusList();
                break;
        }
    }

    private void FocusList()
    {
        if (ViewModel is not { IsList: true })
        {
            return;
        }

        // Les lignes viennent peut-être d'être rechargées : elles doivent exister avant de recevoir le curseur.
        NotesList.UpdateLayout();
        if (NotesList.SelectedIndex >= 0 && NotesList.ContainerFromIndex(NotesList.SelectedIndex) is { } container)
        {
            listAwaitsFocus = false;
            container.Focus();
        }
        else
        {
            listAwaitsFocus = true;
            if (!NotesList.Focus())
            {
                Focus();
            }
        }
    }

    private void OnEntryFocusRequested(object? sender, EntryField field) => FocusEntry(field);

    /// <summary>Place le curseur dans l'écran de saisie ; <c>null</c> = sur la fenêtre ouverte par-dessus.</summary>
    private void FocusEntry(EntryField? field)
    {
        if (hookedEntry is not { } entry)
        {
            return;
        }

        if (entry.IsCreatingProduct)
        {
            FocusBox(QuickNameBox);
            return;
        }

        if (entry.IsConfirming)
        {
            if (entry.Confirmation!.AsksReason)
            {
                FocusBox(ReasonBox);
            }
            else
            {
                Focus();
            }

            return;
        }

        switch (field)
        {
            case EntryField.Barcode when BarcodeBox.IsEffectivelyEnabled:
                FocusBox(BarcodeBox);
                break;

            case EntryField.Quantity when QuantityBox.IsEffectivelyEnabled:
                FocusBox(QuantityBox);
                break;

            case EntryField.Price when PriceBox.IsEffectivelyEnabled:
                FocusBox(PriceBox);
                break;

            default:
                FocusLines();
                break;
        }
    }

    /// <summary>Curseur dans le champ, texte sélectionné : la frappe suivante (ou le scan) le remplace.</summary>
    private static void FocusBox(TextBox box)
    {
        box.Focus();
        box.SelectAll();
    }

    private void FocusLines()
    {
        var list = hookedEntry is { IsHistoryVisible: true } ? HistoryList : LinesList;
        list.UpdateLayout();
        if (list.SelectedIndex >= 0 && list.ContainerFromIndex(list.SelectedIndex) is { } container)
        {
            container.Focus();
        }
        else if (!list.Focus())
        {
            Focus();
        }
    }

    private void OnEntryPropertyChanged(object? sender, PropertyChangedEventArgs change)
    {
        if (hookedEntry is not { } entry)
        {
            return;
        }

        switch (change.PropertyName)
        {
            // La fenêtre s'affiche par ses liaisons, juste après ce changement : le curseur y va ensuite.
            case nameof(NoteEntryViewModel.Confirmation) when entry.IsConfirming:
            case nameof(NoteEntryViewModel.QuickProduct) when entry.IsCreatingProduct:
                Dispatcher.UIThread.Post(() => FocusEntry(null));
                break;

            case nameof(NoteEntryViewModel.SelectedSearchResult) when entry.SelectedSearchResult is { } result:
                SearchResultsList.ScrollIntoView(result);
                break;

            // La ligne qui vient d'être enregistrée reste visible, même au bas d'un long bon.
            case nameof(NoteEntryViewModel.SelectedLine) when entry.SelectedLine is { } line:
                LinesList.ScrollIntoView(line);
                break;

            case nameof(NoteEntryViewModel.IsHistoryVisible) when entry.IsHistoryVisible:
                FocusLines();
                break;
        }
    }

    private void OnFormFocusRequested(object? sender, NewNoteField field) => FocusForm(field);

    private void FocusForm(NewNoteField field)
    {
        switch (field)
        {
            case NewNoteField.Supplier:
                FocusBox(SupplierBox);
                break;
            case NewNoteField.Location:
                LocationBox.Focus();
                break;
            case NewNoteField.Reference:
                FocusBox(ReferenceBox);
                break;
            case NewNoteField.Date:
                FocusBox(DateBox);
                break;
        }
    }

    private void OnFormPropertyChanged(object? sender, PropertyChangedEventArgs change)
    {
        if (change.PropertyName == nameof(NewNoteViewModel.SelectedSuggestion) && hookedForm?.SelectedSuggestion is { } suggestion)
        {
            SuggestionsList.ScrollIntoView(suggestion);
        }
    }

    // ---------- Clavier ----------

    public Task HandleKeyAsync(KeyEventArgs e) => ViewModel switch
    {
        { Screen: NotesScreen.Note, Entry: { } entry } => HandleNoteKeyAsync(entry, e),
        { Screen: NotesScreen.NewNote, NewNote: { } form } module => HandleNewNoteKeyAsync(module, form, e),
        { Screen: NotesScreen.List } module => HandleListKeyAsync(module, e),
        _ => Task.CompletedTask,
    };

    private async Task HandleListKeyAsync(SupplierNotesViewModel module, KeyEventArgs e)
    {
        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        switch (e.Key)
        {
            case Key.N when control:
                e.Handled = true;
                await module.StartNewNoteAsync();
                break;

            case Key.F when control:
                e.Handled = true;
                listAwaitsFocus = false;
                StatusFilter.Focus();
                break;

            case Key.R when control:
                e.Handled = true;
                await ReloadListAsync(module.ReloadAsync);
                break;

            case Key.PageDown when control:
                e.Handled = true;
                await ReloadListAsync(module.NextPageAsync);
                break;

            case Key.PageUp when control:
                e.Handled = true;
                await ReloadListAsync(module.PreviousPageAsync);
                break;

            case Key.Enter when NotesList.IsKeyboardFocusWithin:
                e.Handled = true;
                await module.OpenSelectedAsync();
                break;

            case Key.Enter when FromBox.IsFocused || ToBox.IsFocused:
                e.Handled = true;
                await ReloadListAsync(module.ApplyFiltersAsync);
                break;

            case Key.Enter when (StatusFilter.IsKeyboardFocusWithin && !StatusFilter.IsDropDownOpen)
                                || (SupplierFilter.IsKeyboardFocusWithin && !SupplierFilter.IsDropDownOpen):
                e.Handled = true;
                FocusList();
                break;

            case Key.Escape when !StatusFilter.IsDropDownOpen && !SupplierFilter.IsDropDownOpen:
                e.Handled = true;
                FocusList();
                break;
        }
    }

    /// <summary>Recharge la liste puis replace le curseur dessus, une fois les lignes reconstruites.</summary>
    private async Task ReloadListAsync(Func<Task> reload)
    {
        await reload();
        Dispatcher.UIThread.Post(FocusList, DispatcherPriority.Loaded);
    }

    private async Task HandleNewNoteKeyAsync(SupplierNotesViewModel module, NewNoteViewModel form, KeyEventArgs e)
    {
        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        switch (e.Key)
        {
            case Key.Escape when !LocationBox.IsDropDownOpen:
                e.Handled = true;
                if (!form.CancelCreationProposal())
                {
                    module.CancelNewNote();
                }

                break;

            case Key.Enter when SupplierBox.IsFocused:
                e.Handled = true;
                await form.SubmitSupplierAsync(createNew: control);
                break;

            case Key.Down when SupplierBox.IsFocused:
                e.Handled = true;
                form.MoveSuggestion(+1);
                break;

            case Key.Up when SupplierBox.IsFocused:
                e.Handled = true;
                form.MoveSuggestion(-1);
                break;

            case Key.Enter when LocationBox.IsKeyboardFocusWithin && !LocationBox.IsDropDownOpen:
                e.Handled = true;
                form.SubmitLocation();
                break;

            case Key.Enter when ReferenceBox.IsFocused:
                e.Handled = true;
                form.SubmitReference();
                break;

            case Key.Enter when DateBox.IsFocused:
                e.Handled = true;
                await form.SubmitAsync();
                break;
        }
    }

    private async Task HandleNoteKeyAsync(NoteEntryViewModel entry, KeyEventArgs e)
    {
        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        if (entry.IsConfirming)
        {
            // Tant que la confirmation est affichée, le clavier ne sert qu'à y répondre (et à écrire le motif).
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await entry.ConfirmAsync();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                entry.CancelConfirmation();
            }
            else if (!entry.Confirmation!.AsksReason)
            {
                e.Handled = true;
            }

            return;
        }

        if (entry.IsCreatingProduct)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                entry.CancelQuickProduct();
            }
            else if (e.Key == Key.Enter)
            {
                e.Handled = true;
                if (QuickNameBox.IsFocused && !control)
                {
                    FocusBox(QuickPriceBox);
                }
                else
                {
                    await entry.SubmitQuickProductAsync();
                }
            }

            return;
        }

        if (entry.HasUnconfirmedWrite)
        {
            // Une écriture attend sa réponse : Entrée la renvoie telle quelle, rien d'autre ne s'écrit.
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await entry.RetryAsync();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                await entry.EscapeAsync();
            }

            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                await entry.EscapeAsync();
                break;

            case Key.F7:
                e.Handled = true;
                await entry.ToggleHistoryAsync();
                break;

            case Key.F8:
                e.Handled = true;
                await entry.RetakeAsync();
                break;

            case Key.F9:
                e.Handled = true;
                entry.RequestValidate();
                break;

            case Key.Delete when control:
                e.Handled = true;
                entry.RequestCancelNote();
                break;

            case Key.Enter when BarcodeBox.IsFocused:
                e.Handled = true;
                await entry.SubmitBarcodeAsync();
                break;

            case Key.Enter when QuantityBox.IsFocused:
                e.Handled = true;
                entry.SubmitQuantity();
                break;

            case Key.Enter when PriceBox.IsFocused:
                e.Handled = true;
                await entry.SubmitPriceAsync();
                break;

            case Key.Down when BarcodeBox.IsFocused && entry.HasSearchResults:
                e.Handled = true;
                entry.MoveSearchSelection(+1);
                break;

            case Key.Up when BarcodeBox.IsFocused && entry.HasSearchResults:
                e.Handled = true;
                entry.MoveSearchSelection(-1);
                break;

            case Key.Up when BarcodeBox.IsFocused:
                e.Handled = true;
                entry.FocusLines();
                break;

            case Key.Enter when LinesList.IsKeyboardFocusWithin:
                e.Handled = true;
                entry.BeginEditSelectedLine();
                break;

            case Key.Delete when LinesList.IsKeyboardFocusWithin:
                e.Handled = true;
                entry.RequestCancelSelectedLine();
                break;

            case Key.Down when LinesList.IsKeyboardFocusWithin && entry.CanScan && LinesList.SelectedIndex == LinesList.ItemCount - 1:
                // Sous la dernière ligne, le curseur retourne au champ code-barres.
                e.Handled = true;
                entry.Focus(EntryField.Barcode);
                break;

            default:
                // Un scan pendant que le curseur est sur les lignes : il va dans le champ code-barres.
                if (entry.CanScan && !control && IsTextKey(e.Key) && !IsTextBoxFocused())
                {
                    BarcodeBox.Focus();
                }

                break;
        }
    }

    private static bool IsTextKey(Key key) =>
        key is (>= Key.D0 and <= Key.D9) or (>= Key.NumPad0 and <= Key.NumPad9) or (>= Key.A and <= Key.Z);

    private bool IsTextBoxFocused() => TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox;

    // ---------- Souris : mêmes demandes que le clavier ----------

    private async void OnNewNoteClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } module)
        {
            await module.StartNewNoteAsync();
        }
    }

    private async void OnNoteDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ViewModel is { } module)
        {
            await module.OpenSelectedAsync();
        }
    }

    private async void OnCreateNoteClick(object? sender, RoutedEventArgs e)
    {
        if (hookedForm is { } form)
        {
            await form.SubmitAsync();
        }
    }

    private async void OnHistoryClick(object? sender, RoutedEventArgs e)
    {
        if (hookedEntry is { } entry)
        {
            await entry.ToggleHistoryAsync();
        }
    }

    private void OnValidateClick(object? sender, RoutedEventArgs e) => hookedEntry?.RequestValidate();

    private void OnCancelNoteClick(object? sender, RoutedEventArgs e) => hookedEntry?.RequestCancelNote();

    private async void OnRetakeClick(object? sender, RoutedEventArgs e)
    {
        if (hookedEntry is { } entry)
        {
            await entry.RetakeAsync();
        }
    }

    private async void OnRetryClick(object? sender, RoutedEventArgs e)
    {
        if (hookedEntry is { } entry)
        {
            await entry.RetryAsync();
        }
    }

    private void OnQuickCancelClick(object? sender, RoutedEventArgs e) => hookedEntry?.CancelQuickProduct();

    private async void OnQuickCreateClick(object? sender, RoutedEventArgs e)
    {
        if (hookedEntry is { } entry)
        {
            await entry.SubmitQuickProductAsync();
        }
    }

    private void OnConfirmationCancelClick(object? sender, RoutedEventArgs e) => hookedEntry?.CancelConfirmation();

    private async void OnConfirmationClick(object? sender, RoutedEventArgs e)
    {
        if (hookedEntry is { } entry)
        {
            await entry.ConfirmAsync();
        }
    }
}
