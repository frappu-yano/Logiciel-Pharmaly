# Pharmaly Gestion

Logiciel de gestion des points de vente Pharmaly : bons fournisseurs, ventes, caisse.
Logiciel Windows natif, sans navigateur ; toute la communication passe par le réseau
interne du site, jamais par Internet.

**État : lot 1 en cours (accès).** Le serveur, la base et le logiciel du poste démarrent
et dialoguent ; l'identification à deux niveaux et les permissions sont en place ; le
logiciel du poste a sa structure à modules et l'écran d'administration des employés et
des droits ; le module Diagnostic mesure la rapidité sur un catalogue fictif. Aucune
vente réelle n'est encore enregistrée. Voir `docs/JOURNAL.md` pour l'avancement.

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
dotnet run --project src/Pharmaly.Server                             # serveur, HTTPS sur le port 5443
dotnet run --project src/Pharmaly.Server -- --empreinte              # empreinte de son certificat
dotnet run --project src/Pharmaly.Poste                              # logiciel du poste
```

Le serveur n'écoute qu'en HTTPS, y compris en développement : à son premier lancement, le
poste demande d'approuver l'empreinte du serveur (voir « Échanges chiffrés » plus bas).
Pour un outil qui ne sait pas parler HTTPS, `"Transport": { "ClearHttpPort": 5080 }` dans
`appsettings.Local.json` rétablit une écoute non chiffrée, limitée à `127.0.0.1` et
signalée par un avertissement au démarrage. Ne jamais l'activer sur le serveur d'un magasin.

## Exécutables Windows

Chaque envoi sur GitHub lance la compilation et les tests sous Linux et sous Windows,
puis un essai de rapidité du logiciel sur un vrai Windows. Les exécutables sont
téléchargeables dans l'onglet **Actions** du dépôt (artefact `pharmaly-gestion-windows`).

Sur un poste d'essai :

1. Installer PostgreSQL et créer la base (commandes ci-dessus).
2. Placer `Pharmaly.Server.exe` dans un dossier, avec un fichier `appsettings.Local.json`
   contenant la chaîne de connexion.
3. Lancer une fois `Pharmaly.Server.exe --donnees-fictives 20000`, puis `Pharmaly.Server.exe`.
4. Sur le poste, régler l'adresse du serveur dans `poste.json`
   (`https://adresse-du-serveur:5443`), puis lancer `PharmalyGestion.exe` et approuver
   l'empreinte du serveur (voir « Échanges chiffrés »). Dans le module **Diagnostic** (F6),
   la touche **F9** lance l'essai de rapidité et affiche la mémoire utilisée et les temps
   de réponse.

Options du poste : `--serveur https://adresse:5443`, `--oublier-serveur`, `--essai-auto`,
`--rapport fichier.json`. L'essai automatique tourne sans personne devant l'écran : il lit
l'empreinte attendue du serveur dans la variable d'environnement `PHARMALY_ESSAI_EMPREINTE`
(valeur de la ligne `EMPREINTE=` de `Pharmaly.Server.exe --empreinte`) et refuse tout autre
serveur. Cette variable n'est lue qu'avec `--essai-auto`.

## Essai sur un PC Windows (données fictives)

Chaque envoi sur GitHub produit, dans l'onglet **Actions**, l'archive
`pharmaly-essai-windows` : les deux programmes, `INSTALLER-ESSAI.cmd`,
`DEMARRER-ESSAI.cmd` et `LISEZMOI-ESSAI.txt`. Après installation de PostgreSQL, un
double-clic sur `INSTALLER-ESSAI.cmd` crée une base d'essai, 2 000 produits fictifs avec
du stock, un site, un compte de caisse et un administrateur, puis ouvre le logiciel. Les
identifiants sont écrits dans `ACCES-ESSAI.txt`. Cette installation est rejouée à chaque
envoi sur une machine Windows neuve par l'intégration continue.

Commande utilisée par l'essai pour le stock : `--stock-fictif CODE_SITE QUANTITE MATRICULE`
(refusée si la base contient déjà le moindre mouvement de stock).

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
- La touche **F12** verrouille le poste : l'employé suivant doit s'identifier. Le poste
  se verrouille aussi de lui-même après cinq minutes sans frappe ni souris.
- Les modules s'ouvrent avec **F1 à F6** (ou Ctrl+1 à Ctrl+6) : Vente, Bons fournisseurs,
  Caisse, Produits, Administration, Diagnostic. Seuls ceux que les droits de l'employé
  concernent sont affichés.
- **Administration** (F5, droit « Administrer les utilisateurs et permissions ») : créer
  un employé, cocher ses droits (éventuellement limités à un site), le désactiver,
  remplacer son code personnel.
- Cinq codes faux de suite bloquent le compte pendant cinq minutes.

## Échanges chiffrés entre les postes et le serveur

Tout ce qui circule entre un poste et le serveur (mots de passe de caisse, codes
personnels, jetons, données) est chiffré en HTTPS (TLS 1.2 ou 1.3). Rien à acheter ni à
installer, et pas besoin d'Internet : le serveur fabrique lui-même son certificat, et
chaque poste reconnaît **son** serveur à l'**empreinte** de ce certificat.

**Sur le serveur**

- Port d'écoute : **5443** (réglage `"Transport": { "HttpsPort": 5443 }`). À ouvrir dans le
  pare-feu Windows du serveur ; l'ancien port 5080 n'est plus utilisé et peut être refermé.
- Au premier démarrage, le serveur crée `certificat-serveur.pfx` à côté de
  `Pharmaly.Server.exe`. Ce fichier contient sa clé : il est chiffré par Windows pour cette
  machine et ne se copie pas sur une autre.
- `Pharmaly.Server.exe --empreinte` affiche l'empreinte, par exemple :

  ```
  9F86 D081 884C 7D65 9A2F EAA0 C55A D015
  A3BF 4F1B 2B0B 822C D15D 6C15 B0F0 0A08
  ```

  Notez-la ou gardez la fenêtre ouverte : elle sert à chaque poste. La commande peut être
  lancée avant le premier démarrage du serveur, et autant de fois que nécessaire.

**Première connexion d'un poste**

1. Régler l'adresse dans `poste.json` : `{ "serverUrl": "https://192.168.1.10:5443" }`.
2. Lancer `PharmalyGestion.exe`. Il affiche « Première connexion à ce serveur » et
   l'empreinte qu'il a reçue. À ce stade, rien n'a été envoyé au serveur.
3. Comparer les deux empreintes **caractère par caractère**. Si elles sont identiques,
   appuyer sur **F8**. À la moindre différence, ne pas approuver : le poste ne parle pas
   au bon serveur (mauvaise adresse, ou appareil qui s'interpose sur le réseau).
4. Le poste demande ensuite son compte de caisse, comme avant.

L'approbation se fait une fois par poste (et par utilisateur Windows du poste). Ensuite le
poste refuse tout serveur qui présente une autre empreinte, avec le message « Le serveur
n'est pas celui qui a été approuvé pour ce poste… ». Aucun bouton ne permet de passer outre.

**Changement de serveur, ou réinstallation du serveur**

Un serveur réinstallé, remplacé, ou dont le fichier `certificat-serveur.pfx` a été
supprimé a une nouvelle empreinte : tous les postes le refusent tant qu'elle n'a pas été
approuvée de nouveau. Sur chaque poste, une seule fois :

```
PharmalyGestion.exe --oublier-serveur
```

Le poste oublie l'ancien serveur et son compte de caisse, puis reprend à « Première
connexion d'un poste » : comparer la nouvelle empreinte, F8, compte de caisse. Si un poste
affiche ce refus alors que personne n'a touché au serveur, ne pas utiliser
`--oublier-serveur` : vérifier d'abord l'adresse dans `poste.json` et l'empreinte du
serveur, car c'est exactement ce que verrait un poste dont on détourne la connexion.

Pour garder la même empreinte en changeant seulement de disque ou de dossier sur la **même**
machine, déplacer `certificat-serveur.pfx` avec `Pharmaly.Server.exe`. La sauvegarde de la
base ne contient pas ce fichier.

**Ce que cela protège, et ce que cela ne protège pas**

- Protège contre l'écoute du réseau du magasin (câble ou Wi-Fi) et contre un appareil qui
  se ferait passer pour le serveur après l'approbation.
- La sécurité de la première connexion repose sur la comparaison faite par
  l'installateur : approuver sans comparer revient à faire confiance au réseau ce jour-là.
- Ne protège pas d'une personne qui a la main sur le PC serveur lui-même (elle peut lire
  la base, et utiliser le certificat), ni d'un poste déjà compromis.
- Le certificat n'a pas de date de fin utile (30 ans) et n'est jamais renouvelé
  automatiquement ; pour le renouveler, supprimer le fichier et approuver de nouveau.
- Une adresse `http://` n'est acceptée par le poste que vers `127.0.0.1` (développement).

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

- La liaison entre le serveur et PostgreSQL n'est pas concernée par le chiffrement des
  échanges : PostgreSQL doit rester sur la même machine que le serveur.
- Les modules Vente, Bons fournisseurs, Caisse et Produits affichent seulement « Module
  en cours de développement ».
