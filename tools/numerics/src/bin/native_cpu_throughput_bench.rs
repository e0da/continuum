use continuum_numerics::cpu_throughput::{
    AosView, BlockedView, SoaView, checksum_bodies, fixture, materialize_blocked, materialize_soa,
};
use serde::Serialize;
use std::hint::black_box;
use std::time::{Duration, Instant};

const COUNTS: &[usize] = &[16_384, 65_536, 262_144, 1_048_576, 4_194_304];
const DT: f64 = 1.0 / 60.0;
const SAMPLES: usize = 15;
const WARMUP_WINDOWS: usize = 3;
const MINIMUM_WINDOW: Duration = Duration::from_millis(5);
const CALIBRATION_WINDOW: Duration = Duration::from_millis(10);
const MAXIMUM_STEPS_PER_WINDOW: usize = 16_384;

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Timing {
    median_ns_per_step: f64,
    p95_ns_per_step: f64,
    median_entities_per_second: f64,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Row {
    entities: usize,
    steps_per_timed_window: usize,
    minimum_observed_window_ns: u128,
    aos_scalar: Timing,
    soa_scalar: Timing,
    blocked_scalar: Timing,
    blocked_parallel: Timing,
    soa_speedup_vs_aos: f64,
    blocked_speedup_vs_aos: f64,
    parallel_speedup_vs_aos: f64,
    exact_output_match: bool,
    output_checksum: String,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Report {
    schema: &'static str,
    architecture: &'static str,
    operating_system: &'static str,
    rayon_threads: usize,
    logical_parallelism: usize,
    samples_per_case: usize,
    warmup_windows: usize,
    minimum_requested_window_ns: u128,
    baseline: &'static str,
    soa: &'static str,
    blocked: &'static str,
    parallel: &'static str,
    scope: &'static str,
    rows: Vec<Row>,
}

fn main() {
    if let Err(error) = run() {
        eprintln!("native-cpu-throughput-bench: {error}");
        std::process::exit(1);
    }
}

fn run() -> Result<(), String> {
    let output = continuum_numerics::output_argument()?;
    let mut rows = Vec::new();
    for &count in COUNTS {
        eprintln!("calibrating {count} entities");
        let initial = fixture(count);
        verify_one_step(&initial)?;
        let steps = calibrate(&initial);
        let mut aos = AosView(initial.clone());
        let mut soa = SoaView::from_bodies(&initial);
        let mut blocked = BlockedView::from_bodies(&initial);
        let mut parallel = BlockedView::from_bodies(&initial);
        let mut samples = [Vec::new(), Vec::new(), Vec::new(), Vec::new()];
        for round in 0..(WARMUP_WINDOWS + SAMPLES) {
            for offset in 0..4 {
                let strategy = (round + offset) % 4;
                let elapsed = match strategy {
                    0 => timed(|| repeat(steps, || aos.integrate(black_box(DT)))),
                    1 => timed(|| repeat(steps, || soa.integrate(black_box(DT)))),
                    2 => timed(|| repeat(steps, || blocked.integrate(black_box(DT)))),
                    _ => timed(|| repeat(steps, || parallel.integrate_parallel(black_box(DT)))),
                };
                if round >= WARMUP_WINDOWS {
                    samples[strategy].push(elapsed);
                }
            }
        }
        let aos_output = aos.0;
        let soa_output = materialize_soa(&soa);
        let blocked_output = materialize_blocked(&blocked);
        let parallel_output = materialize_blocked(&parallel);
        let exact = aos_output == soa_output
            && aos_output == blocked_output
            && aos_output == parallel_output;
        if !exact {
            return Err(format!("layout outputs diverged at {count} entities"));
        }
        let mut timings: Vec<Timing> = samples
            .iter()
            .map(|values| summarize(values, steps, count))
            .collect();
        let minimum = samples
            .iter()
            .flatten()
            .map(Duration::as_nanos)
            .min()
            .unwrap_or(0);
        if minimum < MINIMUM_WINDOW.as_nanos() {
            return Err(format!(
                "timed window below resolution guard at {count}: {minimum} ns"
            ));
        }
        rows.push(Row {
            entities: count,
            steps_per_timed_window: steps,
            minimum_observed_window_ns: minimum,
            soa_speedup_vs_aos: timings[0].median_ns_per_step / timings[1].median_ns_per_step,
            blocked_speedup_vs_aos: timings[0].median_ns_per_step / timings[2].median_ns_per_step,
            parallel_speedup_vs_aos: timings[0].median_ns_per_step / timings[3].median_ns_per_step,
            aos_scalar: timings.remove(0),
            soa_scalar: timings.remove(0),
            blocked_scalar: timings.remove(0),
            blocked_parallel: timings.remove(0),
            exact_output_match: exact,
            output_checksum: format!("{:016x}", checksum_bodies(&aos_output)),
        });
    }
    continuum_numerics::write_json_exclusive(
        &output,
        &Report {
            schema: "continuum-native-cpu-throughput/v1",
            architecture: std::env::consts::ARCH,
            operating_system: std::env::consts::OS,
            rayon_threads: rayon::current_num_threads(),
            logical_parallelism: std::thread::available_parallelism()
                .map(usize::from)
                .unwrap_or(1),
            samples_per_case: SAMPLES,
            warmup_windows: WARMUP_WINDOWS,
            minimum_requested_window_ns: MINIMUM_WINDOW.as_nanos(),
            baseline: "safe Rust f64 array-of-structs scalar loop; persistent state; compiler autovectorization not asserted",
            soa: "safe Rust f64 single-allocation cache-skewed structure-of-arrays loop; persistent state",
            blocked: "safe Rust f64 eight-lane array-of-structs-of-arrays scalar loop; persistent state; compiler autovectorization not asserted",
            parallel: "same eight-lane blocked layout using the persistent process-global Rayon pool; every timed window includes dispatch and join",
            scope: "Synthetic independent-body integration only; excludes KSP, Unity, capture, publication, native ABI, solver constraints, contacts, rendering, and frame-rate claims.",
            rows,
        },
    )
}

fn verify_one_step(initial: &[continuum_numerics::cpu_throughput::Body]) -> Result<(), String> {
    let mut aos = AosView(initial.to_vec());
    let mut soa = SoaView::from_bodies(initial);
    let mut blocked = BlockedView::from_bodies(initial);
    let mut parallel = blocked.clone();
    aos.integrate(DT);
    soa.integrate(DT);
    blocked.integrate(DT);
    parallel.integrate_parallel(DT);
    let expected = aos.0;
    if expected != materialize_soa(&soa)
        || expected != materialize_blocked(&blocked)
        || expected != materialize_blocked(&parallel)
    {
        return Err("one-step layout output mismatch".into());
    }
    Ok(())
}

fn calibrate(initial: &[continuum_numerics::cpu_throughput::Body]) -> usize {
    let mut steps = 1;
    loop {
        let durations = [
            {
                let mut v = AosView(initial.to_vec());
                timed(|| repeat(steps, || v.integrate(DT)))
            },
            {
                let mut v = SoaView::from_bodies(initial);
                timed(|| repeat(steps, || v.integrate(DT)))
            },
            {
                let mut v = BlockedView::from_bodies(initial);
                timed(|| repeat(steps, || v.integrate(DT)))
            },
            {
                let mut v = BlockedView::from_bodies(initial);
                timed(|| repeat(steps, || v.integrate_parallel(DT)))
            },
        ];
        if durations
            .iter()
            .all(|duration| *duration >= CALIBRATION_WINDOW)
            || steps >= MAXIMUM_STEPS_PER_WINDOW
        {
            return steps;
        }
        steps *= 2;
    }
}

fn repeat(steps: usize, mut operation: impl FnMut()) {
    for _ in 0..steps {
        operation();
        black_box(());
    }
}
fn timed(mut operation: impl FnMut()) -> Duration {
    let start = Instant::now();
    operation();
    black_box(());
    start.elapsed()
}

fn summarize(samples: &[Duration], steps: usize, entities: usize) -> Timing {
    let mut values: Vec<f64> = samples
        .iter()
        .map(|duration| duration.as_nanos() as f64 / steps as f64)
        .collect();
    values.sort_by(f64::total_cmp);
    let median = values[values.len() / 2];
    let p95 = values[((values.len() as f64 * 0.95).ceil() as usize - 1).min(values.len() - 1)];
    Timing {
        median_ns_per_step: median,
        p95_ns_per_step: p95,
        median_entities_per_second: entities as f64 * 1_000_000_000.0 / median,
    }
}
