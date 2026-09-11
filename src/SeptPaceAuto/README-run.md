# Lancer 7pace auto

## Exécution

```powershell
dotnet run --project src/SeptPaceAuto
```

Prérequis : .NET 8 (SDK installé) et le runtime WebView2, présent d'origine sur Windows 11.
L'interface (`web/`) est copiée à côté de l'exécutable et servie sous `https://app.7pace.local/`.

La fenêtre complète s'ouvre au démarrage. La vue mini reste au-dessus des autres fenêtres,
n'apparaît pas dans la barre des tâches et se déplace en glissant n'importe où dedans.
Une seule fenêtre est visible à la fois ; relancer l'application ramène simplement celle déjà ouverte.
Fermer la fenêtre visible quitte l'application.

## Où sont les données

Tout est local, dans `%LOCALAPPDATA%\7pace-auto` :

- `windows.json` : taille et position des deux fenêtres ;
- `webview\` : profil WebView2 (cache de l'interface) ;
- `microsoft.json` : état de l'inscription d'application Microsoft ;
- le jeton 7pace est saisi dans les réglages de l'application et stocké chiffré par DPAPI dans `token.bin` ; un ancien `%LOCALAPPDATA%\vault-7pace.jeton` reste lu s'il existe.

## Envoi vers 7pace

Rien n'est envoyé à 7pace sans validation explicite de la journée depuis l'application.
Aucun bloc non attribué n'est envoyé. Un envoi refusé est signalé comme un échec : il n'est
jamais présenté comme transmis. L'intégration du calendrier Outlook n'est pas connectée
(autorisation administrateur en attente).

Aucune surveillance des applications, des frappes ni de l'inactivité.
