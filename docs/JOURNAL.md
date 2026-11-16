# Journal d'avancement — Pharmaly Gestion

## 15/11/2026 — Audit et proposition d'architecture

- Reçu le cahier des charges « Pharmaly Gestion V1 ».
- Audit en lecture seule de l'environnement (conteneur Linux cloud, .NET absent,
  PostgreSQL 16 présent) et des dépôts `Logiciel-Pharmaly` et `pharmaly-app`.
- `pharmaly-app` cloné en lecture seule pour inspection ; aucun fichier modifié.
- BML : non consulté, non approché.
- Rédigé `ARCHITECTURE.md`, `DECISIONS.md`, `PLAN.md`.
- Aucun code Pharmaly Gestion écrit. Aucun test exécuté pour cette version.

- Consigne reçue : `pharmaly-app` n'a rien à voir avec ce projet, ne pas y toucher. Copie
  locale de lecture supprimée de l'environnement.

- Décisions de la direction : logiciel natif sans interface web ; communication sans
  Internet. Architecture révisée : client natif Avalonia, serveur par réseau local.
- .NET SDK 10.0.115 installé dans le conteneur. Essai jetable hors dépôt : fenêtre
  Avalonia 12.1.6 compilée et ouverte sans écran (65 Mo), exécutable Windows autonome
  produit (104 Mo), non lancé sous Windows.

- Décisions de la direction : un serveur par site, échanges entre sites par fichier
  (clé USB ou e-mail), un seul site pilote d'abord pour juger rapidité, lourdeur et
  sécurité.

### Lot 0 — socle (livré sur la branche de développement)

Fait :
- Prototype web déplacé dans `prototype/` (rien supprimé).
- Solution .NET 10 : `Pharmaly.Server`, `Pharmaly.Poste`, `Pharmaly.Domain`,
  `Pharmaly.Contracts`, `Pharmaly.Tests`.
- Serveur : migrations SQL versionnées avec somme de contrôle et verrou, migration 0001
  (sites, emplacements, marques, catégories, produits, codes-barres), recherche par mots
  sans accents ni casse, par début de code-barres et par code interne, lecture d'un
  code-barres exact, chargement d'un catalogue fictif.
- Poste : fenêtre native aux couleurs Pharmaly (Poppins embarquée), recherche, lecture
  de code-barres, lignes, total, pilotage complet au clavier, mesures de mémoire et de
  temps de réponse, essai de rapidité (F9 ou `--essai-auto`).
- Intégration continue Linux et Windows (`.github/workflows/ci.yml`).

Tests exécutés dans l'environnement de développement (Linux) : **37 réussis, 0 échec**
(règles, serveur sur une vraie base PostgreSQL créée pour l'occasion, fenêtre sans écran).

Mesures relevées sous Linux, écran virtuel, 20 000 produits fictifs, serveur et poste
sur la même machine (elles ne valent pas pour un Celeron sous Windows) :
- recherche : médiane 7 à 9 ms, 95 % sous 16 à 20 ms ;
- ajout d'une ligne : 0,2 à 0,3 ms ;
- démarrage du poste : 1,2 s ;
- mémoire du poste : 193 à 204 Mo ; mémoire du serveur : 110 Mo après 200 recherches.

Défaut trouvé et corrigé pendant les essais : la recherche par début de code-barres
prenait 2,1 s sur 20 000 produits ; elle prend 5 ms après réécriture de la requête et
passage du code-barres en collation « C ».

Non fait dans ce lot, volontairement : identification, permissions, chiffrement des
échanges (lot 1) ; stock (lot 2).

Vérifié sur un vrai Windows (machine GitHub Actions, exécution n° 3, réussie) :
- les 37 tests passent ; les deux exécutables autonomes sont produits (archive de 91 Mo) ;
- `PharmalyGestion.exe` s'ouvre et joint le serveur ; essai automatique sur 20 000
  produits fictifs : 80 recherches, 95 % sous 29 ms ; 80 lignes ajoutées en 0,3 ms
  chacune ; démarrage en 1,3 s ; mémoire du poste 170 Mo après l'essai (131 Mo à
  l'ouverture) ; mémoire du serveur 68 Mo (relevée à la première exécution).
- Défaut trouvé par cet essai et corrigé : avec l'adresse « localhost », Windows tente
  d'abord l'IPv6 et le poste ne joignait pas le serveur. Adresse par défaut passée en
  127.0.0.1 et serveur à l'écoute en IPv4 et IPv6.

Cette machine est bien plus puissante qu'un poste de magasin.
**Reste à mesurer** : un poste Celeron J4025 / 4 Go avec BML ouvert.

**En attente de la direction** : choix du site pilote ; O2 (transfert simple) avant le lot 3.

### Lot 1, première partie — accès (livré sur la branche de développement)

Décision de la direction : le site pilote sera le magasin dont le poste est le plus
faible et qui est le plus calme ; son nom sera donné au moment de l'installation.

Fait :
- Migration 0002 : employés, comptes de caisse, permissions individuelles (éventuellement
  limitées à un site), modèles de rôles, sessions, journal d'audit en ajout seul.
- Identification à deux niveaux : compte de caisse du poste, puis employé (matricule et
  code personnel). Un seul employé à la fois par poste.
- Contrôle de chaque demande par le serveur : les 21 permissions du cahier des charges,
  retrait d'un droit effectif immédiatement.
- Verrouillage après 5 échecs pendant 5 minutes ; même réponse pour un matricule inconnu
  et un code faux ; secrets hachés (PBKDF2-SHA256), jetons stockés par empreinte.
- Administration côté serveur : créer un employé, fixer ses droits, l'activer ou le
  désactiver, réinitialiser son code. Nul ne modifie ses propres droits.
- Journal d'audit : connexions, refus, verrouillages, changements de droits avec auteur
  et détail (ajoutés, retirés). Ni modifiable, ni supprimable, ni vidable.
- Poste : écrans de connexion du poste et d'identification, verrouillage par F12, nom de
  l'employé affiché, connexion du poste mémorisée et chiffrée par Windows (DPAPI).
- Mise en place d'un site par ligne de commande (site, compte de caisse, administrateur).

Tests exécutés (Linux) : **67 réussis, 0 échec**, dont 21 tests d'accès sur une vraie base
et un parcours complet au clavier dans la fenêtre. Parcours aussi rejoué sur le vrai
programme, écran virtuel, serveur réel.

Défaut d'ergonomie trouvé et corrigé : après un code refusé, le matricule restait dans
le champ et une nouvelle saisie s'y ajoutait.

Choix proposés, à confirmer par la direction :
- identification par matricule court + code personnel (un badge à scanner pourra
  s'ajouter sans changer le serveur) ;
- verrouillage : 5 échecs, 5 minutes ; durée d'une session employé : 12 heures.

Reste à faire dans le lot 1 :
- écran d'administration des employés et des droits (cases à cocher) dans le poste ;
- chiffrement des échanges poste-serveur ;
- modèles de rôles (dépend de O10) ; verrouillage automatique après inactivité.

## 16/11/2026 — Nuit de développement autonome

Instruction de la direction : tout enchaîner sans attendre de réponse, avec des agents en
parallèle. Les règles encore ouvertes sont appliquées comme choix par défaut (D1 à D18
dans `DECISIONS.md`), à valider. Le mode secours hors ligne (O4) n'est pas développé.

- Lot 1, première partie : vérifiée sur Windows (exécution n° 5 réussie).
- Sauvegarde vérifiée et restauration dans une base neuve (`--sauvegarder`, `--restaurer`), 3 tests.
- **Lot 2 serveur fusionné** : migration 0003, moteur de stock (journal en ajout seul,
  solde jamais négatif, verrouillage ordonné), idempotence (`OperationRunner`), catalogue
  (création, modification, prix de vente historisé, fiche produit), ajustement d'inventaire,
  transfert simple. Tests après fusion : **106 réussis, 0 échec**.

- **Interruption** : vers 2 h, la limite d'usage de l'abonnement a coupé les quatre agents
  en cours. Reprise le 16/11 au matin, à trois agents au maximum.
- Fusionnés ensuite, tests après chaque fusion :
  - navigation du poste, administration des droits, verrouillage par inactivité (169) ;
  - bons fournisseurs, serveur (201) ;
  - chiffrement des échanges et épinglage du certificat (247) ;
  - ventes, caisse et impayés, serveur : migration 0005, 83 tests dont les ventes
    concurrentes, le double clic, le règlement groupé tout ou rien, le double règlement
    simultané et un contrôle d'intégrité financière en 19 règles (**330 réussis, 0 échec**).
- Choix faits par les agents en l'absence de règle : D19 à D31 dans `DECISIONS.md`.
- Intégration continue : échec sur Linux après la fusion du chiffrement. Cause : sur la
  machine GitHub, `pg_dump` était plus récent que le serveur PostgreSQL de test, et la
  sauvegarde n'était pas restaurable. Corrigé : la sauvegarde vérifie désormais que ses
  outils sont de la même version que le serveur, et le test Linux utilise le PostgreSQL de
  la machine. **Exécution n° 15 réussie sous Linux et sous Windows** (chiffrement, refus
  d'une empreinte inconnue, essai de rapidité en HTTPS compris).
- Fusionné : écran Produits (416 tests réussis).
- **Deuxième interruption** par la limite d'usage vers 12 h UTC, pendant l'écran de vente,
  l'écran des bons et les retours/clôture côté serveur. Reprise à 14 h 40 UTC.

## 13/11/2026 — Prototype

- Prototype Next.js + PostgreSQL (caisse, ventes, stock simple), testé dans un
  navigateur. Conservé comme maquette ; non conforme au cahier des charges du 15/11.
