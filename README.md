# 7pace auto

Application terminal Windows qui collecte le temps passé sur la branche Git active, puis fait
corriger et envoyer la journée terminée dans
[7pace Timetracker](https://www.7pace.com/) le lendemain matin.

Elle ne remplace pas 7pace : elle n’y lit rien, n’affiche aucun historique et ne permet
aucune correction après envoi. Une journée envoyée appartient à 7pace.

## Fonctionnement

- Relève la branche du dépôt surveillé pendant les horaires configurés.
- Rapproche le Bug ou PBI trouvé dans la branche de son Fix ou de sa Task via `az boards`.
- Un chrono rapide facultatif ouvre un créneau « à attribuer », même pendant un ticket suivi.
- Le lendemain matin, une notification signale la plus ancienne journée terminée en attente.
- Le terminal montre cette journée seule : créneaux, trous, chevauchements et total.
- Après correction, l’envoi écrit un worklog par créneau puis clôt la journée.

Le suivi repose uniquement sur la branche Git active. Aucune application, frappe, navigation
ou période d’inactivité n’est observée. Les journées en attente restent dans
`%LOCALAPPDATA%\7pace-auto\days` ; une journée envoyée n’y laisse que sa date.

Le détail du flux et ses critères d’acceptation : [docs/specs/daily-adjust-submit.md](docs/specs/daily-adjust-submit.md).
Le vocabulaire du domaine : [CONTEXT.md](CONTEXT.md).

## Règles du flux

- La journée en cours est collectée mais jamais envoyable : elle se traite le lendemain.
- Les journées en attente sont proposées de la plus ancienne à la plus récente.
- Un créneau « à attribuer », ou un chrono rapide encore ouvert, bloque tout l’envoi.
- Les chevauchements sont autorisés et signalés : 7pace accepte des imputations simultanées.
- Les trous dans les horaires prévus sont signalés mais n’interdisent pas l’envoi.
- Un envoi partiel verrouille les créneaux acceptés ; les refusés restent modifiables.
- « Ignorer cette journée » la clôt localement, sans aucun appel 7pace.

## Installation

Téléchargez `SeptPaceAuto.Terminal-win-x64.zip` depuis la dernière
[release](../../releases/latest), extrayez l’archive, puis lancez :

```powershell
.\install.ps1 -Startup
```

`-Startup` ajoute le raccourci de session, lancé minimisé : la collecte tourne sans fenêtre au
premier plan. Le script installe l’application dans `%LOCALAPPDATA%\Programs\7pace auto`,
ajoute un raccourci au menu Démarrer et conserve les données lors d’une mise à jour ou d’une
désinstallation. `uninstall.ps1` retire l’application ; `uninstall.ps1 -PurgeData` supprime
aussi les réglages, le jeton et les journées.

Prérequis : Windows 10 ou 11. L’archive publiée inclut le runtime .NET.

## Configuration

Au premier lancement, choisissez **6. Configurer l’application**, puis renseignez :

| Réglage | Utilité |
|---|---|
| Dépôt Git | dossier local dont la branche active est relevée |
| Intervalle de relevé | fréquence du relevé, de 10 à 300 secondes |
| Organisation Azure DevOps | URL utilisée par `az boards` pour trouver le Fix enfant |
| Compte 7pace | sous-domaine `https://<compte>.timehub.7pace.com` |
| Horaires de travail | périodes pendant lesquelles le temps est compté |

Aucun catalogue d’activités n’est tenu par l’application : les numéros des tâches génériques
(réunion, aide, formation…) se saisissent au moment d’attribuer le créneau.

Le jeton se saisit avec **7. Enregistrer ou supprimer le jeton 7pace**. Il est chiffré par
DPAPI pour le compte Windows et ne quitte jamais la machine.

## Mises à jour

L’entrée **8. Rechercher et installer une mise à jour** télécharge l’archive terminal,
remplace l’installation puis relance l’application. Par sécurité, cette action fonctionne
uniquement depuis `%LOCALAPPDATA%\Programs\7pace auto` ; une exécution issue de
`dotnet run` doit d’abord être installée avec `build/install.ps1`.

## Développement

```powershell
dotnet run --project src/SeptPaceAuto.Terminal
powershell -File build/publish.ps1
powershell -File build/install.ps1
```

Le cœur métier (`src/SeptPaceAuto.Core`) porte la collecte Git, les journées, Azure DevOps,
l’envoi 7pace et les mises à jour. Le terminal (`src/SeptPaceAuto.Terminal`) est l’unique
interface : l’ancienne interface graphique est dépréciée.

## Licence

MIT, voir [LICENSE](LICENSE).
