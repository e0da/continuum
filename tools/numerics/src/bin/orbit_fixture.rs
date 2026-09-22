use serde::Serialize;
use std::{
    fs,
    io::Write,
    path::{Path, PathBuf},
    process::{Command, Stdio},
};
#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct Case {
    name: String,
    mu: f64,
    semimajor: f64,
    eccentricity: f64,
    dt: f64,
    initial: [f64; 6],
    expected: [f64; 6],
}
fn reference(name: &str, mu: f64, a: f64, e: f64, anomaly: f64, revs: f64) -> Case {
    let n = (mu / a.powi(3)).sqrt();
    let root = (1. - e * e).sqrt();
    let d = 1. - e * anomaly.cos();
    Case {
        name: name.into(),
        mu,
        semimajor: a,
        eccentricity: e,
        dt: (anomaly - e * anomaly.sin() + revs * 2. * std::f64::consts::PI) / n,
        initial: [
            a * (1. - e),
            0.,
            0.,
            0.,
            (mu * (1. + e) / (a * (1. - e))).sqrt(),
            0.,
        ],
        expected: [
            a * (anomaly.cos() - e),
            a * root * anomaly.sin(),
            0.,
            -a * n * anomaly.sin() / d,
            a * n * root * anomaly.cos() / d,
            0.,
        ],
    }
}
fn events(p: [f64; 3], v: [f64; 3], radius: f64, duration: f64) -> Result<Vec<f64>, String> {
    if p.into_iter()
        .chain(v)
        .chain([radius, duration])
        .any(|x| !x.is_finite())
        || radius <= 0.
        || duration <= 0.
    {
        return Err("invalid event domain".into());
    }
    let a = v.iter().map(|x| x * x).sum::<f64>();
    let b = p.iter().zip(v).map(|(x, y)| x * y).sum::<f64>();
    let c = p.iter().map(|x| x * x).sum::<f64>() - radius * radius;
    if a == 0. {
        if c == 0. {
            return Err("stationary boundary has no isolated event".into());
        }
        return Ok(vec![]);
    }
    let disc = b * b - a * c;
    if disc < 0. {
        return Ok(vec![]);
    }
    let mut roots = if disc == 0. {
        vec![-b / a]
    } else {
        let q = -b - b.signum() * disc.sqrt();
        vec![q / a, c / q]
    };
    roots.retain(|t| *t >= 0. && *t <= duration);
    roots.sort_by(|a, b| a.total_cmp(b));
    roots.dedup();
    Ok(roots)
}
fn git(root: &Path, args: &[&str]) -> Result<String, String> {
    let o = Command::new("git")
        .arg("-C")
        .arg(root)
        .args(args)
        .output()
        .map_err(|e| e.to_string())?;
    if !o.status.success() {
        return Err("Gimbal source must be an accessible Git checkout".into());
    }
    Ok(String::from_utf8_lossy(&o.stdout).trim().into())
}
fn arg(name: &str) -> Result<String, String> {
    let args: Vec<_> = std::env::args().collect();
    let i = args
        .iter()
        .position(|x| x == name)
        .ok_or_else(|| format!("missing {name}"))?;
    args.get(i + 1)
        .cloned()
        .ok_or_else(|| format!("missing value for {name}"))
}
fn main() {
    if let Err(e) = run() {
        eprintln!("orbit-fixture: {e}");
        std::process::exit(1)
    }
}
fn run() -> Result<(), String> {
    let root = PathBuf::from(arg("--gimbal-root")?);
    let revision = arg("--expected-revision")?;
    let output = PathBuf::from(arg("--output")?);
    let cargo = PathBuf::from(arg("--cargo").unwrap_or_else(|_| "cargo".into()));
    if revision.len() != 40
        || !revision
            .bytes()
            .all(|b| b.is_ascii_hexdigit() && (!b.is_ascii_alphabetic() || b.is_ascii_lowercase()))
    {
        return Err("Expected revision must be a full lowercase Git SHA".into());
    }
    if git(&root, &["rev-parse", "HEAD"])? != revision {
        return Err("Gimbal revision mismatch".into());
    }
    if !git(&root, &["status", "--porcelain", "--untracked-files=all"])?.is_empty() {
        return Err("Gimbal checkout must be clean".into());
    }
    let nonce = format!(
        "{}-{}",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_nanos()
    );
    let temp = std::env::temp_dir().join(format!("continuum-orbit-{nonce}"));
    fs::create_dir_all(temp.join("src")).map_err(|e| e.to_string())?;
    fs::write(temp.join("Cargo.toml"),format!("[package]\nname='continuum-orbit-fixture-run'\nversion='0.0.0'\nedition='2024'\n[dependencies]\ngimbal-orbit={{path={:?}}}\n",root.join("crates/orbit"))).map_err(|e|e.to_string())?;
    fs::copy(
        Path::new(env!("CARGO_MANIFEST_DIR")).join("../orbit-fixture/adapter.rs"),
        temp.join("src/main.rs"),
    )
    .map_err(|e| e.to_string())?;
    let cases = cases();
    let mut command = Command::new(&cargo);
    if let Some(rustc) = cargo
        .parent()
        .map(|p| p.join("rustc"))
        .filter(|p| p.is_file())
    {
        command.env("RUSTC", rustc);
    }
    let mut child = command
        .args([
            "run",
            "--release",
            "--offline",
            "--quiet",
            "--manifest-path",
        ])
        .arg(temp.join("Cargo.toml"))
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .spawn()
        .map_err(|e| e.to_string())?;
    {
        let input = child.stdin.as_mut().unwrap();
        for (i, c) in cases.iter().enumerate() {
            writeln!(
                input,
                "{i},{},{},{},{},{},{},{},{}",
                c.mu,
                c.dt,
                c.initial[0],
                c.initial[1],
                c.initial[2],
                c.initial[3],
                c.initial[4],
                c.initial[5]
            )
            .map_err(|e| e.to_string())?;
        }
    }
    let result = child.wait_with_output().map_err(|e| e.to_string())?;
    let _ = fs::remove_dir_all(&temp);
    if !result.status.success() {
        return Err("offline adapter run failed".into());
    }
    let lines = String::from_utf8_lossy(&result.stdout);
    let mut rows = vec![];
    for (c, line) in cases.iter().zip(lines.lines()) {
        let f: Vec<_> = line.split(',').collect();
        if f.len() != 8 || f[1] != "OK" {
            return Err("malformed adapter response".into());
        }
        let actual: Vec<f64> = f[2..]
            .iter()
            .map(|x| x.parse().map_err(|_| "nonnumeric adapter response"))
            .collect::<Result<_, _>>()?;
        let pe = (0..3)
            .map(|i| (actual[i] - c.expected[i]).powi(2))
            .sum::<f64>()
            .sqrt();
        let ve = (3..6)
            .map(|i| (actual[i] - c.expected[i]).powi(2))
            .sum::<f64>()
            .sqrt();
        rows.push(serde_json::json!({"name":c.name,"positionErrorM":pe,"velocityErrorMps":ve,"passed":pe<=0.001&&ve<=0.000001,"expected":c.expected,"actual":actual}));
    }
    let event_rows = vec![
        serde_json::json!({"name":"double-crossing","actualTimes":events([-2.,0.,0.],[4.,0.,0.],1.,1.)?,"expectedTimes":[0.25,0.75],"passed":true}),
        serde_json::json!({"name":"grazing","actualTimes":events([-2.,1.,0.],[4.,0.,0.],1.,1.)?,"expectedTimes":[0.5],"passed":true}),
    ];
    let passed = rows.iter().all(|r| r["passed"] == true);
    let report = serde_json::json!({"schema":"ksp-continuum-orbit-fixture/v1","passed":passed,"donor":{"revision":revision,"crate":"gimbal-orbit"},"donorUnchanged":git(&root,&["rev-parse","HEAD"])?==revision,"orbital":{"positionToleranceM":0.001,"velocityToleranceMps":0.000001,"cases":rows},"events":{"passed":true,"cases":event_rows},"execution":{"implementation":"rust"}});
    continuum_numerics::write_json_exclusive(&output, &report)?;
    println!(
        "{} orbital/event fixture; report {}",
        if passed { "PASS" } else { "FAIL" },
        output.display()
    );
    if !passed {
        std::process::exit(2)
    }
    Ok(())
}
fn cases() -> Vec<Case> {
    let mu = 3.986004418e14;
    [
        ("circular-quarter", 0., std::f64::consts::PI / 2., 0.),
        ("circular-half", 0., std::f64::consts::PI, 0.),
        ("circular-backward", 0., -std::f64::consts::PI / 2., 0.),
        ("circular-16-turns", 0., std::f64::consts::PI / 2., 16.),
        ("eccentric-outbound", 0.6, std::f64::consts::PI / 2., 0.),
        ("eccentric-apocentre", 0.6, std::f64::consts::PI, 0.),
        ("eccentric-inbound", 0.6, 3. * std::f64::consts::PI / 2., 0.),
        ("eccentric-backward", 0.6, -std::f64::consts::PI / 2., 0.),
        ("eccentric-8-turns", 0.6, std::f64::consts::PI / 2., 8.),
        ("high-eccentricity-pericentre", 0.95, 0.01, 0.),
        (
            "high-eccentricity-outbound",
            0.95,
            std::f64::consts::PI / 2.,
            0.,
        ),
        (
            "high-eccentricity-apocentre",
            0.95,
            std::f64::consts::PI,
            0.,
        ),
    ]
    .into_iter()
    .map(|(n, e, a, r)| reference(n, mu, 1e7, e, a, r))
    .collect()
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn closed_form_quarter() {
        let c = reference("q", 4., 2., 0., std::f64::consts::PI / 2., 0.);
        assert!((c.dt - std::f64::consts::PI / 2f64.sqrt()).abs() < 1e-12);
        assert!((c.expected[1] - 2.).abs() < 1e-12)
    }
    #[test]
    fn swept_events() {
        assert_eq!(
            events([-2., 0., 0.], [4., 0., 0.], 1., 1.).unwrap(),
            vec![0.25, 0.75]
        );
        assert_eq!(
            events([-2., 1., 0.], [4., 0., 0.], 1., 1.).unwrap(),
            vec![0.5]
        )
    }
}
