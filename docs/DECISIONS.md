# Décisions — Pharmaly Gestion V1

Trois catégories : **validé** (règle donnée par la direction), **proposé** (choix
technique recommandé, en attente d'accord), **ouvert** (à arbitrer par la direction).
Aucune règle métier ne passe en « validé » sans accord explicite.

## Validé (cahier des charges du 15/11/2026)

- V1 = bons fournisseurs, ventes, caisse, plus les fondations (authentification,
  permissions, catalogue, stock, clients et créances, audit, sauvegardes, secours).
- BML : aucune lecture ni écriture directe, aucun balayage réseau ; uniquement des
  exports autorisés et des données de test.
- Emplacements : Pharmaly 1 — Didouche, Pharmaly 2 — Audin, dépôt central.
- Mêmes fonctionnalités sur tous les postes, y compris 4 Go / Celeron J4025.
- Bon fournisseur : une ligne enregistrée entre immédiatement en stock ; la validation
  finale n'ajoute rien ; une modification applique la différence ; une annulation crée
  un mouvement compensatoire audité ; un seul employé par bon.
- Prix d'achat : dernier prix proposé, modifiable, historisé, jamais rétroactif. Le prix
  de vente ne change jamais automatiquement.
- Vente interdite si le stock est insuffisant, contrôle fait par le serveur dans la
  transaction.
- Vente impayée : stock déduit, créance créée, aucun encaissement fictif.
- Règlement partiel d'un ticket interdit. Règlement groupé de plusieurs tickets du même
  client autorisé, atomique.
- Caisse : identification à deux niveaux (compte de caisse, puis employé).
- Clôture globale quotidienne ; chiffre d'affaires et trésorerie distingués ; pas de
  journée consolidée tant que des opérations hors ligne restent inconnues.
- Impayés sous Caisse > Impayés ; boutons Espèces et TPE ; raccourci `*` = Espèces.
- Permissions individuelles par case à cocher, contrôlées par le serveur, modifications
  tracées ; nul ne peut s'accorder des droits.
- Charte : turquoise #2ECAC8, orange pêche #F5A97A, beige clair, noir, Poppins.

## Proposé (technique, en attente d'accord)

| # | Proposition | Raison |
|---|---|---|
| P1 | Serveur ASP.NET Core (.NET 10 LTS) + PostgreSQL | Préférence exprimée, service Windows, mémoire faible |
| P2 | Interface web dans Edge en mode application, avec repli Avalonia si le budget mémoire n'est pas tenu | Rien à installer, testable de bout en bout |
| P3 | Service local `Pharmaly.Poste` par caisse (impression, tiroir, secours) | Indispensable au secours et à l'impression directe |
| P4 | Stock = journal en ajout seul + solde avec `CHECK >= 0` | Rend la survente impossible côté base |
| P5 | Identifiant d'opération sur toute écriture | Double clic, relance réseau, resynchronisation |
| P6 | Montants `numeric(12,2)` / `decimal`, quantités `numeric(12,3)` | Aucune erreur d'arrondi |
| P7 | Poursuivre dans le dépôt `Logiciel-Pharmaly`, archiver le prototype Next.js sous une étiquette git, passer le dépôt en privé | Un seul dépôt, historique conservé |
| P8 | Compilation et tests sur Windows par GitHub Actions | Aucun Windows disponible en développement |

## Ouvert — bloquant pour démarrer

**O1. Topologie des serveurs.** Les bons sont saisis au dépôt et les ventes se font en
magasin : le stock doit circuler entre sites.
- A. *Un seul serveur pour les trois sites*, reliés par Internet ou VPN ; le mode
  secours couvre les coupures. Stock unique et cohérent, pas de synchronisation à
  construire. Dépend de la liaison entre sites. **Recommandé.**
- B. *Un serveur par site* avec synchronisation. Chaque site est autonome, mais la
  synchronisation du catalogue et des transferts devient un chantier lourd et risqué
  dès la V1.

**O2. Transfert simple dépôt → magasin en V1.** Le cahier des charges exclut les
transferts complexes, mais sans aucun transfert les magasins n'ont rien à vendre.
Recommandation : un transfert en une étape (sortie du dépôt et entrée en magasin dans la
même transaction), sans état « en transit ».

## Ouvert — à trancher avant le lot concerné

**O3. Périmètre de la clôture quotidienne (lot 7).** Recommandation : une clôture par
magasin et par jour, regroupant toutes ses caisses, avec comptage des espèces par caisse,
et une vue consolidée des magasins en lecture. À confirmer aussi : ce qui se passe pour
une vente saisie après la clôture (recommandation : elle appartient à la journée suivante).

**O4. Stock en mode secours (lot 9).**

| Stratégie | Garantit l'absence de survente | Continuité de vente | Complexité |
|---|---|---|---|
| Stock réservé par caisse | Oui, c'est la seule | Faible : un article en un seul exemplaire n'est vendable que sur une caisse | Élevée |
| Dernier stock connu | Non, si deux caisses isolées vendent le même article | Bonne | Faible |
| Ventes provisoires avec régularisation | Non | Totale | Moyenne |

Recommandation : dernier stock connu comme limite locale, et toute vente de secours
enregistrée comme provisoire puis rapprochée à la reconnexion ; un conflit crée un écart
à régulariser, audité, jamais un rejet silencieux. En boutique, le produit est
physiquement dans la main du client : un conflit révèle en pratique une erreur de stock
plutôt qu'une vraie double vente. Opérations de secours proposées : ventes en espèces et
TPE uniquement ; pas d'impayé, de retour, de règlement de créance ni de bon.

**O5. Annulation ou réduction d'une ligne de bon dont le stock est déjà parti (lot 3).**
Recommandation : refuser au-delà de la quantité encore présente à l'emplacement et
orienter vers une correction d'inventaire auditée.

**O6. Vente impayée (lot 5).** Client obligatoire ? Plafond de crédit ? Recommandation :
client obligatoire (le règlement groupé « du même client » en dépend), pas de plafond en V1.

**O7. TPE (lot 5).** Le terminal est-il autonome (le caissier confirme à l'écran) ou
relié au poste ? Touche de raccourci TPE à choisir.

**O8. Motifs de retour et destination du stock (lot 6).** Liste proposée dans le cahier
des charges, à valider.

**O9. Changement de prix de vente (lot 2).** Approbation d'un responsable requise ou non.

**O10. Modèles de rôles (lot 1).** À quel profil correspondent les 13 permissions
sélectionnées précédemment (liste non transmise).

## Écarts relevés

- Le prototype Next.js du 13/11 tolère le stock négatif et clôture par caissier : il
  n'est pas conforme et ne sera pas réutilisé tel quel.
- `pharmaly-app` nomme les boutiques « Meissonier » et « Audin » ; le cahier des charges
  dit « Didouche » et « Audin ». Les noms seront paramétrables.
