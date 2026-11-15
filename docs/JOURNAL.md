# Journal d'avancement — Pharmaly Gestion

## 15/11/2026 — Audit et proposition d'architecture

- Reçu le cahier des charges « Pharmaly Gestion V1 ».
- Audit en lecture seule de l'environnement (conteneur Linux cloud, .NET absent,
  PostgreSQL 16 présent) et des dépôts `Logiciel-Pharmaly` et `pharmaly-app`.
- `pharmaly-app` cloné en lecture seule pour inspection ; aucun fichier modifié.
- BML : non consulté, non approché.
- Rédigé `ARCHITECTURE.md`, `DECISIONS.md`, `PLAN.md`.
- Aucun code Pharmaly Gestion écrit. Aucun test exécuté pour cette version.

**En attente** : accord sur l'architecture (P1 à P8) et réponses à O1 et O2.

**Prochaine étape après accord** : lot 0 (installation du SDK .NET dans l'environnement,
création de la solution, intégration continue Windows, archivage du prototype).

## 13/11/2026 — Prototype

- Prototype Next.js + PostgreSQL (caisse, ventes, stock simple), testé dans un
  navigateur. Conservé comme maquette ; non conforme au cahier des charges du 15/11.
