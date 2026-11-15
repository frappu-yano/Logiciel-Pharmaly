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
- **Logiciel natif** : aucune interface web, aucun navigateur (décision du 15/11).
- **Communication sans Internet** : postes et serveur dialoguent par le réseau interne
  uniquement (décision du 15/11).
- `pharmaly-app` est un projet distinct : ne pas y toucher, ne rien en reprendre.
- Charte : turquoise #2ECAC8, orange pêche #F5A97A, beige clair, noir, Poppins.

## Proposé (technique, en attente d'accord)

| # | Proposition | Raison |
|---|---|---|
| P1 | Serveur ASP.NET Core (.NET 10 LTS) + PostgreSQL | Préférence exprimée, service Windows, mémoire faible |
| P2 | Logiciel natif Avalonia (.NET), avec repli WPF si le budget mémoire n'est pas tenu | Natif, lançable sur le Mac mini, testable automatiquement |
| P3 | Impression, tiroir et journal de secours intégrés au logiciel du poste | Un seul programme par poste |
| P4 | Stock = journal en ajout seul + solde avec `CHECK >= 0` | Rend la survente impossible côté base |
| P5 | Identifiant d'opération sur toute écriture | Double clic, relance réseau, resynchronisation |
| P6 | Montants `numeric(12,2)` / `decimal`, quantités `numeric(12,3)` | Aucune erreur d'arrondi |
| P7 | Poursuivre dans le dépôt `Logiciel-Pharmaly`, archiver le prototype Next.js sous une étiquette git, passer le dépôt en privé | Un seul dépôt, historique conservé |
| P8 | Compilation et tests sur Windows par GitHub Actions | Aucun Windows disponible en développement |

## Ouvert — bloquant pour démarrer

**O1. Liaison entre les trois sites.** La communication se fait sans Internet : un
serveur ne peut donc servir que les postes de son propre réseau local. Les bons sont
saisis au dépôt et les ventes se font en magasin ; il faut savoir comment l'information
passe d'un site à l'autre.
- A. *Les sites partagent un même réseau local* (même bâtiment ou liaison privée
  câblée) : un seul serveur, un seul stock, transferts immédiats.
- B. *Les sites sont séparés* : un serveur par site. Le transfert dépôt → magasin passe
  par un fichier de transfert signé, porté sur clé USB avec la marchandise, importé à
  la réception ; le catalogue voyage de la même façon. Chaque site reste autonome.
- C. *Démarrer la V1 sur un seul site pilote*, et traiter l'échange entre sites ensuite.

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
