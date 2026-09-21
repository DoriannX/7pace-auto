# Collecter aujourd’hui, ajuster et envoyer demain

## Promesse produit

L’application collecte automatiquement le travail Git de la journée en cours. Le lendemain
matin, elle présente la plus ancienne journée terminée encore en attente afin que
l’utilisateur corrige ses créneaux et les envoie dans 7pace en moins de deux minutes.

L’application n’est pas une seconde interface 7pace : elle n’en lit aucune donnée, n’expose
aucun historique et ne permet aucune correction après envoi.

## Frontière des responsabilités

- Le dépôt Git configuré indique le travail courant.
- Azure DevOps résout automatiquement le Bug ou PBI extrait de la branche vers son Fix ou sa
  Task. Cette lecture reste nécessaire pour éviter la ressaisie des tickets.
- L’utilisateur peut démarrer et arrêter un créneau rapide « À attribuer ». Ce créneau
  facultatif coexiste avec le ticket Git suivi ; il ne l’interrompt pas.
- L’utilisateur corrige la journée terminée et attribue lui-même les numéros qui manquent.
- 7pace reçoit les créneaux validés et devient alors l’unique source de vérité.

Une réunion ou une autre activité non observable reste attribuée au ticket Git si
l’utilisateur n’utilise pas le créneau rapide et ne la corrige pas le lendemain. Cette limite
est assumée : l’application ne prétend pas deviner une activité sans source fiable.

## Modèle minimal

Un créneau utile à l’envoi porte :

- une heure de début ;
- une heure de fin ;
- un numéro de work item nullable ;
- une origine informative permettant au suivi Git de prolonger son propre créneau ;
- un état d’envoi local temporaire pour éviter les doublons lors d’un échec partiel.

Les catégories d’activité et les titres saisis disparaissent. Un libellé résolu depuis Azure
peut être affiché pour aider la relecture, mais il n’est ni demandé à l’utilisateur ni envoyé
à 7pace.

Deux créneaux peuvent se chevaucher. Chaque créneau produit son propre worklog : deux périodes
portant le même numéro restent deux worklogs distincts.

## Cycle quotidien

### Collecte

- L’application démarre automatiquement avec Windows, minimisée.
- Elle surveille un seul dépôt Git et uniquement les horaires de travail configurés.
- La branche active continue d’être comptée lorsque le poste reste allumé mais inactif.
- Une interruption de collecte devient un créneau « À attribuer » plutôt qu’une supposition.
- Un relevé Git raté n’est pas une interruption : la branche connue est conservée le temps de
  quelques relevés, puis la collecte s’arrête sur la dernière minute réellement vérifiée.
- Le créneau rapide « À attribuer » se démarre et s’arrête sans demander de numéro.
- Un créneau rapide actif bloque l’envoi jusqu’à son arrêt et son attribution.

### File du matin

- La journée calendaire en cours n’est jamais envoyable.
- À la première activité du matin, une notification Windows unique signale les journées
  terminées en attente.
- Le terminal présente la plus ancienne journée en attente.
- Après traitement, il avance vers la suivante ; il ne fournit aucun sélecteur de date ni
  accès aux journées closes.
- Une journée peut être explicitement ignorée après confirmation forte. Aucun appel 7pace
  n’est alors effectué.

### Ajustement

Le terminal affiche la liste chronologique des créneaux. L’utilisateur peut :

- ajouter un créneau ;
- modifier son début, sa fin ou son numéro de work item ;
- supprimer un créneau ;
- conserver volontairement des créneaux qui se chevauchent.

Les chevauchements et les trous dans les horaires configurés sont signalés sans bloquer
l’envoi. Tout créneau « À attribuer » bloque l’envoi complet.

### Confirmation et envoi

La confirmation affiche chaque créneau, son numéro, les chevauchements, les trous et le total
imputable, puis exige la saisie explicite de `ENVOYER`.

Les worklogs sont envoyés séquentiellement. En cas d’échec partiel :

- chaque créneau accepté est immédiatement verrouillé et ne sera jamais renvoyé ;
- chaque créneau refusé reste modifiable et renvoyable ;
- la journée reste en tête de file jusqu’à son envoi complet ou son abandon explicite.

Après un envoi complet :

- le terminal affiche un reçu jusqu’à sa fermeture ;
- le détail local de la journée est supprimé ;
- seul un marqueur de date close est conservé pour empêcher sa réapparition ;
- toute correction ultérieure se fait directement dans 7pace.

## Interface terminal

Le terminal est l’unique interface prise en charge. Son écran métier contient uniquement :

- la journée en attente et ses créneaux ;
- les avertissements de trous et de chevauchements ;
- les actions Ajouter/corriger, Supprimer, Envoyer et Ignorer la journée ;
- l’action rapide Démarrer/arrêter « À attribuer » pour la journée en cours ;
- un état compact du suivi Git.

Les seuls écrans secondaires sont Réglages, Jeton 7pace et Mise à jour. Les réglages
conservent le dépôt, la fréquence de relevé, l’organisation Azure DevOps, le compte 7pace, les
horaires de travail et les options de mise à jour. Ils ne contiennent aucun catalogue
d’activités : l’utilisateur saisit les numéros requis.

L’ancienne interface graphique affiche un bandeau visible « Version dépréciée » et oriente
vers la version terminal. Elle ne reçoit plus de nouvelle fonctionnalité.

## Non-objectifs

- Lire, synchroniser ou importer les worklogs 7pace.
- Afficher une vue jour choisie, semaine, mois ou historique.
- Afficher des rapports, statistiques ou tendances.
- Corriger ou supprimer depuis l’application une journée déjà envoyée.
- Maintenir un catalogue local des tâches génériques du wiki.
- Détecter automatiquement les réunions ou l’inactivité du poste.
- Surveiller plusieurs dépôts Git.

## Critères d’acceptation

1. La journée en cours est collectée mais ne peut jamais être envoyée.
2. La plus ancienne journée terminée non close est présentée sans sélection de date.
3. Une journée close n’est plus consultable et ne réapparaît après aucun redémarrage.
4. Aucun appel HTTP `GET` n’est adressé à 7pace.
5. La résolution Azure continue d’attribuer automatiquement les créneaux Git.
6. Le chrono rapide crée un créneau « À attribuer » concurrent du suivi Git.
7. Les chevauchements sont autorisés et signalés.
8. Les trous sont signalés mais n’interdisent pas l’envoi.
9. Un numéro manquant ou un chrono rapide encore actif interdit l’envoi.
10. La confirmation montre la liste exacte et le total avant tout appel 7pace.
11. Chaque créneau produit un worklog distinct, même si plusieurs portent le même numéro.
12. Après un échec partiel, seuls les créneaux acceptés sont verrouillés et exclus de la
    tentative suivante.
13. Après succès complet, le détail local est supprimé et seul le marqueur de date close
    subsiste.
14. Ignorer une journée la clôt localement sans contacter 7pace.
15. Le terminal conserve uniquement les accès Réglages, Jeton 7pace et Mise à jour.
16. L’ancienne interface graphique affiche clairement qu’elle est dépréciée.
17. Un relevé Git raté ne ferme pas le créneau en cours, n’ouvre pas de doublon à la reprise
    et ne produit jamais d’intervalle « poste en veille ou arrêté ».
