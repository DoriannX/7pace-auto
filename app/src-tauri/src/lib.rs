mod agent;

use std::collections::HashMap;
use std::sync::{Arc, Mutex};

use serde_json::Value;
use tauri::{AppHandle, Emitter, Manager, WebviewWindow, WindowEvent};
use tauri_plugin_window_state::{AppHandleExt, StateFlags};

use agent::{AgentError, Endpoint, Link};

/// Une liaison par fenêtre : un envoi de plusieurs minutes dans la fenêtre principale ne
/// bloque pas les relevés du widget.
struct Links {
    endpoint: Arc<Endpoint>,
    by_window: Mutex<HashMap<String, Arc<tokio::sync::Mutex<Link>>>>,
}

impl Links {
    fn of(&self, label: &str) -> Arc<tokio::sync::Mutex<Link>> {
        let mut links = self.by_window.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        links
            .entry(label.to_string())
            .or_insert_with(|| Arc::new(tokio::sync::Mutex::new(Link::new(self.endpoint.clone()))))
            .clone()
    }
}

/// Seule porte vers le collecteur : la méthode du contrat et ses paramètres, tels quels.
#[tauri::command]
async fn agent_call(
    window: WebviewWindow,
    links: tauri::State<'_, Links>,
    method: String,
    params: Option<Value>,
) -> Result<Value, AgentError> {
    let link = links.of(window.label());
    let mut link = link.lock().await;
    link.call(&method, params.unwrap_or(Value::Null)).await
}

#[tauri::command]
fn app_pid() -> u32 {
    std::process::id()
}

#[tauri::command]
fn show_main(app: AppHandle) {
    open_main(&app);
}

#[tauri::command]
fn to_widget(app: AppHandle) {
    fold(&app);
}

#[tauri::command]
fn quit(app: AppHandle) {
    let _ = app.save_window_state(remembered());
    app.exit(0);
}

/// Position et taille sont retenues ; la visibilité, elle, dépend du lancement.
fn remembered() -> StateFlags {
    StateFlags::all() - StateFlags::VISIBLE
}

fn open_main(app: &AppHandle) {
    if let Some(widget) = app.get_webview_window("widget") {
        let _ = widget.hide();
    }
    if let Some(main) = app.get_webview_window("main") {
        let _ = main.show();
        let _ = main.unminimize();
        let _ = main.set_focus();
    }
    let _ = app.emit("shown", ());
}

fn fold(app: &AppHandle) {
    if let Some(main) = app.get_webview_window("main") {
        let _ = main.hide();
    }
    if let Some(widget) = app.get_webview_window("widget") {
        let _ = widget.show();
    }
}

pub fn run() {
    let as_widget = std::env::args().any(|argument| argument == "--widget");

    tauri::Builder::default()
        .plugin(tauri_plugin_single_instance::init(|app, _args, _cwd| open_main(app)))
        .plugin(tauri_plugin_window_state::Builder::default().with_state_flags(remembered()).build())
        .manage(Links {
            endpoint: Arc::new(Endpoint::installed()),
            by_window: Mutex::new(HashMap::new()),
        })
        .invoke_handler(tauri::generate_handler![agent_call, app_pid, show_main, to_widget, quit])
        .setup(move |app| {
            if as_widget {
                fold(app.handle());
            } else {
                open_main(app.handle());
            }
            Ok(())
        })
        .on_window_event(|window, event| {
            // Fermer la fenêtre principale la replie en widget ; le widget ne se ferme que
            // par « Quitter ».
            if let WindowEvent::CloseRequested { api, .. } = event {
                api.prevent_close();
                if window.label() == "main" {
                    fold(window.app_handle());
                }
            }
        })
        .run(tauri::generate_context!())
        .expect("7pace auto n'a pas pu démarrer");
}

