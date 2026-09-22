use serde::Serialize;
use std::f64::consts::PI;
const N: usize = 32;
const RETAINED: usize = 7;
const K: f64 = 400.;
const C: f64 = 0.4;
const DT: f64 = 0.001;
const STEPS: usize = 2000;

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Metrics {
    max_shape_rms_error_m: f64,
    max_reference_shape_rms_m: f64,
    max_shape_rms_relative: f64,
    max_tip_shape_error_m: f64,
    max_reference_tip_shape_m: f64,
    max_tip_shape_error_relative: f64,
    max_center_of_mass_error_m: f64,
}
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Workload {
    name: String,
    expected_to_qualify: bool,
    qualified: bool,
    metrics: Metrics,
    gates: serde_json::Value,
    checks: serde_json::Value,
    samples: Vec<serde_json::Value>,
}
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Report {
    pub schema: &'static str,
    pub experiment: &'static str,
    pub qualified: bool,
    interpretation: &'static str,
    production_physics_qualified: bool,
    stock_behavior_qualified: bool,
    performance_win_claimed: bool,
    deterministic_within_rust_build: bool,
    model: serde_json::Value,
    schedule: serde_json::Value,
    bounds: serde_json::Value,
    workloads: Vec<Workload>,
    environment: serde_json::Value,
}
fn dot(a: &[f64], b: &[f64]) -> f64 {
    a.iter().zip(b).map(|(x, y)| x * y).sum()
}
fn modes() -> (Vec<Vec<f64>>, Vec<f64>) {
    let mut vs = Vec::new();
    let mut es = Vec::new();
    for mode_idx in 0..N {
        let scale = if mode_idx == 0 {
            (1. / N as f64).sqrt()
        } else {
            (2. / N as f64).sqrt()
        };
        vs.push(
            (0..N)
                .map(|i| scale * (mode_idx as f64 * PI * (i as f64 + 0.5) / N as f64).cos())
                .collect(),
        );
        es.push(4. * K * (mode_idx as f64 * PI / (2. * N as f64)).sin().powi(2));
    }
    (vs, es)
}
fn chain(p: &[f64], v: &[f64], f: &[f64]) -> Vec<f64> {
    let mut a = f.to_vec();
    for i in 0..N - 1 {
        let t = K * (p[i + 1] - p[i]) + C * (v[i + 1] - v[i]);
        a[i] += t;
        a[i + 1] -= t;
    }
    a
}
fn modal(q: &[f64], r: &[f64], f: &[f64], e: &[f64]) -> Vec<f64> {
    (0..q.len())
        .map(|i| f[i] - e[i] * q[i] - (C / K) * e[i] * r[i])
        .collect()
}
fn step<F: Fn(&[f64], &[f64], &[f64]) -> Vec<f64>>(
    p: Vec<f64>,
    v: Vec<f64>,
    a: Vec<f64>,
    f: &[f64],
    acc: F,
) -> (Vec<f64>, Vec<f64>, Vec<f64>) {
    let np: Vec<_> = (0..p.len())
        .map(|i| p[i] + DT * v[i] + 0.5 * DT * DT * a[i])
        .collect();
    let pv: Vec<_> = (0..p.len()).map(|i| v[i] + DT * a[i]).collect();
    let na = acc(&np, &pv, f);
    let nv: Vec<_> = (0..p.len())
        .map(|i| v[i] + 0.5 * DT * (a[i] + na[i]))
        .collect();
    let na = acc(&np, &nv, f);
    (np, nv, na)
}
fn force(name: &str, t: f64, vs: &[Vec<f64>]) -> Vec<f64> {
    if name == "localizedImpulse" {
        return vec![0.; N];
    }
    let ramp = 0.5 * (1. - (PI * (t / 0.4).min(1.)).cos());
    let env = (PI * ((t - 0.6) / 0.8).clamp(0., 1.)).sin().powi(2);
    (0..N)
        .map(|i| if i == 0 { 2. * ramp } else { 0. } + 0.35 * env * (vs[1][i] + 0.4 * vs[2][i]))
        .collect()
}
fn run(name: &str, vs: &[Vec<f64>], es: &[f64]) -> Workload {
    let rvs = &vs[..RETAINED];
    let res = &es[..RETAINED];
    let mut p = vec![0.; N];
    let mut v = vec![0.; N];
    if name == "localizedImpulse" {
        v[0] = 1.;
    }
    let mut q: Vec<_> = rvs.iter().map(|x| dot(x, &p)).collect();
    let mut r: Vec<_> = rvs.iter().map(|x| dot(x, &v)).collect();
    let f = force(name, 0., vs);
    let mut a = chain(&p, &v, &f);
    let mf: Vec<_> = rvs.iter().map(|x| dot(x, &f)).collect();
    let mut ma = modal(&q, &r, &mf, res);
    let (mut max_rms, mut max_tip, mut ref_rms, mut ref_tip, mut max_com) =
        (0f64, 0f64, 0f64, 0f64, 0f64);
    let mut samples = vec![];
    for s in 0..=STEPS {
        let rp: Vec<f64> = (0..N)
            .map(|i| q.iter().zip(rvs).map(|(x, m)| x * m[i]).sum())
            .collect();
        let pm = p.iter().sum::<f64>() / N as f64;
        let rm = rp.iter().sum::<f64>() / N as f64;
        let ps: Vec<_> = p.iter().map(|x| x - pm).collect();
        let rs: Vec<_> = rp.iter().map(|x| x - rm).collect();
        let er: Vec<_> = rs.iter().zip(&ps).map(|(x, y)| x - y).collect();
        let rms = (dot(&er, &er) / N as f64).sqrt();
        let rr = (dot(&ps, &ps) / N as f64).sqrt();
        max_rms = max_rms.max(rms);
        max_tip = max_tip.max(er[N - 1].abs());
        ref_rms = ref_rms.max(rr);
        ref_tip = ref_tip.max(ps[N - 1].abs());
        max_com = max_com.max((rm - pm).abs());
        if s % 100 == 0 {
            samples.push(serde_json::json!({"step":s,"timeSeconds":s as f64*DT,"fullCenterOfMassM":pm,"reducedCenterOfMassM":rm,"fullTipShapeM":ps[N-1],"reducedTipShapeM":rs[N-1],"shapeRmsErrorM":rms}));
        }
        if s == STEPS {
            break;
        }
        let nf = force(name, (s + 1) as f64 * DT, vs);
        (p, v, a) = step(p, v, a, &nf, chain);
        let nmf: Vec<_> = rvs.iter().map(|x| dot(x, &nf)).collect();
        (q, r, ma) = step(q, r, ma, &nmf, |q, r, f| modal(q, r, f, res));
    }
    let metrics = Metrics {
        max_shape_rms_error_m: max_rms,
        max_reference_shape_rms_m: ref_rms,
        max_shape_rms_relative: max_rms / ref_rms.max(1e-15),
        max_tip_shape_error_m: max_tip,
        max_reference_tip_shape_m: ref_tip,
        max_tip_shape_error_relative: max_tip / ref_tip.max(1e-15),
        max_center_of_mass_error_m: max_com,
    };
    let checks = serde_json::json!({"maxShapeRmsRelative":metrics.max_shape_rms_relative<=0.08,"maxTipShapeErrorRelative":metrics.max_tip_shape_error_relative<=0.12,"maxCenterOfMassErrorM":max_com<=1e-10});
    let qualified = checks.as_object().unwrap().values().all(|v| v == true);
    Workload {
        name: name.into(),
        expected_to_qualify: name == "smooth",
        qualified,
        metrics,
        gates: serde_json::json!({"maxShapeRmsRelative":0.08,"maxTipShapeErrorRelative":0.12,"maxCenterOfMassErrorM":1e-10}),
        checks,
        samples,
    }
}
pub fn report() -> Report {
    let (v, e) = modes();
    let ortho = (0..N)
        .flat_map(|i| {
            (0..N).map({
                let v = &v;
                move |j| (dot(&v[i], &v[j]) - if i == j { 1. } else { 0. }).abs()
            })
        })
        .fold(0f64, f64::max);
    let workloads = vec![run("smooth", &v, &e), run("localizedImpulse", &v, &e)];
    let qualified = workloads[0].qualified && !workloads[1].qualified;
    Report {
        schema: "ksp-continuum-modal-rocket/v1",
        experiment: "MODAL-ROCKET-001",
        qualified,
        interpretation: "The smooth workload passes and the localized impulse remains a retained failure.",
        production_physics_qualified: false,
        stock_behavior_qualified: false,
        performance_win_claimed: false,
        deterministic_within_rust_build: true,
        model: serde_json::json!({"bodyCount":N,"massPerBodyKg":1.,"springStiffnessNPerM":K,"dashpotNsPerM":C,"boundary":"free-free one-dimensional uniform nearest-neighbor chain","fullStateScalars":2*N,"retainedRigidModes":1,"retainedFlexibleModes":6,"reducedStateScalars":2*RETAINED,"modeConstruction":"exact discrete cosine eigenvectors","maxOrthonormalityError":ortho}),
        schedule: serde_json::json!({"stepSeconds":DT,"durationSeconds":2.,"stepsPerWorkload":STEPS,"sampleStride":100}),
        bounds: serde_json::json!({"fixedWorkloadCount":2,"maximumBodies":N,"maximumFlexibleModes":6,"maximumStepsPerWorkload":STEPS}),
        workloads,
        environment: serde_json::json!({"implementation":"rust","rust":option_env!("RUSTC_VERSION").unwrap_or("unknown")}),
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn retained_failure_is_preserved() {
        let r = report();
        assert!(r.qualified);
        assert!(r.workloads[0].qualified);
        assert!(!r.workloads[1].qualified);
        assert_eq!(r.workloads[0].samples.len(), 21)
    }
    #[test]
    fn modes_are_orthonormal() {
        let (v, _) = modes();
        for i in 0..N {
            for j in 0..N {
                assert!((dot(&v[i], &v[j]) - if i == j { 1. } else { 0. }).abs() < 1e-12)
            }
        }
    }
}
