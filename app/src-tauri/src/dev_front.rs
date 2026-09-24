//! Front de développement : quand `dev-front.json` désigne le dossier `app` du dépôt, l'app
//! installée affiche le front servi par Vite, rechargé à chaud, au lieu de celui qu'elle
//! embarque. Sans ce fichier, rien ne change.

use std::fs::File;
use std::net::{SocketAddr, TcpStream};
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::time::{Duration, Instant};

use serde_json::Value;
use tauri::ipc::CapabilityBuilder;
use tauri::{AppHandle, Manager, Url};

pub const ORIGIN: &str = "http://127.0.0.1:1420";
const ADDRESS: ([u8; 4], u16) = ([127, 0, 0, 1], 1420);
const PATIENCE: Duration = Duration::from_secs(20);

/// Mêmes droits que capabilities/default.json, accordés à l'origine de Vite.
const PERMISSIONS: [&str; 7] = [
    "core:default",
    "core:window:allow-start-dragging",
    "allow-agent-call",
    "allow-app-pid",
    "allow-show-main",
    "allow-to-widget",
    "allow-quit",
];

const CREATE_NEW_PROCESS_GROUP: u32 = 0x0000_0200;
const CREATE_NO_WINDOW: u32 = 0x0800_0000;
const CREATE_BREAKAWAY_FROM_JOB: u32 = 0x0100_0000;

/// Dossier app déclaré dans dev-front.json, s'il contient bien un package.json.
pub fn configured(data: &Path) -> Option<PathBuf> {
    let text = std::fs::read_to_string(data.join("dev-front.json")).ok()?;
    let config: Value = serde_json::from_str(text.trim_start_matches('\u{feff}')).ok()?;
    let app = PathBuf::from(config.get("app")?.as_str()?);
    app.join("package.json").is_file().then_some(app)
}

fn reachable() -> bool {
    TcpStream::connect_timeout(&SocketAddr::from(ADDRESS), Duration::from_millis(300)).is_ok()
}

/// Lance Vite sans fenêtre ; il survit à l'app pour rester disponible au navigateur.
fn launch(app: &Path, log: &Path) -> bool {
    use std::os::windows::process::CommandExt;

    let Ok(output) = File::create(log) else { return false };
    let attempt = |flags: u32| -> std::io::Result<()> {
        Command::new("cmd")
            .args(["/d", "/c", "corepack pnpm dev"])
            .current_dir(app)
            .stdin(Stdio::null())
            .stdout(output.try_clone()?)
            .stderr(output.try_clone()?)
            .creation_flags(flags)
            .spawn()
            .map(drop)
    };
    let detached = CREATE_NO_WINDOW | CREATE_NEW_PROCESS_GROUP;
    attempt(detached | CREATE_BREAKAWAY_FROM_JOB).or_else(|_| attempt(detached)).is_ok()
}

/// Bascule les deux fenêtres sur Vite dès qu'il répond ; sinon le front intégré reste.
pub fn attach(app: &AppHandle) {
    if tauri::is_dev() {
        return;
    }
    let data = crate::agent::data_folder();
    let Some(folder) = configured(&data) else { return };
    let app = app.clone();
    std::thread::spawn(move || {
        if !reachable() {
            if !launch(&folder, &data.join("vite.log")) {
                return;
            }
            let deadline = Instant::now() + PATIENCE;
            while !reachable() {
                if Instant::now() >= deadline {
                    return;
                }
                std::thread::sleep(Duration::from_millis(250));
            }
        }
        let capability = PERMISSIONS.iter().fold(
            CapabilityBuilder::new("dev-front").remote(format!("{ORIGIN}/*")).local(false).windows(["main", "widget"]),
            |capability, permission| capability.permission(*permission),
        );
        if app.add_capability(capability).is_err() {
            return;
        }
        for (label, page) in [("main", "index.html"), ("widget", "widget.html")] {
            if let (Some(window), Ok(url)) = (app.get_webview_window(label), Url::parse(&format!("{ORIGIN}/{page}"))) {
                let _ = window.navigate(url);
            }
        }
    });
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicUsize, Ordering};

    fn scratch() -> PathBuf {
        static NEXT: AtomicUsize = AtomicUsize::new(0);
        let folder = std::env::temp_dir()
            .join(format!("septpace-dev-front-{}-{}", std::process::id(), NEXT.fetch_add(1, Ordering::SeqCst)));
        std::fs::create_dir_all(&folder).unwrap();
        folder
    }

    fn declare(data: &Path, app: &Path) {
        let text = format!("\u{feff}{}", serde_json::json!({ "app": app }));
        std::fs::write(data.join("dev-front.json"), text).unwrap();
    }

    #[test]
    fn sans_fichier_le_front_integre_reste() {
        assert_eq!(configured(&scratch()), None);
    }

    #[test]
    fn un_fichier_illisible_est_ignore() {
        let data = scratch();
        std::fs::write(data.join("dev-front.json"), "{ app: ").unwrap();
        assert_eq!(configured(&data), None);
    }

    #[test]
    fn un_dossier_sans_package_json_est_ignore() {
        let data = scratch();
        declare(&data, &data.join("absent"));
        assert_eq!(configured(&data), None);
    }

    #[test]
    fn designe_le_dossier_app_du_depot() {
        let (data, app) = (scratch(), scratch());
        std::fs::write(app.join("package.json"), "{}").unwrap();
        declare(&data, &app);
        assert_eq!(configured(&data), Some(app));
    }
}
