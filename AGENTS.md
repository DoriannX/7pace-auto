L'app installée affiche le front du dépôt, servi par Vite sur http://127.0.0.1:1420 et rechargé à chaud (`dev-front.json`, voir README). Une modification dans `app/src` s'y voit sans rien relancer.

Pour tester l'interface, ouvre http://127.0.0.1:1420/index.html et /widget.html dans le navigateur intégré : les données y sont fictives (`app/src/lib/fixtures`). Si le port ne répond pas, lance `corepack pnpm dev` dans `app`.

Une modification Rust (`app/src-tauri`) ou .NET (`src`) ne s'applique qu'après `build/publish.ps1` puis `build/install.ps1`. Le front ne doit pas appeler une nouvelle commande Tauri avant cette réinstallation.
