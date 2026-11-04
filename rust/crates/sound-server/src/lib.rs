//! Authenticated v1 API. Network/disk work never owns the output callback.
pub mod engine;
pub mod settings;

use axum::{
    Json, Router,
    extract::{
        DefaultBodyLimit, Path, State, WebSocketUpgrade,
        ws::{CloseFrame, Message, WebSocket},
    },
    http::{Request, StatusCode},
    middleware::{self, Next},
    response::{IntoResponse, Response},
    routing::{any, get, patch, put},
};
use engine::{Engine, Ingress};
use serde::de::DeserializeOwned;
use settings::SettingsStore;
use sound_core::{MixSettings, SourceSettings};
use sound_protocol::{AudioHello, KEEPALIVE, MAX_HELLO, MAX_MESSAGE, SourceSettingsPatch};
use std::{
    collections::HashMap,
    net::SocketAddr,
    path::PathBuf,
    sync::{Arc, Mutex as StdMutex},
    time::Duration,
};
use subtle::ConstantTimeEq;
use tokio::{net::TcpListener, sync::Mutex, task::JoinHandle};
use tokio_util::sync::CancellationToken;
use uuid::Uuid;

struct Session {
    device_id: Option<Uuid>,
    settings: SourceSettings,
    stop: CancellationToken,
}
#[derive(Default)]
struct Registry {
    sessions: HashMap<Uuid, Session>,
    updating: bool,
}
#[derive(Clone)]
pub struct Hub {
    engine: Engine,
    key: Arc<str>,
    registry: Arc<Mutex<Registry>>,
    saved: Arc<StdMutex<SettingsStore>>,
    stop: CancellationToken,
}
pub struct RunningServer {
    pub hub: Hub,
    pub address: SocketAddr,
    task: Option<JoinHandle<std::io::Result<()>>>,
}
impl Drop for RunningServer {
    fn drop(&mut self) {
        self.hub.stop.cancel();
    }
}
impl RunningServer {
    pub async fn bind(
        engine: Engine,
        key: String,
        address: SocketAddr,
        settings: Option<PathBuf>,
    ) -> std::io::Result<Self> {
        if key.len() < 24 || key.len() > 512 {
            return Err(std::io::Error::other(
                "Connection key must contain 24-512 bytes",
            ));
        }
        let saved = SettingsStore::load(settings)?;
        let hub = Hub {
            engine,
            key: key.into(),
            registry: Arc::new(Mutex::new(Registry::default())),
            saved: Arc::new(StdMutex::new(saved)),
            stop: CancellationToken::new(),
        };
        let router = Router::new()
            .route("/v1/state", get(state))
            .route("/v1/mix", put(set_mix))
            .route(
                "/v1/sources/{id}",
                put(put_source).merge(patch(patch_source)),
            )
            .route("/v1/audio", any(audio))
            .route("/v1/browser-audio", any(browser_audio))
            .layer(DefaultBodyLimit::max(MAX_HELLO))
            .layer(middleware::from_fn_with_state(hub.clone(), authenticate))
            .with_state(hub.clone());
        let listener = TcpListener::bind(address).await?;
        let address = listener.local_addr()?;
        let cancel = hub.stop.clone();
        let task = tokio::spawn(async move {
            axum::serve(listener, router)
                .with_graceful_shutdown(cancel.cancelled_owned())
                .await
        });
        Ok(Self {
            hub,
            address,
            task: Some(task),
        })
    }
    pub async fn stop(&mut self) {
        self.hub.stop.cancel();
        if let Some(mut task) = self.task.take()
            && tokio::time::timeout(Duration::from_secs(3), &mut task)
                .await
                .is_err()
        {
            task.abort();
            let _ = task.await;
        }
    }
}
impl Hub {
    pub fn engine(&self) -> Engine {
        self.engine.clone()
    }
    pub async fn begin_update(&self, require_idle: bool) -> bool {
        let mut registry = self.registry.lock().await;
        if require_idle && !registry.sessions.is_empty() {
            return false;
        }
        registry.updating = true;
        true
    }
    pub async fn cancel_update(&self) {
        self.registry.lock().await.updating = false;
    }
    async fn change_source(&self, id: Uuid, patch: SourceSettingsPatch) -> StatusCode {
        let mut registry = self.registry.lock().await;
        let Some(session) = registry.sessions.get_mut(&id) else {
            return StatusCode::NOT_FOUND;
        };
        let Ok(next) = patch.apply(&session.settings) else {
            return StatusCode::BAD_REQUEST;
        };
        if let Some(device_id) = session.device_id {
            let saved = self.saved.clone();
            let value = next.clone();
            let result = tokio::task::spawn_blocking(move || {
                saved
                    .lock()
                    .map_err(|_| std::io::Error::other("Settings lock failed"))?
                    .save(device_id, value)
            })
            .await;
            if !matches!(result, Ok(Ok(()))) {
                return StatusCode::SERVICE_UNAVAILABLE;
            }
        }
        if self.engine.configure(id, next.clone()).await.is_err() {
            return StatusCode::SERVICE_UNAVAILABLE;
        }
        session.settings = next;
        StatusCode::OK
    }
    async fn register(
        &self,
        hello: AudioHello,
    ) -> Result<(Uuid, Arc<Ingress>, CancellationToken), u16> {
        let mut registry = self.registry.lock().await;
        if registry.updating || self.stop.is_cancelled() {
            return Err(1001);
        }
        if let Some(device_id) = hello.device_id {
            let previous = registry
                .sessions
                .iter()
                .find(|(_, s)| s.device_id == Some(device_id))
                .map(|(&id, _)| id);
            if let Some(previous) = previous {
                if let Some(session) = registry.sessions.remove(&previous) {
                    session.stop.cancel();
                }
                self.engine.remove(previous).await.map_err(|_| 1011u16)?;
            }
        }
        if registry.sessions.len() >= 16 {
            return Err(1007);
        }
        let settings = if let Some(device) = hello.device_id {
            self.saved.lock().map_err(|_| 1011u16)?.get(device)
        } else {
            SourceSettings::default()
        };
        let id = Uuid::new_v4();
        let stop = CancellationToken::new();
        let input = self
            .engine
            .add(id, hello.name, hello.format, settings.clone())
            .await
            .map_err(|_| 1011u16)?;
        registry.sessions.insert(
            id,
            Session {
                device_id: hello.device_id,
                settings,
                stop: stop.clone(),
            },
        );
        Ok((id, input, stop))
    }
    async fn unregister(&self, id: Uuid) {
        let mut registry = self.registry.lock().await;
        if registry.sessions.remove(&id).is_some() {
            let _ = self.engine.remove(id).await;
        }
    }
}
fn authorized(expected: &str, supplied: &str) -> bool {
    bool::from(expected.as_bytes().ct_eq(supplied.as_bytes()))
}
fn browser_origin(origin: &str) -> bool {
    let Some(host) = origin.strip_prefix("chrome-extension://") else {
        return false;
    };
    let host = host.strip_suffix('/').unwrap_or(host);
    host.len() == 32 && host.bytes().all(|c| (b'a'..=b'p').contains(&c))
}
async fn authenticate(
    State(hub): State<Hub>,
    request: Request<axum::body::Body>,
    next: Next,
) -> Response {
    if request.uri().path() == "/v1/browser-audio" {
        let origin = request
            .headers()
            .get("Origin")
            .and_then(|h| h.to_str().ok())
            .unwrap_or("");
        let upgrade = request
            .headers()
            .get("Upgrade")
            .and_then(|h| h.to_str().ok())
            .unwrap_or("");
        if !browser_origin(origin) || !upgrade.eq_ignore_ascii_case("websocket") {
            return StatusCode::FORBIDDEN.into_response();
        }
    } else {
        let key = request
            .headers()
            .get("X-Sound-Key")
            .and_then(|h| h.to_str().ok())
            .unwrap_or("");
        if !authorized(&hub.key, key) {
            return StatusCode::UNAUTHORIZED.into_response();
        }
    }
    next.run(request).await
}
fn decode<T: DeserializeOwned>(body: &[u8]) -> Result<T, StatusCode> {
    serde_json::from_slice(body).map_err(|_| StatusCode::BAD_REQUEST)
}
async fn state(State(hub): State<Hub>) -> Response {
    match hub.engine.snapshot().await {
        Ok(state) => Json(state).into_response(),
        Err(_) => StatusCode::SERVICE_UNAVAILABLE.into_response(),
    }
}
async fn set_mix(State(hub): State<Hub>, body: axum::body::Bytes) -> Response {
    let settings: MixSettings = match decode(&body) {
        Ok(s) => s,
        Err(status) => return status.into_response(),
    };
    if settings.validate().is_err() {
        return StatusCode::BAD_REQUEST.into_response();
    }
    if hub.engine.set_mix(settings).await.is_err() {
        return StatusCode::SERVICE_UNAVAILABLE.into_response();
    }
    state(State(hub)).await
}
async fn patch_source(
    State(hub): State<Hub>,
    Path(id): Path<String>,
    body: axum::body::Bytes,
) -> StatusCode {
    let Ok(id) = Uuid::parse_str(&id) else {
        return StatusCode::NOT_FOUND;
    };
    match decode(&body) {
        Ok(patch) => hub.change_source(id, patch).await,
        Err(status) => status,
    }
}
async fn put_source(
    State(hub): State<Hub>,
    Path(id): Path<String>,
    body: axum::body::Bytes,
) -> StatusCode {
    let Ok(id) = Uuid::parse_str(&id) else {
        return StatusCode::NOT_FOUND;
    };
    let next: SourceSettings = match decode(&body) {
        Ok(s) => s,
        Err(status) => return status,
    };
    if next.validate().is_err() {
        return StatusCode::BAD_REQUEST;
    }
    hub.change_source(
        id,
        SourceSettingsPatch {
            volume: Some(next.volume),
            azimuth: Some(next.azimuth),
            spatial: Some(next.spatial),
            muted: Some(next.muted),
            display_name: next.display_name,
            reset_name: false,
        },
    )
    .await
}
async fn audio(
    State(hub): State<Hub>,
    upgrade: Result<WebSocketUpgrade, axum::extract::ws::rejection::WebSocketUpgradeRejection>,
) -> Response {
    upgrade_audio(hub, upgrade, false).await
}
async fn browser_audio(
    State(hub): State<Hub>,
    upgrade: Result<WebSocketUpgrade, axum::extract::ws::rejection::WebSocketUpgradeRejection>,
) -> Response {
    upgrade_audio(hub, upgrade, true).await
}
async fn upgrade_audio(
    hub: Hub,
    upgrade: Result<WebSocketUpgrade, axum::extract::ws::rejection::WebSocketUpgradeRejection>,
    browser: bool,
) -> Response {
    if hub.registry.lock().await.updating {
        return StatusCode::SERVICE_UNAVAILABLE.into_response();
    }
    match upgrade {
        Ok(upgrade) => upgrade
            .max_message_size(MAX_MESSAGE)
            .max_frame_size(MAX_MESSAGE)
            .on_upgrade(move |socket| handle_audio(hub, socket, browser)),
        Err(_) => StatusCode::BAD_REQUEST.into_response(),
    }
}
async fn close(socket: &mut WebSocket, code: u16) {
    let reason = match code {
        1008 => "Authentication failed",
        1001 => "Server restarting; retry",
        1011 => "Audio engine unavailable",
        _ => "Invalid audio protocol or source limit",
    };
    let _ = tokio::time::timeout(
        Duration::from_secs(2),
        socket.send(Message::Close(Some(CloseFrame {
            code,
            reason: reason.into(),
        }))),
    )
    .await;
}
async fn first_hello(socket: &mut WebSocket) -> Result<AudioHello, u16> {
    let message = tokio::time::timeout(Duration::from_secs(10), socket.recv())
        .await
        .map_err(|_| 1007u16)?
        .ok_or(1007u16)?
        .map_err(|_| 1007u16)?;
    let Message::Text(text) = message else {
        return Err(1007);
    };
    if text.len() > MAX_HELLO {
        return Err(1007);
    }
    serde_json::from_str(&text).map_err(|_| 1007)
}
async fn handle_audio(hub: Hub, mut socket: WebSocket, browser: bool) {
    let hello =
        tokio::select! { _=hub.stop.cancelled()=>return, hello=first_hello(&mut socket)=>hello };
    let hello = match hello {
        Ok(hello) => hello,
        Err(code) => {
            close(&mut socket, code).await;
            return;
        }
    };
    if browser && !authorized(&hub.key, hello.key.as_deref().unwrap_or("")) {
        close(&mut socket, 1008).await;
        return;
    }
    if hello.validate().is_err() {
        close(&mut socket, 1007).await;
        return;
    }
    let format = hello.format;
    let (id, input, stop) = match hub.register(hello).await {
        Ok(session) => session,
        Err(code) => {
            close(&mut socket, code).await;
            return;
        }
    };
    let ready = serde_json::json!({"sourceId":id}).to_string();
    let sent = tokio::time::timeout(
        Duration::from_secs(2),
        socket.send(Message::Text(ready.into())),
    )
    .await;
    if matches!(sent, Ok(Ok(()))) {
        loop {
            let received = tokio::select! {
                _=hub.stop.cancelled()=>{close(&mut socket,1001).await;break;},
                _=stop.cancelled()=>{close(&mut socket,1001).await;break;},
                next=tokio::time::timeout(Duration::from_secs(10),socket.recv())=>next
            };
            match received {
                Ok(Some(Ok(Message::Binary(bytes))))
                    if !bytes.is_empty() && bytes.len() % format.frame_bytes() == 0 =>
                {
                    input.push(bytes.to_vec())
                }
                Ok(Some(Ok(Message::Text(text)))) if text.as_bytes() == KEEPALIVE => {}
                Ok(Some(Ok(Message::Ping(_) | Message::Pong(_)))) => {}
                Ok(Some(Ok(Message::Close(_)))) => {
                    // Tungstenite already queued the close reply. Sending another Close
                    // fails after CloseReceived and would drop the queued handshake.
                    let _ = futures_util::SinkExt::flush(&mut socket).await;
                    break;
                }
                Ok(None) | Ok(Some(Err(_))) => break,
                _ => {
                    close(&mut socket, 1007).await;
                    break;
                }
            }
        }
    }
    hub.unregister(id).await;
}
