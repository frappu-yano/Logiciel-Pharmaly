# Pharmaly Gestion

Logiciel de gestion des points de vente Pharmaly : bons fournisseurs, ventes, caisse.
Logiciel Windows natif, sans navigateur ; toute la communication passe par le réseau
interne du site, jamais par Internet.

**État : lot 1 en cours (accès).** Le serveur, la base et le logiciel du poste démarrent
et dialoguent ; l'identification à deux niveaux et les permissions sont en place ; un
écran d'essai mesure la rapidité sur un catalogue fictif. Aucune vente réelle n'est
encore enregistrée. Voir `docs/JOURNAL.md` pour l'avancement.

## Organisation

| Dossier | Contenu |
|---|---|
| `src/Pharmaly.Server` | Serveur du site (service Windows) : règles métier, base PostgreSQL, migrations |
| `src/Pharmaly.Poste` | Logiciel installé sur chaque poste (`PharmalyGestion.exe`) |
| `src/Pharmaly.Domain` | Règles communes : montants, codes-barres, recherche |
| `src/Pharmaly.Contracts` | Messages échangés entre le poste et le serveur |
| `tests/Pharmaly.Tests` | Tests automatiques (règles, serveur sur vraie base, fenêtre) |
| `docs/` | Architecture, décisions, plan, journal |
| `prototype/` | Ancienne maquette web du 13/11, conservée pour mémoire, non utilisée |

## Développement

Prérequis : SDK .NET 10 et PostgreSQL 16 ou plus récent.

```bash
# Base de développement
psql -U postgres -c "CREATE ROLE pharmaly LOGIN PASSWORD 'pharmaly' CREATEDB"
psql -U postgres -c "CREATE DATABASE pharmaly_gestion_dev OWNER pharmaly"

# Réglage local du serveur (fichier non versionné)
cat > src/Pharmaly.Server/appsettings.Local.json <<'JSON'
{ "ConnectionStrings": { "Pharmaly": "Host=localhost;Database=pharmaly_gestion_dev;Username=pharmaly;Password=pharmaly" } }
JSON

dotnet test                                                          # tous les tests
dotnet run --project src/Pharmaly.Server -- --donnees-fictives 20000 # catalogue fictif, base vide uniquement
dotnet run --project src/Pharmaly.Server                             # serveur sur le port 5080
dotnet run --project src/Pharmaly.Poste                              # logiciel du poste
```

## Exécutables Windows

Chaque envoi sur GitHub lance la compilation et les tests sous Linux et sous Windows,
puis un essai de rapidité du logiciel sur un vrai Windows. Les exécutables sont
téléchargeables dans l'onglet **Actions** du dépôt (artefact `pharmaly-gestion-windows`).

Sur un poste d'essai :

1. Installer PostgreSQL et créer la base (commandes ci-dessus).
2. Placer `Pharmaly.Server.exe` dans un dossier, avec un fichier `appsettings.Local.json`
   contenant la chaîne de connexion.
3. Lancer une fois `Pharmaly.Server.exe --donnees-fictives 20000`, puis `Pharmaly.Server.exe`.
4. Sur le poste, régler l'adresse du serveur dans `poste.json`, puis lancer
   `PharmalyGestion.exe`. La touche **F9** lance l'essai de rapidité et affiche la mémoire
   utilisée et les temps de réponse.

Options du poste : `--serveur http://adresse:5080`, `--essai-auto`, `--rapport fichier.json`.

## Mise en place d'un site

À faire une fois, sur le serveur, par la personne qui l'installe. Chaque secret généré
s'affiche une seule fois, sur une ligne `SECRET=...` : notez-le aussitôt.

```
Pharmaly.Server.exe --initialiser-site D1 "Pharmaly 1 — Didouche" MAGASIN
Pharmaly.Server.exe --creer-compte-caisse caisse1 "Caisse 1" D1
Pharmaly.Server.exe --creer-administrateur 1 "Nom du responsable"
```

- Le **compte de caisse** (identifiant et mot de passe) connecte un poste, une seule fois.
- Chaque **employé** s'identifie ensuite avec son matricule et son code personnel.
- La touche **F12** verrouille le poste : l'employé suivant doit s'identifier.
- Cinq codes faux de suite bloquent le compte pendant cinq minutes.

## Sauvegarde et restauration

```
Pharmaly.Server.exe --sauvegarder D:\Sauvegardes\Pharmaly
Pharmaly.Server.exe --restaurer D:\Sauvegardes\Pharmaly\pharmaly-20261116-230000.dump pharmaly_verification
```

- Chaque sauvegarde est relue avant d'être conservée ; les 30 plus récentes sont gardées.
- Une restauration se fait toujours dans une base **neuve** : la base en service n'est
  jamais écrasée. Pour remettre un site en route, restaurer puis pointer
  `appsettings.Local.json` vers la base restaurée.
- Sauvegarde automatique chaque nuit, à créer une fois sur le serveur (invite de commandes
  en administrateur) :
  `schtasks /Create /TN "Pharmaly sauvegarde" /SC DAILY /ST 23:30 /RU SYSTEM /TR "C:\Pharmaly\Pharmaly.Server.exe --sauvegarder D:\Sauvegardes\Pharmaly"`
- Copier régulièrement le dossier de sauvegardes sur un support qui quitte le magasin.
- Si `pg_dump` n'est pas trouvé, indiquer son dossier dans `appsettings.Local.json` :
  `"Postgres": { "BinDirectory": "C:\\Program Files\\PostgreSQL\\17\\bin" }`.

## Limites actuelles

- Les échanges poste-serveur ne sont pas encore chiffrés : à n'utiliser que sur un réseau
  d'essai, avec des données fictives.
- La gestion des employés et de leurs droits existe côté serveur, mais l'écran
  d'administration n'est pas encore dans le logiciel du poste.
