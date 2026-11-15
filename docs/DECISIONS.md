# Décisions — Pharmaly Gestion V1

Trois catégories : **validé** (règle donnée par la direction), **proposé** (choix
technique recommandé, en attente d'accord), **ouvert** (à arbitrer par la direction).
Aucune règle métier ne passe en « validé » sans accord explicite.

## Validé (cahier des charges du 15/11/2026)

- V1 = bons fournisseurs, ventes, caisse, plus les fondations (authentification,
  permissions, catalogue, stock, clients et créances, audit, sauvegardes, secours).
- BML : aucune lecture ni écriture directe, aucun balayage réseau ; uniquement des
  exports autorisés et des données de test.
- Emplacements : Pharmaly 1 — Didouche, Pharmaly 2 — Audin, dépôt central.
- Mêmes fonctionnalités sur tous les postes, y compris 4 Go / Celeron J4025.
- Bon fournisseur : une ligne enregistrée entre immédiatement en stock ; la validation
  finale n'ajoute rien ; une modification applique la différence ; une annulation crée
  un mouvement compensatoire audité ; un seul employé par bon.
- Prix d'achat : dernier prix proposé, modifiable, historisé, jamais rétroactif. Le prix
  de vente ne change jamais automatiquement.
- Vente interdite si le stock est insuffisant, contrôle fait par le serveur dans la
  transaction.
- Vente impayée : stock déduit, créance créée, aucun encaissement fictif.
- Règlement partiel d'un ticket interdit. Règlement groupé de plusieurs tickets du même
  client autorisé, atomique.
- Caisse : identification à deux niveaux (compte de caisse, puis employé).
- Clôture globale quotidienne ; chiffre d'affaires et trésorerie distingués ; pas de
  journée consolidée tant que des opérations hors ligne restent inconnues.
- Impayés sous Caisse > Impayés ; boutons Espèces et TPE ; raccourci `*` = Espèces.
- Permissions individuelles par case à cocher, contrôlées par le serveur, modifications
  tracées ; nul ne peut s'accorder des droits.
- **Logiciel natif** : aucune interface web, aucun navigateur (décision du 15/11).
- **Communication sans Internet** : postes et serveur dialoguent par le réseau interne
  uniquement (décision du 15/11).
- **Un serveur par site**, sites séparés. Les échanges entre sites se font par fichier,
  sur clé USB ou par e-mail (décision du 15/11).
- **Un seul site pilote d'abord**, pour juger la rapidité, la lourdeur et la sécurité
  (décision du 15/11).
- `pharmaly-app` est un projet distinct : ne pas y toucher, ne rien en reprendre.
- Charte : turquoise #2ECAC8, orange pêche #F5A97A, beige clair, noir, Poppins.

## Proposé (technique, en attente d'accord)

| # | Proposition | Raison |
|---|---|---|
| P1 | Serveur ASP.NET Core (.NET 10 LTS) + PostgreSQL | Préférence exprimée, service Windows, mémoire faible |
| P2 | Logiciel natif Avalonia (.NET), avec repli WPF si le budget mémoire n'est pas tenu. **Utilisé pour le lot 0 ; à confirmer par la mesure sur un Celeron J4025** | Natif, lançable sur le Mac mini, testable automatiquement |
| P3 | Impression, tiroir et journal de secours intégrés au logiciel du poste | Un seul programme par poste |
| P4 | Stock = journal en ajout seul + solde avec `CHECK >= 0` | Rend la survente impossible côté base |
| P5 | Identifiant d'opération sur toute écriture | Double clic, relance réseau, resynchronisation |
| P6 | Montants `numeric(12,2)` / `decimal`, quantités `numeric(12,3)` | Aucune erreur d'arrondi |
| P7 | Poursuivre dans le dépôt `Logiciel-Pharmaly`. **Fait le 15/11** : prototype déplacé dans `prototype/`, sans rien supprimer. Reste recommandé : passer le dépôt en privé | Un seul dépôt, historique conservé |
| P8 | Compilation et tests sur Windows par GitHub Actions | Aucun Windows disponible en développement |
| P9 | Fichiers d'échange entre sites signés et chiffrés | Un e-mail traverse Internet : le fichier doit être illisible et infalsifiable hors des sites |
| P10 | Accès à la base par SQL explicite (Npgsql, Dapper) et migrations SQL versionnées | Transactions et verrous maîtrisés, démarrage rapide sur petit poste |

## Choix par défaut pris le 16/11/2026 — À VALIDER PAR LA DIRECTION

La direction a demandé de poursuivre toute la nuit sans attendre de réponse. Pour ne pas
bloquer, les recommandations ci-dessous sont appliquées **comme valeurs par défaut**.
Aucune n'est une règle validée : chacune peut être changée, et le code les isole (réglage
ou constante nommée) pour que le changement soit simple.

| # | Sujet | Choix appliqué par défaut |
|---|---|---|
| D1 | Identification de l'employé | Matricule court + code personnel |
| D2 | Verrouillage | 5 échecs → 5 minutes ; session employé de 12 h ; verrouillage du poste après 5 minutes d'inactivité |
| D3 | Transfert dépôt → magasin (O2) | Transfert simple en une étape : sortie et entrée dans la même transaction, sans état « en transit ». Sur un site pilote unique, il sert entre emplacements du même serveur |
| D4 | Ligne de bon dont le stock est déjà parti (O5) | Réduction ou annulation refusée au-delà de la quantité encore présente à l'emplacement ; message clair ; correction par ajustement d'inventaire audité |
| D5 | Vente impayée (O6) | Client obligatoire ; pas de plafond de crédit |
| D6 | TPE (O7) | Terminal autonome : le caissier confirme à l'écran « paiement accepté » ou « refusé ». Rien n'est soldé avant « accepté » |
| D7 | Raccourci TPE | Réglable. Valeur **provisoire** : F8. `*` = Espèces est confirmé |
| D8 | Motifs de retour (O8) | Les quatre exemples du cahier des charges, modifiables : revendable → stock vendable ; endommagé → pas de réintégration ; défectueux → emplacement « mis à l'écart » ; erreur de vente → stock vendable |
| D9 | Changement de prix de vente (O9) | Soumis à la permission dédiée ; approbation d'un responsable prévue comme réglage, désactivée par défaut |
| D10 | Rôles (O10) | Un seul modèle fourni : « Administrateur » (tous les droits). Les autres profils attendent la liste de la direction |
| D11 | Clôture quotidienne (O3) | Une clôture par magasin et par jour, regroupant toutes ses caisses, avec comptage des espèces par caisse. Une vente saisie après la clôture appartient à la journée suivante |
| D12 | Remises | En montant ou en pourcentage, sur la vente entière ; jamais supérieures au total |
| D13 | Annulation d'une vente | Possible tant que la journée n'est pas clôturée ; au-delà, passer par un retour |

**Non décidé et donc NON développé : le mode secours hors ligne (O4).** La direction a
demandé explicitement de ne pas choisir sans son accord. Le code le prépare (identifiants
générés par le poste, idempotence) mais aucune vente hors ligne n'est possible.

## Ouvert

**O4. Stock en mode secours.**

| Stratégie | Garantit l'absence de survente | Continuité de vente | Complexité |
|---|---|---|---|
| Stock réservé par caisse | Oui, c'est la seule | Faible : un article en un seul exemplaire n'est vendable que sur une caisse | Élevée |
| Dernier stock connu | Non, si deux caisses isolées vendent le même article | Bonne | Faible |
| Ventes provisoires avec régularisation | Non | Totale | Moyenne |

Recommandation : dernier stock connu comme limite locale, ventes de secours provisoires
rapprochées à la reconnexion, tout conflit enregistré comme écart à régulariser. Opérations
de secours proposées : ventes en espèces et TPE uniquement.

## Écarts relevés

- Le prototype Next.js du 13/11 tolère le stock négatif et clôture par caissier : il
  n'est pas conforme et ne sera pas réutilisé tel quel.
- `pharmaly-app` nomme les boutiques « Meissonier » et « Audin » ; le cahier des charges
  dit « Didouche » et « Audin ». Les noms seront paramétrables.
