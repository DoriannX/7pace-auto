# Bouton « Synchroniser » — miroir de 7pace

Spécification. Aucun code n'est écrit ici ; les références `fichier:ligne` décrivent
l'existant au moment de la rédaction.

## Modèle

Le planning local est de deux natures, et une seule frontière les sépare : la validation.

- **Brouillon** — tout créneau non envoyé. Il n'appartient qu'à l'application : relevé par
  git, créé ou corrigé à la main. 7pace ne le connaît pas et n'a rien à en dire.
- **Miroir** — tout créneau envoyé. Il n'est plus qu'un reflet de ce que 7pace contient.
  7pace fait autorité, sans exception ni arbitrage.

Aujourd'hui le miroir n'existe pas : l'application ne sait que pousser, marque `sentAt`
après un POST accepté (`Model.cs:30`, `DayStore.StampSent`) et ne relit jamais. Un temps
corrigé ou supprimé dans l'interface web de 7pace, ou saisi ailleurs, reste invisible.

Le bouton « Synchroniser » rétablit le miroir sur la plage affichée.

Non-objectifs : pousser des corrections locales vers 7pace, lire ou écrire les « activity
types » de 7pace, toucher au brouillon.

## Conséquence directe : un worklog par créneau

Un miroir n'est fidèle que si 7pace porte la même granularité que l'écran. L'envoi actuel
regroupe les créneaux par work item avant de poster (`SevenPaceClient.cs:49-129`) : 7pace ne
retient qu'un total par ticket et par jour, et toute relecture écraserait le découpage
horaire en un bloc.

L'envoi passe donc à **un worklog par créneau**. C'est la seule forme compatible avec le
modèle, et elle rend la synchronisation non destructrice dans le cas normal.

Coût assumé : N requêtes par journée envoyée au lieu d'une par ticket, et des lignes plus
courtes dans les rapports 7pace. Le débit est géré (voir « Débit et 429 »).

## Comportement

### Déclenchement

- Manuel uniquement. Aucune synchronisation au démarrage ni au changement de plage.
- Icône seule dans la barre du haut, zone `toolbar-end` (`web/index.html:57-62`), voisine de
  « Ajouter » et « Mini-chrono », libellé « Synchroniser » au survol et en `aria-label`.
- Une seule synchronisation à la fois : bouton désactivé pendant l'exécution, le reste de
  l'écran reste utilisable.

### Portée

- La plage actuellement affichée : jour, semaine ou mois selon le sélecteur de vue
  (`web/index.html:48-54`). Jamais l'historique entier.
- Deux volets dans un même clic : lecture 7pace, puis re-résolution Azure.

### Volet 7pace — lecture

- `GET https://<compte>.timehub.7pace.com/api/rest/workLogs?api-version=3.2`, même
  construction d'adresse que l'envoi (`AppSettings.cs:109`), même en-tête
  `Authorization: Bearer <jeton>` (`Probes.cs:263`).
- Bornes : `$fromTimestamp` / `$toTimestamp` sur la plage affichée.
- Pagination automatique par `$count` (500 maximum) et `$skip`, jusqu'à épuisement. Une page
  tronquée ne doit jamais servir de base à un écrasement.
- Champs exploités : `id` (UUID), `timestamp` (heure de début, ISO 8601 UTC), `length`
  (secondes), `workItemId`. `activityType`, `comment` et `flags` sont ignorés.

### Volet 7pace — application

Règles, dans cet ordre :

1. **Le brouillon est intouchable.** `sentAt` nul ⇒ créneau conservé tel quel, quoi que dise
   7pace.
2. **7pace gagne sur tout le miroir.** Un créneau envoyé dont l'heure, la durée ou le ticket
   diffère du worklog apparié est réécrit sans question.
3. **Créneau envoyé sans worklog correspondant : supprimé.** Le temps a été supprimé dans
   7pace ; le miroir doit le refléter.
4. **Worklog sans créneau local : importé.** Créé à son heure de début, pour sa durée, marqué
   envoyé, non modifiable, origine `7pace`.

Dans le cas normal — tu envoies depuis l'application et personne ne touche à rien ailleurs —
la synchronisation ne modifie rien : un worklog par créneau signifie que le miroir est déjà
exact.

### Chevauchement brouillon / miroir

Un créneau brouillon peut chevaucher un temps venu de 7pace. Les deux coexistent, le
chevauchement est signalé à l'écran ; ni l'un ni l'autre n'est déplacé ou supprimé
automatiquement. `DayStore` refuse aujourd'hui les chevauchements à la saisie : cette
contrainte ne s'applique pas aux créneaux écrits par la synchronisation, qui reflètent un
fait extérieur.

### Appariement

- Nouveau champ `workLogId` (UUID, nullable) sur le créneau (`Model.cs`).
- Renseigné à l'**envoi**, depuis l'`id` retourné par le POST : c'est le seul moment où cet
  identifiant est disponible, aucun endpoint ne permet de le retrouver après coup.
- Renseigné aussi à la **synchronisation**, sur les créneaux importés.
- Un worklog par créneau ⇒ correspondance 1 pour 1, sans heuristique de date ou de durée.

### Première synchronisation (transition)

Les journées envoyées avant cette évolution n'ont pas de `workLogId`, et leurs worklogs sont
groupés par ticket. À la première synchronisation d'une plage, cette partie envoyée est
remplacée telle quelle par ce que renvoie 7pace : des blocs groupés par ticket et par jour.
Le détail horaire de ces journées-là est perdu, une fois. Ensuite le miroir est fin.

Aucun rattrapage heuristique n'est spécifié : il ne servirait qu'une fois.

### Débit et 429

Le plan gratuit 7pace plafonne autour de 5 requêtes par minute et 50 par heure ; les plans
payants n'ont pas de limite publiée mais répondent 429 en cas d'excès.

- Les POST d'une journée sont envoyés séquentiellement.
- Sur 429, attente progressive puis reprise là où l'envoi s'est arrêté.
- L'envoi rapporte déjà le partiel (`SevenPaceClient.SubmitAsync`, `TrackingApp.cs:234-245`) :
  les créneaux acceptés sont marqués envoyés avec leur `workLogId`, les autres restent
  brouillon et peuvent repartir plus tard. Aucun temps n'est compté deux fois.

### Volet Azure

- Relance `az boards work-item show` pour **tous les tickets de la plage affichée**, cache
  ignoré.
- Le contournement du cache est indispensable : `WorkItemResolver` refuse tout nouvel essai
  pendant 2 minutes après un échec transitoire et 10 minutes après un échec permanent
  (`WorkItemResolver.cs:11-13`, `Fresh`). Sans lui, le bouton ne ferait rien de visible dans
  le cas le plus fréquent — un Fix enfant créé après le relevé.
- Appels sérialisés par le sémaphore existant, 20 secondes de budget par ticket : le coût est
  linéaire, une plage « mois » chargée peut prendre plusieurs minutes.
- Une résolution nouvelle met à jour `workitems.json` et le libellé des créneaux **brouillon**
  uniquement ; le miroir ne dépend que de 7pace.

### Origine des créneaux

Le champ `source` (`Model.cs:29`, valeurs `git` et `manual`) reçoit une troisième valeur
`7pace` pour les créneaux importés. Distinction discrète à l'écran, aucun nouvel état à gérer
ailleurs.

### Activités

Un worklog ne porte qu'un numéro de work item. Si ce numéro correspond à une activité
configurée (stand-up, réunion, formation…), le créneau importé prend cette activité ; sinon
c'est un ticket. Les « activity types » propres à 7pace ne sont ni lus ni écrits.

## Confirmation

La synchronisation n'écrit jamais sans confirmation. La lecture 7pace a donc lieu **avant**
la confirmation, qui annonce :

- la plage concernée ;
- le nombre de créneaux remplacés ;
- le nombre de créneaux supprimés ;
- le nombre de créneaux importés ;
- le delta de durée totale sur la plage.

Refus ⇒ rien n'est écrit sur disque. Quand les trois compteurs sont nuls, la confirmation est
remplacée par un simple « déjà à jour ».

## Échecs

- **Échec partiel assumé** : ce qui a abouti est appliqué, un message nomme précisément ce qui
  a échoué (« 7pace relu, résolution Azure indisponible : az introuvable »).
- **Lecture 7pace en échec** (jeton, réseau, 401/403) : aucun écrasement, aucune suppression.
- **Pagination interrompue** : aucun écrasement. Un jeu incomplet supprimerait à tort.
- **429 pendant la lecture** : attente progressive ; au-delà, abandon sans écrasement.
- **`az` indisponible ou organisation non réglée** : volet Azure sauté, volet 7pace appliqué.

## Dette corrigée dans le même lot

L'envoi écrit l'horodatage au format `MM/dd/yyyy HH:mm:ss` (`SevenPaceClient.cs:143`) là où
l'API attend de l'ISO 8601 UTC (`YYYY-MM-DDThh:mm:ss.sssZ`). Invisible tant que rien ne
relit ; visible dès la première synchronisation sous forme d'heures décalées et
d'appariements faux. Corrigé ici, pas renvoyé à un ticket séparé.

## Nouvelle méthode du pont web

Une méthode `synchronize` s'ajoute au routeur existant (`TrackingApp.HandleAsync`,
`TrackingApp.cs:68-167`), à côté de `submitDay` et `loadRange` :

- entrée : bornes `from` et `to` de la plage affichée, indicateur `apply` (faux = aperçu pour
  la confirmation, vrai = application) ;
- sortie : compteurs remplacés / supprimés / importés, delta de durée, liste des échecs, et
  l'instantané de la plage quand `apply` vaut vrai.

## Critères d'acceptation

1. Une journée envoyée depuis l'application, puis synchronisée sans modification côté 7pace,
   conserve **exactement** son découpage horaire : zéro remplacé, zéro supprimé, zéro importé.
2. Un créneau brouillon est strictement inchangé après synchronisation, y compris quand 7pace
   ne contient rien pour cette journée.
3. Un temps supprimé dans l'interface web de 7pace disparaît du planning après
   synchronisation.
4. Une durée ou une heure corrigée dans 7pace est reportée telle quelle, sans question, sur le
   seul créneau concerné.
5. Un temps saisi directement dans 7pace apparaît à son heure de début, marqué envoyé, non
   modifiable, origine `7pace`.
6. Un brouillon chevauchant un créneau importé est conservé et le chevauchement est signalé.
7. Deux synchronisations consécutives sans changement côté 7pace ne modifient rien.
8. Refuser la confirmation ne modifie aucun fichier de `%LOCALAPPDATA%\7pace-auto\days`.
9. Un envoi interrompu par un 429 laisse les créneaux acceptés marqués envoyés avec leur
   `workLogId` et les autres en brouillon ; une reprise n'envoie aucun doublon.
10. Une plage dépassant 500 worklogs est lue intégralement avant tout écrasement.
11. Jeton invalide ou réseau coupé : aucun créneau supprimé, message d'échec explicite.
12. Un ticket resté « à attribuer » sur un créneau brouillon est résolu par la synchronisation
    dès que le Fix enfant existe côté Azure, même si l'échec précédent date de moins de
    10 minutes.
13. Le bouton est inopérant et le signale quand le compte 7pace ou le jeton n'est pas
    configuré.
