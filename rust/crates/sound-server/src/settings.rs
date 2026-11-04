use serde::{Deserialize, Serialize};
use sound_core::SourceSettings;
use std::{
    collections::HashMap,
    fs,
    io::{self, Write},
    path::{Path, PathBuf},
};
use uuid::Uuid;

#[derive(Serialize, Deserialize)]
struct Document {
    #[serde(rename = "Version", alias = "version")]
    version: u32,
    #[serde(rename = "Devices", alias = "devices")]
    devices: HashMap<Uuid, StoredSettings>,
}
#[derive(Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
struct StoredSettings {
    volume: f32,
    azimuth: f32,
    spatial: bool,
    muted: bool,
    #[serde(default)]
    display_name: Option<String>,
}
impl From<SourceSettings> for StoredSettings {
    fn from(value: SourceSettings) -> Self {
        Self {
            volume: value.volume,
            azimuth: value.azimuth,
            spatial: value.spatial,
            muted: value.muted,
            display_name: value.display_name,
        }
    }
}
impl From<&StoredSettings> for SourceSettings {
    fn from(value: &StoredSettings) -> Self {
        Self {
            volume: value.volume,
            azimuth: value.azimuth,
            spatial: value.spatial,
            muted: value.muted,
            display_name: value.display_name.clone(),
        }
    }
}

/// Same PascalCase v1 document as the C# server. A failed write never changes memory.
pub struct SettingsStore {
    path: Option<PathBuf>,
    devices: HashMap<Uuid, SourceSettings>,
}
impl SettingsStore {
    pub fn load(path: Option<PathBuf>) -> io::Result<Self> {
        let mut devices = HashMap::new();
        if let Some(path) = path.as_ref().filter(|p| p.exists()) {
            if fs::metadata(path)?.len() > 1_048_576 {
                return Err(io::Error::other(
                    "Settings file too large; preserved original",
                ));
            }
            let document: Document = serde_json::from_slice(&fs::read(path)?)?;
            if document.version != 1 || document.devices.len() > 4096 {
                return Err(io::Error::other("Unsupported settings; preserved original"));
            }
            for (id, value) in document.devices {
                let settings = SourceSettings::from(&value);
                if id.is_nil() || settings.validate().is_err() {
                    return Err(io::Error::other("Invalid settings; preserved original"));
                }
                devices.insert(id, settings);
            }
        }
        Ok(Self { path, devices })
    }
    pub fn get(&self, id: Uuid) -> SourceSettings {
        self.devices.get(&id).cloned().unwrap_or_default()
    }
    pub fn save(&mut self, id: Uuid, settings: SourceSettings) -> io::Result<()> {
        if id.is_nil() || settings.validate().is_err() {
            return Err(io::Error::other("Invalid source settings"));
        }
        if !self.devices.contains_key(&id) && self.devices.len() >= 4096 {
            return Err(io::Error::other("Settings limit"));
        }
        let mut next = self.devices.clone();
        next.insert(id, settings);
        if let Some(path) = &self.path {
            let document = Document {
                version: 1,
                devices: next
                    .iter()
                    .map(|(&id, s)| (id, StoredSettings::from(s.clone())))
                    .collect(),
            };
            let data = serde_json::to_vec(&document)?;
            if data.len() > 1_048_576 {
                return Err(io::Error::other("Settings file too large"));
            }
            let folder = path
                .parent()
                .filter(|p| !p.as_os_str().is_empty())
                .unwrap_or(Path::new("."));
            fs::create_dir_all(folder)?;
            let temporary = folder.join(format!(".source-settings-{}.tmp", Uuid::new_v4()));
            let result = (|| {
                let mut file = fs::OpenOptions::new()
                    .write(true)
                    .create_new(true)
                    .open(&temporary)?;
                file.write_all(&data)?;
                file.sync_all()?;
                drop(file);
                fs::rename(&temporary, path)
            })();
            let _ = fs::remove_file(&temporary);
            result?;
        }
        self.devices = next;
        Ok(())
    }
}
