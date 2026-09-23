pub mod cpu_throughput;
pub mod curve;
pub mod event_certificate;
pub mod execution_view;
pub mod field;
pub mod gpu_execution;
pub mod modal;
pub mod persistent_execution_view;
pub mod transactional_simulation;

use serde::Serialize;
use std::{fs::OpenOptions, io::Write, path::Path};

pub fn write_json_exclusive<T: Serialize>(path: &Path, value: &T) -> Result<(), String> {
    let mut file = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(path)
        .map_err(|error| error.to_string())?;
    let mut payload = serde_json::to_string_pretty(value).map_err(|error| error.to_string())?;
    payload.push('\n');
    file.write_all(payload.as_bytes())
        .map_err(|error| error.to_string())
}

pub fn output_argument() -> Result<std::path::PathBuf, String> {
    let mut args = std::env::args_os().skip(1);
    match (args.next(), args.next(), args.next()) {
        (Some(flag), Some(path), None) if flag == "--output" => Ok(path.into()),
        _ => Err("usage: --output NEW_REPORT.json".into()),
    }
}
