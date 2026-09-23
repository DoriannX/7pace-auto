//! Liaison avec le collecteur de fond (SeptPaceAuto.Agent.exe).
//!
//! Protocole : une ligne JSON UTF-8 par message sur un tuyau nommé réservé au compte
//! Windows, `{v, id, method, params}` à l'aller, `{v, id, ok, result | error, kind}` au
//! retour. Seules les lectures sont rejouées après une coupure : une correction ou un envoi
//! interrompu remonte à l'utilisateur plutôt que d'être renvoyé à l'aveugle.

use std::path::{Path, PathBuf};
use std::process::Stdio;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use serde::Serialize;
use serde_json::{json, Value};
use sha2::{Digest, Sha256};
use tokio::io::{split, AsyncBufReadExt, AsyncWriteExt, BufReader, ReadHalf, WriteHalf};
use tokio::net::windows::named_pipe::{ClientOptions, NamedPipeClient};

pub const PROTOCOL: i64 = 1;
pub const AGENT_EXECUTABLE: &str = "SeptPaceAuto.Agent.exe";

const ERROR_FILE_NOT_FOUND: i32 = 2;
const ERROR_PIPE_BUSY: i32 = 231;
const CREATE_NEW_PROCESS_GROUP: u32 = 0x0000_0200;
const CREATE_NO_WINDOW: u32 = 0x0800_0000;
/// Tokio annonce par défaut le niveau « identification » : le collecteur lit alors le nom du
/// client, le compare à « DOMAINE\compte » et coupe la liaison. Le niveau anonyme reproduit le
/// client .NET ; la liste d'accès du tuyau garantit déjà que seul ce compte s'y connecte.
const SECURITY_ANONYMOUS: u32 = 0;

/// Erreur rendue à l'interface. `kind` vaut domain, protocol, internal ou unreachable ;
/// `message` est déjà la phrase à afficher.
#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
pub struct AgentError {
    pub message: String,
    pub kind: String,
}

impl AgentError {
    fn new(kind: &str, message: impl Into<String>) -> Self {
        Self { message: message.into(), kind: kind.to_string() }
    }

    fn unreachable(message: impl Into<String>) -> Self {
        Self::new("unreachable", message)
    }

    fn protocol(message: impl Into<String>) -> Self {
        Self::new("protocol", message)
    }
}

/// Démarrage du collecteur, isolé pour que les tests n'aient pas à lancer de processus.
pub trait Launcher: Send + Sync {
    fn executable(&self) -> Option<PathBuf>;
    fn launch(&self) -> bool;
}

/// Rendez-vous d'un profil de données, partagé par toutes les liaisons de l'app.
pub struct Endpoint {
    pipe: Box<dyn Fn() -> String + Send + Sync>,
    launcher: Box<dyn Launcher>,
    connect_patience: Duration,
    start_patience: Duration,
    launched_at: Mutex<Option<Instant>>,
    /// Posé par un arrêt demandé : le collecteur n'est plus relancé en silence.
    stopped: AtomicBool,
}

impl Endpoint {
    pub fn new(
        pipe: impl Fn() -> String + Send + Sync + 'static,
        launcher: impl Launcher + 'static,
        start_patience: Duration,
    ) -> Self {
        Self {
            pipe: Box::new(pipe),
            launcher: Box::new(launcher),
            connect_patience: Duration::from_secs(2),
            start_patience,
            launched_at: Mutex::new(None),
            stopped: AtomicBool::new(false),
        }
    }

    /// Profil actif (SEPTPACE_DATA le cas échéant) et collecteur installé à côté de l'app.
    pub fn installed() -> Self {
        let folder = data_folder();
        Self::new(move || pipe_path(&folder), InstalledAgent::find(), Duration::from_secs(25))
    }

    /// Un seul lancement par fenêtre d'attente, même si plusieurs liaisons le demandent.
    fn launch_once(&self) -> bool {
        let mut last = self.launched_at.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        if matches!(*last, Some(at) if at.elapsed() < self.start_patience) {
            return true;
        }
        *last = Some(Instant::now());
        self.launcher.launch()
    }
}

/// Dossier du profil : SEPTPACE_DATA, sinon %LOCALAPPDATA%\7pace-auto.
pub fn data_folder() -> PathBuf {
    if let Ok(declared) = std::env::var("SEPTPACE_DATA") {
        let declared = declared.trim();
        if !declared.is_empty() {
            return std::path::absolute(declared).unwrap_or_else(|_| PathBuf::from(declared));
        }
    }
    PathBuf::from(std::env::var("LOCALAPPDATA").unwrap_or_default()).join("7pace-auto")
}

/// Empreinte du profil, identique à AgentEndpoint.KeyOf côté .NET.
pub fn profile_key(folder: &str) -> String {
    let digest = Sha256::digest(folder.to_lowercase().as_bytes());
    digest[..8].iter().map(|byte| format!("{byte:02X}")).collect()
}

/// Tuyau déclaré par le collecteur dans agent.json, sinon celui que l'empreinte désigne.
pub fn pipe_path(folder: &Path) -> String {
    let declared = std::fs::read_to_string(folder.join("agent.json"))
        .ok()
        .and_then(|text| serde_json::from_str::<Value>(text.trim_start_matches('\u{feff}')).ok())
        .and_then(|info| info.get("pipe").and_then(Value::as_str).map(str::to_string))
        .filter(|name| !name.is_empty());
    let name = declared.unwrap_or_else(|| format!("SeptPaceAuto.agent.{}", profile_key(&folder.to_string_lossy())));
    format!(r"\\.\pipe\{name}")
}

/// Patience accordée par appel, reprise de AgentClient.TimeoutFor.
pub fn timeout_for(method: &str) -> Duration {
    match method {
        "applyUpdate" => Duration::from_secs(15 * 60),
        "pendingDay" | "saveSettings" | "submitDay" | "checkUpdate" => Duration::from_secs(5 * 60),
        "probeRepo" | "probeAzure" | "probeToken" => Duration::from_secs(2 * 60),
        _ if method.starts_with("agent.") => Duration::from_secs(15),
        _ => Duration::from_secs(60),
    }
}

/// Appels sans effet, rejouables après une reconnexion (AgentClient.CanReplay).
pub fn can_replay(method: &str) -> bool {
    matches!(
        method,
        "bootstrap" | "currentDay" | "loadSettings" | "checkUpdate" | "probeRepo" | "probeAzure" | "probeToken"
    ) || method.starts_with("agent.")
}

/// Collecteur installé : SEPTPACE_AGENT, sinon à côté de l'exécutable de l'app.
pub struct InstalledAgent {
    executable: Option<PathBuf>,
}

impl InstalledAgent {
    pub fn find() -> Self {
        if let Ok(declared) = std::env::var("SEPTPACE_AGENT") {
            let path = PathBuf::from(declared);
            if path.is_file() {
                return Self { executable: Some(path) };
            }
        }
        let beside = std::env::current_exe()
            .ok()
            .and_then(|exe| exe.parent().map(|folder| folder.join(AGENT_EXECUTABLE)))
            .filter(|path| path.is_file());
        if beside.is_some() {
            return Self { executable: beside };
        }
        // En développement, le collecteur compilé du dépôt fait l'affaire.
        #[cfg(debug_assertions)]
        {
            let built = Path::new(env!("CARGO_MANIFEST_DIR"))
                .join("../../src/SeptPaceAuto.Agent/bin/Debug/net8.0-windows")
                .join(AGENT_EXECUTABLE);
            if built.is_file() {
                return Self { executable: Some(built) };
            }
        }
        Self { executable: None }
    }
}

impl Launcher for InstalledAgent {
    fn executable(&self) -> Option<PathBuf> {
        self.executable.clone()
    }

    fn launch(&self) -> bool {
        use std::os::windows::process::CommandExt;

        let Some(path) = &self.executable else { return false };
        std::process::Command::new(path)
            .current_dir(path.parent().unwrap_or(Path::new(".")))
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .creation_flags(CREATE_NO_WINDOW | CREATE_NEW_PROCESS_GROUP)
            .spawn()
            .is_ok()
    }
}

struct Conn {
    reader: BufReader<ReadHalf<NamedPipeClient>>,
    writer: WriteHalf<NamedPipeClient>,
}

enum Failure {
    Broken,
    Timeout,
    Protocol(AgentError),
}

enum Open {
    Missing,
    Failed(String),
}

/// Une connexion au collecteur, rouverte à la demande.
pub struct Link {
    endpoint: Arc<Endpoint>,
    conn: Option<Conn>,
    next_id: i64,
}

impl Link {
    pub fn new(endpoint: Arc<Endpoint>) -> Self {
        Self { endpoint, conn: None, next_id: 0 }
    }

    pub async fn call(&mut self, method: &str, params: Value) -> Result<Value, AgentError> {
        let params = if params.is_object() { params } else { json!({}) };

        // Méthode locale : lève l'arrêt demandé et rejoint (ou relance) le collecteur.
        if method == "agent.start" {
            self.endpoint.stopped.store(false, Ordering::SeqCst);
            self.ensure().await?;
            return Ok(json!({ "ok": true, "message": "Suivi en arrière-plan relancé." }));
        }

        self.ensure().await?;
        let reply = match self.exchange(method, &params).await {
            Ok(reply) => reply,
            Err(Failure::Broken) if can_replay(method) => {
                self.conn = None;
                self.ensure().await?;
                match self.exchange(method, &params).await {
                    Ok(reply) => reply,
                    Err(failure) => return Err(self.fail(method, failure)),
                }
            }
            Err(failure) => return Err(self.fail(method, failure)),
        };

        if method == "agent.stop" && reply.is_ok() {
            self.endpoint.stopped.store(true, Ordering::SeqCst);
            self.conn = None;
        }
        reply
    }

    fn fail(&mut self, method: &str, failure: Failure) -> AgentError {
        self.conn = None;
        match failure {
            Failure::Broken if can_replay(method) => AgentError::unreachable("Le collecteur ne répond plus."),
            Failure::Broken => AgentError::unreachable(format!(
                "La liaison avec le collecteur s’est coupée pendant « {method} » : rien n’a été renvoyé, vérifie puis recommence."
            )),
            Failure::Timeout => AgentError::unreachable(format!("Le collecteur n’a pas répondu à « {method} » dans le délai prévu.")),
            Failure::Protocol(error) => error,
        }
    }

    async fn ensure(&mut self) -> Result<(), AgentError> {
        if self.conn.is_some() {
            return Ok(());
        }
        let endpoint = self.endpoint.clone();
        match open(&(endpoint.pipe)(), endpoint.connect_patience).await {
            Ok(client) => return self.adopt(client).await,
            Err(Open::Missing) => {}
            Err(Open::Failed(message)) => return Err(AgentError::unreachable(message)),
        }

        if endpoint.stopped.load(Ordering::SeqCst) {
            return Err(AgentError::unreachable("Suivi arrêté : relance-le depuis les réglages."));
        }
        if endpoint.launcher.executable().is_none() {
            return Err(AgentError::unreachable(format!(
                "{AGENT_EXECUTABLE} est introuvable à côté de l’app : réinstalle-la avec build\\install.ps1."
            )));
        }
        if !endpoint.launch_once() {
            return Err(AgentError::unreachable("Le collecteur n’a pas pu être lancé."));
        }

        let deadline = Instant::now() + endpoint.start_patience;
        loop {
            tokio::time::sleep(Duration::from_millis(200)).await;
            match open(&(endpoint.pipe)(), Duration::from_millis(500)).await {
                Ok(client) => return self.adopt(client).await,
                Err(Open::Missing) if Instant::now() < deadline => continue,
                Err(Open::Missing) => {
                    return Err(AgentError::unreachable(format!(
                        "Collecteur lancé, mais toujours muet après {} s.",
                        endpoint.start_patience.as_secs_f32().round()
                    )))
                }
                Err(Open::Failed(message)) => return Err(AgentError::unreachable(message)),
            }
        }
    }

    /// Adopte un tuyau ouvert : le collecteur doit se présenter dans la bonne version.
    async fn adopt(&mut self, client: NamedPipeClient) -> Result<(), AgentError> {
        let (reader, writer) = split(client);
        self.conn = Some(Conn { reader: BufReader::new(reader), writer });
        match self.exchange("agent.hello", &json!({})).await {
            Ok(Ok(info)) => {
                let protocol = info.get("protocol").and_then(Value::as_i64).unwrap_or(PROTOCOL);
                if protocol == PROTOCOL {
                    // Joint : un prochain collecteur disparu pourra être relancé tout de suite.
                    *self.endpoint.launched_at.lock().unwrap_or_else(|poisoned| poisoned.into_inner()) = None;
                    return Ok(());
                }
                self.conn = None;
                Err(AgentError::protocol(incompatible(protocol)))
            }
            Ok(Err(error)) => {
                self.conn = None;
                Err(error)
            }
            Err(failure) => Err(self.fail("agent.hello", failure)),
        }
    }

    async fn exchange(&mut self, method: &str, params: &Value) -> Result<Result<Value, AgentError>, Failure> {
        self.next_id += 1;
        let id = self.next_id;
        let Some(conn) = self.conn.as_mut() else { return Err(Failure::Broken) };

        let mut line = json!({ "v": PROTOCOL, "id": id, "method": method, "params": params }).to_string();
        line.push('\n');

        let io = async {
            conn.writer.write_all(line.as_bytes()).await.map_err(|_| Failure::Broken)?;
            conn.writer.flush().await.map_err(|_| Failure::Broken)?;
            let mut reply = String::new();
            let read = conn.reader.read_line(&mut reply).await.map_err(|_| Failure::Broken)?;
            if read == 0 {
                return Err(Failure::Broken);
            }
            Ok(reply)
        };
        let reply = tokio::time::timeout(timeout_for(method), io).await.map_err(|_| Failure::Timeout)??;
        parse_reply(&reply, id)
    }
}

fn incompatible(version: i64) -> String {
    format!(
        "Protocole incompatible : le collecteur parle la version {version}, l’app la version {PROTOCOL}. Relance l’installation puis réessaie."
    )
}

fn parse_reply(line: &str, id: i64) -> Result<Result<Value, AgentError>, Failure> {
    let unreadable = || Failure::Protocol(AgentError::protocol("Message illisible reçu du collecteur."));
    let root: Value = serde_json::from_str(line.trim()).map_err(|_| unreadable())?;
    let reply = root.as_object().ok_or_else(unreadable)?;

    let version = reply.get("v").and_then(Value::as_i64).unwrap_or(0);
    if version != PROTOCOL {
        return Err(Failure::Protocol(AgentError::protocol(incompatible(version))));
    }

    let ok = reply.get("ok").and_then(Value::as_bool) == Some(true);
    let error = || {
        let message = reply
            .get("error")
            .and_then(Value::as_str)
            .unwrap_or("Le collecteur a refusé l’appel sans préciser pourquoi.");
        let kind = reply.get("kind").and_then(Value::as_str).unwrap_or("internal");
        AgentError::new(kind, message)
    };

    if reply.get("id").and_then(Value::as_i64) != Some(id) {
        // Un refus sans identifiant vient d'un appel que le collecteur n'a pas su lire.
        if !ok && reply.get("id").and_then(Value::as_i64) == Some(0) {
            return Err(Failure::Protocol(error()));
        }
        return Err(Failure::Protocol(AgentError::protocol("Réponse inattendue du collecteur : la liaison est réinitialisée.")));
    }

    if ok {
        return Ok(Ok(reply.get("result").cloned().unwrap_or_else(|| json!({}))));
    }
    Ok(Err(error()))
}

async fn open(pipe: &str, patience: Duration) -> Result<NamedPipeClient, Open> {
    let started = Instant::now();
    loop {
        match ClientOptions::new().security_qos_flags(SECURITY_ANONYMOUS).open(pipe) {
            Ok(client) => return Ok(client),
            Err(error) if error.raw_os_error() == Some(ERROR_PIPE_BUSY) => {
                if started.elapsed() >= patience {
                    return Err(Open::Failed("Le collecteur est occupé : réessaie dans un instant.".into()));
                }
                tokio::time::sleep(Duration::from_millis(50)).await;
            }
            Err(error) if error.raw_os_error() == Some(ERROR_FILE_NOT_FOUND) => return Err(Open::Missing),
            Err(error) => return Err(Open::Failed(format!("Liaison impossible avec le collecteur : {error}"))),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::AtomicUsize;
    use tokio::net::windows::named_pipe::{NamedPipeServer, ServerOptions};

    static NEXT: AtomicUsize = AtomicUsize::new(0);

    fn unique_pipe() -> String {
        format!(r"\\.\pipe\septpace-test-{}-{}", std::process::id(), NEXT.fetch_add(1, Ordering::SeqCst))
    }

    struct NoAgent {
        found: bool,
    }

    impl Launcher for NoAgent {
        fn executable(&self) -> Option<PathBuf> {
            self.found.then(|| PathBuf::from("fictif.exe"))
        }

        fn launch(&self) -> bool {
            true
        }
    }

    fn link(pipe: &str, found: bool, patience: Duration) -> Link {
        let name = pipe.to_string();
        Link::new(Arc::new(Endpoint::new(move || name.clone(), NoAgent { found }, patience)))
    }

    type Script = Arc<dyn Fn(&Value, usize) -> Option<Value> + Send + Sync>;

    /// Faux collecteur : `script` reçoit chaque appel et le rang de la connexion ; None
    /// coupe la connexion sans répondre.
    async fn serve(pipe: &str, script: Script) -> Arc<AtomicUsize> {
        let calls = Arc::new(AtomicUsize::new(0));
        let mut server = ServerOptions::new().first_pipe_instance(true).create(pipe).unwrap();
        let (pipe, counter) = (pipe.to_string(), calls.clone());
        tokio::spawn(async move {
            let mut connection = 0;
            loop {
                if server.connect().await.is_err() {
                    return;
                }
                let connected = server;
                server = ServerOptions::new().create(&pipe).unwrap();
                tokio::spawn(handle(connected, script.clone(), connection, counter.clone()));
                connection += 1;
            }
        });
        calls
    }

    async fn handle(server: NamedPipeServer, script: Script, connection: usize, calls: Arc<AtomicUsize>) {
        let (reader, mut writer) = split(server);
        let mut lines = BufReader::new(reader).lines();
        while let Ok(Some(line)) = lines.next_line().await {
            let call: Value = serde_json::from_str(&line).unwrap();
            if call["method"] != "agent.hello" {
                calls.fetch_add(1, Ordering::SeqCst);
            }
            let Some(mut reply) = script(&call, connection) else { return };
            if reply.get("id").is_none() {
                reply["id"] = call["id"].clone();
            }
            writer.write_all(format!("{reply}\n").as_bytes()).await.unwrap();
        }
    }

    fn ok(result: Value) -> Option<Value> {
        Some(json!({ "v": 1, "ok": true, "result": result }))
    }

    fn hello_or(call: &Value, otherwise: impl FnOnce() -> Option<Value>) -> Option<Value> {
        if call["method"] == "agent.hello" {
            ok(json!({ "protocol": 1, "pid": 1 }))
        } else {
            otherwise()
        }
    }

    #[test]
    fn empreinte_identique_au_collecteur() {
        assert_eq!(profile_key(r"C:\Temp\7pace"), "BD59DB4210029A2A");
        assert_eq!(profile_key(r"c:\temp\7PACE"), "BD59DB4210029A2A");
    }

    #[tokio::test]
    async fn se_presente_puis_rend_le_resultat() {
        let pipe = unique_pipe();
        serve(&pipe, Arc::new(|call: &Value, _: usize| hello_or(call, || ok(json!({ "echo": call["params"]["x"] }))))).await;
        let mut link = link(&pipe, true, Duration::from_secs(1));
        assert_eq!(link.call("currentDay", json!({ "x": 7 })).await, Ok(json!({ "echo": 7 })));
    }

    #[tokio::test]
    async fn rend_les_refus_du_collecteur_avec_leur_nature() {
        let pipe = unique_pipe();
        serve(
            &pipe,
            Arc::new(|call: &Value, _: usize| {
                hello_or(call, || {
                    let kind = if call["method"] == "saveEntry" { "domain" } else { "protocol" };
                    Some(json!({ "v": 1, "ok": false, "error": "Refusé.", "kind": kind }))
                })
            }),
        )
        .await;
        let mut link = link(&pipe, true, Duration::from_secs(1));
        assert_eq!(link.call("saveEntry", json!({})).await.unwrap_err(), AgentError::new("domain", "Refusé."));
        assert_eq!(link.call("bootstrap", json!({})).await.unwrap_err().kind, "protocol");
    }

    #[tokio::test]
    async fn refuse_une_autre_version_du_protocole() {
        let pipe = unique_pipe();
        serve(&pipe, Arc::new(|_: &Value, _: usize| Some(json!({ "v": 2, "ok": true, "result": {} })))).await;
        let mut link = link(&pipe, true, Duration::from_secs(1));
        let error = link.call("currentDay", json!({})).await.unwrap_err();
        assert_eq!(error.kind, "protocol");
        assert!(error.message.contains("version 2"));
    }

    #[tokio::test]
    async fn ne_rejoue_que_les_lectures_apres_une_coupure() {
        let pipe = unique_pipe();
        // Première connexion : coupée au premier appel. Les suivantes répondent.
        let calls = serve(&pipe, Arc::new(|call: &Value, connection: usize| hello_or(call, || (connection > 0).then(|| json!({ "v": 1, "ok": true, "result": "lu" }))))).await;

        let mut reader = link(&pipe, true, Duration::from_secs(1));
        assert_eq!(reader.call("currentDay", json!({})).await, Ok(json!("lu")));
        assert_eq!(calls.load(Ordering::SeqCst), 2);

        let pipe = unique_pipe();
        let calls = serve(&pipe, Arc::new(|call: &Value, connection: usize| hello_or(call, || (connection > 0).then(|| json!({ "v": 1, "ok": true, "result": {} }))))).await;
        let mut writer = link(&pipe, true, Duration::from_secs(1));
        let error = writer.call("saveEntry", json!({})).await.unwrap_err();
        assert_eq!(error.kind, "unreachable");
        assert_eq!(calls.load(Ordering::SeqCst), 1);
    }

    #[tokio::test]
    async fn injoignable_sans_collecteur_installe() {
        let mut link = link(&unique_pipe(), false, Duration::from_secs(1));
        let error = link.call("currentDay", json!({})).await.unwrap_err();
        assert_eq!(error.kind, "unreachable");
        assert!(error.message.contains(AGENT_EXECUTABLE));
    }

    #[tokio::test]
    async fn injoignable_quand_le_collecteur_lance_reste_muet() {
        let mut link = link(&unique_pipe(), true, Duration::from_millis(600));
        let started = Instant::now();
        let error = link.call("currentDay", json!({})).await.unwrap_err();
        assert_eq!(error.kind, "unreachable");
        assert!(started.elapsed() >= Duration::from_millis(600));
    }

    #[tokio::test]
    async fn un_arret_demande_empeche_la_relance_silencieuse() {
        let pipe = unique_pipe();
        serve(&pipe, Arc::new(|call: &Value, _: usize| hello_or(call, || ok(json!({ "ok": true, "message": "arrêté" }))))).await;
        let mut link = link(&pipe, true, Duration::from_secs(1));
        link.call("agent.stop", json!({})).await.unwrap();
        assert!(link.endpoint.stopped.load(Ordering::SeqCst));
    }

    #[tokio::test]
    async fn une_liaison_etablie_rearme_la_relance() {
        let pipe = unique_pipe();
        serve(&pipe, Arc::new(|call: &Value, _: usize| hello_or(call, || ok(json!({}))))).await;
        let mut link = link(&pipe, true, Duration::from_secs(25));
        assert!(link.endpoint.launch_once());
        link.call("currentDay", json!({})).await.unwrap();
        assert!(link.endpoint.launched_at.lock().unwrap().is_none());
    }
}




