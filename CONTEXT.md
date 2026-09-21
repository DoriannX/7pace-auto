# Glossaire

## Journée en cours

La date calendaire actuelle. L’application y collecte automatiquement les créneaux, mais elle ne peut pas l’envoyer.

## Consultation de la journée en cours

Vue du terminal, en lecture seule, qui montre les créneaux déjà collectés aujourd’hui et la santé du suivi. Elle sert à vérifier que la collecte tourne ; elle ne rend la journée en cours ni modifiable ni envoyable.

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

## Lecture Git impossible

Relevé de branche qui n’aboutit pas : git dépasse son délai, ne se lance pas, ou le dossier du dépôt est momentanément indisponible. Ce n’est ni un dépôt absent, qui demande un réglage, ni une veille du poste : la branche connue reste valable quelques relevés, puis la collecte se fige sur la dernière minute réellement vérifiée.
