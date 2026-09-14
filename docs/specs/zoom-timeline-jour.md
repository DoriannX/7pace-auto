# Zoom dans la timeline jour

Statut : spécifié, non implémenté.
Périmètre : vue **jour** du planning (`#entries-list`, colonne `.day-timeline`).
Non-objectifs : vues semaine et mois strictement inchangées ; aucun zoom typographique ;
aucun élargissement de la plage horaire affichée ; aucune création de créneau par glisser.

## 1. Problème

L'échelle verticale de la vue jour est aujourd'hui imposée : `applyTimelineScale()`
(`src/SeptPaceAuto/web/app.js:467-486`) calcule `--day-hour` pour que les dix heures de la
plage `DAY_START = 480` → `DAY_END = 1080` (`app.js:70-71`) tiennent dans la hauteur
disponible, avec un plancher `DAY_HOUR_MIN = 36` px (`app.js:82`). Sur une fenêtre courante,
une heure occupe donc quelques dizaines de pixels et **une minute moins d'un pixel**.

Conséquences observées dans le rendu actuel :

- un créneau court est écrasé à la hauteur minimale de 6 px (`app.js:733-742`, `style.css:504`) ;
- les seuils de container query masquent successivement le détail (40 px), le titre (27 px),
  l'heure (12 px), puis la pastille de durée (5 px) (`style.css:655-670`) : un créneau de
  quelques minutes n'affiche plus rien et n'est plus distinguable de son voisin ;
- aucun moyen de le lire ou de le viser : il n'existe ni zoom, ni densité, ni réglage
  d'échelle dans le code ou le CSS.

Le besoin principal est donc le **zoom avant** sur les créneaux de 2 à 5 minutes produits par
le suivi de branche. Le zoom arrière n'a de valeur que pour revenir à la vue d'ensemble.

## 2. Comportement attendu

### 2.1 Geste

Le zoom se fait au **Ctrl + glisser vertical**, sans molette et sans raccourci clavier.

| Aspect | Règle |
|---|---|
| Zone active | toute la colonne horaire de la vue jour, **blocs de créneaux compris** ; le rail de synthèse est exclu |
| Sens | monter = zoomer avant, descendre = zoomer arrière |
| Loi | géométrique : facteur `×2` par 150 px de déplacement, appliqué en continu (pas de paliers) |
| Seuil d'entrée | 3 px de déplacement vertical ; en dessous, le geste n'a aucun effet sur l'échelle |
| Ancre | l'instant sous le curseur à l'instant du `pointerdown` reste immobile à l'écran pendant tout le geste |
| Pendant le geste | pointeur capturé **au franchissement du seuil** (le geste survit à la sortie de la grille et de la fenêtre ; capturer plus tôt détournerait le clic de compatibilité et casserait le Ctrl+clic immobile), sélection de texte neutralisée, curseur de redimensionnement vertical |
| Annulation | aucune. Le geste est réversible par lui-même : redescendre défait la montée. `Échap` conserve son unique rôle actuel, fermer éditeur/aperçu/tiroirs (`app.js:2664-2677`) |

Interaction avec le clic existant (`openBlock()`, `app.js:697/724`) :

- **Ctrl + clic sans franchir le seuil** de 3 px : comportement d'un clic normal, l'éditeur du
  créneau s'ouvre ;
- **Ctrl + glisser ayant franchi le seuil** : le relâchement termine le zoom et **n'ouvre pas**
  l'éditeur, même si le geste a démarré sur un créneau ;
- le surlignage lié bloc ↔ rail (`linkGroup()`, `app.js:795-805`) n'est pas modifié.

### 2.2 Bornes

L'échelle est exprimée en **pixels par heure** et bornée à chaque application :

- **minimum** = valeur d'ajustement à la fenêtre, c'est-à-dire la valeur que
  `applyTimelineScale()` calcule aujourd'hui (`max(36, floor(hauteurDisponible / 10))`).
  On ne peut jamais dézoomer au-delà : la journée n'est jamais plus petite que la fenêtre,
  il n'y a jamais de bande vide sous 18:00 ;
- **maximum** = **600 px/h**, soit 10 px par minute : les dix heures occupent 6000 px
  (~7 écrans) et un créneau d'une minute dépasse tous les seuils de masquage du texte.

La plage affichée reste **08:00 – 18:00**. Le zoom ne modifie que `px/heure` ; il ne révèle
pas d'heures supplémentaires et ne touche pas `DAY_START`/`DAY_END`.

### 2.3 Graduations

L'axe n'affiche aujourd'hui qu'une étiquette par heure (`addTimeLabels()`, `app.js:690-698`).
Le pas devient adaptatif : on choisit **le pas le plus fin de la suite 60 / 30 / 15 / 5 / 1
minute dont l'espacement atteint au moins 28 px**.

| Échelle | Pas retenu |
|---|---|
| < 56 px/h | 60 min (identique à l'existant) |
| ≥ 56 px/h | 30 min |
| ≥ 112 px/h | 15 min |
| ≥ 336 px/h | 5 min |
| = 600 px/h (borne) | 1 min |

Les sous-graduations sont visuellement plus discrètes que l'heure pleine, comme les
demi-heures de la vue semaine. La ligne « maintenant » (`placeNowLine()`, `app.js:527-541`) et
les bandes de congé suivent l'échelle sans autre changement.

### 2.4 Indicateur et retour au défaut

- Pendant le geste et pendant ~1 s après son relâchement, une **surimpression près du curseur**
  affiche la **plage visible** : « 1 h 45 visible ». Elle disparaît ensuite.
- Dès que l'échelle diffère du défaut, un **petit bouton « Réinitialiser le zoom »** apparaît
  dans l'en-tête de la vue jour, près des contrôles de navigation de jour ; il porte un libellé
  accessible et disparaît lorsque le défaut est rétabli. Il remet l'échelle au défaut
  (ajustement à la fenêtre) et efface la préférence mémorisée.

### 2.5 Défilement

La vue jour défile déjà via `.surface-scroll` (`app.js:450`, `style.css:550`), ce qui ne se
produisait quasiment jamais puisque la grille était ajustée à la hauteur disponible.

- **À l'ouverture de l'application** avec un zoom mémorisé : le défilement place la **ligne
  « maintenant » au centre** de la zone visible. Si l'heure courante est hors de 08:00 – 18:00,
  la vue reste en haut (08:00).
- **Au changement de jour** : la **même fenêtre horaire** est conservée, afin de comparer deux
  journées sur la même plage.
- **Pendant le geste** : le défilement est recalculé à chaque étape pour maintenir l'instant
  ancré (§ 2.1).

### 2.6 Rémanence et redimensionnement

Le zoom est une **préférence d'affichage**, jamais un état de la journée : rien n'est écrit
dans les JSON de `%LOCALAPPDATA%\7pace-auto\days`.

- **Valeur persistée** : l'échelle en **pixels par heure**, dans le fichier de réglages de
  l'hôte (`AppSettings.DayHourPx`), écrite par un appel dédié `saveDayZoom` du pont WebView2
  et relue au démarrage avec le reste de la configuration (`loadSettings`). L'appel dédié
  évite de faire passer une préférence d'affichage par la validation stricte des réglages.
- **Retour à l'ajustement** : un geste ramené jusqu'au minimum, comme le bouton de
  réinitialisation, mémorise `0` — « aucun zoom » — et non la valeur du plancher courante,
  qui figerait l'échelle à la taille actuelle de la fenêtre.
- **Absente ou hors bornes** : retour silencieux au défaut, sans erreur ni migration de
  fichier de configuration. Le réglage **n'apparaît pas** dans l'écran de configuration.
- **Défaut** (aucun geste jamais effectué) : le comportement actuel, ajustement à la fenêtre.
  Dès qu'une valeur est mémorisée, elle remplace l'ajustement automatique.
- **Redimensionnement de fenêtre** : l'échelle mémorisée est **bornée à l'affichage** si la
  fenêtre agrandie rend le minimum plus haut que la valeur choisie ; la préférence stockée
  n'est **pas** écrasée et redevient effective si la fenêtre rétrécit.
- **Bascule fenêtre complète ↔ mini-chrono** (`app.js:2293`) : traitée exactement comme un
  redimensionnement.

## 3. Découpage technique

### 3.1 `src/SeptPaceAuto/web/scale.js` (nouveau, sans DOM)

Contient **tout le calcul**, testable sans navigateur :

- bornage de l'échelle : minimum d'ajustement (hauteur disponible, plancher 36 px/h) et
  maximum 600 px/h ;
- conversion déplacement → facteur (`×2` par 150 px) ;
- défilement préservant l'instant ancré (échelle avant/après, position du curseur, ancien
  défilement → nouveau défilement) ;
- choix du pas de graduation (§ 2.3) ;
- validation/bornage du réglage relu.

Chargement : **second `<script defer>` placé avant `app.js`** dans `index.html`, exposant ses
fonctions sur un objet global ; export ESM conditionnel pour Node. `app.js` ne garde que la
gestion des événements, l'écriture des variables CSS (`--day-hour`, `--day-minute`,
`--day-timeline`, `app.js:483-485`) et le rendu des graduations.

### 3.2 Impacts

| Fichier | Nature de la modification |
|---|---|
| `web/scale.js` | nouveau module de calcul |
| `web/index.html` | balise `<script>` supplémentaire |
| `web/app.js` | `applyTimelineScale()` consomme la préférence ; gestionnaires `pointerdown/move/up` Ctrl ; `addTimeLabels()` devient adaptatif ; défilement ancré, à l'ouverture et au changement de jour ; indicateur ; bouton de réinitialisation ; neutralisation de l'ouverture d'éditeur après zoom |
| `web/style.css` | styles de l'indicateur, du bouton, des sous-graduations |
| `Services/AppSettings.cs` | un champ d'échelle persisté, hors écran de configuration |
| `Services/TrackingApp.cs` | appel `saveDayZoom` du pont, à côté de `loadSettings`/`saveSettings` |
| `.github/workflows/` | nouveau workflow `node --test` sur push et pull request |

Les fonctions partagées avec la semaine (`createBlock`, `updateBlock`, `renderBlocks`,
`app.js:716-771`) ne doivent pas changer de comportement : la semaine utilise ses propres
variables `--hour`/`--minute` (`app.js:473-479`) et reste intacte.

## 4. Vérification

### 4.1 Tests unitaires — `node --test`

Nouveau workflow GitHub Actions déclenché sur **push et pull request** : `actions/setup-node`
puis `node --test` sur les tests de `web/`. Cas couverts, tous sur `scale.js` :

1. le bornage refuse une échelle inférieure à l'ajustement à la fenêtre et la ramène à ce
   minimum, sans modifier la valeur demandée retournée pour persistance ;
2. le bornage plafonne à 600 px/h ;
3. 150 px de montée doublent l'échelle ; 300 px la quadruplent ; 150 px de descente la
   ramènent à l'identique (aller-retour exact) ;
4. l'instant ancré conserve la même ordonnée écran avant et après un changement d'échelle,
   ancre en haut, au milieu et en bas de la zone visible ;
5. le pas de graduation vaut 60 / 30 / 15 / 5 / 1 min aux échelles frontières du tableau § 2.3,
   et la règle des 28 px est respectée juste en dessous et juste au-dessus de chaque seuil ;
6. un réglage relu absent, nul, négatif, non numérique ou supérieur à 600 donne le défaut.

### 4.2 Scénario manuel

À dérouler dans l'application sur Windows, sur une journée contenant au moins un créneau de
moins de 5 minutes :

1. **Geste** — Ctrl + glisser vers le haut sur la grille : les créneaux s'allongent en continu,
   l'instant sous le curseur ne bouge pas, la surimpression annonce la plage visible.
2. **Créneau court** — le créneau de quelques minutes finit par afficher son heure puis son
   titre ; il est cliquable et ouvrable.
3. **Sur un bloc** — le même geste démarré sur un créneau zoome sans ouvrir l'éditeur ;
   un Ctrl + clic immobile sur ce créneau ouvre bien l'éditeur.
4. **Borne haute** — continuer à monter : l'échelle s'arrête, le pas de graduation atteint
   1 minute.
5. **Borne basse** — glisser vers le bas : l'échelle s'arrête dès que les dix heures tiennent
   dans la fenêtre, sans espace vide sous 18:00.
6. **Hors fenêtre** — poursuivre le geste en sortant de la grille et de la fenêtre : le zoom
   continue de suivre le pointeur ; aucun texte n'est surligné.
7. **Réinitialiser** — le bouton apparaît hors défaut, restaure l'ajustement, puis disparaît.
8. **Changement de jour** — la veille s'affiche à la même échelle et sur la même plage horaire.
9. **Persistance** — fermer et relancer l'application : l'échelle est conservée et la vue est
   centrée sur l'heure courante.
10. **Redimensionnement** — agrandir la fenêtre jusqu'à dépasser l'échelle choisie
    (l'affichage se borne), puis la rétrécir : l'échelle choisie revient.
11. **Mini-chrono** — basculer en mini puis revenir : l'échelle choisie est retrouvée.
12. **Non-régression** — les vues semaine et mois sont identiques à avant ; la ligne
    « maintenant » et les bandes de congé restent à la bonne position à toute échelle.
