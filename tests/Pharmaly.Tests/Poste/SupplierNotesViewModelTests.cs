using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Poste.Modules;
using Pharmaly.Poste.Modules.SupplierNotes;

namespace Pharmaly.Tests.Poste;

/// <summary>Minuterie du battement pilotée par le test : rien n'attend trente secondes.</summary>
internal sealed class ManualHeartbeat : INoteHeartbeat
{
    public event EventHandler? Elapsed;

    public bool IsRunning { get; private set; }

    public void Start() => IsRunning = true;

    public void Stop() => IsRunning = false;

    /// <summary>L'intervalle du battement vient de s'écouler.</summary>
    public void Beat()
    {
        Assert.True(IsRunning, "La minuterie du battement ne tourne pas.");
        Elapsed?.Invoke(this, EventArgs.Empty);
    }

    public bool HasListeners => Elapsed is not null;
}

public sealed class SupplierNotesViewModelTests
{
    private static readonly DateOnly Today = new(2026, 11, 16);

    private readonly FakeServer server = new() { HasPosteSession = true, EmployeeIdentified = true };
    private readonly ManualHeartbeat heartbeat = new();

    /// <summary>Sans droit précisé, l'employé de test a tous les droits.</summary>
    private SupplierNotesViewModel CreateModule(params string[] permissions)
    {
        if (permissions.Length == 0)
        {
            server.AsAdministrator();
        }
        else
        {
            server.EmployeePermissions = permissions;
        }

        return new SupplierNotesViewModel(
            server, server, code => server.EmployeePermissions.Contains(code), heartbeat, TimeSpan.Zero, () => Today);
    }

    /// <summary>Ouvre, depuis la liste, un bon en saisie déjà enregistré sur le serveur.</summary>
    private async Task<(SupplierNotesViewModel Module, NoteEntryViewModel Entry, Guid NoteId)> OpenNoteAsync(params string[] permissions)
    {
        var noteId = server.SeedNote();
        var module = CreateModule(permissions);
        await module.ActivateAsync();
        await module.OpenSelectedAsync();
        return (module, module.Entry!, noteId);
    }

    /// <summary>Le lecteur de codes-barres : il tape le code puis Entrée.</summary>
    private static async Task ScanAsync(NoteEntryViewModel entry, string barcode)
    {
        entry.BarcodeText = barcode;
        await entry.SubmitBarcodeAsync();
    }

    /// <summary>Une ligne complète : scan, quantité, Entrée, prix (laissé tel quel si <c>null</c>), Entrée.</summary>
    private static async Task EnterLineAsync(NoteEntryViewModel entry, string barcode, string quantity, string? price = null)
    {
        await ScanAsync(entry, barcode);
        entry.QuantityText = quantity;
        entry.SubmitQuantity();
        if (price is not null)
        {
            entry.PriceText = price;
        }

        await entry.SubmitPriceAsync();
    }

    // =====================================================================================
    // Liste et nouveau bon
    // =====================================================================================

    [Fact]
    public async Task La_liste_affiche_les_bons_et_se_filtre_par_etat_fournisseur_et_dates()
    {
        server.SeedNote(date: new DateOnly(2026, 11, 2));
        server.SeedNote(SupplierNoteStatuses.Validated, FakeServer.Dermalab, new DateOnly(2026, 11, 10));
        server.SeedNote(SupplierNoteStatuses.Cancelled, date: new DateOnly(2026, 11, 14));
        var module = CreateModule();

        await module.ActivateAsync();

        Assert.Equal(["3", "2", "1"], module.Notes.Select(row => row.NumberText));
        Assert.Equal("3 bons", module.CountText);
        Assert.Equal("3", module.SelectedNote!.NumberText);
        Assert.Equal(["Tous les fournisseurs", "Biocos Distribution", "Dermalab"], module.SupplierChoices.Select(choice => choice.Label));

        module.SelectedStatus = module.StatusChoices.Single(choice => choice.Value == SupplierNoteStatuses.Validated);
        Assert.Equal(["2"], module.Notes.Select(row => row.NumberText));
        Assert.Equal("Validé", module.Notes[0].StatusText);

        module.SelectedStatus = module.StatusChoices[0];
        module.SelectedSupplier = module.SupplierChoices.Single(choice => choice.Value?.Id == FakeServer.Biocos.Id);
        Assert.Equal(["3", "1"], module.Notes.Select(row => row.NumberText));

        module.FromText = "10/11/2026";
        module.ToText = "15.11.2026";
        await module.ApplyFiltersAsync();
        Assert.Equal(["3"], module.Notes.Select(row => row.NumberText));
        Assert.Equal(new DateOnly(2026, 11, 10), server.NoteListRequests[^1].From);
        Assert.Equal(new DateOnly(2026, 11, 15), server.NoteListRequests[^1].To);

        // Une date illisible n'envoie rien et ne vide pas le champ.
        var sent = server.NoteListRequests.Count;
        module.FromText = "hier";
        await module.ApplyFiltersAsync();
        Assert.Equal(NoteInput.InvalidDate, module.ErrorMessage);
        Assert.Equal("hier", module.FromText);
        Assert.Equal(sent, server.NoteListRequests.Count);
    }

    [Fact]
    public async Task La_liste_dit_qui_saisit_un_bon_tenu()
    {
        var noteId = server.SeedNote();
        server.LetKarimTakeNote(noteId);
        var module = CreateModule();

        await module.ActivateAsync();

        Assert.Equal($"Saisie : {FakeServer.KarimName}", module.Notes[0].LockText);
    }

    [Fact]
    public async Task Nouveau_bon_retient_le_fournisseur_trouve_par_son_nom_puis_ouvre_la_saisie_avec_le_verrou()
    {
        var module = CreateModule();
        await module.ActivateAsync();

        await module.StartNewNoteAsync();
        var form = module.NewNote!;
        Assert.True(module.IsNewNote);
        Assert.Equal(NewNoteField.Supplier, form.ActiveField);
        Assert.Equal("16/11/2026", form.DateText);
        Assert.Equal(FakeServer.Depot.Id, form.SelectedLocation!.Location.Id);

        form.SupplierText = "derma";
        Assert.Equal(["Dermalab"], form.Suggestions.Select(supplier => supplier.Name));
        await form.SubmitSupplierAsync();
        Assert.Equal(FakeServer.Dermalab, form.Supplier);
        Assert.Equal("Dermalab", form.SupplierText);
        Assert.Equal(NewNoteField.Location, form.ActiveField);

        form.SubmitLocation();
        Assert.Equal(NewNoteField.Reference, form.ActiveField);

        // La référence est obligatoire : le curseur reste dessus.
        form.SubmitReference();
        Assert.Equal(NewNoteField.Reference, form.ActiveField);
        Assert.Contains("référence du fournisseur est obligatoire", form.ErrorMessage);

        form.Reference = "BL-7781";
        form.SubmitReference();
        Assert.Equal(NewNoteField.Date, form.ActiveField);
        form.DateText = "14/11/2026";
        await form.SubmitAsync();

        var request = Assert.Single(server.NoteCreateRequests);
        Assert.Equal(FakeServer.Dermalab.Id, request.SupplierId);
        Assert.Equal(FakeServer.Depot.Id, request.LocationId);
        Assert.Equal("BL-7781", request.SupplierReference);
        Assert.Equal(new DateOnly(2026, 11, 14), request.NoteDate);
        Assert.Empty(server.SupplierCreateRequests);

        // Le bon créé est réservé à l'employé : la saisie commence, curseur sur le code-barres.
        Assert.True(module.IsNote);
        Assert.Null(module.NewNote);
        var entry = module.Entry!;
        Assert.Equal("Bon n° 1 — Dermalab", entry.Title);
        Assert.True(entry.HoldsLock);
        Assert.True(entry.CanScan);
        Assert.True(heartbeat.IsRunning);
        Assert.Equal(EntryField.Barcode, entry.ActiveField);
        Assert.Equal(FakeServer.EmployeeId, server.NoteHolder(entry.Note.Id));
    }

    [Fact]
    public async Task Nouveau_bon_cree_a_la_volee_le_fournisseur_inconnu_apres_une_seconde_Entree()
    {
        var module = CreateModule();
        await module.ActivateAsync();
        await module.StartNewNoteAsync();
        var form = module.NewNote!;

        form.SupplierText = "Laboratoires Venus";
        await form.SubmitSupplierAsync();

        // Première Entrée : rien n'est créé, la création est proposée.
        Assert.True(form.IsCreationProposed);
        Assert.Empty(server.SupplierCreateRequests);
        Assert.Contains("Entrée : le créer", form.SupplierHint);

        await form.SubmitSupplierAsync();
        var created = Assert.Single(server.SupplierCreateRequests);
        Assert.Equal("Laboratoires Venus", created.Name);
        Assert.Equal("Laboratoires Venus", form.Supplier!.Name);
        Assert.Equal(NewNoteField.Location, form.ActiveField);

        form.SubmitLocation();
        form.Reference = "F-12";
        form.SubmitReference();
        await form.SubmitAsync();
        Assert.Equal("Bon n° 1 — Laboratoires Venus", module.Entry!.Title);
    }

    [Fact]
    public async Task Nouveau_bon_relance_apres_une_panne_garde_le_meme_identifiant_et_ne_cree_qu_un_bon()
    {
        var module = CreateModule();
        await module.ActivateAsync();
        await module.StartNewNoteAsync();
        var form = module.NewNote!;
        form.SupplierText = "Biocos Distribution";
        await form.SubmitSupplierAsync();
        form.Reference = "BL-1";

        server.LoseNextNoteResponse = true;
        await form.SubmitAsync();
        Assert.True(module.IsNewNote);
        Assert.Contains("ne sera pas créé deux fois", form.ErrorMessage);

        await form.SubmitAsync();
        Assert.Equal(2, server.NoteCreateRequests.Count);
        Assert.Equal(server.NoteCreateRequests[0].OperationId, server.NoteCreateRequests[1].OperationId);
        Assert.True(module.IsNote);
        await module.Entry!.LeaveAsync();
        Assert.Single(module.Notes);
    }

    [Fact]
    public async Task Sans_le_droit_de_saisir_Nouveau_bon_n_est_pas_propose()
    {
        var module = CreateModule(Permissions.NotesView);
        await module.ActivateAsync();

        Assert.False(module.CanCreateNote);
        await module.StartNewNoteAsync();
        Assert.True(module.IsList);
        Assert.Null(module.NewNote);
    }

    // =====================================================================================
    // Boucle de saisie
    // =====================================================================================

    [Fact]
    public async Task Boucle_de_saisie_scan_quantite_prix_puis_retour_au_code_barres()
    {
        var (_, entry, noteId) = await OpenNoteAsync();
        var focus = new List<EntryField>();
        entry.FocusRequested += (_, field) => focus.Add(field);
        Assert.Equal(EntryField.Barcode, entry.ActiveField);

        // 1. Scan : le produit s'affiche, dernier prix d'achat proposé, curseur sur la quantité.
        await ScanAsync(entry, FakeServer.Creme.Barcode!);
        Assert.Equal(FakeServer.Creme.Name, entry.ProductTitle);
        Assert.Equal("1200,00", entry.PriceText);
        Assert.Equal("Vente 1 850,10 DA · Dernier achat 1 200,00 DA (Biocos Distribution, 12/10/2026)", entry.ProductDetails);
        Assert.Equal("Stock Dépôt : 10", entry.StockText);
        Assert.Equal([EntryField.Quantity], focus);

        // 2. Quantité puis Entrée : curseur sur le prix d'achat. Rien n'est parti vers le serveur.
        entry.QuantityText = "15";
        entry.SubmitQuantity();
        Assert.Equal([EntryField.Quantity, EntryField.Price], focus);
        Assert.Empty(server.NoteLineRequests);
        Assert.Equal(10m, server.NoteStock[FakeServer.Creme.Id]);

        // 3. Entrée sur le prix : la ligne est enregistrée, le stock entre, le curseur revient au code-barres.
        await entry.SubmitPriceAsync();
        var sent = Assert.Single(server.NoteLineRequests);
        Assert.Equal((FakeServer.Creme.Id, 15m, 1200m), (sent.ProductId, sent.Quantity, sent.PurchasePrice));
        Assert.NotEqual(Guid.Empty, sent.OperationId);
        Assert.Equal(25m, server.NoteStock[FakeServer.Creme.Id]);
        Assert.Equal([EntryField.Quantity, EntryField.Price, EntryField.Barcode], focus);
        Assert.Equal(string.Empty, entry.BarcodeText);
        Assert.Equal(string.Empty, entry.QuantityText);
        Assert.Equal(string.Empty, entry.PriceText);
        Assert.False(entry.HasProduct);

        var row = Assert.Single(entry.Lines);
        Assert.Equal(("1", "15", "1 200,00 DA", "18 000,00 DA"), (row.PositionText, row.QuantityText, row.PriceText, row.TotalText));
        Assert.Equal("1 ligne", entry.LineCountText);
        Assert.Equal("18 000,00 DA", entry.TotalText);
        Assert.Single(server.NoteLines(noteId));

        // Effet affiché sans gêner la saisie suivante : stock avant → après, marge inchangée.
        var outcome = entry.LastOutcome!;
        Assert.Equal($"Ligne 1 enregistrée — {FakeServer.Creme.Name}", outcome.Title);
        Assert.Equal("Stock : 10 → 25 (+15)", outcome.StockText);
        Assert.Equal("Vente 1 850,10 DA · achat 1 200,00 DA", outcome.PriceText);
        Assert.Equal("Marge inchangée : 650,10 DA (35,14 %)", outcome.MarginText);
        Assert.False(outcome.MarginDropped);
    }

    [Fact]
    public async Task Une_quantite_en_cours_de_frappe_ne_touche_jamais_au_stock()
    {
        var (_, entry, noteId) = await OpenNoteAsync();
        await ScanAsync(entry, FakeServer.Creme.Barcode!);

        entry.QuantityText = "1";
        entry.QuantityText = "15";
        entry.QuantityText = "150";
        entry.SubmitQuantity();
        entry.PriceText = "1300";

        Assert.Empty(server.NoteLineRequests);
        Assert.Equal(10m, server.NoteStock[FakeServer.Creme.Id]);

        // Échap abandonne la ligne : rien n'a été envoyé, rien n'est à défaire.
        await entry.EscapeAsync();
        Assert.False(entry.HasProduct);
        Assert.Equal(EntryField.Barcode, entry.ActiveField);
        Assert.Empty(server.NoteLineRequests);
        Assert.Empty(server.NoteLines(noteId));
        Assert.Equal(10m, server.NoteStock[FakeServer.Creme.Id]);
        Assert.True(entry.HoldsLock);
    }

    [Fact]
    public async Task Un_prix_d_achat_en_hausse_montre_la_marge_qui_baisse_sans_toucher_au_prix_de_vente()
    {
        var (_, entry, _) = await OpenNoteAsync();

        await EnterLineAsync(entry, FakeServer.Creme.Barcode!, "4", "1400,50");

        var outcome = entry.LastOutcome!;
        Assert.Equal("Vente 1 850,10 DA · achat 1 200,00 DA → 1 400,50 DA", outcome.PriceText);
        Assert.Equal("Marge : 650,10 DA (35,14 %) → 449,60 DA (24,3 %) — la marge baisse", outcome.MarginText);
        Assert.True(outcome.MarginDropped);
        Assert.True(entry.Lines[0].MarginDropped);
        Assert.Equal("449,60 DA · 24,3 %", entry.Lines[0].MarginText);

        // Un produit jamais acheté : pas de prix proposé, pas de marge « avant ».
        await ScanAsync(entry, FakeServer.Serum.Barcode!);
        Assert.Equal(string.Empty, entry.PriceText);
        Assert.Equal("Vente 4 200,20 DA · Jamais acheté", entry.ProductDetails);
        entry.QuantityText = "2";
        entry.SubmitQuantity();
        entry.PriceText = "3000";
        await entry.SubmitPriceAsync();
        Assert.Equal("Marge (premier achat connu) : 1 200,20 DA (28,57 %)", entry.LastOutcome!.MarginText);
        Assert.False(entry.LastOutcome.MarginDropped);
    }

    [Fact]
    public async Task Pendant_l_envoi_une_seconde_Entree_n_envoie_rien()
    {
        var (_, entry, noteId) = await OpenNoteAsync();
        await ScanAsync(entry, FakeServer.Creme.Barcode!);
        entry.QuantityText = "15";
        entry.SubmitQuantity();

        // La première Entrée part ; le serveur n'a pas encore répondu.
        server.NoteLineGate = new TaskCompletionSource();
        var first = entry.SubmitPriceAsync();
        Assert.True(entry.IsSending);

        // Double appui, relance de la commande, « Réessayer » : rien de plus ne part.
        await entry.SubmitPriceAsync();
        await entry.SubmitPriceAsync();
        await entry.RetryAsync();
        Assert.Single(server.NoteLineRequests);

        server.NoteLineGate.SetResult();
        await first;
        Assert.False(entry.IsSending);

        // Après la réponse, une Entrée de trop tombe dans un champ vide : toujours une seule ligne.
        await entry.SubmitPriceAsync();
        await entry.SubmitBarcodeAsync();
        Assert.Single(server.NoteLineRequests);
        Assert.Single(server.NoteLines(noteId));
        Assert.Single(entry.Lines);
        Assert.Equal(25m, server.NoteStock[FakeServer.Creme.Id]);
    }

    [Fact]
    public async Task Panne_reseau_la_ligne_reste_non_confirmee_puis_Reessayer_renvoie_le_meme_identifiant()
    {
        var (_, entry, noteId) = await OpenNoteAsync();
        await ScanAsync(entry, FakeServer.Creme.Barcode!);
        entry.QuantityText = "15";
        entry.SubmitQuantity();

        // Le serveur enregistre la ligne, mais sa réponse se perd.
        server.LoseNextNoteResponse = true;
        await entry.SubmitPriceAsync();

        Assert.True(entry.HasUnconfirmedWrite);
        Assert.StartsWith($"NON CONFIRMÉ — Ligne « {FakeServer.Creme.Name} » (15 à 1 200,00 DA).", entry.UnconfirmedMessage);
        Assert.Contains("on ne sait pas encore si c'est enregistré", entry.UnconfirmedMessage);
        Assert.False(entry.IsSending);

        // La ligne reste à l'écran telle qu'elle a été tapée ; elle n'est pas comptée comme enregistrée.
        Assert.Equal(("15", "1200,00"), (entry.QuantityText, entry.PriceText));
        Assert.True(entry.HasProduct);
        Assert.Empty(entry.Lines);
        Assert.Null(entry.LastOutcome);
        Assert.False(entry.CanType);
        Assert.False(entry.CanScan);

        // Tant que ce n'est pas tranché, aucune autre écriture ne part.
        entry.RequestValidate();
        Assert.False(entry.IsConfirming);

        // « Réessayer » : même identifiant, le serveur renvoie la ligne déjà enregistrée.
        await entry.RetryAsync();

        Assert.Equal(2, server.NoteLineRequests.Count);
        Assert.Equal(server.NoteLineRequests[0].OperationId, server.NoteLineRequests[1].OperationId);
        Assert.Equal(server.NoteLineRequests[0], server.NoteLineRequests[1]);
        Assert.False(entry.HasUnconfirmedWrite);
        Assert.Equal(string.Empty, entry.UnconfirmedMessage);
        Assert.Single(entry.Lines);
        Assert.Single(server.NoteLines(noteId));
        Assert.Equal(25m, server.NoteStock[FakeServer.Creme.Id]);
        Assert.Equal("Stock : 10 → 25 (+15)", entry.LastOutcome!.StockText);
        Assert.Equal(EntryField.Barcode, entry.ActiveField);
        Assert.True(entry.CanScan);

        // Une nouvelle ligne est une nouvelle intention : nouvel identifiant.
        await EnterLineAsync(entry, FakeServer.Serum.Barcode!, "3", "2800");
        Assert.Equal(3, server.NoteLineRequests.Count);
        Assert.NotEqual(server.NoteLineRequests[0].OperationId, server.NoteLineRequests[2].OperationId);
        Assert.Equal(2, server.NoteLines(noteId).Count);
    }

    [Fact]
    public async Task Demande_jamais_arrivee_Entree_reessaie_avec_le_meme_identifiant_et_n_enregistre_qu_une_ligne()
    {
        var (_, entry, noteId) = await OpenNoteAsync();
        await ScanAsync(entry, FakeServer.Creme.Barcode!);
        entry.QuantityText = "15";
        entry.SubmitQuantity();

        server.DropNextNoteRequest = true;
        await entry.SubmitPriceAsync();
        Assert.True(entry.HasUnconfirmedWrite);
        Assert.Empty(server.NoteLines(noteId));

        // Deuxième panne : toujours non confirmée, toujours le même identifiant.
        server.DropNextNoteRequest = true;
        await entry.SubmitPriceAsync();
        Assert.True(entry.HasUnconfirmedWrite);

        await entry.SubmitPriceAsync();
        Assert.False(entry.HasUnconfirmedWrite);
        Assert.Equal(3, server.NoteLineRequests.Count);
        Assert.Single(server.NoteLineRequests.Select(request => request.OperationId).Distinct());
        Assert.Single(server.NoteLines(noteId));
        Assert.Equal(25m, server.NoteStock[FakeServer.Creme.Id]);
    }

    [Fact]
    public async Task Quitter_un_bon_avec_une_ecriture_non_confirmee_demande_confirmation()
    {
        var (module, entry, _) = await OpenNoteAsync();
        await ScanAsync(entry, FakeServer.Creme.Barcode!);
        entry.QuantityText = "15";
        server.DropNextNoteRequest = true;
        await entry.SubmitPriceAsync();

        await entry.EscapeAsync();
        Assert.True(module.IsNote);
        Assert.Contains("n'est pas confirmée", entry.Confirmation!.Message);

        await entry.ConfirmAsync();
        Assert.True(module.IsList);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("1,2,3")]
    [InlineData("2,5")]
    [InlineData("2000000000015")]
    public async Task Une_quantite_invalide_est_refusee_sans_vider_le_champ(string typed)
    {
        var (_, entry, _) = await OpenNoteAsync();
        await ScanAsync(entry, FakeServer.Creme.Barcode!);

        entry.QuantityText = typed;
        entry.SubmitQuantity();

        Assert.Equal(EntryField.Quantity, entry.ActiveField);
        Assert.Equal(typed, entry.QuantityText);
        Assert.Equal(
            typed == "2,5" ? $"« {FakeServer.Creme.Name} » se vend à l'unité : saisissez une quantité entière." : NoteInput.InvalidQuantity,
            entry.ErrorMessage);

        // Même par l'Entrée finale, rien ne part.
        await entry.SubmitPriceAsync();
        Assert.Empty(server.NoteLineRequests);
        Assert.Equal(typed, entry.QuantityText);
        Assert.True(entry.HasProduct);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("-5")]
    [InlineData("12,345")]
    [InlineData("1.200,50")]
    public async Task Un_prix_invalide_est_refuse_sans_vider_le_champ(string typed)
    {
        var (_, entry, _) = await OpenNoteAsync();
        await ScanAsync(entry, FakeServer.Creme.Barcode!);
        entry.QuantityText = "15";
        entry.SubmitQuantity();

        entry.PriceText = typed;
        await entry.SubmitPriceAsync();

        Assert.Equal(NoteInput.InvalidPrice, entry.ErrorMessage);
        Assert.Equal(EntryField.Price, entry.ActiveField);
        Assert.Equal(typed, entry.PriceText);
        Assert.Equal("15", entry.QuantityText);
        Assert.Empty(server.NoteLineRequests);
    }

    [Theory]
    [InlineData("12", "1250,5", 12, 1250.5)]
    [InlineData("12", "1250.50", 12, 1250.5)]
    [InlineData(" 7 ", "1 250,50", 7, 1250.5)]
    [InlineData("3", "0", 3, 0)]
    public async Task Virgule_ou_point_sont_acceptes(string quantity, string price, double expectedQuantity, double expectedPrice)
    {
        var (_, entry, _) = await OpenNoteAsync();

        await EnterLineAsync(entry, FakeServer.Creme.Barcode!, quantity, price);

        var sent = Assert.Single(server.NoteLineRequests);
        Assert.Equal(((decimal)expectedQuantity, (decimal)expectedPrice), (sent.Quantity, sent.PurchasePrice));
    }

    [Fact]
    public async Task Un_produit_vendu_au_poids_accepte_une_quantite_fractionnee()
    {
        var (_, entry, _) = await OpenNoteAsync();

        await EnterLineAsync(entry, FakeServer.Henna.Barcode!, "2.5", "400");

        Assert.Equal(2.5m, Assert.Single(server.NoteLineRequests).Quantity);
        Assert.Equal("2,5", entry.Lines[0].QuantityText);
        Assert.Equal("Stock : 0 → 2,5 (+2,5)", entry.LastOutcome!.StockText);
    }

    [Fact]
    public async Task Recherche_par_nom_puis_choix_au_clavier()
    {
        var (_, entry, _) = await OpenNoteAsync();

        // « 0 ml » correspond aux deux produits : la liste s'affiche, le premier est surligné.
        entry.BarcodeText = "0 ml";
        Assert.Equal([FakeServer.Creme, FakeServer.Serum], entry.SearchResults);
        Assert.Equal(FakeServer.Creme, entry.SelectedSearchResult);
        Assert.True(entry.HasSearchResults);

        entry.MoveSearchSelection(+1);
        entry.MoveSearchSelection(+1);
        Assert.Equal(FakeServer.Serum, entry.SelectedSearchResult);
        await entry.SubmitBarcodeAsync();

        Assert.Equal(FakeServer.Serum.Name, entry.ProductTitle);
        Assert.False(entry.HasSearchResults);
        Assert.Equal(EntryField.Quantity, entry.ActiveField);

        // Un seul résultat : Entrée le choisit directement.
        await entry.EscapeAsync();
        entry.BarcodeText = "crème";
        await entry.SubmitBarcodeAsync();
        Assert.Equal(FakeServer.Creme.Name, entry.ProductTitle);

        await entry.EscapeAsync();
        entry.BarcodeText = "dentifrice";
        await entry.SubmitBarcodeAsync();
        Assert.Equal("Aucun produit ne correspond à « dentifrice ».", entry.ErrorMessage);
        Assert.False(entry.HasProduct);
        Assert.Equal("dentifrice", entry.BarcodeText);
    }

    // =====================================================================================
    // Produit inconnu, produit désactivé
    // =====================================================================================

    [Fact]
    public async Task Produit_inconnu_creation_rapide_puis_retour_au_bon_curseur_sur_la_quantite()
    {
        var (_, entry, noteId) = await OpenNoteAsync();

        await ScanAsync(entry, FakeServer.UnknownBarcode);

        // Le formulaire s'ouvre, code-barres déjà rempli, vendu à l'unité par défaut.
        var form = entry.QuickProduct!;
        Assert.True(entry.IsCreatingProduct);
        Assert.Equal(FakeServer.UnknownBarcode, form.Barcode);
        Assert.True(form.SoldByUnit);
        Assert.Empty(server.QuickProductRequests);

        // La désignation et le prix de vente sont obligatoires.
        await entry.SubmitQuickProductAsync();
        Assert.Equal("La désignation est obligatoire.", form.ErrorMessage);
        form.Name = "Baume à lèvres 4 g";
        form.SalePriceText = "abc";
        await entry.SubmitQuickProductAsync();
        Assert.Equal(NoteInput.InvalidPrice, form.ErrorMessage);
        Assert.Empty(server.QuickProductRequests);

        form.SalePriceText = "450,00";
        form.Brand = " Nivea ";
        await entry.SubmitQuickProductAsync();

        var request = Assert.Single(server.QuickProductRequests);
        Assert.Equal("Baume à lèvres 4 g", request.Name);
        Assert.Equal([FakeServer.UnknownBarcode], request.Barcodes);
        Assert.Equal((450m, "Nivea", true), (request.SalePrice, request.Brand, request.SoldByUnit));
        Assert.Null(request.Category);

        // Retour automatique au bon : produit sélectionné, curseur sur la quantité.
        Assert.False(entry.IsCreatingProduct);
        Assert.Equal("Baume à lèvres 4 g", entry.ProductTitle);
        Assert.Equal(EntryField.Quantity, entry.ActiveField);
        Assert.Equal(string.Empty, entry.PriceText);
        Assert.Equal("Vente 450,00 DA · Jamais acheté", entry.ProductDetails);
        Assert.Equal("Stock Dépôt : 0", entry.StockText);
        Assert.Contains("créé", entry.InfoMessage);

        // La saisie continue comme pour n'importe quel produit.
        entry.QuantityText = "24";
        entry.SubmitQuantity();
        entry.PriceText = "280";
        await entry.SubmitPriceAsync();
        Assert.Equal("Baume à lèvres 4 g", Assert.Single(server.NoteLines(noteId)).ProductName);
        Assert.Equal(EntryField.Barcode, entry.ActiveField);
    }

    [Fact]
    public async Task Creation_rapide_relancee_apres_une_panne_garde_le_meme_identifiant()
    {
        var (_, entry, _) = await OpenNoteAsync();
        await ScanAsync(entry, FakeServer.UnknownBarcode);
        var form = entry.QuickProduct!;
        form.Name = "Baume à lèvres 4 g";
        form.SalePriceText = "450";

        server.LoseNextNoteResponse = true;
        await entry.SubmitQuickProductAsync();
        Assert.True(entry.IsCreatingProduct);
        Assert.Contains("Entrée pour réessayer", form.ErrorMessage);

        await entry.SubmitQuickProductAsync();
        Assert.Equal(2, server.QuickProductRequests.Count);
        Assert.Equal(server.QuickProductRequests[0].OperationId, server.QuickProductRequests[1].OperationId);
        Assert.False(entry.IsCreatingProduct);
        Assert.Equal("Baume à lèvres 4 g", entry.ProductTitle);
    }

    [Fact]
    public async Task Echap_dans_la_creation_rapide_revient_au_code_barres_sans_rien_creer()
    {
        var (_, entry, _) = await OpenNoteAsync();
        await ScanAsync(entry, FakeServer.UnknownBarcode);

        await entry.EscapeAsync();

        Assert.False(entry.IsCreatingProduct);
        Assert.Equal(EntryField.Barcode, entry.ActiveField);
        Assert.Empty(server.QuickProductRequests);
        Assert.False(entry.HasProduct);
    }

    [Fact]
    public async Task Produit_inconnu_sans_le_droit_de_creer_aucune_creation_n_est_proposee()
    {
        var (_, entry, _) = await OpenNoteAsync(Permissions.NotesEnter);

        await ScanAsync(entry, FakeServer.UnknownBarcode);

        Assert.False(entry.IsCreatingProduct);
        Assert.Null(entry.QuickProduct);
        Assert.Equal(
            $"Aucun produit ne porte le code-barres {FakeServer.UnknownBarcode}. "
            + "Vous n'avez pas le droit de créer un produit : adressez-vous à un responsable.",
            entry.ErrorMessage);
        Assert.Equal(EntryField.Barcode, entry.ActiveField);
        Assert.False(entry.HasProduct);
    }

    [Fact]
    public async Task Produit_desactive_message_clair_et_pas_de_ligne()
    {
        var (_, entry, _) = await OpenNoteAsync();

        await ScanAsync(entry, FakeServer.Retired.Barcode!);

        Assert.False(entry.HasProduct);
        Assert.StartsWith($"« {FakeServer.Retired.Name} » est désactivé : il ne peut pas être reçu sur un bon.", entry.ErrorMessage);
        Assert.Equal(EntryField.Barcode, entry.ActiveField);

        // Les Entrées suivantes n'enregistrent rien.
        entry.SubmitQuantity();
        await entry.SubmitPriceAsync();
        Assert.Empty(server.NoteLineRequests);
    }

    // =====================================================================================
    // Verrou
    // =====================================================================================

    [Fact]
    public async Task Ouvrir_un_bon_prend_le_verrou_et_lance_le_battement()
    {
        var (module, entry, noteId) = await OpenNoteAsync();

        Assert.True(module.IsNote);
        Assert.True(entry.HoldsLock);
        Assert.False(entry.IsReadOnly);
        Assert.Equal(FakeServer.EmployeeId, server.NoteHolder(noteId));
        Assert.Equal(["prendre 1"], server.NoteLockCalls);
        Assert.True(heartbeat.IsRunning);

        // La minuterie bat : le bail est prolongé, sans rien changer à l'écran.
        heartbeat.Beat();
        heartbeat.Beat();
        Assert.Equal(["prendre 1", "battre 1", "battre 1"], server.NoteLockCalls);
        Assert.True(entry.HoldsLock);
    }

    [Fact]
    public async Task Un_bon_tenu_par_un_autre_s_ouvre_en_lecture_seule_avec_le_message_du_serveur()
    {
        var noteId = server.SeedNote();
        server.SeedNoteLine(noteId, FakeServer.Creme, 5, 1200);
        server.LetKarimTakeNote(noteId);
        var module = CreateModule();
        await module.ActivateAsync();

        await module.OpenSelectedAsync();

        var entry = module.Entry!;
        Assert.True(module.IsNote);
        Assert.True(entry.IsReadOnly);
        Assert.Equal(
            $"Ce bon est en cours de saisie par {FakeServer.KarimName} (poste « {FakeServer.KarimRegister} »). Le bon est ouvert en lecture seule.",
            entry.ReadOnlyText);
        Assert.Single(entry.Lines);
        Assert.False(entry.CanType);
        Assert.False(entry.CanScan);
        Assert.False(entry.CanValidate);
        Assert.False(entry.CanCancelNote);
        Assert.True(entry.CanRetake);
        Assert.False(heartbeat.IsRunning);
        Assert.Equal(FakeServer.KarimId, server.NoteHolder(noteId));

        // Aucune touche n'écrit quoi que ce soit.
        await ScanAsync(entry, FakeServer.Creme.Barcode!);
        entry.SelectedLine = entry.Lines[0];
        entry.BeginEditSelectedLine();
        entry.RequestCancelSelectedLine();
        entry.RequestValidate();
        entry.RequestCancelNote();
        Assert.False(entry.HasProduct);
        Assert.False(entry.IsEditing);
        Assert.False(entry.IsConfirming);

        // Quitter ne libère pas le verrou du collègue.
        await entry.EscapeAsync();
        Assert.True(module.IsList);
        Assert.Equal(FakeServer.KarimId, server.NoteHolder(noteId));
        Assert.DoesNotContain("libérer 1", server.NoteLockCalls);
    }

    [Fact]
    public async Task Verrou_perdu_a_l_enregistrement_lecture_seule_et_saisie_en_cours_conservee()
    {
        var (_, entry, noteId) = await OpenNoteAsync();
        await ScanAsync(entry, FakeServer.Creme.Barcode!);
        entry.QuantityText = "15";
        entry.SubmitQuantity();
        entry.PriceText = "1250";

        // Un collègue a repris le bon (bail échu, libération forcée…).
        server.LetKarimTakeNote(noteId);
        await entry.SubmitPriceAsync();

        Assert.True(entry.IsReadOnly);
        Assert.Equal(
            $"Ce bon est en cours de saisie par {FakeServer.KarimName} (poste « {FakeServer.KarimRegister} »). {NoteEntryViewModel.ReadOnlySuffix}",
            entry.ReadOnlyText);
        Assert.False(heartbeat.IsRunning);

        // Rien n'est enregistré, et la ligne tapée est toujours là.
        Assert.Empty(server.NoteLines(noteId));
        Assert.Equal(10m, server.NoteStock[FakeServer.Creme.Id]);
        Assert.False(entry.HasUnconfirmedWrite);
        Assert.Equal(FakeServer.Creme.Barcode, entry.BarcodeText);
        Assert.Equal(FakeServer.Creme.Name, entry.ProductTitle);
        Assert.Equal(("15", "1250"), (entry.QuantityText, entry.PriceText));
        Assert.False(entry.CanType);

        // En lecture seule, Entrée n'envoie plus rien.
        await entry.SubmitPriceAsync();
        Assert.Single(server.NoteLineRequests);
    }

    [Fact]
    public async Task Verrou_perdu_vu_par_le_battement_puis_Reprendre_le_bon_garde_la_saisie()
    {
        var (_, entry, noteId) = await OpenNoteAsync();
        await ScanAsync(entry, FakeServer.Creme.Barcode!);
        entry.QuantityText = "15";

        // Le verrou a été libéré de force : le battement suivant l'apprend.
        server.ForceReleaseNote(noteId);
        heartbeat.Beat();

        Assert.True(entry.IsReadOnly);
        Assert.Equal($"Vous ne tenez pas ce bon. Reprenez-le avant de le modifier. {NoteEntryViewModel.ReadOnlySuffix}", entry.ReadOnlyText);
        Assert.Equal("15", entry.QuantityText);
        Assert.True(entry.HasProduct);
        Assert.False(heartbeat.IsRunning);
        Assert.True(entry.CanRetake);

        // « Reprendre le bon » : le verrou revient, la saisie continue où elle en était.
        await entry.RetakeAsync();

        Assert.False(entry.IsReadOnly);
        Assert.True(heartbeat.IsRunning);
        Assert.Equal(FakeServer.EmployeeId, server.NoteHolder(noteId));
        Assert.Equal("15", entry.QuantityText);
        Assert.Equal(EntryField.Quantity, entry.ActiveField);
        entry.SubmitQuantity();
        await entry.SubmitPriceAsync();
        Assert.Single(server.NoteLines(noteId));
    }

    [Fact]
    public async Task Reprendre_un_bon_encore_tenu_par_un_autre_reste_en_lecture_seule()
    {
        var (_, entry, noteId) = await OpenNoteAsync();
        server.LetKarimTakeNote(noteId);
        heartbeat.Beat();
        Assert.True(entry.IsReadOnly);

        await entry.RetakeAsync();

        Assert.True(entry.IsReadOnly);
        Assert.Contains(FakeServer.KarimName, entry.ReadOnlyText);
        Assert.False(heartbeat.IsRunning);
    }

    [Fact]
    public async Task Une_panne_de_reseau_pendant_le_battement_ne_fait_pas_perdre_le_bon()
    {
        var (_, entry, _) = await OpenNoteAsync();

        server.IsDown = true;
        heartbeat.Beat();
        Assert.True(entry.HoldsLock);
        Assert.True(heartbeat.IsRunning);

        server.IsDown = false;
        heartbeat.Beat();
        Assert.True(entry.HoldsLock);
    }

    [Fact]
    public async Task Quitter_le_bon_libere_le_verrou_et_arrete_le_battement()
    {
        var (module, entry, noteId) = await OpenNoteAsync();

        // Champ vide : Échap quitte le bon.
        await entry.EscapeAsync();

        Assert.True(module.IsList);
        Assert.Null(module.Entry);
        Assert.Null(server.NoteHolder(noteId));
        Assert.Equal(["prendre 1", "libérer 1"], server.NoteLockCalls);
        Assert.False(heartbeat.IsRunning);
        Assert.False(heartbeat.HasListeners);
        Assert.Equal(string.Empty, module.Notes[0].LockText);
    }

    [Fact]
    public async Task Changer_de_module_libere_le_verrou_et_y_revenir_le_reprend_sans_perdre_la_saisie()
    {
        var (module, entry, noteId) = await OpenNoteAsync();
        await ScanAsync(entry, FakeServer.Creme.Barcode!);
        entry.QuantityText = "15";

        // L'employé passe à un autre module : ses collègues peuvent prendre le bon.
        await module.DeactivateAsync();
        Assert.Null(server.NoteHolder(noteId));
        Assert.False(heartbeat.IsRunning);
        heartbeat.Start();
        heartbeat.Beat();
        Assert.Equal(["prendre 1", "libérer 1"], server.NoteLockCalls);
        heartbeat.Stop();

        // Il revient : le bon est repris, la ligne en cours de frappe est toujours là.
        await module.ActivateAsync();
        Assert.Same(entry, module.Entry);
        Assert.True(entry.HoldsLock);
        Assert.Equal(FakeServer.EmployeeId, server.NoteHolder(noteId));
        Assert.True(heartbeat.IsRunning);
        Assert.Equal("15", entry.QuantityText);
        Assert.Equal(EntryField.Quantity, entry.ActiveField);
    }

    [Fact]
    public async Task Au_retour_sur_le_module_un_bon_pris_entre_temps_par_un_autre_passe_en_lecture_seule()
    {
        var (module, entry, noteId) = await OpenNoteAsync();
        await module.DeactivateAsync();
        server.LetKarimTakeNote(noteId);

        await module.ActivateAsync();

        Assert.True(entry.IsReadOnly);
        Assert.Contains(FakeServer.KarimName, entry.ReadOnlyText);
        Assert.False(heartbeat.IsRunning);
    }

    [Fact]
    public async Task La_destruction_du_module_arrete_le_battement_et_demande_la_liberation_du_verrou()
    {
        var (module, entry, noteId) = await OpenNoteAsync();
        await ScanAsync(entry, FakeServer.Creme.Barcode!);
        entry.QuantityText = "15";

        module.Close();

        Assert.Equal(["prendre 1", "libérer 1"], server.NoteLockCalls);
        Assert.Null(server.NoteHolder(noteId));
        Assert.False(heartbeat.IsRunning);
        Assert.False(heartbeat.HasListeners);

        // Rien de l'employé ne reste dans le module, et rien n'est parti vers le stock.
        Assert.Null(module.Entry);
        Assert.Empty(module.Notes);
        Assert.Empty(server.NoteLineRequests);
    }

    [Fact]
    public async Task Consultation_seule_sans_droit_d_ecriture_le_verrou_n_est_pas_demande()
    {
        var noteId = server.SeedNote();
        server.SeedNoteLine(noteId, FakeServer.Creme, 5, 1200);
        var module = CreateModule(Permissions.NotesView);
        await module.ActivateAsync();

        await module.OpenSelectedAsync();

        var entry = module.Entry!;
        Assert.Empty(server.NoteLockCalls);
        Assert.Null(server.NoteHolder(noteId));
        Assert.True(entry.IsReadOnly);
        Assert.Equal(NoteEntryViewModel.ViewOnly, entry.ReadOnlyText);
        Assert.False(entry.CanRetake);
        Assert.False(entry.HasLineRight);
        Assert.Single(entry.Lines);
        Assert.False(heartbeat.IsRunning);
    }

    [Fact]
    public async Task Un_bon_annule_s_ouvre_en_lecture_seule_sans_reprise_possible()
    {
        server.SeedNote(SupplierNoteStatuses.Cancelled);
        var module = CreateModule();
        await module.ActivateAsync();

        await module.OpenSelectedAsync();

        var entry = module.Entry!;
        Assert.True(entry.IsReadOnly);
        Assert.Equal(NoteEntryViewModel.CancelledNote, entry.ReadOnlyText);
        Assert.False(entry.CanRetake);
        Assert.Empty(server.NoteLockCalls);
    }

    [Fact]
    public async Task Session_perdue_pendant_la_saisie_le_poste_redemande_l_identification()
    {
        var (module, entry, _) = await OpenNoteAsync();
        var asked = 0;
        module.IdentificationRequired += (_, _) => asked++;
        await ScanAsync(entry, FakeServer.Creme.Barcode!);
        entry.QuantityText = "15";

        server.EmployeeIdentified = false;
        await entry.SubmitPriceAsync();

        Assert.Equal(1, asked);
        Assert.Empty(server.NoteLineRequests);
        Assert.False(entry.HasUnconfirmedWrite);
    }

    // =====================================================================================
    // Modifier et annuler une ligne
    // =====================================================================================

    [Fact]
    public async Task Modifier_une_ligne_envoie_la_version_lue_et_n_applique_que_la_difference()
    {
        var (_, entry, noteId) = await OpenNoteAsync();
        await EnterLineAsync(entry, FakeServer.Creme.Barcode!, "15");
        var lineId = entry.Lines[0].Id;

        // ↑ depuis le code-barres puis Entrée : les valeurs de la ligne reviennent dans les champs.
        entry.FocusLines();
        Assert.Equal(EntryField.Lines, entry.ActiveField);
        entry.BeginEditSelectedLine();
        Assert.True(entry.IsEditing);
        Assert.Equal("Modification de la ligne 1", entry.EntryTitle);
        Assert.Equal(("15", "1200,00"), (entry.QuantityText, entry.PriceText));
        Assert.Equal(EntryField.Quantity, entry.ActiveField);
        Assert.False(entry.CanScan);

        entry.QuantityText = "12";
        entry.SubmitQuantity();
        Assert.Empty(server.NoteLineUpdateRequests);
        await entry.SubmitPriceAsync();

        var sent = Assert.Single(server.NoteLineUpdateRequests);
        Assert.Equal((1, 12m, 1200m), (sent.ExpectedVersion, sent.Quantity, sent.PurchasePrice));
        Assert.Equal(22m, server.NoteStock[FakeServer.Creme.Id]);
        var row = Assert.Single(entry.Lines);
        Assert.Equal((lineId, "12", 2), (row.Id, row.QuantityText, row.Line.Version));
        Assert.Equal("Ligne 1 modifiée — Crème hydratante 50 ml", entry.LastOutcome!.Title);
        Assert.Equal("Stock : 25 → 22 (−3)", entry.LastOutcome.StockText);
        Assert.Equal("14 400,00 DA", entry.TotalText);
        Assert.False(entry.IsEditing);
        Assert.Equal(EntryField.Barcode, entry.ActiveField);
        Assert.Single(server.NoteLines(noteId));

        // Une seconde modification part avec la nouvelle version.
        entry.BeginEditSelectedLine();
        entry.PriceText = "1100";
        await entry.SubmitPriceAsync();
        Assert.Equal(2, server.NoteLineUpdateRequests[1].ExpectedVersion);
    }

    [Fact]
    public async Task Modifier_une_ligne_sans_rien_changer_n_envoie_rien()
    {
        var (_, entry, _) = await OpenNoteAsync();
        await EnterLineAsync(entry, FakeServer.Creme.Barcode!, "15");

        entry.BeginEditSelectedLine();
        entry.SubmitQuantity();
        await entry.SubmitPriceAsync();

        Assert.Empty(server.NoteLineUpdateRequests);
        Assert.False(entry.IsEditing);
        Assert.Equal("Ligne 1 inchangée.", entry.InfoMessage);
    }

    [Fact]
    public async Task Echap_abandonne_la_modification_d_une_ligne_sans_rien_envoyer()
    {
        var (_, entry, _) = await OpenNoteAsync();
        await EnterLineAsync(entry, FakeServer.Creme.Barcode!, "15");
        entry.BeginEditSelectedLine();
        entry.QuantityText = "1";

        await entry.EscapeAsync();

        Assert.False(entry.IsEditing);
        Assert.Empty(server.NoteLineUpdateRequests);
        Assert.Equal("15", entry.Lines[0].QuantityText);
        Assert.Equal(EntryField.Barcode, entry.ActiveField);
    }

    [Fact]
    public async Task Version_perimee_le_bon_est_recharge_et_l_employe_en_est_informe()
    {
        var (_, entry, noteId) = await OpenNoteAsync();
        await EnterLineAsync(entry, FakeServer.Creme.Barcode!, "15");
        var lineId = entry.Lines[0].Id;
        entry.BeginEditSelectedLine();
        entry.QuantityText = "12";

        // Entre-temps, la ligne a été modifiée ailleurs.
        server.ChangeNoteLineElsewhere(noteId, lineId, 20);
        await entry.SubmitPriceAsync();

        Assert.Equal(
            "Cette ligne a été modifiée depuis son affichage. Rechargez le bon avant de recommencer. "
            + "Le bon vient d'être rechargé : vérifiez-le avant de recommencer.",
            entry.ErrorMessage);
        Assert.False(entry.IsEditing);
        Assert.False(entry.HasProduct);
        var row = Assert.Single(entry.Lines);
        Assert.Equal(("20", 2), (row.QuantityText, row.Line.Version));
        Assert.Equal(20m, server.NoteLines(noteId)[0].Quantity);
        Assert.True(entry.HoldsLock);

        // Après rechargement, la modification repart avec la version à jour.
        entry.SelectedLine = entry.Lines[0];
        entry.BeginEditSelectedLine();
        entry.QuantityText = "12";
        await entry.SubmitPriceAsync();
        Assert.Equal(2, server.NoteLineUpdateRequests[^1].ExpectedVersion);
        Assert.Equal(12m, server.NoteLines(noteId)[0].Quantity);
    }

    [Fact]
    public async Task Stock_deja_sorti_le_message_du_serveur_est_affiche_tel_quel_et_la_ligne_reste_inchangee()
    {
        var (_, entry, noteId) = await OpenNoteAsync();
        await EnterLineAsync(entry, FakeServer.Creme.Barcode!, "15");

        // 21 des 25 exemplaires ont été vendus ou transférés : il en reste 4.
        server.NoteStock[FakeServer.Creme.Id] = 4m;
        entry.BeginEditSelectedLine();
        entry.QuantityText = "5";
        await entry.SubmitPriceAsync();

        Assert.Equal(
            "Impossible de ramener « Crème hydratante 50 ml » de 15 à 5 : il n'en reste que 4 en stock à cet emplacement, le reste est déjà "
            + "parti (vendu ou transféré). Quantité minimale possible pour cette ligne : 11. Au-delà, corrigez par un ajustement d'inventaire.",
            entry.ErrorMessage);
        Assert.Equal(15m, server.NoteLines(noteId)[0].Quantity);
        Assert.Equal("15", entry.Lines[0].QuantityText);
        Assert.Equal(4m, server.NoteStock[FakeServer.Creme.Id]);

        // La modification reste ouverte, curseur sur la quantité : l'employé corrige avec la quantité minimale.
        Assert.True(entry.IsEditing);
        Assert.Equal("5", entry.QuantityText);
        Assert.Equal(EntryField.Quantity, entry.ActiveField);
        entry.QuantityText = "11";
        await entry.SubmitPriceAsync();
        Assert.Equal(string.Empty, entry.ErrorMessage);
        Assert.Equal("11", entry.Lines[0].QuantityText);
        Assert.Equal(0m, server.NoteStock[FakeServer.Creme.Id]);
    }

    [Fact]
    public async Task Annuler_une_ligne_demande_confirmation_puis_retire_sa_quantite_du_stock()
    {
        var (_, entry, noteId) = await OpenNoteAsync();
        await EnterLineAsync(entry, FakeServer.Creme.Barcode!, "15");
        await EnterLineAsync(entry, FakeServer.Serum.Barcode!, "2", "3000");
        entry.SelectedLine = entry.Lines[0];

        entry.RequestCancelSelectedLine();

        // Opération critique : rien ne part avant la confirmation.
        Assert.Equal(
            "Annuler la ligne 1 « Crème hydratante 50 ml » ? Sa quantité (15) sera retirée du stock.", entry.Confirmation!.Message);
        Assert.False(entry.Confirmation.AsksReason);
        Assert.Empty(server.NoteLineCancelRequests);

        // Échap : rien n'est annulé.
        await entry.EscapeAsync();
        Assert.False(entry.IsConfirming);
        Assert.Empty(server.NoteLineCancelRequests);

        entry.RequestCancelSelectedLine();
        await entry.ConfirmAsync();

        Assert.Equal(1, Assert.Single(server.NoteLineCancelRequests).ExpectedVersion);
        Assert.True(server.NoteLines(noteId)[0].IsCancelled);
        Assert.Equal(10m, server.NoteStock[FakeServer.Creme.Id]);
        Assert.True(entry.Lines[0].IsCancelled);
        Assert.Equal("Annulée", entry.Lines[0].TotalText);
        Assert.Equal("Stock : 25 → 10 (−15)", entry.LastOutcome!.StockText);
        Assert.Equal(("1 ligne", "6 000,00 DA"), (entry.LineCountText, entry.TotalText));

        // Une ligne annulée ne se modifie plus et ne s'annule pas deux fois.
        entry.SelectedLine = entry.Lines[0];
        entry.BeginEditSelectedLine();
        Assert.False(entry.IsEditing);
        Assert.Equal("Cette ligne est annulée : elle ne peut plus être modifiée.", entry.ErrorMessage);
        entry.RequestCancelSelectedLine();
        Assert.False(entry.IsConfirming);
        Assert.Single(server.NoteLineCancelRequests);
    }

    [Fact]
    public async Task Annulation_d_une_ligne_refusee_si_son_stock_est_deja_sorti()
    {
        var (_, entry, noteId) = await OpenNoteAsync();
        await EnterLineAsync(entry, FakeServer.Creme.Barcode!, "15");
        server.NoteStock[FakeServer.Creme.Id] = 4m;

        entry.RequestCancelSelectedLine();
        await entry.ConfirmAsync();

        Assert.StartsWith("Impossible d'annuler la ligne « Crème hydratante 50 ml » (15) : il n'en reste que 4 en stock", entry.ErrorMessage);
        Assert.False(server.NoteLines(noteId)[0].IsCancelled);
        Assert.False(entry.Lines[0].IsCancelled);
    }

    // =====================================================================================
    // Valider et annuler le bon
    // =====================================================================================

    [Fact]
    public async Task Valider_le_bon_demande_confirmation_puis_revient_a_la_liste()
    {
        var (module, entry, noteId) = await OpenNoteAsync();
        await EnterLineAsync(entry, FakeServer.Creme.Barcode!, "15");

        entry.RequestValidate();

        Assert.StartsWith("Valider le bon n° 1 de Biocos Distribution ? 1 ligne, total d'achat 18 000,00 DA.", entry.Confirmation!.Message);
        Assert.Empty(server.NoteValidateRequests);
        Assert.Equal(SupplierNoteStatuses.InProgress, server.NoteStatus(noteId));

        await entry.ConfirmAsync();

        Assert.Single(server.NoteValidateRequests);
        Assert.Equal(SupplierNoteStatuses.Validated, server.NoteStatus(noteId));

        // La validation ne fait rien entrer une deuxième fois.
        Assert.Equal(25m, server.NoteStock[FakeServer.Creme.Id]);
        Assert.True(module.IsList);
        Assert.Null(module.Entry);
        Assert.Equal("Bon n° 1 validé : 1 ligne, total d'achat 18 000,00 DA.", module.InfoMessage);
        Assert.Equal("Validé", module.Notes[0].StatusText);
        Assert.Null(server.NoteHolder(noteId));
        Assert.False(heartbeat.IsRunning);
        Assert.False(heartbeat.HasListeners);
    }

    [Fact]
    public async Task Valider_est_refuse_tant_qu_une_ligne_est_en_cours_de_saisie()
    {
        var (_, entry, _) = await OpenNoteAsync();
        await EnterLineAsync(entry, FakeServer.Creme.Barcode!, "15");
        await ScanAsync(entry, FakeServer.Serum.Barcode!);
        entry.QuantityText = "3";

        entry.RequestValidate();

        Assert.False(entry.IsConfirming);
        Assert.Contains("Une ligne est en cours de saisie", entry.ErrorMessage);
        Assert.Equal("3", entry.QuantityText);
    }

    [Fact]
    public async Task Valider_un_bon_vide_affiche_le_refus_du_serveur()
    {
        var (module, entry, _) = await OpenNoteAsync();

        entry.RequestValidate();
        await entry.ConfirmAsync();

        Assert.Equal("Ce bon n'a aucune ligne : il ne peut pas être validé.", entry.ErrorMessage);
        Assert.True(module.IsNote);
        Assert.True(entry.HoldsLock);
    }

    [Fact]
    public async Task Annuler_le_bon_demande_confirmation_avec_motif_puis_retire_ses_lignes_du_stock()
    {
        var (module, entry, noteId) = await OpenNoteAsync();
        await EnterLineAsync(entry, FakeServer.Creme.Barcode!, "15");

        entry.RequestCancelNote();

        var confirmation = entry.Confirmation!;
        Assert.True(confirmation.AsksReason);
        Assert.StartsWith("Annuler le bon n° 1 de Biocos Distribution ? Toutes ses lignes (1 ligne, 18 000,00 DA) seront retirées du stock.", confirmation.Message);
        Assert.Empty(server.NoteCancelRequests);

        confirmation.Reason = "  Livraison refusée  ";
        await entry.ConfirmAsync();

        Assert.Equal("Livraison refusée", Assert.Single(server.NoteCancelRequests).Reason);
        Assert.Equal(SupplierNoteStatuses.Cancelled, server.NoteStatus(noteId));
        Assert.Equal(10m, server.NoteStock[FakeServer.Creme.Id]);
        Assert.True(module.IsList);
        Assert.Equal("Bon n° 1 annulé : ses lignes sont retirées du stock.", module.InfoMessage);
        Assert.False(heartbeat.IsRunning);
    }

    [Fact]
    public async Task Annuler_le_bon_sans_motif_envoie_un_motif_absent()
    {
        var (_, entry, _) = await OpenNoteAsync();

        entry.RequestCancelNote();
        await entry.ConfirmAsync();

        Assert.Null(Assert.Single(server.NoteCancelRequests).Reason);
    }

    [Fact]
    public async Task Annulation_du_bon_refusee_si_du_stock_est_deja_sorti_le_refus_du_serveur_est_affiche()
    {
        var (module, entry, noteId) = await OpenNoteAsync();
        await EnterLineAsync(entry, FakeServer.Creme.Barcode!, "15");
        server.NoteStock[FakeServer.Creme.Id] = 4m;

        entry.RequestCancelNote();
        await entry.ConfirmAsync();

        Assert.Equal(
            "Annulation impossible : une partie de la marchandise de ce bon a déjà quitté le stock (vendue ou transférée). "
            + "Produits concernés : « Crème hydratante 50 ml » (15 reçu, 4 encore en stock). Rien n'a été modifié.",
            entry.ErrorMessage);
        Assert.Equal(SupplierNoteStatuses.InProgress, server.NoteStatus(noteId));
        Assert.True(module.IsNote);
        Assert.True(entry.HoldsLock);
        Assert.Single(entry.Lines);
    }

    [Fact]
    public async Task Validation_restee_sans_reponse_Reessayer_garde_le_meme_identifiant()
    {
        var (module, entry, noteId) = await OpenNoteAsync();
        await EnterLineAsync(entry, FakeServer.Creme.Barcode!, "15");

        server.LoseNextNoteResponse = true;
        entry.RequestValidate();
        await entry.ConfirmAsync();
        Assert.True(module.IsNote);
        Assert.True(entry.HasUnconfirmedWrite);
        Assert.StartsWith("NON CONFIRMÉ — Validation du bon n° 1.", entry.UnconfirmedMessage);

        await entry.RetryAsync();
        Assert.Equal(2, server.NoteValidateRequests.Count);
        Assert.Equal(server.NoteValidateRequests[0].OperationId, server.NoteValidateRequests[1].OperationId);
        Assert.True(module.IsList);
        Assert.Equal(SupplierNoteStatuses.Validated, server.NoteStatus(noteId));
    }

    // =====================================================================================
    // Droits et historique
    // =====================================================================================

    [Fact]
    public async Task Les_actions_ne_sont_proposees_qu_avec_le_droit_correspondant()
    {
        // Saisir seulement : ni validation, ni annulation du bon.
        var (_, entry, _) = await OpenNoteAsync(Permissions.NotesEnter);
        Assert.True(entry.CanEnterLines);
        Assert.False(entry.CanValidate);
        Assert.False(entry.CanCancelNote);
        entry.RequestValidate();
        entry.RequestCancelNote();
        Assert.False(entry.IsConfirming);
        Assert.Empty(server.NoteValidateRequests);
        await entry.LeaveAsync();

        // Valider seulement : le bon est tenu pour la validation, mais aucune ligne ne s'écrit.
        var validatorModule = CreateModule(Permissions.NotesValidate);
        await validatorModule.ActivateAsync();
        await validatorModule.OpenSelectedAsync();
        var validator = validatorModule.Entry!;
        Assert.True(validator.HoldsLock);
        Assert.True(validator.CanValidate);
        Assert.False(validator.CanCancelNote);
        Assert.False(validator.HasLineRight);
        Assert.False(validator.CanType);
        await ScanAsync(validator, FakeServer.Creme.Barcode!);
        Assert.False(validator.HasProduct);
        await validator.LeaveAsync();

        // Annuler seulement.
        var cancellerModule = CreateModule(Permissions.NotesCancel);
        await cancellerModule.ActivateAsync();
        await cancellerModule.OpenSelectedAsync();
        Assert.True(cancellerModule.Entry!.CanCancelNote);
        Assert.False(cancellerModule.Entry.CanValidate);
        Assert.False(cancellerModule.Entry.HasLineRight);
    }

    [Fact]
    public async Task Un_bon_valide_ne_se_corrige_qu_avec_le_droit_de_modifier()
    {
        var noteId = server.SeedNote(SupplierNoteStatuses.Validated);
        server.SeedNoteLine(noteId, FakeServer.Creme, 15, 1200);

        var entererModule = CreateModule(Permissions.NotesEnter);
        await entererModule.ActivateAsync();
        await entererModule.OpenSelectedAsync();
        Assert.False(entererModule.Entry!.HasLineRight);
        Assert.False(entererModule.Entry.CanValidate);
        await entererModule.Entry.LeaveAsync();

        var editorModule = CreateModule(Permissions.NotesEdit);
        await editorModule.ActivateAsync();
        await editorModule.OpenSelectedAsync();
        var editor = editorModule.Entry!;
        Assert.True(editor.CanEnterLines);
        editor.SelectedLine = editor.Lines[0];
        editor.BeginEditSelectedLine();
        editor.QuantityText = "14";
        await editor.SubmitPriceAsync();
        Assert.Equal(14m, server.NoteLines(noteId)[0].Quantity);
    }

    [Fact]
    public async Task L_historique_des_etats_est_consultable_du_plus_recent_au_plus_ancien()
    {
        var module = CreateModule();
        await module.ActivateAsync();
        await module.StartNewNoteAsync();
        var form = module.NewNote!;
        form.SupplierText = "Biocos Distribution";
        await form.SubmitSupplierAsync();
        form.Reference = "BL-9";
        await form.SubmitAsync();
        var entry = module.Entry!;
        await EnterLineAsync(entry, FakeServer.Creme.Barcode!, "15");

        await entry.ToggleHistoryAsync();

        Assert.True(entry.IsHistoryVisible);
        Assert.Equal(2, entry.History.Count);
        Assert.StartsWith("Ligne 1 « Crème hydratante 50 ml »", entry.History[0].Description);
        Assert.Equal("Bon n° 1 créé pour « Biocos Distribution », référence BL-9.", entry.History[1].Description);
        Assert.Equal("En saisie", entry.History[1].StatusText);
        Assert.Equal(FakeServer.EmployeeName, entry.History[1].EmployeeName);

        // Échap referme l'historique sans quitter le bon.
        await entry.EscapeAsync();
        Assert.False(entry.IsHistoryVisible);
        Assert.True(module.IsNote);
    }

    [Fact]
    public async Task Serveur_injoignable_au_scan_rien_n_est_enregistre_et_le_message_le_dit()
    {
        var (_, entry, _) = await OpenNoteAsync();

        server.IsDown = true;
        await ScanAsync(entry, FakeServer.Creme.Barcode!);

        Assert.Equal($"{ModuleViewModel.ServerUnreachableMessage} Rien n'a été enregistré.", entry.ErrorMessage);
        Assert.False(entry.HasProduct);
        Assert.False(entry.HasUnconfirmedWrite);
        Assert.Equal(FakeServer.Creme.Barcode, entry.BarcodeText);
    }
}
