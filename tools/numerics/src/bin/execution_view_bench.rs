use continuum_numerics::execution_view::{IntegrationView, fixture};
use serde::Serialize;
use std::hint::black_box;
use std::time::{Duration, Instant};

const SIZES: &[usize] = &[
    16, 64, 256, 1_024, 4_096, 16_384, 65_536, 262_144, 1_048_576,
];
const SAMPLES: usize = 31;
const DT: f64 = 1.0 / 60.0;

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Timing {
    median_ns: f64,
    p95_ns: f64,
    ns_per_entity_at_median: f64,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Row {
    entities: usize,
    pack: Timing,
    scalar: Timing,
    cpu_parallel: Timing,
    scalar_checksum: String,
    cpu_parallel_checksum: String,
    exact_match: bool,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct CostEstimate {
    fixed_ns: f64,
    per_entity_ns: f64,
    r_squared: f64,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Estimates {
    pack: CostEstimate,
    scalar: CostEstimate,
    cpu_parallel: CostEstimate,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Report {
    schema: &'static str,
    architecture: &'static str,
    operating_system: &'static str,
    rayon_threads: usize,
    samples_per_case: usize,
    scalar_implementation: &'static str,
    parallel_implementation: &'static str,
    observed_parallel_crossover_entities: Option<usize>,
    estimates: Estimates,
    rows: Vec<Row>,
}

fn main() {
    if let Err(error) = run() {
        eprintln!("execution-view-bench: {error}");
        std::process::exit(1);
    }
}

fn run() -> Result<(), String> {
    let output = continuum_numerics::output_argument()?;
    let mut rows = Vec::new();
    for &count in SIZES {
        let bodies = fixture(count);
        let baseline = IntegrationView::pack(&bodies);
        let pack = measure(|| black_box(IntegrationView::pack(black_box(&bodies))));
        let mut scalar_timing_view = baseline.clone();
        let scalar = measure(|| scalar_timing_view.integrate_scalar(black_box(DT)));
        black_box(scalar_timing_view.checksum());
        let mut parallel_timing_view = baseline.clone();
        let parallel = measure(|| parallel_timing_view.integrate_parallel(black_box(DT)));
        black_box(parallel_timing_view.checksum());

        let mut scalar_result = baseline.clone();
        let mut parallel_result = baseline.clone();
        scalar_result.integrate_scalar(DT);
        parallel_result.integrate_parallel(DT);
        let scalar_checksum = scalar_result.checksum();
        let parallel_checksum = parallel_result.checksum();
        rows.push(Row {
            entities: count,
            pack: summarize(&pack, count),
            scalar: summarize(&scalar, count),
            cpu_parallel: summarize(&parallel, count),
            scalar_checksum: format!("{scalar_checksum:016x}"),
            cpu_parallel_checksum: format!("{parallel_checksum:016x}"),
            exact_match: scalar_checksum == parallel_checksum,
        });
    }

    if rows.iter().any(|row| !row.exact_match) {
        return Err("scalar and CPU-parallel results diverged".into());
    }
    let report = Report {
        schema: "continuum-execution-view-bench/v1",
        architecture: std::env::consts::ARCH,
        operating_system: std::env::consts::OS,
        rayon_threads: rayon::current_num_threads(),
        samples_per_case: SAMPLES,
        scalar_implementation: "safe Rust SoA loop; compiler autovectorization not asserted",
        parallel_implementation: "safe Rust Rayon over independent SoA lanes",
        observed_parallel_crossover_entities: stable_crossover(&rows),
        estimates: Estimates {
            pack: estimate(&rows, |row| row.pack.median_ns),
            scalar: estimate(&rows, |row| row.scalar.median_ns),
            cpu_parallel: estimate(&rows, |row| row.cpu_parallel.median_ns),
        },
        rows,
    };
    continuum_numerics::write_json_exclusive(&output, &report)
}

fn measure<T>(mut operation: impl FnMut() -> T) -> Vec<Duration> {
    for _ in 0..3 {
        black_box(operation());
    }
    let mut elapsed = Vec::with_capacity(SAMPLES);
    for _ in 0..SAMPLES {
        let started = Instant::now();
        black_box(operation());
        elapsed.push(started.elapsed());
    }
    elapsed
}

fn summarize(samples: &[Duration], entities: usize) -> Timing {
    let mut nanoseconds: Vec<f64> = samples
        .iter()
        .map(|value| value.as_nanos() as f64)
        .collect();
    nanoseconds.sort_by(f64::total_cmp);
    let median = nanoseconds[nanoseconds.len() / 2];
    let p95 = nanoseconds
        [((nanoseconds.len() as f64 * 0.95).ceil() as usize - 1).min(nanoseconds.len() - 1)];
    Timing {
        median_ns: median,
        p95_ns: p95,
        ns_per_entity_at_median: median / entities as f64,
    }
}

fn stable_crossover(rows: &[Row]) -> Option<usize> {
    rows.iter().enumerate().find_map(|(index, row)| {
        (row.cpu_parallel.median_ns < row.scalar.median_ns
            && rows[index..]
                .iter()
                .all(|later| later.cpu_parallel.median_ns < later.scalar.median_ns))
        .then_some(row.entities)
    })
}

fn estimate(rows: &[Row], select: impl Fn(&Row) -> f64) -> CostEstimate {
    let count = rows.len() as f64;
    let mean_x = rows.iter().map(|row| row.entities as f64).sum::<f64>() / count;
    let mean_y = rows.iter().map(&select).sum::<f64>() / count;
    let covariance = rows
        .iter()
        .map(|row| (row.entities as f64 - mean_x) * (select(row) - mean_y))
        .sum::<f64>();
    let variance_x = rows
        .iter()
        .map(|row| (row.entities as f64 - mean_x).powi(2))
        .sum::<f64>();
    let per_entity = covariance / variance_x;
    let fixed = mean_y - per_entity * mean_x;
    let residual = rows
        .iter()
        .map(|row| (select(row) - (fixed + per_entity * row.entities as f64)).powi(2))
        .sum::<f64>();
    let total = rows
        .iter()
        .map(|row| (select(row) - mean_y).powi(2))
        .sum::<f64>();
    CostEstimate {
        fixed_ns: fixed,
        per_entity_ns: per_entity,
        r_squared: 1.0 - residual / total,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn crossover_requires_all_larger_sizes_to_win() {
        let timing = |value| Timing {
            median_ns: value,
            p95_ns: value,
            ns_per_entity_at_median: value,
        };
        let row = |entities, scalar, parallel| Row {
            entities,
            pack: timing(1.0),
            scalar: timing(scalar),
            cpu_parallel: timing(parallel),
            scalar_checksum: String::new(),
            cpu_parallel_checksum: String::new(),
            exact_match: true,
        };
        let rows = vec![
            row(16, 1.0, 2.0),
            row(64, 3.0, 2.0),
            row(256, 3.0, 4.0),
            row(1024, 8.0, 4.0),
        ];
        assert_eq!(stable_crossover(&rows), Some(1024));
    }
}
