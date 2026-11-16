using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Pharmaly.Contracts;
using Pharmaly.Poste;
using Pharmaly.Poste.Modules.Products;
using Pharmaly.Poste.Modules.SupplierNotes;
using Pharmaly.Poste.ViewModels;
using Pharmaly.Poste.Views;

namespace Pharmaly.Tests.Poste;

/// <summary>Parcours clavier du module Bons fournisseurs, dans la vraie fenêtre, sans écran.</summary>
public sealed class SupplierNotesWindowTests
{
    private readonly FakeServer server = new FakeServer().AsAdministrator();
    private readonly ManualInactivityTimer timer = new();

    private (MainWindow Window, ShellViewModel Shell) OpenWindow()
    {
        var shell = new ShellViewModel(server.Clients, timer, TimeSpan.Zero);
        var window = new MainWindow(shell, new LaunchOptions(null, false, null));
        window.Show();
        return (window, shell);
    }

    private static async Task SettleAsync()
    {
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None) =>
        window.KeyPressQwerty(key, modifiers);

    /// <summary>Le lecteur de codes-barres : les chiffres puis Entrée, comme un clavier.</summary>
    private static async Task ScanAsync(Window window, string barcode)
    {
        window.KeyTextInput(barcode);
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
    }

    private static async Task SignInWithKeyboardAsync(MainWindow window)
    {
        await SettleAsync();
        window.KeyTextInput(FakeServer.RegisterLogin);
        Press(window, PhysicalKey.Enter);
        window.KeyTextInput(FakeServer.RegisterPassword);
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        window.KeyTextInput(FakeServer.EmployeeNumber.ToString());
        Press(window, PhysicalKey.Enter);
        window.KeyTextInput(FakeServer.EmployeeSecret);
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
    }

    private static SupplierNotesView NotesView(MainWindow window) =>
        Assert.IsType<SupplierNotesView>(window.FindControl<ContentControl>("ModuleHost")!.Content);

    [AvaloniaFact]
    public async Task Identification_F2_nouveau_bon_deux_lignes_au_lecteur_puis_validation()
    {
        var (window, shell) = OpenWindow();
        await SignInWithKeyboardAsync(window);

        // F2 : Bons fournisseurs. La liste est vide.
        Press(window, PhysicalKey.F2);
        await SettleAsync();
        var view = NotesView(window);
        var module = Assert.IsType<SupplierNotesViewModel>(shell.CurrentModule);
        Assert.True(module.IsList);
        Assert.Equal("Aucun bon", view.FindControl<TextBlock>("CountLabel")!.Text);
        Assert.True(view.FindControl<Button>("NewNoteButton")!.IsEffectivelyVisible);

        // Ctrl+N : nouveau bon, curseur sur le fournisseur.
        Press(window, PhysicalKey.N, RawInputModifiers.Control);
        await SettleAsync();
        Assert.True(module.IsNewNote);
        var supplierBox = view.FindControl<TextBox>("SupplierBox")!;
        Assert.True(supplierBox.IsFocused);

        // « bio » puis Entrée : le fournisseur trouvé est retenu, le curseur passe à l'emplacement.
        window.KeyTextInput("bio");
        await SettleAsync();
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        Assert.Equal("Biocos Distribution", supplierBox.Text);
        Assert.True(view.FindControl<ComboBox>("LocationBox")!.IsKeyboardFocusWithin);

        // Entrée garde l'emplacement proposé ; référence ; Entrée sur la date du jour crée le bon.
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        Assert.True(view.FindControl<TextBox>("ReferenceBox")!.IsFocused);
        window.KeyTextInput("BL-2026-118");
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        Assert.True(view.FindControl<TextBox>("DateBox")!.IsFocused);
        Press(window, PhysicalKey.Enter);
        await SettleAsync();

        var created = Assert.Single(server.NoteCreateRequests);
        Assert.Equal((FakeServer.Biocos.Id, FakeServer.Depot.Id, "BL-2026-118"), (created.SupplierId, created.LocationId, created.SupplierReference));
        Assert.True(module.IsNote);
        var entry = module.Entry!;
        var noteId = entry.Note.Id;
        Assert.Equal("Bon n° 1 — Biocos Distribution", view.FindControl<TextBlock>("NoteTitleLabel")!.Text);
        Assert.Equal("En saisie", view.FindControl<TextBlock>("NoteStatusLabel")!.Text);
        Assert.Equal(FakeServer.EmployeeId, server.NoteHolder(noteId));

        // Le curseur attend le lecteur dans le champ code-barres.
        var barcodeBox = view.FindControl<TextBox>("BarcodeBox")!;
        var quantityBox = view.FindControl<TextBox>("QuantityBox")!;
        var priceBox = view.FindControl<TextBox>("PriceBox")!;
        Assert.True(barcodeBox.IsFocused);

        // Ligne 1 — scan : le produit s'affiche, prix d'achat proposé, curseur sur la quantité.
        await ScanAsync(window, FakeServer.Creme.Barcode!);
        Assert.Equal(FakeServer.Creme.Name, view.FindControl<TextBlock>("ProductLabel")!.Text);
        Assert.Equal("En stock (Dépôt) : 10", view.FindControl<TextBlock>("StockLabel")!.Text);
        Assert.Equal("1200,00", priceBox.Text);
        Assert.True(quantityBox.IsFocused);

        // Quantité, Entrée : curseur sur le prix. Rien n'est encore envoyé.
        window.KeyTextInput("15");
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        Assert.True(priceBox.IsFocused);
        Assert.Empty(server.NoteLineRequests);
        Assert.Equal(10m, server.NoteStock[FakeServer.Creme.Id]);

        // Entrée : la ligne est enregistrée, le stock entre, le curseur revient au code-barres.
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        Assert.Single(server.NoteLineRequests);
        Assert.Equal(25m, server.NoteStock[FakeServer.Creme.Id]);
        Assert.True(barcodeBox.IsFocused);
        Assert.Equal(string.Empty, barcodeBox.Text ?? string.Empty);
        Assert.Equal("Stock : 10 → 25 (+15)", view.FindControl<TextBlock>("OutcomeStockLabel")!.Text);
        Assert.Equal("Marge inchangée : 650,10 DA (35,14 %)", view.FindControl<TextBlock>("OutcomeMarginLabel")!.Text);
        Assert.Equal("1 ligne", view.FindControl<TextBlock>("LineCountLabel")!.Text);

        // Ligne 2 — produit jamais acheté : le prix d'achat est tapé (remplace le champ vide), avec une virgule.
        await ScanAsync(window, FakeServer.Serum.Barcode!);
        Assert.True(quantityBox.IsFocused);
        window.KeyTextInput("6");
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        Assert.True(priceBox.IsFocused);
        window.KeyTextInput("2800,50");
        Press(window, PhysicalKey.Enter);
        await SettleAsync();

        Assert.Equal(2, server.NoteLineRequests.Count);
        Assert.Equal((FakeServer.Serum.Id, 6m, 2800.50m),
            (server.NoteLineRequests[1].ProductId, server.NoteLineRequests[1].Quantity, server.NoteLineRequests[1].PurchasePrice));
        Assert.NotEqual(server.NoteLineRequests[0].OperationId, server.NoteLineRequests[1].OperationId);
        Assert.Equal(6m, server.NoteStock[FakeServer.Serum.Id]);
        Assert.True(barcodeBox.IsFocused);
        Assert.Equal(2, view.FindControl<ListBox>("LinesList")!.ItemCount);
        Assert.Equal("2 lignes", view.FindControl<TextBlock>("LineCountLabel")!.Text);
        Assert.Equal("34 803,00 DA", entry.TotalText);

        // F9 : la validation demande confirmation ; rien ne part avant l'Entrée.
        Press(window, PhysicalKey.F9);
        await SettleAsync();
        Assert.True(view.FindControl<Border>("ConfirmationPanel")!.IsVisible);
        Assert.Empty(server.NoteValidateRequests);
        Press(window, PhysicalKey.Enter);
        await SettleAsync();

        // Le bon est validé, le verrou libéré, retour à la liste avec le curseur dessus.
        Assert.Single(server.NoteValidateRequests);
        Assert.Equal(SupplierNoteStatuses.Validated, server.NoteStatus(noteId));
        Assert.Null(server.NoteHolder(noteId));
        Assert.Equal(25m, server.NoteStock[FakeServer.Creme.Id]);
        Assert.True(module.IsList);
        Assert.Equal("Bon n° 1 validé : 2 lignes, total d'achat 34 803,00 DA.", view.FindControl<TextBlock>("ListInfoLabel")!.Text);
        var list = view.FindControl<ListBox>("NotesList")!;
        Assert.Equal(1, list.ItemCount);
        Assert.True(list.IsKeyboardFocusWithin);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Produit_inconnu_au_lecteur_creation_rapide_au_clavier_puis_la_ligne()
    {
        var noteId = server.SeedNote();
        var (window, shell) = OpenWindow();
        await SignInWithKeyboardAsync(window);
        Press(window, PhysicalKey.F2);
        await SettleAsync();
        var view = NotesView(window);
        var module = Assert.IsType<SupplierNotesViewModel>(shell.CurrentModule);

        // Entrée sur le bon de la liste : il s'ouvre, verrou pris, curseur sur le code-barres.
        Assert.True(view.FindControl<ListBox>("NotesList")!.IsKeyboardFocusWithin);
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        Assert.True(module.IsNote);
        Assert.True(view.FindControl<TextBox>("BarcodeBox")!.IsFocused);

        // Code inconnu : la création rapide s'ouvre, curseur sur la désignation.
        await ScanAsync(window, FakeServer.UnknownBarcode);
        Assert.True(view.FindControl<Border>("QuickProductPanel")!.IsVisible);
        Assert.True(view.FindControl<TextBox>("QuickNameBox")!.IsFocused);

        // Désignation, Entrée, prix de vente, Entrée : le produit est créé.
        window.KeyTextInput("Baume 4 g");
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        Assert.True(view.FindControl<TextBox>("QuickPriceBox")!.IsFocused);
        window.KeyTextInput("450");
        Press(window, PhysicalKey.Enter);
        await SettleAsync();

        Assert.Equal("Baume 4 g", Assert.Single(server.QuickProductRequests).Name);
        Assert.False(view.FindControl<Border>("QuickProductPanel")!.IsVisible);

        // Retour automatique au bon : produit sélectionné, curseur sur la quantité.
        Assert.Equal("Baume 4 g", view.FindControl<TextBlock>("ProductLabel")!.Text);
        Assert.True(view.FindControl<TextBox>("QuantityBox")!.IsFocused);
        window.KeyTextInput("12");
        Press(window, PhysicalKey.Enter);
        window.KeyTextInput("300");
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        Assert.Equal("Baume 4 g", Assert.Single(server.NoteLines(noteId)).ProductName);
        Assert.True(view.FindControl<TextBox>("BarcodeBox")!.IsFocused);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Modifier_puis_annuler_une_ligne_au_clavier()
    {
        var noteId = server.SeedNote();
        server.SeedNoteLine(noteId, FakeServer.Creme, 15, 1200);
        var (window, _) = OpenWindow();
        await SignInWithKeyboardAsync(window);
        Press(window, PhysicalKey.F2);
        await SettleAsync();
        var view = NotesView(window);
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        var barcodeBox = view.FindControl<TextBox>("BarcodeBox")!;
        var quantityBox = view.FindControl<TextBox>("QuantityBox")!;
        var lines = view.FindControl<ListBox>("LinesList")!;
        Assert.True(barcodeBox.IsFocused);

        // ↑ : le curseur passe sur la dernière ligne ; Entrée : elle revient dans les champs, quantité sélectionnée.
        Press(window, PhysicalKey.ArrowUp);
        await SettleAsync();
        Assert.True(lines.IsKeyboardFocusWithin);
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        Assert.True(quantityBox.IsFocused);
        Assert.Equal("15", quantityBox.Text);
        Assert.Equal("Modification de la ligne 1", view.FindControl<TextBlock>("EntryTitleLabel")!.Text);
        Assert.False(barcodeBox.IsEffectivelyEnabled);

        // La frappe remplace la quantité sélectionnée ; deux Entrées enregistrent.
        window.KeyTextInput("12");
        Press(window, PhysicalKey.Enter);
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        Assert.Equal((1, 12m), (server.NoteLineUpdateRequests[0].ExpectedVersion, server.NoteLineUpdateRequests[0].Quantity));
        Assert.Equal(22m, server.NoteStock[FakeServer.Creme.Id]);
        Assert.True(barcodeBox.IsFocused);

        // ↑ puis Suppr : confirmation ; Échap n'annule rien ; Suppr puis Entrée annule la ligne.
        Press(window, PhysicalKey.ArrowUp);
        await SettleAsync();
        Press(window, PhysicalKey.Delete);
        await SettleAsync();
        Assert.True(view.FindControl<Border>("ConfirmationPanel")!.IsVisible);
        Press(window, PhysicalKey.Escape);
        await SettleAsync();
        Assert.False(view.FindControl<Border>("ConfirmationPanel")!.IsVisible);
        Assert.Empty(server.NoteLineCancelRequests);

        Press(window, PhysicalKey.ArrowUp);
        await SettleAsync();
        Press(window, PhysicalKey.Delete);
        await SettleAsync();
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        Assert.True(server.NoteLines(noteId)[0].IsCancelled);
        Assert.Equal(10m, server.NoteStock[FakeServer.Creme.Id]);
        Assert.True(barcodeBox.IsFocused);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Changer_de_module_libere_le_bon_et_F2_le_reprend()
    {
        var noteId = server.SeedNote();
        var (window, shell) = OpenWindow();
        await SignInWithKeyboardAsync(window);
        Press(window, PhysicalKey.F2);
        await SettleAsync();
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        Assert.Equal(FakeServer.EmployeeId, server.NoteHolder(noteId));
        await ScanAsync(window, FakeServer.Creme.Barcode!);
        window.KeyTextInput("15");

        // F4 : module Produits. Le bon est libéré pour les collègues.
        Press(window, PhysicalKey.F4);
        await SettleAsync();
        Assert.IsType<ProductsView>(window.FindControl<ContentControl>("ModuleHost")!.Content);
        Assert.Null(server.NoteHolder(noteId));

        // F2 : retour au bon, repris, avec la quantité en cours de frappe et le curseur dessus.
        Press(window, PhysicalKey.F2);
        await SettleAsync();
        var view = NotesView(window);
        var module = Assert.IsType<SupplierNotesViewModel>(shell.CurrentModule);
        Assert.True(module.IsNote);
        Assert.Equal(FakeServer.EmployeeId, server.NoteHolder(noteId));
        var quantityBox = view.FindControl<TextBox>("QuantityBox")!;
        Assert.Equal("15", quantityBox.Text);
        Assert.True(quantityBox.IsFocused);
        Assert.Empty(server.NoteLineRequests);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Verrouillage_du_poste_rien_du_bon_ne_reste_a_l_ecran_et_l_employe_suivant_repart_de_la_liste()
    {
        var noteId = server.SeedNote();
        var (window, shell) = OpenWindow();
        await SignInWithKeyboardAsync(window);
        Press(window, PhysicalKey.F2);
        await SettleAsync();
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        await ScanAsync(window, FakeServer.Creme.Barcode!);
        window.KeyTextInput("15");
        var module = Assert.IsType<SupplierNotesViewModel>(shell.CurrentModule);
        var entry = module.Entry!;

        // F12 : le module est détruit, l'écran de saisie disparaît.
        Press(window, PhysicalKey.F12);
        await SettleAsync();
        Assert.Null(window.FindControl<ContentControl>("ModuleHost")!.Content);
        Assert.Null(shell.CurrentModule);
        Assert.Null(module.Entry);
        Assert.False(entry.HoldsLock);
        Assert.Empty(server.NoteLineRequests);
        Assert.Equal(10m, server.NoteStock[FakeServer.Creme.Id]);

        // L'employé suivant arrive sur la liste : aucune saisie en cours, aucun bon ouvert.
        window.KeyTextInput(FakeServer.EmployeeNumber.ToString());
        Press(window, PhysicalKey.Enter);
        window.KeyTextInput(FakeServer.EmployeeSecret);
        Press(window, PhysicalKey.Enter);
        await SettleAsync();
        Press(window, PhysicalKey.F2);
        await SettleAsync();
        var nextModule = Assert.IsType<SupplierNotesViewModel>(shell.CurrentModule);
        Assert.NotSame(module, nextModule);
        Assert.True(nextModule.IsList);
        Assert.Null(nextModule.Entry);
        Assert.Equal(string.Empty, NotesView(window).FindControl<TextBox>("QuantityBox")!.Text ?? string.Empty);
        Assert.NotEqual(Guid.Empty, noteId);
        window.Close();
    }
}
