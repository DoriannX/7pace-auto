fn main() {
    // Commandes déclarées pour que le front de Vite (origine distante) puisse y être autorisé.
    let commands = tauri_build::AppManifest::new().commands(&["agent_call", "app_pid", "show_main", "to_widget", "quit"]);
    tauri_build::try_build(tauri_build::Attributes::new().app_manifest(commands)).expect("échec de tauri-build");
}
