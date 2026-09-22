use serde::{Deserialize, Serialize};
use std::env;
use std::fs;
use std::io::{BufRead, BufReader, Write};
use std::net::{TcpStream, ToSocketAddrs};
use std::path::PathBuf;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

const PROTOCOL: &str = "1.0.0";
const MAX_SAMPLES: usize = 10_000;

#[derive(Debug)]
struct Options {
    address: String,
    samples: usize,
    output: PathBuf,
    timeout: Duration,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Reply {
    status: String,
    reason: Option<String>,
    request_id: Option<String>,
    identity: Option<Identity>,
    snapshot: Option<serde_json::Value>,
    observer_nanoseconds: Option<u64>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Identity {
    session_id: String,
    epoch: u64,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Sample {
    sequence: usize,
    round_trip_nanoseconds: u64,
    observer_nanoseconds: u64,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Receipt {
    schema: &'static str,
    protocol_version: &'static str,
    address: String,
    requested_samples: usize,
    completed_samples: usize,
    started_unix_milliseconds: u128,
    elapsed_nanoseconds: u64,
    status: &'static str,
    session_id: String,
    epoch: u64,
    samples: Vec<Sample>,
}

fn main() {
    if let Err(error) = run(env::args().skip(1).collect()) {
        eprintln!("live-control-client: {error}");
        std::process::exit(1);
    }
}

fn run(arguments: Vec<String>) -> Result<(), String> {
    let options = parse_options(&arguments)?;
    let started_wall = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_err(|_| "system clock precedes Unix epoch".to_string())?
        .as_millis();
    let started = Instant::now();
    let mut stream = connect(&options)?;
    let mut reader = BufReader::new(stream.try_clone().map_err(|e| e.to_string())?);
    let hello = exchange(&mut stream, &mut reader, "hello 1.0.0 qualification-hello")?;
    require_ok(&hello, "qualification-hello", false)?;
    let identity = hello.identity.ok_or("hello reply omitted identity")?;
    let mut samples = Vec::with_capacity(options.samples);
    for sequence in 0..options.samples {
        let request_id = format!("qualification-{sequence}");
        let command = format!(
            "snapshot {PROTOCOL} {request_id} {} {} 0",
            identity.session_id, identity.epoch
        );
        let before = Instant::now();
        let reply = exchange(&mut stream, &mut reader, &command)?;
        let round_trip = nanos(before.elapsed())?;
        require_ok(&reply, &request_id, true)?;
        let observed = reply
            .identity
            .as_ref()
            .ok_or("snapshot reply omitted identity")?;
        if observed.session_id != identity.session_id || observed.epoch != identity.epoch {
            return Err(format!("request {request_id} changed identity"));
        }
        samples.push(Sample {
            sequence,
            round_trip_nanoseconds: round_trip,
            observer_nanoseconds: reply
                .observer_nanoseconds
                .ok_or("snapshot reply omitted observerNanoseconds")?,
        });
    }
    let receipt = Receipt {
        schema: "ksp-continuum-live-control-qualification/v1",
        protocol_version: PROTOCOL,
        address: options.address,
        requested_samples: options.samples,
        completed_samples: samples.len(),
        started_unix_milliseconds: started_wall,
        elapsed_nanoseconds: nanos(started.elapsed())?,
        status: "complete",
        session_id: identity.session_id,
        epoch: identity.epoch,
        samples,
    };
    let encoded = serde_json::to_vec_pretty(&receipt).map_err(|e| e.to_string())?;
    if let Some(parent) = options.output.parent() {
        fs::create_dir_all(parent).map_err(|e| e.to_string())?;
    }
    fs::write(&options.output, encoded).map_err(|e| e.to_string())?;
    println!("{}", options.output.display());
    Ok(())
}

fn connect(options: &Options) -> Result<TcpStream, String> {
    let address = options
        .address
        .to_socket_addrs()
        .map_err(|e| format!("invalid address: {e}"))?
        .next()
        .ok_or("address resolved to no endpoints")?;
    if !address.ip().is_loopback() {
        return Err("address must resolve to loopback".into());
    }
    let stream = TcpStream::connect_timeout(&address, options.timeout)
        .map_err(|e| format!("connect failed: {e}"))?;
    stream
        .set_read_timeout(Some(options.timeout))
        .map_err(|e| e.to_string())?;
    stream
        .set_write_timeout(Some(options.timeout))
        .map_err(|e| e.to_string())?;
    Ok(stream)
}

fn exchange(
    stream: &mut TcpStream,
    reader: &mut BufReader<TcpStream>,
    command: &str,
) -> Result<Reply, String> {
    stream
        .write_all(format!("{command}\n").as_bytes())
        .map_err(|e| format!("write failed: {e}"))?;
    let mut line = String::new();
    let count = reader
        .read_line(&mut line)
        .map_err(|e| format!("read failed: {e}"))?;
    if count == 0 {
        return Err("server closed before reply".into());
    }
    serde_json::from_str(&line).map_err(|e| format!("invalid reply JSON: {e}"))
}

fn require_ok(reply: &Reply, request_id: &str, snapshot: bool) -> Result<(), String> {
    if reply.request_id.as_deref() != Some(request_id) {
        return Err("reply requestId did not match request".into());
    }
    if reply.status != "ok" {
        return Err(format!(
            "request {request_id} returned {}: {}",
            reply.status,
            reply.reason.as_deref().unwrap_or("unspecified")
        ));
    }
    if snapshot && reply.snapshot.is_none() {
        return Err(format!("request {request_id} omitted snapshot"));
    }
    Ok(())
}

fn parse_options(arguments: &[String]) -> Result<Options, String> {
    let mut address = "127.0.0.1:47771".to_string();
    let mut samples = 300usize;
    let mut output = None;
    let mut timeout_ms = 5_000u64;
    let mut index = 0;
    while index < arguments.len() {
        let value = arguments.get(index + 1).ok_or_else(usage)?;
        match arguments[index].as_str() {
            "--address" => address = value.clone(),
            "--samples" => samples = value.parse().map_err(|_| "invalid --samples")?,
            "--output" => output = Some(PathBuf::from(value)),
            "--timeout-ms" => timeout_ms = value.parse().map_err(|_| "invalid --timeout-ms")?,
            _ => return Err(usage()),
        }
        index += 2;
    }
    if samples == 0 || samples > MAX_SAMPLES {
        return Err(format!("--samples must be between 1 and {MAX_SAMPLES}"));
    }
    if timeout_ms == 0 || timeout_ms > 60_000 {
        return Err("--timeout-ms must be between 1 and 60000".into());
    }
    Ok(Options {
        address,
        samples,
        output: output.ok_or_else(usage)?,
        timeout: Duration::from_millis(timeout_ms),
    })
}

fn nanos(duration: Duration) -> Result<u64, String> {
    duration
        .as_nanos()
        .try_into()
        .map_err(|_| "duration exceeds receipt range".into())
}

fn usage() -> String {
    "usage: continuum-live-control-client [--address HOST:PORT] [--samples 1..10000] --output RECEIPT.json [--timeout-ms 1..60000]".into()
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::net::TcpListener;
    use std::thread;

    #[test]
    fn rejects_unbounded_sample_count() {
        let args = vec![
            "--samples".into(),
            "10001".into(),
            "--output".into(),
            "x".into(),
        ];
        assert!(parse_options(&args).unwrap_err().contains("between 1 and"));
    }

    #[test]
    fn persistent_exchange_is_correlated() {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let address = listener.local_addr().unwrap();
        let server = thread::spawn(move || {
            let (mut socket, _) = listener.accept().unwrap();
            let mut reader = BufReader::new(socket.try_clone().unwrap());
            for expected in [
                "hello 1.0.0 qualification-hello",
                "snapshot 1.0.0 qualification-0 session 7 0",
            ] {
                let mut line = String::new();
                reader.read_line(&mut line).unwrap();
                assert_eq!(line.trim_end(), expected);
                let reply = if expected.starts_with("hello") {
                    r#"{"status":"ok","requestId":"qualification-hello","identity":{"sessionId":"session","epoch":7}}"#
                } else {
                    r#"{"status":"ok","requestId":"qualification-0","identity":{"sessionId":"session","epoch":7},"snapshot":{"parts":3},"observerNanoseconds":123}"#
                };
                writeln!(socket, "{reply}").unwrap();
            }
        });
        let mut stream = TcpStream::connect(address).unwrap();
        let mut reader = BufReader::new(stream.try_clone().unwrap());
        let hello = exchange(&mut stream, &mut reader, "hello 1.0.0 qualification-hello").unwrap();
        require_ok(&hello, "qualification-hello", false).unwrap();
        let reply = exchange(
            &mut stream,
            &mut reader,
            "snapshot 1.0.0 qualification-0 session 7 0",
        )
        .unwrap();
        require_ok(&reply, "qualification-0", true).unwrap();
        assert_eq!(reply.observer_nanoseconds, Some(123));
        server.join().unwrap();
    }
}
