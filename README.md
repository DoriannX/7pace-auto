# 7pace auto

Application Windows qui collecte le temps passé sur la branche Git active, puis fait
corriger et envoyer la journée terminée dans
[7pace Timetracker](https://www.7pace.com/) le lendemain matin.

Elle tient en deux morceaux : un **collecteur** sans fenêtre, qui relève le temps toute la
journée, et une **app** de bureau qui s’y connecte. L’app se présente comme un **widget**
d’une ligne, toujours au premier plan, qui se déplie en **fenêtre** de correction. Quitter
l’app ne coupe jamais la collecte.

Elle ne remplace pas 7pace : elle n’y lit rien, n’affiche aucun historique et ne permet
aucune correction après envoi. Une journée envoyée appartient à 7pace.

## Fonctionnement

- Le collecteur relève la branche du dépôt surveillé pendant les horaires configurés.
- Rapproche le Bug ou PBI trouvé dans la branche de son Fix ou de sa Task via `az boards`.
- Un chrono rapide facultatif ouvre un créneau « à attribuer », même pendant un ticket suivi.
- Le widget montre en permanence le ticket suivi, son chrono et l’état de la collecte.
- Le lendemain matin, la fenêtre s’ouvre d’elle-même sur la plus ancienne journée en attente.
- Elle montre cette journée sur une frise horaire : créneaux, trous, chevauchements et total.
- Après correction, l’envoi écrit un worklog par créneau puis clôt la journée.

Le suivi repose uniquement sur la branche Git active. Aucune application, frappe, navigation
ou période d’inactivité n’est observée. Les journées en attente restent dans
`%LOCALAPPDATA%\7pace-auto\days` ; une journée envoyée n’y laisse que sa date.

Le détail du flux et ses critères d’acceptation : [docs/specs/daily-adjust-submit.md](docs/specs/daily-adjust-submit.md).
Le vocabulaire du domaine : [CONTEXT.md](CONTEXT.md).

## Collecteur et app

`SeptPaceAuto.Agent.exe` est le collecteur : il n’a ni fenêtre ni icône, il est seul à relever
la branche et à écrire les journées. Il tourne tant que la session Windows est ouverte.

`SeptPaceAuto.App.exe` est l’interface. Elle ne collecte rien : elle interroge le collecteur
par une liaison locale réservée au compte Windows, affiche ce qu’il sait et lui transmet les
corrections. Elle lance le collecteur s’il ne tourne pas, et le relance s’il disparaît.

Le widget tient en une ligne : une pastille verte quand le suivi tourne, ambre hors horaires,
rouge en cas de panne ou de collecteur injoignable ; le ticket suivi ; le chrono du créneau en
cours ; le bouton du chrono rapide. Il se déplace par sa poignée `⋮` et garde sa place. Un
clic l’ouvre en fenêtre, un clic droit propose Ouvrir et Quitter.

Dans la fenêtre, on glisse un créneau pour le déplacer et ses bords pour l’étirer, au pas de
5 minutes ; glisser dans le vide crée un créneau, cliquer un trou le comble. Le créneau
sélectionné se corrige sous la frise, avec les tickets de la journée proposés en un clic.
« Envoyer » et « Ignorer » partent d’un appui maintenu d’une seconde. « Aujourd’hui » montre
la journée en cours, en lecture seule. Fermer la fenêtre la replie en widget.

Arrêter réellement la collecte se fait dans les réglages (icône en haut à droite) : **Arrêter**,
par appui maintenu. Le collecteur ferme alors ses créneaux, écrit son dernier relevé, puis
sort, et l’app ne le relance plus jusqu’à **Relancer**.

## Règles du flux

- La journée en cours est collectée mais jamais envoyable : elle se traite le lendemain.
- Elle reste consultable à tout moment, en lecture seule, sans jamais pouvoir être corrigée
  ni envoyée.
- Les journées en attente sont proposées de la plus ancienne à la plus récente.
- Un créneau « à attribuer », ou un chrono rapide encore ouvert, bloque tout l’envoi.
- Les chevauchements sont autorisés et signalés : 7pace accepte des imputations simultanées.
- Les trous dans les horaires prévus sont signalés mais n’interdisent pas l’envoi.
- Un envoi partiel verrouille les créneaux acceptés ; les refusés restent modifiables.
- « Ignorer » clôt la journée localement, sans aucun appel 7pace.

## Installation

Téléchargez `SeptPaceAuto-win-x64.zip` depuis la dernière
[release](../../releases/latest), extrayez l’archive, puis lancez :

```powershell
.\install.ps1 -Startup
```

`-Startup` ajoute le raccourci de session qui ouvre l’app en widget ; elle lance le
collecteur. Le raccourci du menu Démarrer ouvre la fenêtre. Sans `-Startup`, le collecteur
démarre à la première ouverture de l’app. Une mise à niveau conserve le choix déjà fait.

Le script installe l’application dans `%LOCALAPPDATA%\Programs\7pace auto` et conserve les
données lors d’une mise à jour ou d’une désinstallation. `uninstall.ps1` arrête le collecteur
puis retire l’application ; `uninstall.ps1 -PurgeData` supprime aussi les réglages, le jeton et
les journées.

Prérequis : Windows 10 ou 11 avec le runtime WebView2 (présent d’office sur Windows 11) et la
police JetBrainsMono Nerd Font. L’archive inclut le runtime .NET.

La mise à jour automatique d’une version 1.x (terminal) refuse cette archive : installez-la
une fois avec `install.ps1`, qui ferme l’ancien terminal et reprend le démarrage de session.

## Configuration

Au premier lancement, ouvrez les réglages et renseignez :

| Réglage | Utilité |
|---|---|
| Dépôt Git | dossier local dont la branche active est relevée |
| Organisation Azure | URL utilisée par `az boards` pour trouver le Fix enfant |
| Compte 7pace | sous-domaine `https://<compte>.timehub.7pace.com` |
| Jeton 7pace | chiffré par DPAPI pour le compte Windows, il ne quitte jamais la machine |
| Horaires | périodes pendant lesquelles le temps est compté |

Chaque champ se vérifie sur place. Aucun catalogue d’activités n’est tenu par l’application :
les numéros des tâches génériques (réunion, aide, formation…) se saisissent au moment
d’attribuer le créneau.

## Vérifier que la collecte tourne

Le widget passe au rouge dès que le suivi est figé, que le dépôt ne se lit plus, ou que le
collecteur ne répond plus depuis 10 secondes. « Aujourd’hui » montre ce qui a déjà été
enregistré dans la journée. En ligne de commande, `SeptPaceAuto.Agent.exe --status` décrit
le collecteur en place et `SeptPaceAuto.Agent.exe --stop` l’arrête proprement.

## Mises à jour

Dans les réglages, **Version › Vérifier** puis **Installer** télécharge l’archive et lance un
programme d’installation qui attend la sortie du collecteur et de l’app avant de toucher aux
fichiers. L’installation précédente est mise de côté, et une copie ratée la remet en place.
Le collecteur est relancé, puis l’app en widget. Les réglages, le jeton et les journées ne
sont jamais touchés.

Cette action fonctionne uniquement depuis `%LOCALAPPDATA%\Programs\7pace auto`, avec les
deux exécutables présents.

## Développement

Prérequis : SDK .NET 8, Node.js avec corepack, Rust `stable-x86_64-pc-windows-msvc` (rustup)
et les Build Tools Visual Studio 2022 avec la charge C++.

```powershell
dotnet build src/SeptPaceAuto.Agent      # collecteur lancé par l’app en développement
cd app
corepack pnpm install
corepack pnpm tauri dev                   # app réelle, front rechargé à chaud
corepack pnpm dev                         # front seul dans le navigateur, données fictives
corepack pnpm test
cargo test --manifest-path src-tauri/Cargo.toml
cd ..
dotnet test tests/SeptPaceAuto.Tests
powershell -File build/publish.ps1
```

Le cœur métier (`src/SeptPaceAuto.Core`) porte la collecte Git, les journées, Azure DevOps,
l’envoi 7pace, les mises à jour et la liaison locale. Le collecteur
(`src/SeptPaceAuto.Agent`) héberge ce cœur et lui seul écrit. L’app (`app/`) est un front
Svelte 5 dans Tauri 2 ; sa partie Rust (`app/src-tauri`) ne fait que parler au collecteur et
gérer les deux fenêtres.

En `tauri dev`, l’app rejoint le collecteur du profil actif, ou lance celui compilé dans
`src/SeptPaceAuto.Agent/bin/Debug`. `SEPTPACE_DATA` déplace le profil de données : tuyau,
verrous et journées en suivent, ce qui isole un essai de l’installation réelle.

## Licence

MIT, voir [LICENSE](LICENSE).
