use futures_util::{SinkExt, StreamExt};
use reqwest::StatusCode;
use serde_json::{Value, json};
use sound_protocol::MixerState;
use sound_server::{
    RunningServer,
    engine::{Engine, EngineWorker},
    settings::SettingsStore,
};
use std::{path::PathBuf, time::Duration};
use tokio::net::TcpStream;
use tokio_tungstenite::{
    WebSocketStream, connect_async,
    tungstenite::{Message, client::IntoClientRequest, protocol::frame::coding::CloseCode},
};
use uuid::Uuid;

type Socket = WebSocketStream<tokio_tungstenite::MaybeTlsStream<TcpStream>>;
struct Fixture {
    server: RunningServer,
    _worker: EngineWorker,
    http: reqwest::Client,
    key: String,
    url: String,
}
impl Fixture {
    async fn new(path: Option<PathBuf>) -> Self {
        let worker = Engine::start(None).unwrap();
        let key = Uuid::new_v4().simple().to_string();
        let server = RunningServer::bind(
            worker.engine(),
            key.clone(),
            "127.0.0.1:0".parse().unwrap(),
            path,
        )
        .await
        .unwrap();
        let http = reqwest::Client::builder()
            .timeout(Duration::from_secs(5))
            .build()
            .unwrap();
        let url = format!("http://{}", server.address);
        Self {
            server,
            _worker: worker,
            http,
            key,
            url,
        }
    }
    async fn state(&self) -> MixerState {
        self.http
            .get(format!("{}/v1/state", self.url))
            .header("X-Sound-Key", &self.key)
            .send()
            .await
            .unwrap()
            .json()
            .await
            .unwrap()
    }
    async fn socket(&self, browser: bool) -> Socket {
        let mut request = format!(
            "ws://{}/v1/{}",
            self.server.address,
            if browser { "browser-audio" } else { "audio" }
        )
        .into_client_request()
        .unwrap();
        if browser {
            request.headers_mut().insert(
                "Origin",
                "chrome-extension://abcdefghijklmnopabcdefghijklmnop"
                    .parse()
                    .unwrap(),
            );
        } else {
            request
                .headers_mut()
                .insert("X-Sound-Key", self.key.parse().unwrap());
        }
        connect_async(request).await.unwrap().0
    }
    async fn hello(&self, socket: &mut Socket, device: Option<Uuid>, browser: bool) -> Uuid {
        socket.send(Message::Text(json!({"version":1,"name":"테스트 음원","format":{"sampleRate":48000,"channels":2,"encoding":"pcm16"},"deviceId":device,"key":if browser{self.key.clone()}else{String::new()}}).to_string().into())).await.unwrap();
        let ready = socket.next().await.unwrap().unwrap();
        let text = ready.to_text().unwrap();
        let value: Value = serde_json::from_str(text).unwrap();
        value["sourceId"].as_str().unwrap().parse().unwrap()
    }
    async fn change(&self, id: Uuid, value: Value) -> StatusCode {
        self.http
            .patch(format!("{}/v1/sources/{}", self.url, id))
            .header("X-Sound-Key", &self.key)
            .json(&value)
            .send()
            .await
            .unwrap()
            .status()
    }
    async fn wait_count(&self, count: usize) {
        for _ in 0..200 {
            if self.state().await.sources.len() == count {
                return;
            }
            tokio::time::sleep(Duration::from_millis(5)).await;
        }
        panic!("source count did not become {count}");
    }
    async fn close(&self, socket: &mut Socket) {
        socket.send(Message::Close(None)).await.unwrap();
        let _ = socket.next().await;
    }
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn requires_auth_and_enforces_browser_origin_before_upgrade() {
    let fixture = Fixture::new(None).await;
    assert_eq!(
        fixture
            .http
            .get(format!("{}/v1/state", fixture.url))
            .send()
            .await
            .unwrap()
            .status(),
        StatusCode::UNAUTHORIZED
    );
    assert_eq!(
        fixture
            .http
            .get(format!("{}/v1/browser-audio", fixture.url))
            .send()
            .await
            .unwrap()
            .status(),
        StatusCode::FORBIDDEN
    );
    let mut request = format!("ws://{}/v1/audio", fixture.server.address)
        .into_client_request()
        .unwrap();
    assert!(connect_async(request.clone()).await.is_err());
    *request.uri_mut() = format!("ws://{}/v1/browser-audio", fixture.server.address)
        .parse()
        .unwrap();
    request
        .headers_mut()
        .insert("Origin", "https://example.com".parse().unwrap());
    assert!(connect_async(request).await.is_err());
    let mut socket = fixture.socket(true).await;
    socket.send(Message::Text(json!({"version":1,"name":"wrong key","format":{"sampleRate":48000,"channels":2,"encoding":"pcm16"},"key":"invalid"}).to_string().into())).await.unwrap();
    let Message::Close(Some(frame)) = socket.next().await.unwrap().unwrap() else {
        panic!("expected auth rejection");
    };
    assert_eq!(frame.code, CloseCode::Policy);
    assert!(fixture.state().await.sources.is_empty());
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn controls_validate_and_patch_does_not_overwrite_other_settings() {
    let fixture = Fixture::new(None).await;
    let mut socket = fixture.socket(false).await;
    let id = fixture
        .hello(&mut socket, Some(Uuid::new_v4()), false)
        .await;
    assert_eq!(
        fixture
            .change(
                id,
                json!({"volume":0.4,"azimuth":270,"spatial":true,"displayName":"음악 PC"})
            )
            .await,
        StatusCode::OK
    );
    assert_eq!(
        fixture.change(id, json!({"muted":true})).await,
        StatusCode::OK
    );
    let state = fixture.state().await;
    let saved = &state.sources[0].settings;
    assert_eq!(saved.volume, 0.4);
    assert_eq!(saved.azimuth, 270.0);
    assert!(saved.muted && saved.spatial);
    assert_eq!(state.sources[0].name, "음악 PC");
    assert_eq!(
        fixture.change(id, json!({"volume":2.01})).await,
        StatusCode::BAD_REQUEST
    );
    assert_eq!(
        fixture
            .change(id, json!({"resetName":true,"displayName":"ambiguous"}))
            .await,
        StatusCode::BAD_REQUEST
    );
    let response = fixture
        .http
        .put(format!("{}/v1/sources/{}", fixture.url, id))
        .header("X-Sound-Key", &fixture.key)
        .json(&json!({"volume":0.5}))
        .send()
        .await
        .unwrap();
    assert_eq!(response.status(), StatusCode::OK);
    assert_eq!(fixture.state().await.sources[0].name, "음악 PC");
    assert_eq!(
        fixture.change(id, json!({"resetName":true})).await,
        StatusCode::OK
    );
    assert_eq!(fixture.state().await.sources[0].name, "테스트 음원");
    let invalid = fixture
        .http
        .put(format!("{}/v1/mix", fixture.url))
        .header("X-Sound-Key", &fixture.key)
        .json(&json!({"masterVolume":1.1}))
        .send()
        .await
        .unwrap();
    assert_eq!(invalid.status(), StatusCode::BAD_REQUEST);
    assert_eq!(fixture.state().await.settings.master_volume, 0.7);
    fixture.close(&mut socket).await;
    fixture.wait_count(0).await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn restores_pascal_case_csharp_settings_before_first_pcm_and_replaces_session() {
    let folder = tempfile::tempdir().unwrap();
    let path = folder.path().join("source-settings.json");
    let device = Uuid::new_v4();
    std::fs::write(&path,json!({"Version":1,"Devices":{device.to_string():{"Volume":0.32,"Azimuth":155,"Spatial":true,"Muted":true,"DisplayName":"저장된 탭"}}}).to_string()).unwrap();
    let fixture = Fixture::new(Some(path.clone())).await;
    let mut old = fixture.socket(false).await;
    let old_id = fixture.hello(&mut old, Some(device), false).await;
    assert_eq!(fixture.state().await.sources[0].settings.volume, 0.32);
    let mut newer = fixture.socket(true).await;
    let new_id = fixture.hello(&mut newer, Some(device), true).await;
    assert_ne!(new_id, old_id);
    fixture.wait_count(1).await;
    let state = fixture.state().await;
    assert_eq!(state.sources[0].id, new_id);
    assert!(state.sources[0].settings.muted);
    assert_eq!(state.sources[0].name, "저장된 탭");
    assert_eq!(
        fixture.change(new_id, json!({"volume":0.64})).await,
        StatusCode::OK
    );
    let document: Value = serde_json::from_slice(&std::fs::read(&path).unwrap()).unwrap();
    assert_eq!(document["Version"], 1);
    assert!(
        (document["Devices"][device.to_string()]["Volume"]
            .as_f64()
            .unwrap()
            - 0.64)
            .abs()
            < 1e-6
    );
    let _ = old.next().await;
    fixture.close(&mut newer).await;
    fixture.wait_count(0).await;
    let mut restart = fixture.socket(false).await;
    fixture.hello(&mut restart, Some(device), false).await;
    assert_eq!(fixture.state().await.sources[0].settings.volume, 0.64);
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn idle_update_gate_does_not_interrupt_active_audio_and_blocks_new_sessions() {
    let fixture = Fixture::new(None).await;
    let mut socket = fixture.socket(false).await;
    fixture.hello(&mut socket, None, false).await;
    assert!(!fixture.server.hub.begin_update(true).await);
    fixture.close(&mut socket).await;
    fixture.wait_count(0).await;
    assert!(fixture.server.hub.begin_update(true).await);
    let mut request = format!("ws://{}/v1/audio", fixture.server.address)
        .into_client_request()
        .unwrap();
    request
        .headers_mut()
        .insert("X-Sound-Key", fixture.key.parse().unwrap());
    assert!(connect_async(request).await.is_err());
    fixture.server.hub.cancel_update().await;
    let mut restored = fixture.socket(false).await;
    fixture.hello(&mut restored, None, false).await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn streams_pcm_keepalive_and_receives_independent_sources() {
    let fixture = Fixture::new(None).await;
    let mut sockets = Vec::new();
    for i in 0..10 {
        let mut socket = fixture.socket(i % 2 == 0).await;
        fixture
            .hello(&mut socket, Some(Uuid::new_v4()), i % 2 == 0)
            .await;
        sockets.push(socket);
    }
    fixture.wait_count(10).await;
    let packet: Vec<u8> = (0..3840).flat_map(|_| 100i16.to_le_bytes()).collect();
    for socket in &mut sockets {
        socket
            .send(Message::Binary(packet.clone().into()))
            .await
            .unwrap();
        socket
            .send(Message::Text("{\"type\":\"keepalive\"}".into()))
            .await
            .unwrap();
    }
    let mut heard = false;
    for _ in 0..100 {
        let state = fixture.state().await;
        if state.output_rms > 0.0 {
            heard = true;
            assert!(state.output_peak <= 1.0);
            break;
        }
        tokio::time::sleep(Duration::from_millis(2)).await;
    }
    assert!(heard, "raw PCM must reach the Rust DSP");
    let state = fixture.state().await;
    assert!(
        state
            .sources
            .iter()
            .all(|s| s.diagnostics.received_packets == 1)
    );
    for socket in &mut sockets {
        fixture.close(socket).await;
    }
    fixture.wait_count(0).await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn partial_frames_and_oversized_or_invalid_hello_are_rejected() {
    let fixture = Fixture::new(None).await;
    for message in ["{".to_owned(), "x".repeat(4097)] {
        let mut socket = fixture.socket(false).await;
        socket.send(Message::Text(message.into())).await.unwrap();
        assert!(matches!(
            socket.next().await.unwrap().unwrap(),
            Message::Close(_)
        ));
    }
    let mut socket = fixture.socket(false).await;
    fixture.hello(&mut socket, None, false).await;
    socket
        .send(Message::Binary(vec![0, 1, 2].into()))
        .await
        .unwrap();
    assert!(matches!(
        socket.next().await.unwrap().unwrap(),
        Message::Close(_)
    ));
    fixture.wait_count(0).await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn disk_failure_returns_503_without_applying_unpersisted_settings() {
    let folder = tempfile::tempdir().unwrap();
    let path = folder.path().join("source.json");
    let fixture = Fixture::new(Some(path.clone())).await;
    std::fs::create_dir(&path).unwrap();
    let mut socket = fixture.socket(false).await;
    let id = fixture
        .hello(&mut socket, Some(Uuid::new_v4()), false)
        .await;
    assert_eq!(
        fixture.change(id, json!({"muted":true})).await,
        StatusCode::SERVICE_UNAVAILABLE
    );
    assert!(!fixture.state().await.sources[0].settings.muted);
}

#[test]
fn corrupted_store_is_preserved_and_pascal_case_document_can_be_read_again() {
    let folder = tempfile::tempdir().unwrap();
    let path = folder.path().join("state.json");
    std::fs::write(&path, b"bad-json").unwrap();
    assert!(SettingsStore::load(Some(path.clone())).is_err());
    assert_eq!(std::fs::read(&path).unwrap(), b"bad-json");
    std::fs::remove_file(&path).unwrap();
    let mut store = SettingsStore::load(Some(path.clone())).unwrap();
    let id = Uuid::new_v4();
    store
        .save(
            id,
            sound_core::SourceSettings {
                display_name: Some("브라우저".into()),
                muted: true,
                ..Default::default()
            },
        )
        .unwrap();
    let restored = SettingsStore::load(Some(path)).unwrap();
    assert_eq!(restored.get(id).display_name.as_deref(), Some("브라우저"));
    assert!(restored.get(id).muted);
}
