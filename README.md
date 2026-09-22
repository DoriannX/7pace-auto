# 7pace auto

Application Windows qui collecte le temps passé sur la branche Git active, puis fait
corriger et envoyer la journée terminée dans
[7pace Timetracker](https://www.7pace.com/) le lendemain matin.

Elle tient en deux morceaux : un **collecteur** sans fenêtre, qui démarre avec la session
Windows et relève le temps toute la journée, et un **terminal** qui s’y connecte pour afficher
et corriger. Fermer le terminal ne coupe jamais la collecte.

Elle ne remplace pas 7pace : elle n’y lit rien, n’affiche aucun historique et ne permet
aucune correction après envoi. Une journée envoyée appartient à 7pace.

## Fonctionnement

- Le collecteur relève la branche du dépôt surveillé pendant les horaires configurés.
- Rapproche le Bug ou PBI trouvé dans la branche de son Fix ou de sa Task via `az boards`.
- Un chrono rapide facultatif ouvre un créneau « à attribuer », même pendant un ticket suivi.
- La journée en cours se consulte en lecture seule, pour vérifier que la collecte tourne.
- Le lendemain matin, une notification signale la plus ancienne journée terminée en attente.
- Le terminal montre cette journée seule : créneaux, trous, chevauchements et total.
- Après correction, l’envoi écrit un worklog par créneau puis clôt la journée.

Le suivi repose uniquement sur la branche Git active. Aucune application, frappe, navigation
ou période d’inactivité n’est observée. Les journées en attente restent dans
`%LOCALAPPDATA%\7pace-auto\days` ; une journée envoyée n’y laisse que sa date.

Le détail du flux et ses critères d’acceptation : [docs/specs/daily-adjust-submit.md](docs/specs/daily-adjust-submit.md).
Le vocabulaire du domaine : [CONTEXT.md](CONTEXT.md).

## Collecteur et terminal

`SeptPaceAuto.Agent.exe` est le collecteur : il n’a ni fenêtre ni icône, il est seul à relever
la branche, à écrire les journées et à envoyer la notification du matin. Il tourne tant que la
session Windows est ouverte.

`SeptPaceAuto.Terminal.exe` est l’interface. Elle ne collecte rien : elle interroge le
collecteur par une liaison locale réservée au compte Windows, affiche ce qu’il sait et lui
transmet les corrections. Ouvrir le terminal alors qu’aucun collecteur ne tourne en démarre un
en silence ; le fermer, par la croix comme par CTRL+C, ne ferme que l’interface. Le terminal
affiche en permanence l’état de la liaison, et le dit franchement quand le collecteur ne
répond plus, au lieu de laisser croire que le temps est compté.

Plusieurs terminaux peuvent être ouverts en même temps : aucun n’écrit sur le disque, et le
collecteur sérialise les corrections qu’il reçoit. Un seul collecteur existe par profil de
données ; un second lancement se retire sans rien toucher.

Arrêter réellement la collecte est une action explicite : **10. Arrêter complètement le suivi
en arrière-plan**, confirmée en tapant `ARRETER`. Le collecteur ferme alors ses créneaux, écrit
son dernier relevé, puis sort. **11. Relancer ou rejoindre le collecteur** le remet en route.

## Règles du flux

- La journée en cours est collectée mais jamais envoyable : elle se traite le lendemain.
- Elle reste consultable à tout moment, en lecture seule : créneaux déjà enregistrés, état
  du suivi et fraîcheur du dernier relevé, sans jamais pouvoir être corrigée ni envoyée.
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

`-Startup` ajoute le raccourci de session vers le **collecteur** et le démarre tout de suite :
la collecte tourne dès l’installation, sans fenêtre, et repart à chaque ouverture de session.
Le raccourci du menu Démarrer ouvre le **terminal**. Sans `-Startup`, l’installation reste
utilisable : le collecteur démarre à la première ouverture du terminal, mais pas à l’ouverture
de session. Une mise à niveau conserve le choix déjà fait.

Le script installe l’application dans `%LOCALAPPDATA%\Programs\7pace auto` et conserve les
données lors d’une mise à jour ou d’une désinstallation. `uninstall.ps1` arrête le collecteur
puis retire l’application ; `uninstall.ps1 -PurgeData` supprime aussi les réglages, le jeton et
les journées.

Prérequis : Windows 10 ou 11. L’archive publiée inclut le runtime .NET.

## Configuration

Au premier lancement, choisissez **7. Configurer l’application**, puis renseignez :

| Réglage | Utilité |
|---|---|
| Dépôt Git | dossier local dont la branche active est relevée |
| Intervalle de relevé | fréquence du relevé, de 10 à 300 secondes |
| Organisation Azure DevOps | URL utilisée par `az boards` pour trouver le Fix enfant |
| Compte 7pace | sous-domaine `https://<compte>.timehub.7pace.com` |
| Horaires de travail | périodes pendant lesquelles le temps est compté |

Aucun catalogue d’activités n’est tenu par l’application : les numéros des tâches génériques
(réunion, aide, formation…) se saisissent au moment d’attribuer le créneau.

Le jeton se saisit avec **8. Enregistrer ou supprimer le jeton 7pace**. Il est chiffré par
DPAPI pour le compte Windows et ne quitte jamais la machine.

## Vérifier que la collecte tourne

L’entrée **6. Voir la journée en cours (lecture seule)** montre ce qui a déjà été
enregistré aujourd’hui — heures, ticket résolu, libellé, origine du créneau — ainsi que
l’état réel du suivi : branche lue, créneau en cours, date du dernier relevé et dernière
erreur rencontrée. Un suivi qui ne relève plus est annoncé comme figé.

L’écran se rafraîchit toutes les deux secondes et se ferme à la première touche frappée.
Rien n’y est modifiable : la journée en cours se corrige et s’envoie le lendemain matin.

La ligne **Collecteur** du menu répond à la question d’avant : elle donne la version, le
numéro de processus et l’heure de démarrage du collecteur joint, ou dit pourquoi il est
injoignable. En ligne de commande, `SeptPaceAuto.Agent.exe --status` donne la même chose, et
`SeptPaceAuto.Agent.exe --stop` l’arrête proprement.

## Mises à jour

L’entrée **9. Rechercher et installer une mise à jour** télécharge l’archive, puis lance un
programme d’installation qui attend la sortie du collecteur et du terminal avant de toucher
aux fichiers : rien n’est verrouillé pendant le remplacement. L’installation précédente est
mise de côté, et une copie ratée la remet en place plutôt que de laisser un dossier à moitié
remplacé. Le collecteur est relancé ensuite, puis le terminal. Les réglages, le jeton et les
journées ne sont jamais touchés.

Par sécurité, cette action fonctionne uniquement depuis `%LOCALAPPDATA%\Programs\7pace auto`,
avec les deux exécutables présents ; une exécution issue de `dotnet run` doit d’abord être
installée avec `build/install.ps1`.

### Venir d’une version 1.0.x

La mise à jour depuis l’application installe bien les deux exécutables, mais elle ne touche
pas aux raccourcis : celui de session continue d’ouvrir le terminal, qui démarre alors le
collecteur. Le suivi survit dès lors à la fermeture du terminal, ce qui est l’essentiel.
Pour que la session lance directement le collecteur, sans aucune fenêtre, relance une fois
`install.ps1 -Startup` depuis l’archive.

## Développement

```powershell
dotnet run --project src/SeptPaceAuto.Terminal
dotnet run --project src/SeptPaceAuto.Agent
dotnet test tests/SeptPaceAuto.Tests
powershell -File build/publish.ps1
powershell -File build/install.ps1
```

Le cœur métier (`src/SeptPaceAuto.Core`) porte la collecte Git, les journées, Azure DevOps,
l’envoi 7pace, les mises à jour et la liaison locale. Le collecteur
(`src/SeptPaceAuto.Agent`) héberge ce cœur et lui seul écrit. Le terminal
(`src/SeptPaceAuto.Terminal`) est l’unique interface, et n’est qu’un client :
l’ancienne interface graphique est dépréciée.

`SEPTPACE_DATA` déplace le profil de données : tuyau, verrous et journées en suivent. C’est
ainsi que les tests lancent de vrais collecteurs sans toucher à l’installation réelle.

## Licence

MIT, voir [LICENSE](LICENSE).
