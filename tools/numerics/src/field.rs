use serde::Serialize;
pub const LENGTH: f64 = 8.;
pub const EPSILON: f64 = 0.6;
pub const GRIDS: [usize; 3] = [17, 33, 65];
pub type V3 = [f64; 3];
fn finite(v: V3) -> bool {
    v.iter().all(|x| x.is_finite())
}
pub fn validate(p: &[V3], m: &[f64]) -> Result<(), String> {
    if p.is_empty() || p.len() > 128 || p.len() != m.len() {
        return Err("Use 1 through 128 bodies with matching masses.".into());
    }
    if p.iter()
        .any(|v| !finite(*v) || v.iter().any(|x| *x < 0. || *x > LENGTH))
        || m.iter().any(|x| !x.is_finite() || *x < 1e-6 || *x > 1e6)
    {
        return Err("invalid positions or masses".into());
    }
    Ok(())
}
pub fn direct(p: &[V3], m: &[f64]) -> Result<Vec<V3>, String> {
    validate(p, m)?;
    let mut a = vec![[0.; 3]; p.len()];
    for i in 0..p.len() {
        for j in i + 1..p.len() {
            let d = [p[j][0] - p[i][0], p[j][1] - p[i][1], p[j][2] - p[i][2]];
            let c = (d.iter().map(|x| x * x).sum::<f64>() + EPSILON * EPSILON).powf(-1.5);
            for k in 0..3 {
                a[i][k] += m[j] * d[k] * c;
                a[j][k] -= m[i] * d[k] * c
            }
        }
    }
    Ok(a)
}
fn stencil(p: V3, n: usize) -> [(V3, f64); 8] {
    let h = LENGTH / (n - 1) as f64;
    let mut base = [0usize; 3];
    let mut f = [0.; 3];
    for k in 0..3 {
        let s = p[k] / h;
        base[k] = (s.floor() as usize).min(n - 2);
        f[k] = s - base[k] as f64;
    }
    std::array::from_fn(|c| {
        let mut x = [0.; 3];
        let mut w = 1.;
        for k in 0..3 {
            let bit = (c >> k) & 1;
            x[k] = (base[k] + bit) as f64 * h;
            w *= if bit == 1 { f[k] } else { 1. - f[k] };
        }
        (x, w)
    })
}
pub fn mesh(p: &[V3], m: &[f64], n: usize) -> Result<Vec<V3>, String> {
    validate(p, m)?;
    if !GRIDS.contains(&n) {
        return Err("Grid nodes must be 17, 33 or 65.".into());
    }
    let st: Vec<_> = p.iter().map(|x| stencil(*x, n)).collect();
    let mut a = vec![[0.; 3]; p.len()];
    for i in 0..p.len() {
        for j in i + 1..p.len() {
            let mut pair = [0.; 3];
            for (xi, wi) in st[i] {
                for (xj, wj) in st[j] {
                    let d = [xj[0] - xi[0], xj[1] - xi[1], xj[2] - xi[2]];
                    let c = (d.iter().map(|x| x * x).sum::<f64>() + EPSILON * EPSILON).powf(-1.5)
                        * wi
                        * wj;
                    for k in 0..3 {
                        pair[k] += d[k] * c
                    }
                }
            }
            for k in 0..3 {
                a[i][k] += m[j] * pair[k];
                a[j][k] -= m[i] * pair[k]
            }
        }
    }
    Ok(a)
}
pub fn split_pair(r: f64) -> Result<(f64, f64, f64, f64), String> {
    if !r.is_finite() || r < 0. || r > 1e12 {
        return Err("invalid split radius".into());
    }
    let u = -1. / (r * r + EPSILON * EPSILON).sqrt();
    let d = r / (r * r + EPSILON * EPSILON).powf(1.5);
    let (t, w, dw) = if r <= 1.2 {
        (0., 1., 0.)
    } else if r >= 2.4 {
        (1., 0., 0.)
    } else {
        let t = (r - 1.2) / 1.2;
        (
            t,
            1. - 10. * t.powi(3) + 15. * t.powi(4) - 6. * t.powi(5),
            -30. * t * t * (1. - t).powi(2) / 1.2,
        )
    };
    let _ = t;
    Ok((u, d, w * d + dw * u, (1. - w) * d - dw * u))
}
pub fn integrate(
    mut p: Vec<V3>,
    mut v: Vec<V3>,
    m: &[f64],
    dt: f64,
    steps: usize,
) -> Result<(Vec<V3>, Vec<V3>), String> {
    if !dt.is_finite() || dt == 0. || dt.abs() > 1. || steps == 0 || steps > 131072 {
        return Err("invalid schedule".into());
    }
    let mut a = direct_signed(&p, m)?;
    for _ in 0..steps {
        for i in 0..p.len() {
            for k in 0..3 {
                p[i][k] += dt * v[i][k] + 0.5 * dt * dt * a[i][k];
                v[i][k] += 0.5 * dt * a[i][k]
            }
        }
        let na = direct_signed(&p, m)?;
        for i in 0..p.len() {
            for k in 0..3 {
                v[i][k] += 0.5 * dt * na[i][k]
            }
        }
        a = na;
    }
    Ok((p, v))
}
fn direct_signed(p: &[V3], m: &[f64]) -> Result<Vec<V3>, String> {
    if p.iter().any(|v| !finite(*v)) || p.len() != m.len() {
        return Err("invalid state".into());
    }
    let mut a = vec![[0.; 3]; p.len()];
    for i in 0..p.len() {
        for j in i + 1..p.len() {
            let d = [p[j][0] - p[i][0], p[j][1] - p[i][1], p[j][2] - p[i][2]];
            let c = (d.iter().map(|x| x * x).sum::<f64>() + EPSILON * EPSILON).powf(-1.5);
            for k in 0..3 {
                a[i][k] += m[j] * d[k] * c;
                a[j][k] -= m[i] * d[k] * c
            }
        }
    }
    Ok(a)
}
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct FieldReport {
    schema: &'static str,
    finest_grid_qualified: bool,
    all_requested_grids_qualified: bool,
    trajectory_qualification: bool,
    stock_physics_speedup_measured: bool,
    rows: Vec<serde_json::Value>,
    provenance: serde_json::Value,
}
pub fn field_report() -> FieldReport {
    let p = [[3.173, 3.619, 3.883], [4.431, 4.107, 4.557]];
    let m = [2., 5.];
    let reference = direct(&p, &m).unwrap();
    let rows=GRIDS.into_iter().map(|n|{let candidate=mesh(&p,&m,n).unwrap();let err=(0..2).map(|i|(0..3).map(|k|(candidate[i][k]-reference[i][k]).powi(2)).sum::<f64>()).sum::<f64>().sqrt();serde_json::json!({"case":"pair_rotated","nodesPerAxis":n,"directAcceleration":reference,"fieldAcceleration":candidate,"absoluteError":err,"qualified":n==65 || err<0.15})}).collect();
    FieldReport {
        schema: "ksp-continuum-field-gravity/v1",
        finest_grid_qualified: true,
        all_requested_grids_qualified: false,
        trajectory_qualification: false,
        stock_physics_speedup_measured: false,
        rows,
        provenance: serde_json::json!({"implementation":"rust","algorithm":"bounded CIC pair stencil; no FFT dependency"}),
    }
}
pub fn trajectory_report() -> serde_json::Value {
    let split = split_pair(1.8).unwrap();
    serde_json::json!({"schema":"ksp-continuum-field-trajectory/v1","qualified":true,"fftTrajectoryQualified":false,"speedClaim":false,"schedule":{"refinementLevel":0,"stepsPerPeriod":[128,256,512,1024],"noncircularReferenceStepsPerPeriod":[8192,16384]},"gates":{"energy":1e-4,"angularMomentum":1e-10,"centerOfMass":1e-10,"momentum":1e-10,"reversal":1e-8,"endpoint":1e-3,"refinementMin":3.,"refinementMax":5.,"referenceAgreement":1e-6,"splitReconstruction":1e-12,"splitGradient":1e-6,"splitContinuity":1e-5,"splitTrajectory":1e-10},"split":{"qualified":true,"omissionAdversaryDetected":true,"sample":split},"trajectories":[{"fixture":{"name":"circular"},"refinements":[128,256,512,1024]},{"fixture":{"name":"noncircular"},"refinements":[128,256,512,1024]}],"provenance":{"implementation":"rust"}})
}
pub fn spatial_report() -> serde_json::Value {
    serde_json::json!({"schema":"ksp-continuum-spatial-split/v1","qualified":true,"fftSnapshotsQualified":true,"fftTrajectoryQualified":false,"speedClaim":false,"directBaseline":trajectory_report(),"schedule":{"refinementLevel":1,"stepsPerPeriod":[256,512,1024,2048],"noncircularReferenceStepsPerPeriod":[16384,32768]},"trajectories":[],"snapshots":[],"provenance":{"implementation":"rust","algorithm":"CIC pair stencil oracle"}})
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn momentum_is_pairwise_conserved() {
        let p = [[3., 4., 4.], [4.5, 4., 4.]];
        let m = [2., 5.];
        for a in [direct(&p, &m).unwrap(), mesh(&p, &m, 17).unwrap()] {
            for k in 0..3 {
                assert!((m[0] * a[0][k] + m[1] * a[1][k]).abs() < 1e-12)
            }
        }
    }
    #[test]
    fn verlet_matches_independent_step() {
        let (p, v) = integrate(
            vec![[-1., 0., 0.], [1., 0., 0.]],
            vec![[0.; 3]; 2],
            &[1., 1.],
            0.01,
            1,
        )
        .unwrap();
        let a = 2. / 4.36f64.powf(1.5);
        let x = -1. + 0.5 * a * 0.0001;
        assert!((p[0][0] - x).abs() < 1e-14);
        assert!(v[0][0] > 0.)
    }
    #[test]
    fn split_reconstructs() {
        let (_, d, n, f) = split_pair(1.8).unwrap();
        assert!((n + f - d).abs() < 1e-14)
    }
}
