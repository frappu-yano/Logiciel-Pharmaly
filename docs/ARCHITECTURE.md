# Pharmaly Gestion V1 — audit et proposition d'architecture

État : **proposition en attente de validation**. Rien de structurel n'a été modifié.
Les consommations mémoire citées sont des **estimations** à mesurer sur un poste
Celeron J4025 / 4 Go pendant le lot 0 ; aucune n'a été mesurée à ce jour.

## 1. Audit (15/11/2026, lecture seule)

### Environnement de développement réellement utilisé

| Élément | Constat |
|---|---|
| Machine | Conteneur Linux dans le cloud (Ubuntu 24.04, 4 cœurs, 15 Go), **pas le Mac mini** |
| Mac mini | Non joignable depuis cette session (aucun dossier partagé via Remote Control) |
| .NET SDK | **Absent**. nuget.org et packages.microsoft.com sont joignables : installation possible |
| PostgreSQL | 16.15 installé |
| Node.js | 22.23 |
| Docker | Client 29.9 présent |
| Windows | Aucun poste Windows accessible : l'exécution sur Windows ne peut pas être testée ici |

Conséquences : le code est écrit et testé ici (API, base, tests de concurrence,
interface dans un navigateur sans écran), versionné sur GitHub, et **compilé et testé
sur un vrai Windows par GitHub Actions** (`windows-latest`). Les mesures sur les petits
postes et les essais d'imprimante se font sur vos machines, avec votre aide.

### Projets existants

| Dépôt | Contenu | Rapport avec Pharmaly Gestion |
|---|---|---|
| `Logiciel-Pharmaly` (public) | Prototype Next.js + PostgreSQL écrit le 13/11 : caisse, ventes, stock simple | Sert de maquette. Ses règles ne respectent pas le cahier des charges (stock négatif toléré, clôture par caissier, pas d'impayés ni de bons) |
| `pharmaly-app` (privé, actif) | Application interne en ligne (React, Supabase, Vercel) : planning, pointage, RH, paie, colis, finance | Produit distinct, **à ne pas toucher**. Pas de stock ni de ventes |
| `app-reserviti`, `site` | Non inspectés (sans rapport apparent) | — |

Décision du 15/11 : `pharmaly-app` est un projet sans rapport avec Pharmaly Gestion.
Rien n'en est repris, aucun couplage, aucune modification.

Aucun projet « Pharmaly Gestion » transactionnel n'existe. BML n'a été ni consulté ni
approché ; aucun fichier ni export BML n'est présent dans l'environnement.

## 2. Comparaison des architectures

### Serveur

| Option | RAM estimée | Atouts | Limites |
|---|---|---|---|
| **ASP.NET Core (.NET 10 LTS)** | 80–150 Mo | Service Windows natif, exécutable autonome sans installation de runtime, type `decimal` pour l'argent, typage fort | SDK à installer dans l'environnement de développement |
| Node.js (prototype actuel) | 150–250 Mo | Déjà écrit | Runtime à installer et à maintenir sur Windows, pas de type décimal natif |

La cohérence transactionnelle vient de **PostgreSQL** dans les deux cas ; .NET est
retenu pour le déploiement Windows et la mémoire, pas pour l'intégrité.

**Base : PostgreSQL 16 ou 17.** Transactions ACID, verrous de ligne, contraintes `CHECK`,
sauvegardes à chaud. SQLite est écarté pour le serveur (un seul écrivain à la fois) mais
retenu pour le journal local des caisses.

### Poste de travail

| Option | RAM estimée | Hors ligne | Impression ticket | Testable hors Windows | Verdict |
|---|---|---|---|---|---|
| Electron | 300–500 Mo | Oui | Oui | Oui | Écarté : embarque un navigateur complet par application |
| Interface web dans Edge en mode application | 200–350 Mo | Avec agent local | Via agent local | **Oui, intégralement** | **Recommandé** |
| Client natif WPF / WinForms | 80–150 Mo | Oui | Oui | Non : ni lancé ni testé sur Mac ou Linux | Écarté pour la V1 |
| Client natif Avalonia | 100–200 Mo | Oui | Limité | Oui | **Solution de repli** |
| Tauri / WebView2 | 200–300 Mo | Oui | Oui | Partiel | Pas de gain net sur Edge, outillage en plus |

Pourquoi l'interface web : Edge est déjà présent sur tout Windows 10/11 (rien à
installer), la même interface sert aux postes, au Mac et au téléphone, et chaque écran
peut être testé automatiquement ici avant d'arriver en magasin. Un client WPF serait
plus léger mais je ne pourrais rien en vérifier avant vos essais.

**Garde-fou.** Le lot 0 mesure la mémoire et la réactivité sur un Celeron J4025 avec BML
ouvert. Budget visé : moins de 350 Mo pour l'interface, moins de 100 ms pour l'ajout
d'une ligne. Si le budget n'est pas tenu, le poste passe sur un client Avalonia branché
sur la même API ; le serveur et la base ne changent pas.

## 3. Composants

```
Poste de caisse (Windows)                 Serveur du magasin ou central (Windows)
┌────────────────────────────┐            ┌──────────────────────────────────┐
│ Edge (mode application)    │            │ Pharmaly.Server (service Windows)│
│   interface TypeScript     │   HTTPS    │   API + règles métier            │
│            │               │◄──────────►│   contrôle des permissions       │
│ Pharmaly.Poste (service)   │            │   sert l'interface               │
│   impression ESC/POS       │            │            │                     │
│   tiroir-caisse            │            │ PostgreSQL                       │
│   journal local SQLite     │            │   + sauvegardes planifiées       │
│   file de synchronisation  │            └──────────────────────────────────┘
└────────────────────────────┘
```

- **Pharmaly.Server** : toute la logique métier et tous les contrôles. L'interface ne
  décide de rien.
- **Pharmaly.Poste** : petit service local sur chaque caisse. Il imprime, ouvre le
  tiroir, garde l'interface disponible quand le serveur ne répond plus et conserve les
  opérations de secours. Introduit aux lots 8 et 9, pas avant.
- **Interface** : application TypeScript statique, sans animation, pilotable
  entièrement au clavier, polices locales.

## 4. Modèle transactionnel

1. **Le stock est un journal.** `stock_movements` est en ajout seul (ni modification ni
   suppression, imposé par la base). `stock_levels` contient le solde par produit et par
   emplacement, mis à jour dans la **même transaction** que le mouvement.
2. **Pas de stock négatif.** Contrainte `CHECK (quantity >= 0)` sur `stock_levels`. Deux
   caissiers qui vendent le dernier exemplaire sont mis en file par le verrou de ligne ;
   le second reçoit « stock insuffisant ». La règle tient même si l'interface a un défaut.
3. **Idempotence.** Chaque commande d'écriture porte un identifiant d'opération généré
   par le poste. Il est inscrit dans `operations` (clé unique) dans la même transaction.
   Un double clic, une relance après délai réseau ou une resynchronisation renvoient le
   résultat déjà enregistré sans rien refaire.
4. **Ordre des verrous.** Les lignes de stock sont verrouillées par identifiant de
   produit croissant, pour éviter les interblocages.
5. **Corrections par compensation.** Annulation, retour et correction créent des
   mouvements inverses reliés à l'opération d'origine. Rien n'est effacé.
6. **Contrôle permanent.** Pour chaque produit et emplacement, la somme des mouvements
   doit égaler le solde. Vérifié après chaque test et par une tâche nocturne.

### Bons fournisseurs

- Verrou de saisie à bail : employé, poste, expiration, renouvelé tant que l'écran est
  ouvert. Reprise automatique par le même employé ; libération forcée soumise à une
  permission et auditée.
- Ligne enregistrée : mouvement `ENTREE_BON` de +quantité, immédiat.
- Ligne modifiée : mouvement de la différence seulement.
- Ligne annulée : mouvement compensatoire.
- Validation finale : changement d'état uniquement, **aucun mouvement**.
- Prix d'achat : une ligne d'historique immuable par ligne de bon (fournisseur, bon,
  date). Le prix de vente n'est jamais modifié automatiquement ; l'écran affiche la
  variation de marge.

### Ventes, impayés, règlements

- Vente : lignes, mouvements de sortie et paiement dans une seule transaction.
- Vente impayée : même sortie de stock, une créance `OUVERTE`, **aucun paiement**.
- Règlement : `receivable_settlement_items` porte une clé unique sur la créance, donc un
  second règlement est refusé par la base. Un règlement groupé verrouille toutes les
  créances sélectionnées et les solde dans une seule transaction, ou aucune.
- TPE : paiement `EN_ATTENTE`, puis `CONFIRME` ou `ECHOUE`. La créance n'est soldée
  qu'à la confirmation.

## 5. Modèle de données (première version)

Montants : `numeric(12,2)` en dinars, type `decimal` côté serveur. Quantités :
`numeric(12,3)`, avec un indicateur par produit qui interdit les fractions pour les
articles vendus à l'unité. Identifiants : UUID générés côté poste pour les documents
(nécessaire au mode secours et à la future synchronisation).

| Table | Rôle | Contraintes clés |
|---|---|---|
| `stores`, `stock_locations` | Magasins, dépôt, emplacements | Un emplacement appartient à un magasin ou au dépôt |
| `employees` | Personnes réelles, code PIN ou badge | PIN haché, unique |
| `register_accounts`, `cash_registers` | Comptes de caisse (niveau 1) et caisses | Une caisse appartient à un magasin |
| `permissions`, `role_templates`, `employee_permissions` | Droits individuels, rôles servant de modèles | Unique (employé, permission, magasin éventuel) |
| `products`, `product_barcodes` | Catalogue, plusieurs codes-barres | Code-barres unique ; ancien code BML conservé **sans** unicité |
| `brands`, `categories`, `suppliers` | Référentiels | — |
| `product_sale_prices` | Historique des prix de vente | Ajout seul |
| `purchase_prices` | Historique des achats | Lié à la ligne de bon, immuable |
| `supplier_notes`, `supplier_note_lines` | Bons et lignes | État, verrou à bail, version de ligne |
| `stock_levels` | Solde par produit et emplacement | `CHECK (quantity >= 0)` |
| `stock_movements` | Journal du stock | Ajout seul ; type, quantité signée, document source, employé, opération |
| `sales`, `sale_lines` | Tickets | Numéro stable et code-barres imprimable, unique |
| `payments` | Encaissements | Moyen, état, session de caisse |
| `customers`, `receivables` | Clients et créances | Une créance par ticket impayé |
| `receivable_settlements`, `receivable_settlement_items` | Règlements, éventuellement groupés | Créance unique dans les règlements |
| `returns`, `return_lines`, `return_reasons` | Retours et motifs configurables | Le motif détermine la destination du stock |
| `cash_sessions`, `cash_movements`, `daily_closures` | Caisse et clôture quotidienne | Une seule clôture par périmètre et par jour |
| `operations` | Idempotence | Identifiant d'opération unique |
| `audit_logs` | Journal d'audit | Ajout seul |
| `offline_operations` | Opérations de secours reçues | Identifiant unique, état de rapprochement |

## 6. Déploiement, sauvegardes, évolution

- **Serveur** : un PC parmi les plus puissants, PostgreSQL et Pharmaly.Server installés
  comme services Windows. La topologie (un serveur pour tous les sites ou un par site)
  est une décision ouverte : voir DECISIONS.md.
- **Mises à jour** : un exécutable autonome par composant, remplacé par un installateur ;
  migrations de base appliquées au démarrage, toujours précédées d'une sauvegarde.
- **Sauvegardes** : `pg_dump` planifié chaque nuit et avant chaque mise à jour, copie sur
  un second support et hors site, **restauration testée automatiquement** sur une base
  vierge. L'archivage continu (WAL) pourra s'ajouter si la perte maximale d'une journée
  n'est pas acceptable.
- **Synchronisation future** : les identifiants UUID, le journal de mouvements et la
  table `operations` permettent de rejouer des opérations d'un site vers un autre sans
  doublon. Aucun mécanisme de synchronisation entre serveurs n'est construit en V1.
