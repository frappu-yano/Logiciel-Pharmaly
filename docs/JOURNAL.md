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

**En attente** : accord sur le client natif Avalonia (P2), réponses à O1 et O2.

**Prochaine étape après accord** : lot 0 (installation du SDK .NET dans l'environnement,
création de la solution, intégration continue Windows, archivage du prototype).

## 13/11/2026 — Prototype

- Prototype Next.js + PostgreSQL (caisse, ventes, stock simple), testé dans un
  navigateur. Conservé comme maquette ; non conforme au cahier des charges du 15/11.
