use serde::Deserialize;
use serde_json::json;
use sound_core::MixSettings;
use sound_server::{
    RunningServer,
    engine::{AudioQueue, BLOCK_FRAMES, Engine},
};
use std::{
    io::{self, Write},
    net::{IpAddr, SocketAddr},
    path::PathBuf,
    sync::{
        Arc,
        atomic::{AtomicU64, Ordering},
    },
    time::Duration,
};
use tokio::io::{AsyncBufReadExt, AsyncReadExt, BufReader};

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct StartConfig {
    key: String,
    #[serde(default = "default_host")]
    host: IpAddr,
    #[serde(default = "default_port")]
    port: u16,
    output_device_id: Option<String>,
    settings_path: Option<PathBuf>,
    #[serde(default)]
    mix: MixSettings,
    #[serde(default)]
    headless: bool,
}
fn default_host() -> IpAddr {
    "0.0.0.0".parse().expect("literal IPv4")
}
fn default_port() -> u16 {
    17843
}
fn respond(value: serde_json::Value) -> io::Result<()> {
    let mut output = io::stdout().lock();
    serde_json::to_writer(&mut output, &value)?;
    output.write_all(b"\n")?;
    output.flush()
}
#[tokio::main(worker_threads = 2)]
async fn main() {
    if let Err(error) = run().await {
        let _ = respond(json!({"event":"error","message":error}));
        std::process::exit(1);
    }
}
async fn run() -> Result<(), String> {
    // The pairing key travels over the private stdin pipe, never process arguments or logs.
    let mut input = BufReader::new(tokio::io::stdin());
    let mut line = Vec::new();
    let read = (&mut input)
        .take(65537)
        .read_until(b'\n', &mut line)
        .await
        .map_err(|e| e.to_string())?;
    if read == 0 || read > 65536 || line.last() != Some(&b'\n') {
        return Err("Invalid startup configuration".into());
    }
    let config: StartConfig =
        serde_json::from_slice(&line).map_err(|_| "Invalid startup configuration".to_owned())?;
    if config.mix.validate().is_err() {
        return Err("Invalid initial mix settings".into());
    }
    let queue = Arc::new(AudioQueue::new(6));
    let worker = Engine::start(if config.headless {
        None
    } else {
        Some(queue.clone())
    })
    .map_err(|e| e.to_string())?;
    let engine = worker.engine();
    engine.set_mix(config.mix).await?;
    let underruns = Arc::new(AtomicU64::new(0));
    let missed = underruns.clone();
    let output = if config.headless {
        None
    } else {
        let id = config
            .output_device_id
            .ok_or_else(|| "Select an output device".to_owned())?;
        let mut current = [0.0; BLOCK_FRAMES * 2];
        let mut offset = current.len();
        Some(sound_output::Output::start(id, move |samples| {
            let mut written = 0;
            while written < samples.len() {
                if offset == current.len() {
                    if let Some(block) = queue.pop() {
                        current = block;
                        offset = 0;
                    } else {
                        samples[written..].fill(0.0);
                        missed.fetch_add(1, Ordering::Relaxed);
                        break;
                    }
                }
                let count = (current.len() - offset).min(samples.len() - written);
                samples[written..written + count].copy_from_slice(&current[offset..offset + count]);
                offset += count;
                written += count;
            }
        })?)
    };
    let mut server = RunningServer::bind(
        engine.clone(),
        config.key,
        SocketAddr::new(config.host, config.port),
        config.settings_path,
    )
    .await
    .map_err(|e| e.to_string())?;
    respond(json!({"event":"ready","port":server.address.port(),"backend":"rust","version":env!("CARGO_PKG_VERSION")})).map_err(|e|e.to_string())?;
    let mut monitor = tokio::time::interval(Duration::from_millis(250));
    line.clear();
    loop {
        let mut control = (&mut input).take(65537 - line.len() as u64);
        tokio::select! {
            result=control.read_until(b'\n',&mut line)=> {
                let read=result.map_err(|e|e.to_string())?;
                if read==0&&line.is_empty() {break;}
                if line.len()>65536||line.last()!=Some(&b'\n'){return Err("Invalid control message".into());}
                let message:serde_json::Value=serde_json::from_slice(&line).map_err(|_|"Invalid control message".to_owned())?;
                let request=message.get("requestId").cloned().unwrap_or(serde_json::Value::Null);
                let op=message.get("op").and_then(|op|op.as_str()).unwrap_or("");
                let response=match op {
                    "stop"=>{respond(json!({"event":"reply","requestId":request,"ok":true})).map_err(|e|e.to_string())?;break;},
                    "beginUpdate"=>json!({"event":"reply","requestId":request,"ok":server.hub.begin_update(message.get("requireIdle").and_then(|v|v.as_bool()).unwrap_or(true)).await}),
                    "cancelUpdate"=>{server.hub.cancel_update().await;json!({"event":"reply","requestId":request,"ok":true})},
                    "state"=> {
                        let mut state=engine.snapshot().await?;state.diagnostics.output_underruns=underruns.load(Ordering::Relaxed);
                        json!({"event":"reply","requestId":request,"ok":true,"state":state})
                    },
                    _=>json!({"event":"reply","requestId":request,"ok":false})
                };
                respond(response).map_err(|e|e.to_string())?;
                line.clear();
            }
            _=monitor.tick()=>{if let Some(error)=output.as_ref().and_then(|output|output.failure()){server.stop().await;return Err(error);}},
            _=tokio::signal::ctrl_c()=>break,
        }
    }
    server.stop().await;
    drop(output);
    drop(worker);
    Ok(())
}
