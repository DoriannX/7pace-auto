# 7pace auto

Application Windows qui suit le temps passé sur la branche Git active, laisse corriger la
journée, puis envoie les temps validés dans [7pace Timetracker](https://www.7pace.com/)
après relecture.

Interface en français, deux vues : une fenêtre de gestion (planning horaire jour / semaine /
mois) et un mini-chrono à laisser dans un coin de l'écran, toujours au-dessus des autres
fenêtres.

## Ce que fait l'application

- Relève la branche du dépôt surveillé pendant les créneaux de travail configurés et
  accumule le temps sur le ticket correspondant.
- Rapproche le numéro de Bug/PBI trouvé dans le nom de branche du Fix ou de la tâche
  enfant, via l'Azure CLI (`az boards`) quand elle est installée et configurée.
- Laisse créer, corriger ou exclure un créneau, y compris sur une journée passée.
- N'envoie rien tant qu'un créneau reste à attribuer, et jamais sans confirmation
  explicite dans l'aperçu.
- Marque les créneaux déjà envoyés : ils deviennent non modifiables et ne repartent pas.

## Ce qu'elle ne fait pas

- Aucun suivi des applications, des frappes, de la navigation ou de l'inactivité : le seul
  signal est la branche Git active.
- Aucune télémétrie, aucun envoi vers un service tiers autre que 7pace et, pour les mises à
  jour, l'API publique de GitHub.
- L'intégration du calendrier Outlook est préparée mais **non connectée** : elle attend une
  approbation administrateur côté Microsoft Entra et n'affiche donc aucune réunion.

## Installation

Téléchargez `SeptPaceAuto-win-x64.zip` depuis la dernière
[release](../../releases/latest), puis :

```powershell
# depuis le dossier du dépôt, ou avec le zip téléchargé
powershell -ExecutionPolicy Bypass -File build/install.ps1 -Zip <chemin-du-zip>
```

L'installation se fait pour l'utilisateur courant, sans droits administrateur, dans
`%LOCALAPPDATA%\Programs\7pace auto`, avec un raccourci dans le menu Démarrer.
`build/uninstall.ps1` retire l'application ; vos journées restent en place sauf
`-PurgeData`.

Prérequis : Windows 10/11 et le runtime WebView2, déjà présent sur un Windows à jour.

## Première configuration

Au premier lancement, l'application demande sa configuration :

| Réglage | À quoi il sert |
|---|---|
| Dépôt Git surveillé | dossier local dont la branche active est relevée |
| Intervalle de relevé | fréquence du relevé, 10 à 300 secondes |
| Organisation Azure DevOps | URL utilisée par `az boards` pour trouver le Fix enfant |
| Compte 7pace | sous-domaine `https://<compte>.timehub.7pace.com` |
| Créneaux de travail et pause | heures pendant lesquelles le temps est compté |
| Activités | vos tâches génériques (stand-up, réunion, formation…) et leur numéro |
| Jeton 7pace | créé dans 7pace ▸ Settings ▸ Reporting & API |

Le jeton est chiffré par DPAPI pour votre compte Windows et ne quitte jamais la machine.
Les journées sont stockées en JSON dans `%LOCALAPPDATA%\7pace-auto\days`.

## Mises à jour

L'application interroge les releases GitHub du dépôt indiqué dans les paramètres. Quand une
version plus récente existe, un bandeau propose de l'installer : le zip est téléchargé,
déposé à côté de l'application, puis un script remplace les fichiers et relance
l'application. La recherche peut être désactivée dans les paramètres.

## Développement

```powershell
dotnet run --project src/SeptPaceAuto        # lancer en développement
powershell -File build/publish.ps1           # produire artifacts/SeptPaceAuto-win-x64.zip
```

.NET 8, WinForms et WebView2 pour l'hôte ; l'interface est du HTML/CSS/JS servi depuis
`src/SeptPaceAuto/web`. Une seule dépendance NuGet : `Microsoft.Web.WebView2`.

## Licence

MIT, voir [LICENSE](LICENSE). La police Inter est distribuée sous SIL Open Font License,
voir `src/SeptPaceAuto/web/inter-OFL.txt`.
