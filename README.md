# Pharmaly — logiciel de gestion

Application web de gestion pour les boutiques Pharmaly : vente au comptoir, caisse,
stock, utilisateurs et sites. Elle s'installe sur un serveur et s'utilise depuis un
navigateur, sur ordinateur (postes de caisse) comme sur téléphone.

## Fonctionnalités de cette première version

- **Caisse** : lecture de codes-barres, recherche de produits, panier, remise, paiement
  (espèces, carte, chèque, virement), calcul de la monnaie à rendre, ticket imprimable.
- **Sessions de caisse** : ouverture avec fond de caisse, entrées et sorties d'espèces,
  clôture avec comptage et calcul de l'écart, rapport de caisse imprimable.
- **Ventes** : historique par jour et par site, détail, réimpression du ticket,
  annulation par un gérant (remise en stock automatique).
- **Produits et stock** : fiche produit, prix, catégories, stock séparé par site,
  réceptions et corrections d'inventaire, historique des mouvements en base.
- **Utilisateurs et sites** : trois rôles (administrateur, gérant, caissier), accès
  limité aux sites affectés, plusieurs boutiques et une boutique en ligne.

## Matériel

- **Lecteur de codes-barres** : tout lecteur USB ou Bluetooth en mode clavier. Sur
  l'écran de caisse, scannez directement : le produit est ajouté au panier.
- **Imprimante de tickets** : toute imprimante thermique 80 mm installée sur le poste.
  Le ticket s'imprime par la fenêtre d'impression du navigateur. Pour imprimer sans
  fenêtre de confirmation, lancez Chrome avec l'option `--kiosk-printing` et
  choisissez l'imprimante de tickets par défaut.
- **Tiroir-caisse** : branché sur l'imprimante, il s'ouvre à l'impression si l'option
  est activée dans le pilote de l'imprimante.

## Installation sur un serveur

Prérequis : Node.js 22 et PostgreSQL 16.

```bash
# 1. Base de données
sudo -u postgres psql -c "CREATE USER pharmaly WITH PASSWORD 'MOT_DE_PASSE'"
sudo -u postgres psql -c "CREATE DATABASE pharmaly OWNER pharmaly"

# 2. Configuration
cp .env.example .env      # puis renseigner DATABASE_URL et SESSION_SECRET

# 3. Installation
npm ci
npm run db:migrate        # crée les tables
npm run db:seed           # crée les sites, les caisses et le compte « admin »
npm run build
npm run start             # http://ADRESSE_DU_SERVEUR:3000
```

`npm run db:seed` affiche une seule fois le mot de passe du compte `admin`. Changez-le
ensuite dans **Paramètres → Utilisateurs**. Pour essayer le logiciel avec quelques
produits fictifs : `SEED_DEMO=1 npm run db:seed`.

Si le logiciel est accessible depuis Internet, placez-le derrière HTTPS (nginx ou
Caddy) et mettez `COOKIE_SECURE="true"` dans `.env`.

## Développement

```bash
npm run dev         # serveur de développement
npm run typecheck   # vérification TypeScript
npm run lint        # contrôle du code
```

Les montants sont stockés en centimes (entiers) et affichés en dinars (DA).
Le schéma de la base est dans `prisma/schema.prisma`.
