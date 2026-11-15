# Cahier des charges — Pharmaly Gestion V1

Transcription des règles données par la direction le 15/11/2026. **Ce document fait foi.**
Ne jamais inventer une règle métier absente d'ici ou de `DECISIONS.md`.

## Contexte

- Remplace progressivement BML PHARM dans les points de vente Pharmaly (Algérie).
- Emplacements : Pharmaly 1 — Didouche, Pharmaly 2 — Audin, stock intermédiaire / dépôt central.
- BML : ne jamais modifier, supprimer, lire directement sa base, ni explorer son serveur.
  Uniquement des exports autorisés et des données de test.
- Postes Windows, certains à 4 Go de RAM et Celeron J4025. Mêmes fonctionnalités partout,
  pas de version dégradée. Le logiciel ne doit ralentir ni Windows ni BML.
- Priorité absolue : intégrité des stocks, des ventes et des encaissements. Chaque
  mouvement critique est atomique, traçable, durable, protégé contre les doublons et
  correct en cas de concurrence (idempotence, verrouillage, reprise après panne).

## Périmètre V1

Trois modules : saisie des bons fournisseurs, ventes, caisse. Fondations : authentification,
permissions individuelles, catalogue, stocks par emplacement, mouvements de stock, clients
et créances, journal d'audit, base, sauvegardes et restauration, gestion des erreurs, mode
de secours local. Hors V1 : réapprovisionnement avancé, prévisions, merchandising,
transferts complexes (préparer leur intégration sans surdimensionner).

## Produits

- Identifiant interne unique et stable. Désignation, un ou plusieurs codes-barres, marque,
  catégorie, prix d'achat, prix de vente, stock par emplacement, historique des prix
  d'achat, historique des mouvements, état actif/inactif.
- Ne pas supposer que les anciens codes BML sont uniques.
- Produit inconnu scanné pendant la saisie d'un bon : proposer sa création, ouvrir une
  création rapide, revenir automatiquement au bon, uniquement si l'employé en a le droit.

## Bons fournisseurs

- Saisie surtout au dépôt. Un seul employé par bon à la fois : réservation fiable, reprise
  après déconnexion, libération contrôlée du verrou. Saisie très rapide clavier/scanner.
- **Dès qu'une ligne est enregistrée, sa quantité entre immédiatement en stock.**
  Exemple : stock 10, ligne de 15 validée par Entrée → stock 25, sans attendre la
  validation finale.
- Une quantité en cours de frappe ne modifie jamais le stock.
- Une ligne enregistrée produit un mouvement de stock atomique.
- La validation finale n'ajoute jamais une deuxième fois les quantités.
- Modifier une ligne applique uniquement la différence.
- Annuler une ligne nécessite un mouvement compensatoire audité.
- Empêcher les doubles enregistrements lors de tentatives répétées.
- Gérer correctement les quantités déjà transférées ou vendues.
- Prix d'achat : afficher le dernier prix connu, modifiable à la saisie ; conserver
  l'historique ; associer le prix au fournisseur, au bon et à la date ; ne jamais modifier
  rétroactivement les anciens achats.
- Si le prix d'achat change : conserver le prix de vente, afficher la variation de marge,
  ne pas modifier automatiquement le prix de vente ; modification explicite soumise aux
  permissions et, selon configuration, à l'approbation d'un responsable.
- États des bons et corrections avec historique complet.

## Ventes

- Interface très rapide : recherche, code-barres, lignes, quantités, total, remises en
  montant et en pourcentage, espèces, TPE, vente impayée, retours, annulations, historiques.
- Les permissions déterminent les actions disponibles.
- **Vente interdite si le stock disponible est insuffisant**, contrôle côté serveur dans la
  transaction. Deux caissiers ne peuvent pas vendre simultanément le dernier exemplaire.
- Une vente produit exactement les mouvements attendus, sans double déduction.
- Annulations et retours : opérations compensatoires traçables.

## Ventes impayées

1. La vente est enregistrée. 2. Le stock est déduit immédiatement. 3. Une créance est créée.
4. Aucun encaissement fictif. 5. Le ticket est identifié comme impayé.
- Le règlement ultérieur ne provoque jamais une deuxième sortie de stock.
- **Règlement partiel d'un ticket interdit** : un ticket impayé se règle intégralement.
- **Règlement groupé** : plusieurs tickets impayés du même client soldés par un seul
  encaissement (ex. 3 500 + 6 200 + 1 800 = 11 500 DA). Chaque ticket est intégralement
  soldé ; opération atomique (tout ou rien) ; double règlement impossible.
- Recherche : scan du code-barres du ticket, numéro de ticket, tableau, client.
- Tickets : identifiant stable et code-barres imprimable.

## Caisse

- Identification à deux niveaux : compte de caisse, puis employé. Chaque opération
  conserve l'identité réelle de l'employé.
- Ouverture, encaissements, espèces, TPE, règlements d'impayés, retours et remboursements,
  annulations, historique, rapports, clôture.
- **Clôture globale quotidienne**, consolidant toutes les opérations des caisses du
  périmètre. Distinguer : ventes encaissées du jour, règlements d'anciennes créances,
  espèces, TPE, retours, remboursements, écarts. Ne pas confondre chiffre d'affaires et
  flux de trésorerie.
- Une opération hors ligne non synchronisée compte dans les règles de clôture. Ne jamais
  présenter une journée comme consolidée si des transactions restent inconnues, sauf
  procédure exceptionnelle explicitement autorisée et auditée.

## Caisse > Impayés

- Tableau : numéro de ticket, date, client ou compte débiteur, montant dû, statut, employé,
  date du règlement si soldé, moyen de règlement.
- Indicateurs : total des impayés ouverts, nombre de tickets impayés, total des créances,
  historique des créances soldées.
- Sélection d'un ou plusieurs impayés du même client.
- Deux boutons visibles et cliquables : **ESPÈCES** et **TPE**, avec raccourcis clavier.
  Raccourci confirmé : `*` = Espèces. Raccourci TPE : à définir avec la direction, ne pas
  attribuer arbitrairement une touche définitive.
- Choisir le moyen de paiement n'est pas confirmer le règlement.
- TPE : ne pas marquer la dette soldée avant confirmation du paiement réussi.
- Protection contre les encaissements simultanés.

## Retours clients

- La réintégration au stock dépend du motif, configurable. Exemples proposés (à valider) :
  retour revendable → stock vendable après contrôle ; endommagé → pas de réintégration ;
  défectueux → mise à l'écart ; erreur de vente → selon le contexte.
- Mouvements distincts et historique complet.

## Permissions

- Attribuables individuellement, par case à cocher ; les rôles sont des modèles de départ.
- Liste : consulter les produits ; créer un produit ; modifier un produit ; modifier un
  prix de vente ; consulter les bons ; créer et saisir un bon ; modifier un bon ; valider
  un bon ; annuler un bon ; effectuer une vente ; encaisser les paiements ; appliquer une
  remise ; effectuer un retour ; annuler une vente ; consulter l'historique des ventes ;
  ouvrir une caisse ; clôturer une caisse ; consulter les rapports de caisse ; consulter
  les impayés ; régler les impayés ; administrer les utilisateurs et permissions.
- Contrôles côté serveur. Nul ne s'attribue des droits. Modifications tracées. Droits
  éventuellement limités à un magasin.

## Mode de secours local

- Objectifs : continuer les opérations autorisées, ne perdre aucune vente, conserver
  durablement, synchroniser à la reconnexion, empêcher les doublons, conserver l'identité
  des employés, gérer les conflits explicitement.
- **La stratégie de contrôle du stock hors ligne n'est pas validée.** Ne pas choisir
  silencieusement entre stock réservé par caisse, dernier stock connu, ventes provisoires.
  Demander l'accord de la direction avant d'implémenter.

## Interface

- Professionnelle, moderne, claire, extrêmement rapide. Turquoise #2ECAC8, orange pêche
  #F5A97A, beige clair, noir, Poppins. Performance et lisibilité avant les animations.
- Navigation au clavier, scanners, focus automatique, validation par Entrée, grands
  boutons, usage intensif, messages d'erreur compréhensibles, confirmation des opérations
  critiques, raccourcis configurables. Réactif sur 4 Go de RAM.

## Base de données

- Jamais un stock modifiable sans historique des mouvements.
- Quantités décimales quand l'unité le nécessite, jamais de fraction sur un produit vendu
  à l'unité. Montants en dinars sans erreur d'arrondi.

## Tests obligatoires

Deux ventes concurrentes sur le dernier produit ; double clic sur Enregistrer ; répétition
d'une requête après délai réseau ; coupure pendant l'enregistrement d'une ligne de bon ;
modification d'une ligne déjà comptabilisée ; annulation d'un bon après transfert de
stock ; vente impayée ; règlement intégral d'un impayé ; règlement groupé ; tentative de
double règlement ; échec de paiement TPE ; retour sans remise en stock ; employé sans
permission ; déconnexion du serveur ; redémarrage brutal d'un poste ; sauvegarde et
restauration ; clôture avec opérations non synchronisées. Vérifier les invariants métier
après chaque test.

## Collaboration

Ne jamais inventer une règle non validée ; ne pas affirmer qu'un test réussit sans l'avoir
exécuté ; code maintenable, documenté, testable ; pas de complexité inutile.
