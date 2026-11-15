# Plan d'implémentation — Pharmaly Gestion V1

Chaque lot est livré sur une branche, avec ses tests automatiques. Retour arrière commun
à tous les lots : annulation du commit de fusion ; chaque migration de base a son script
inverse et est précédée d'une sauvegarde. Un lot n'est « terminé » que si ses tests ont
été exécutés et sont verts, sur Linux et sur Windows (GitHub Actions).

| Lot | Objectif | Contenu | Tests et critères d'acceptation | Risque principal |
|---|---|---|---|---|
| **0. Socle** | Projet exécutable sur Windows | Solution .NET (`src/Server`, `src/Domain`, `tests`), interface minimale, migrations, intégration continue Linux + Windows, exécutable Windows autonome | L'exécutable démarre sur Windows et répond ; mesure mémoire et réactivité sur un Celeron J4025 avec BML ouvert | Budget mémoire non tenu → repli Avalonia |
| **1. Accès** | Qui fait quoi | Comptes de caisse, employés (PIN), permissions individuelles, modèles de rôles, journal d'audit | Employé sans droit refusé par le serveur ; impossible de s'accorder un droit ; chaque changement de droit tracé | Ergonomie de la double identification |
| **2. Catalogue et stock** | Moteur de stock fiable | Produits, codes-barres multiples, marques, catégories, emplacements, mouvements atomiques, idempotence, contrôle journal = solde | 50 écritures concurrentes sans écart ; requête rejouée sans double effet ; stock négatif impossible | Interblocages sous charge |
| **3. Bons fournisseurs** | Saisie rapide au dépôt | Bons, lignes, verrou à bail, entrée en stock immédiate, création rapide de produit, historique des prix d'achat, variation de marge, transfert simple (si O2 accepté) | Ligne de 15 sur stock 10 → 25 ; double Entrée → une seule entrée ; modification = différence ; validation finale sans mouvement ; coupure en cours d'enregistrement sans demi-écriture ; second employé bloqué | Règle O5 non tranchée |
| **4. Ventes** | Vente au comptoir | Recherche, scan, lignes, remises en montant et en pourcentage, espèces, TPE, ticket numéroté avec code-barres | Deux ventes simultanées du dernier exemplaire → une seule réussit ; double clic → une vente ; totaux exacts | Vitesse de saisie sur petit poste |
| **5. Caisse et impayés** | Encaissements et créances | Ouverture de caisse, vente impayée, tableau Caisse > Impayés, règlement unitaire et groupé, Espèces (`*`) et TPE | Impayé : stock déduit, aucun paiement ; règlement sans seconde sortie de stock ; groupé tout ou rien ; double règlement refusé ; échec TPE → créance toujours ouverte | O6 et O7 non tranchés |
| **6. Retours et annulations** | Corrections traçables | Motifs configurables, destination du stock selon le motif, remboursements, annulations | Retour « endommagé » sans remise en stock vendable ; mouvements compensatoires reliés à l'origine | O8 non tranché |
| **7. Clôture** | Journée fiable | Clôture quotidienne, ventes du jour distinguées des règlements d'anciennes créances, espèces, TPE, retours, écarts, rapports | Chiffre d'affaires ≠ trésorerie vérifié sur un jeu d'essai ; clôture refusée s'il reste des opérations de secours inconnues | O3 non tranché |
| **8. Poste** | Matériel | Service `Pharmaly.Poste`, ticket ESC/POS, code-barres du ticket, tiroir-caisse, raccourcis configurables | Impression sur l'imprimante réelle d'un magasin | Diversité des imprimantes |
| **9. Secours** | Continuité | Journal local, synchronisation, rapprochement, écarts | Serveur coupé : vente conservée après redémarrage brutal du poste ; resynchronisation sans doublon | O4 non tranché |
| **10. Exploitation** | Mise en service | Sauvegarde planifiée, restauration testée, installateur Windows, import des exports BML autorisés, guide | Restauration sur base vierge identique à l'origine ; import rejouable sans doublon | Qualité des exports BML |

Ordre : 0 → 7 forment la première version exécutable en magasin pilote, sans secours.
Les lots 8 à 10 la rendent exploitable au quotidien.
