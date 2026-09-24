# Glossaire

## Collecteur

Processus de fond, sans fenêtre, qui relève la branche Git et le calendrier Outlook, tient les journées et écrit sur le disque. L’app le lance à l’ouverture de session et il continue quel que soit son sort. Un seul collecteur existe par profil de données, et lui seul écrit.

## App

Interface de bureau, faite du widget et de la fenêtre. Elle ne collecte rien : elle interroge le collecteur, affiche ce qu’il sait et lui transmet les corrections. La quitter ne ferme que l’interface.

## Widget

Ligne toujours au premier plan qui montre l’état du suivi, le ticket suivi et son chrono, et porte le chrono rapide. C’est la forme repliée de l’app.

## Liaison

Canal local entre l’app et le collecteur de son profil de données, réservé au compte Windows qui l’a ouvert. Son état est visible en permanence dans le widget : un collecteur injoignable est annoncé comme tel, jamais présenté comme un suivi qui tourne.

## Arrêt complet du suivi

Action explicite et confirmée qui demande au collecteur de fermer ses créneaux, d’écrire son dernier relevé puis de sortir. Elle se distingue de la sortie de l’app, qui ne touche pas à la collecte.

## Profil de données

Dossier qui porte les réglages, le jeton et les journées, ordinairement `%LOCALAPPDATA%\7pace-auto`. Il détermine à lui seul le collecteur, sa liaison et son verrou : deux profils distincts ne se voient jamais.

## Journée en cours

La date calendaire actuelle. L’application y collecte automatiquement les créneaux, mais elle ne peut pas l’envoyer.

## Consultation de la journée en cours

Vue de la fenêtre, en lecture seule, qui montre les créneaux déjà collectés aujourd’hui et la santé du suivi. Elle sert à vérifier que la collecte tourne ; elle ne rend la journée en cours ni modifiable ni envoyable.

## Suivi figé

État d’un suivi dont le dernier relevé remonte à plus de trois intervalles de relevé. La collecte ne progresse plus et la journée en cours ne reflète plus le travail réel.

## Journée en attente

Une journée calendaire terminée qui contient des créneaux locaux et qui n’est ni envoyée ni ignorée. Les journées en attente sont traitées de la plus ancienne à la plus récente.

## Journée close

Une journée entièrement envoyée à 7pace ou explicitement ignorée. Son détail n’est plus consultable dans l’application ; 7pace est l’unique source de vérité pour une journée envoyée.

## Créneau

Une période définie par une heure de début, une heure de fin et, lorsqu’elle est attribuée, un numéro de work item. Deux créneaux peuvent se chevaucher.

## À attribuer

État d’un créneau qui ne porte pas encore de numéro de work item. Une journée contenant un tel créneau ne peut pas être envoyée.

## Trou

Période des horaires de travail configurés que ne couvre aucun créneau. Un trou est signalé mais n’interdit pas l’envoi.

## Chevauchement

Période couverte par plusieurs créneaux. Un chevauchement est signalé mais reste envoyable, car il peut représenter plusieurs imputations simultanées volontaires.

## Créneau occupé

Période marquée « occupé » dans le calendrier Outlook publié par lien ICS, retenue dans les horaires de travail. Elle interrompt le suivi Git et devient un créneau imputé à #175 pour le standup de 09:15–09:30, à #83 sinon. Le flux ne dit pas si l’invitation a été acceptée.

## Lecture Git impossible

Relevé de branche qui n’aboutit pas : git dépasse son délai, ne se lance pas, ou le dossier du dépôt est momentanément indisponible. Ce n’est ni un dépôt absent, qui demande un réglage, ni une veille du poste : la branche connue reste valable quelques relevés, puis la collecte se fige sur la dernière minute réellement vérifiée.
