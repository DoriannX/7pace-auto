# Collecter aujourd’hui, ajuster et envoyer demain

## Promesse produit

L’application collecte automatiquement le travail Git de la journée en cours. Le lendemain
matin, elle présente la plus ancienne journée terminée encore en attente afin que
l’utilisateur corrige ses créneaux et les envoie dans 7pace en moins de deux minutes.

L’application n’est pas une seconde interface 7pace : elle n’en lit aucune donnée, n’expose
aucun historique et ne permet aucune correction après envoi.

La collecte ne dépend d’aucune fenêtre ouverte. Un collecteur sans interface la porte du
démarrage de la session jusqu’à son arrêt explicite ; l’app n’est qu’une vue que l’on
ouvre et quitte sans conséquence.

## Frontière des responsabilités

- Le collecteur est seul à relever et à écrire. L’app ne fait qu’afficher
  et demander.
- Le dépôt Git configuré indique le travail courant hors des créneaux occupés du calendrier Outlook publié.
- Azure DevOps résout automatiquement le Bug ou PBI extrait de la branche vers son Fix ou sa
  Task. Cette lecture reste nécessaire pour éviter la ressaisie des tickets.
- L’utilisateur peut démarrer et arrêter un créneau rapide « À attribuer ». Ce créneau
  facultatif coexiste avec le ticket Git suivi ; il ne l’interrompt pas.
- L’utilisateur corrige la journée terminée et attribue lui-même les numéros qui manquent.
- 7pace reçoit les créneaux validés et devient alors l’unique source de vérité.

Les créneaux occupés du calendrier publié interrompent le suivi Git : le standup de 09:15 à
09:30 reçoit #175, les autres #83. Le flux ICS ne permet pas de prouver que l’invitation a été
acceptée ; les rendez-vous personnels marqués « occupé » sont inclus. Hors de cette source,
une réunion reste attribuée au ticket Git si l’utilisateur ne la corrige pas le lendemain.

## Modèle minimal

Un créneau utile à l’envoi porte :

- une heure de début ;
- une heure de fin ;
- un numéro de work item nullable ;
- une origine informative (Git, calendrier, chrono rapide, saisie) qui permet au collecteur de
  prolonger ou de remplacer ses propres créneaux ;
- un état d’envoi local temporaire pour éviter les doublons lors d’un échec partiel.

Les catégories d’activité et les titres saisis disparaissent. Un libellé résolu depuis Azure
peut être affiché pour aider la relecture, mais il n’est ni demandé à l’utilisateur ni envoyé
à 7pace.

Deux créneaux peuvent se chevaucher. Des créneaux du même numéro qui se suivent sans écart
partent en un seul worklog ; séparés par un écart ou un chevauchement, ils restent distincts.

## Cycle quotidien

### Collecte

- L’app démarre avec la session Windows, en widget, et lance le collecteur s’il ne tourne pas.
- Quitter l’app ne l’arrête pas ; un collecteur disparu est relancé en silence par l’app.
- Un seul collecteur écrit dans un profil de données donné ; un second lancement se retire.
- Arrêter la collecte est une action explicite des réglages, confirmée par un appui maintenu,
  qui fait fermer au collecteur ses créneaux et son dernier relevé avant de sortir. L’app ne le
  relance plus alors que sur demande.
- Elle surveille un seul dépôt Git et uniquement les horaires de travail configurés.
- Si un lien ICS est renseigné, le calendrier est relu toutes les 5 minutes. Un flux illisible
  depuis plus de 30 minutes passe le widget au rouge et laisse le suivi Git seul.
- La branche active continue d’être comptée lorsque le poste reste allumé mais inactif.
- Une interruption de collecte devient un créneau « À attribuer » plutôt qu’une supposition.
- Un relevé Git raté n’est pas une interruption : la branche connue est conservée le temps de
  quelques relevés, puis la collecte s’arrête sur la dernière minute réellement vérifiée.
- Le créneau rapide « À attribuer » se démarre et s’arrête sans demander de numéro.
- Un créneau rapide actif bloque l’envoi jusqu’à son arrêt et son attribution.

### File du matin

- La journée calendaire en cours n’est jamais envoyable.
- Quand une journée terminée se met à attendre, la fenêtre principale s’ouvre d’elle-même,
  une seule fois par date.
- La fenêtre présente la plus ancienne journée en attente.
- Après traitement, il avance vers la suivante ; il ne fournit aucun sélecteur de date ni
  accès aux journées closes.
- Une journée peut être explicitement ignorée après confirmation forte. Aucun appel 7pace
  n’est alors effectué.

### Consultation de la journée en cours

La collecte est silencieuse : sans retour, une panne de relevé ne se découvre que le
lendemain, quand la journée arrive vide ou fausse. Le widget montre en permanence l’état du
suivi, et la fenêtre expose une consultation
de la journée calendaire en cours, en lecture seule et disponible même lorsqu’une journée
terminée attend déjà d’être traitée.

Elle montre dans l’agenda les créneaux réellement enregistrés aujourd’hui — heures, ticket
résolu, libellé de relecture — les créneaux encore à attribuer, l’heure courante, le total et
l’état du suivi : ticket ou réunion en cours, hors horaires, lecture Git en échec ou dépôt
introuvable. Seuls les trous déjà écoulés sont signalés : les horaires à venir n’en sont pas.

Le collecteur expose aussi la santé détaillée du suivi : branche lue, créneau en cours,
dernier relevé, dernière lecture de branche, dernière écriture et dernière erreur. Passé trois
intervalles de relevé sans nouveau relevé, le widget annonce le suivi figé. Une lecture Git
impossible se distingue d’un dépôt absent : le relevé reste frais alors que la dernière
lecture de branche vieillit.

Cette consultation ne corrige, ne supprime, n’ignore ni n’envoie quoi que ce soit, ne lit
aucune donnée 7pace et n’écrit rien sur le disque pour se rafraîchir. Elle ne remplace pas
la file du matin, qui garde la priorité, et propose d’y revenir quand une journée attend.

### Ajustement

La fenêtre affiche la journée en agenda vertical, les chevauchements en colonnes côte à côte.
L’utilisateur peut :

- ajouter un créneau en glissant dans le vide, ou combler un trou d’un clic ;
- déplacer un créneau ou étirer ses bords, aimantés à 5 minutes ;
- modifier son début, sa fin ou son numéro de work item, choisi parmi ceux de la journée ;
- supprimer un créneau ;
- conserver volontairement des créneaux qui se chevauchent.

Les chevauchements et les trous dans les horaires configurés sont signalés sans bloquer
l’envoi. Tout créneau « À attribuer » bloque l’envoi complet.

### Confirmation et envoi

L’agenda montre déjà chaque créneau, son numéro, les chevauchements et les trous ; le panneau
de droite donne le total imputable et les créneaux sans ticket. L’envoi part d’un appui
maintenu d’une seconde sur « Envoyer », sans autre boîte de dialogue ; un clic ne suffit pas.

Les worklogs sont envoyés séquentiellement. En cas d’échec partiel :

- chaque créneau accepté est immédiatement verrouillé et ne sera jamais renvoyé ;
- chaque créneau refusé reste modifiable et renvoyable ;
- la journée reste en tête de file jusqu’à son envoi complet ou son abandon explicite.

Après un envoi complet :

- la fenêtre affiche le compte rendu de l’envoi ;
- le détail local de la journée est supprimé ;
- seul un marqueur de date close est conservé pour empêcher sa réapparition ;
- toute correction ultérieure se fait directement dans 7pace.

## Interface

L’interface est une app de bureau Tauri à deux fenêtres sans cadre, aux couleurs
arachnid-dark du poste.

Le widget est une ligne toujours au premier plan : pastille d’état (verte quand le suivi ou
une réunion tourne, ambre hors horaires, rouge en cas de panne, de suivi figé, de calendrier
illisible ou de collecteur injoignable), ticket suivi, chrono du créneau en cours et bouton
« Hors ticket » du chrono rapide. Un clic ouvre la fenêtre ; un clic droit propose Ouvrir et
Quitter.

La fenêtre contient uniquement l’agenda de la journée en attente et, à droite, sa date, le
nombre d’autres journées en attente, le total, les créneaux sans ticket ou l’édition du
créneau sélectionné, Envoyer, Ignorer et une bascule vers la journée en cours en lecture
seule. La fermer la replie en widget.

Un seul panneau secondaire regroupe les réglages (dépôt, organisation Azure DevOps, compte et
jeton 7pace, lien ICS Outlook, horaires), la mise à jour, l’arrêt ou la relance du collecteur
et « Quitter l’app ». Il ne contient aucun catalogue d’activités : l’utilisateur saisit les
numéros requis.

L’app n’écrit rien elle-même, et le collecteur sérialise les demandes qui modifient une
journée. Un collecteur injoignable est annoncé tel quel ; l’interface ne laisse jamais croire
que le temps continue d’être compté.

## Non-objectifs

- Lire, synchroniser ou importer les worklogs 7pace.
- Installer un service Windows ou demander des droits administrateur.
- Exposer la liaison du collecteur hors du compte Windows qui l’héberge.
- Afficher une vue jour choisie, semaine, mois ou historique.
- Afficher des rapports, statistiques ou tendances.
- Corriger ou supprimer depuis l’application une journée déjà envoyée.
- Maintenir un catalogue local des tâches génériques du wiki.
- Détecter l’inactivité du poste ou lire la réponse aux invitations Outlook.
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
10. L’envoi exige un appui maintenu ; l’agenda montre la liste exacte et le total avant.
11. Des créneaux du même numéro qui se suivent sans écart produisent un seul worklog.
12. Après un échec partiel, seuls les créneaux acceptés sont verrouillés et exclus de la
    tentative suivante.
13. Après succès complet, le détail local est supprimé et seul le marqueur de date close
    subsiste.
14. Ignorer une journée la clôt localement sans contacter 7pace.
15. Hors de la journée, l’app ne donne accès qu’au panneau des réglages.
16. Le widget passe au rouge « injoignable » moins de 10 s après la perte du collecteur.
17. Un relevé Git raté ne ferme pas le créneau en cours, n’ouvre pas de doublon à la reprise
    et ne produit jamais d’intervalle « poste en veille ou arrêté ».
18. La journée en cours est consultable en lecture seule à tout moment, y compris quand une
    journée terminée attend déjà, et cette consultation ne la rend ni modifiable ni
    envoyable.
19. La consultation distingue un suivi vivant d’un suivi figé, et n’écrit rien sur le disque
    pour se rafraîchir.
20. Fermer la fenêtre ou quitter l’app laisse la collecte se poursuivre.
21. Ouvrir l’app sans collecteur en démarre un, puis s’y connecte ; la rouvrir ensuite
    retrouve l’état et les données déjà collectées.
22. Un second collecteur lancé sur le même profil de données se retire sans rien écrire.
23. Seule une action explicite et confirmée des réglages arrête la collecte de fond, et le
    collecteur écrit son dernier relevé avant de sortir.
24. Une liaison perdue est annoncée, jamais masquée ; une écriture interrompue n’est pas
    rejouée automatiquement.
25. Une mise à jour attend la sortie du collecteur et de l’app, ne laisse jamais deux
    versions actives, et préserve réglages, jeton et journées.
