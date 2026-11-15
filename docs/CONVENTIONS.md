# Conventions de développement

À lire avant d'écrire du code. Les règles métier sont dans `CAHIER_DES_CHARGES.md` et
`DECISIONS.md` ; l'architecture dans `ARCHITECTURE.md`.

## Langues

- Code, noms de tables et de colonnes : anglais.
- Tout ce que voit l'employé (écrans, messages d'erreur), commentaires, noms de tests,
  documentation : français.

## Serveur (`src/Pharmaly.Server`)

- ASP.NET Core minimal API, Npgsql + Dapper, SQL explicite. Pas d'ORM.
- Un dossier par module (`Access`, `Catalog`, `Stock`…), avec ses routes dans une méthode
  `MapXxxEndpoints` appelée depuis `Program.cs`.
- Migrations : fichiers `Database/Migrations/NNNN_nom.sql`, numérotés à la suite, jamais
  modifiés une fois fusionnés. Types : montants `numeric(12,2)`, quantités `numeric(12,3)`.
- Chaque route est protégée par `.RequirePermission(Permissions.X)` (ou `RequireEmployee`,
  `RequirePoste`). L'identité vient de `HttpContext.GetIdentity()`.
- Erreurs : `ApiError(code, message)` avec un code stable de `ApiErrorCodes` et un message
  français compréhensible par un employé.
- **Toute écriture métier est une transaction unique** qui contient l'effet, les
  mouvements de stock, l'audit et l'enregistrement d'idempotence.
- **Idempotence** : toute commande d'écriture porte un `operationId` (UUID généré par le
  poste). Rejouer la même commande renvoie le résultat déjà enregistré sans rien refaire.
- **Stock** : jamais de mise à jour directe du solde. Tout passe par le moteur de stock
  (journal `stock_movements` en ajout seul + solde `stock_levels` avec `CHECK >= 0`), les
  lignes étant verrouillées par identifiant de produit croissant.
- Audit : `AuditLog.WriteAsync` dans la transaction, pour toute action sensible.
- Contrats partagés avec le poste : `src/Pharmaly.Contracts`.

## Poste (`src/Pharmaly.Poste`)

- Avalonia 12, MVVM avec CommunityToolkit.Mvvm, liaisons compilées.
- Le poste ne décide de rien : il affiche et envoie des commandes. Les droits servent
  seulement à masquer ce qui serait refusé.
- Tout doit se faire au clavier ; le curseur est toujours placé au bon endroit ; Entrée
  valide ; un lecteur de codes-barres est un clavier qui termine par Entrée.
- Aucune animation. Couleurs et styles : ressources de `App.axaml`. Police Poppins.
- Montants affichés avec `Money.ToDisplay`.

## Tests (`tests/Pharmaly.Tests`)

- xUnit v3. Noms de tests en français, décrivant la règle vérifiée.
- Tests serveur : sur une vraie base PostgreSQL via `ServerFixture` (crée une base neuve).
- Tests de concurrence obligatoires pour toute écriture de stock ou d'argent.
- Après chaque scénario, vérifier l'invariant : somme des mouvements = solde.
- Tests du poste : modèles de vue avec un faux serveur, et parcours clavier sans écran
  (`Avalonia.Headless.XUnit`).
- Commande : `dotnet test -c Release`. `TreatWarningsAsErrors` est actif.
- Ne jamais désactiver, ignorer ou affaiblir un test pour le faire passer.

## Environnement

- PostgreSQL local : rôle `pharmaly` / mot de passe `pharmaly`, droit de créer des bases.
- Pas d'accès à Windows : l'intégration continue (`.github/workflows/ci.yml`) compile,
  teste et lance le logiciel sur Windows.
