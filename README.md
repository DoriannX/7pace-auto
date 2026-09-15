# 7pace auto

Application terminal Windows qui suit le temps passé sur la branche Git active, permet de
corriger une journée, puis envoie les temps validés dans
[7pace Timetracker](https://www.7pace.com/) après confirmation.

## Fonctionnalités

- Relève la branche du dépôt surveillé pendant les créneaux configurés.
- Rapproche le Bug ou PBI trouvé dans la branche de son Fix ou de sa Task via `az boards`.
- Ajoute, corrige, exclut et supprime des créneaux, y compris sur une journée passée.
- Prévisualise puis envoie une journée dans 7pace après confirmation explicite.
- Synchronise les créneaux déjà envoyés avec les worklogs présents dans 7pace.
- Recherche et installe les nouvelles versions publiées sur GitHub.

Le suivi repose uniquement sur la branche Git active. Aucune application, frappe, navigation
ou période d’inactivité n’est observée. Les journées restent dans
`%LOCALAPPDATA%\7pace-auto\days`.

## Installation

Téléchargez `SeptPaceAuto.Terminal-win-x64.zip` depuis la dernière
[release](../../releases/latest), extrayez l’archive, puis lancez :

```powershell
.\install.ps1
```

Le script installe l’application dans `%LOCALAPPDATA%\Programs\7pace auto`, ajoute un
raccourci au menu Démarrer et conserve les données lors d’une mise à jour ou d’une
désinstallation. `uninstall.ps1` retire l’application ; `uninstall.ps1 -PurgeData` supprime
aussi les réglages, le jeton et les journées.

Prérequis : Windows 10 ou 11. L’archive publiée inclut le runtime .NET.

## Configuration

Au premier lancement, choisissez **8. Configurer l’application**, puis renseignez :

| Réglage | Utilité |
|---|---|
| Dépôt Git | dossier local dont la branche active est relevée |
| Intervalle de relevé | fréquence du relevé, de 10 à 300 secondes |
| Organisation Azure DevOps | URL utilisée par `az boards` pour trouver le Fix enfant |
| Compte 7pace | sous-domaine `https://<compte>.timehub.7pace.com` |
| Créneaux de travail et pause | heures pendant lesquelles le temps est compté |
| Activités | tâches génériques et numéro de Fix ou Task associé |

Le jeton se saisit avec **9. Enregistrer ou supprimer le jeton 7pace**. Il est chiffré par
DPAPI pour le compte Windows et ne quitte jamais la machine.

## Mises à jour

L’entrée **10. Rechercher et installer une mise à jour** télécharge l’archive terminal,
remplace l’installation puis relance l’application. Par sécurité, cette action fonctionne
uniquement depuis `%LOCALAPPDATA%\Programs\7pace auto` ; une exécution issue de
`dotnet run` doit d’abord être installée avec `build/install.ps1`.

## Développement

```powershell
dotnet run --project src/SeptPaceAuto.Terminal
powershell -File build/publish.ps1
powershell -File build/install.ps1
```

Le cœur métier (`src/SeptPaceAuto.Core`) porte le suivi Git, les journées, Azure DevOps,
7pace et les mises à jour. Le terminal (`src/SeptPaceAuto.Terminal`) est l’unique interface.

## Licence

MIT, voir [LICENSE](LICENSE).
