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

Téléchargez `7pace-auto-setup.msi` depuis la dernière [release](../../releases/latest) et
double-cliquez dessus : c'est tout. L'installation se fait pour votre compte, sans droits
administrateur ni fenêtre d'élévation ; l'application apparaît ensuite dans le menu
Démarrer et dans « Applications installées », d'où elle se désinstalle.

Les mises à jour se font depuis l'application elle-même, pas en réinstallant le MSI. Qui
préfère une installation scriptée garde `SeptPaceAuto-win-x64.zip` et
`build/install.ps1 -Zip <chemin-du-zip>` ; `build/uninstall.ps1` fait le retrait. Dans les
deux cas, vos journées (`%LOCALAPPDATA%\7pace-auto`) survivent à la désinstallation.

Déjà installé avec le script ? Désinstallez cette ancienne installation depuis
« Applications installées » avant de passer au MSI ; vos données restent en place.

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
dotnet run --project src/SeptPaceAuto.Terminal # lancer le MVP interactif dans le terminal
dotnet run --project src/SeptPaceAuto          # lancer l'interface Windows
powershell -File build/publish.ps1             # produire le zip et 7pace-auto-setup.msi
```

Le cœur (`src/SeptPaceAuto.Core`) porte le suivi Git, les journées et les intégrations.
Les adaptateurs Windows et terminal utilisent le même contrat JSON et le même profil de
données ; ils ne se lancent donc pas simultanément sur ce profil. Le terminal couvre la
configuration, le suivi, la correction, l'envoi confirmé et la synchronisation 7pace.

.NET 8, WinForms et WebView2 pour l'hôte Windows ; l'interface est du HTML/CSS/JS servi
depuis `src/SeptPaceAuto/web`. Une seule dépendance NuGet : `Microsoft.Web.WebView2`.

L'installateur est décrit par `build/installer/Package.wxs` (WiX 5, installé à la demande
par `build/pack-msi.ps1`).

## Licence

MIT, voir [LICENSE](LICENSE). La police Inter est distribuée sous SIL Open Font License,
voir `src/SeptPaceAuto/web/inter-OFL.txt`.
