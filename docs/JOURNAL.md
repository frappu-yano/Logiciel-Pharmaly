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

**À vérifier** : résultat de l'intégration continue sous Windows ; mesure sur un poste
Celeron J4025 / 4 Go avec BML ouvert.

**En attente de la direction** : choix du site pilote ; O2 (transfert simple) avant le lot 3.

## 13/11/2026 — Prototype

- Prototype Next.js + PostgreSQL (caisse, ventes, stock simple), testé dans un
  navigateur. Conservé comme maquette ; non conforme au cahier des charges du 15/11.
