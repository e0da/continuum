use continuum_numerics::execution_view::{IntegrationView, fixture};
use continuum_numerics::gpu_execution::{GpuIntegrator, GpuOutput};
use serde::Serialize;
use std::hint::black_box;
use std::time::{Duration, Instant};

const SIZES: &[usize] = &[
    16, 64, 256, 1_024, 4_096, 16_384, 65_536, 262_144, 1_048_576,
];
const CPU_SAMPLES: usize = 31;
const GPU_SAMPLES: usize = 15;
const WARMUPS: usize = 3;
const RESIDENT_DISPATCHES: u32 = 64;
const DT: f64 = 1.0 / 60.0;
const ABSOLUTE_TOLERANCE: f64 = 0.05;
const RELATIVE_TOLERANCE: f64 = 0.000_01;

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct Timing {
    median_ns: f64,
    p95_ns: f64,
    ns_per_entity_at_median: f64,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Accuracy {
    scalar_steps: u64,
    gpu_precision: &'static str,
    scalar_precision: &'static str,
    maximum_absolute_error: f64,
    maximum_relative_error: f64,
    absolute_tolerance: f64,
    relative_tolerance: f64,
    within_mixed_tolerance: bool,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Row {
    entities: usize,
    pack: Timing,
    scalar: Timing,
    cpu_parallel: Timing,
    gpu_cold_end_to_end: Timing,
    gpu_resident_amortized_per_step: Timing,
    gpu_cold_speedup_vs_scalar: f64,
    gpu_resident_speedup_vs_scalar: f64,
    gpu_resident_speedup_vs_cpu_parallel: f64,
    scalar_checksum: String,
    cpu_parallel_checksum: String,
    cpu_exact_match: bool,
    gpu_cold_accuracy: Accuracy,
    gpu_resident_accuracy: Accuracy,
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
    gpu_cold_end_to_end: CostEstimate,
    gpu_resident_amortized_per_step: CostEstimate,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Report {
    schema: &'static str,
    architecture: &'static str,
    operating_system: &'static str,
    gpu_adapter: String,
    rayon_threads: usize,
    cpu_samples_per_case: usize,
    gpu_samples_per_case: usize,
    resident_dispatches_per_sample: u32,
    scalar_implementation: &'static str,
    parallel_implementation: &'static str,
    gpu_implementation: &'static str,
    observed_parallel_crossover_entities: Option<usize>,
    observed_gpu_cold_crossover_vs_scalar_entities: Option<usize>,
    observed_gpu_resident_crossover_vs_scalar_entities: Option<usize>,
    observed_gpu_resident_crossover_vs_parallel_entities: Option<usize>,
    estimates: Estimates,
    rows: Vec<Row>,
}

fn main() {
    if let Err(error) = pollster::block_on(run()) {
        eprintln!("execution-view-bench: {error}");
        std::process::exit(1);
    }
}

async fn run() -> Result<(), String> {
    let output = continuum_numerics::output_argument()?;
    let gpu = GpuIntegrator::new().await?;
    let gpu_adapter = gpu.adapter_identity();
    let mut rows = Vec::new();
    for &count in SIZES {
        eprintln!("measuring {count} entities on {gpu_adapter}");
        let bodies = fixture(count);
        let baseline = IntegrationView::pack(&bodies);
        let pack_samples = measure(CPU_SAMPLES, || {
            black_box(IntegrationView::pack(black_box(&bodies)))
        });
        let mut scalar_timing_view = baseline.clone();
        let scalar_samples = measure(CPU_SAMPLES, || {
            scalar_timing_view.integrate_scalar(black_box(DT))
        });
        black_box(scalar_timing_view.checksum());
        let mut parallel_timing_view = baseline.clone();
        let parallel_samples = measure(CPU_SAMPLES, || {
            parallel_timing_view.integrate_parallel(black_box(DT))
        });
        black_box(parallel_timing_view.checksum());

        let mut scalar_result = baseline.clone();
        let mut parallel_result = baseline.clone();
        scalar_result.integrate_scalar(DT);
        parallel_result.integrate_parallel(DT);
        let scalar_checksum = scalar_result.checksum();
        let parallel_checksum = parallel_result.checksum();

        let (cold_samples, cold_output) =
            measure_result(GPU_SAMPLES, || gpu.cold_integrate(&baseline, DT as f32))?;
        let cold_accuracy = accuracy(&scalar_result, &cold_output, 1);

        let resident = gpu.upload_resident(&baseline, DT as f32);
        for _ in 0..WARMUPS {
            black_box(gpu.integrate_resident(&resident, RESIDENT_DISPATCHES)?);
        }
        let mut resident_samples = Vec::with_capacity(GPU_SAMPLES);
        let mut resident_output = None;
        for _ in 0..GPU_SAMPLES {
            let started = Instant::now();
            let value = gpu.integrate_resident(&resident, RESIDENT_DISPATCHES)?;
            resident_samples.push(started.elapsed().div_f64(RESIDENT_DISPATCHES as f64));
            resident_output = Some(value);
        }
        let resident_steps = (WARMUPS + GPU_SAMPLES) as u64 * RESIDENT_DISPATCHES as u64;
        let mut resident_scalar = baseline.clone();
        for _ in 0..resident_steps {
            resident_scalar.integrate_scalar(DT);
        }
        let resident_accuracy = accuracy(
            &resident_scalar,
            resident_output
                .as_ref()
                .ok_or("resident GPU produced no output")?,
            resident_steps,
        );
        eprintln!(
            "accuracy {count}: cold abs={:.6e} rel={:.6e}; resident abs={:.6e} rel={:.6e}",
            cold_accuracy.maximum_absolute_error,
            cold_accuracy.maximum_relative_error,
            resident_accuracy.maximum_absolute_error,
            resident_accuracy.maximum_relative_error,
        );

        let pack = summarize(&pack_samples, count);
        let scalar = summarize(&scalar_samples, count);
        let cpu_parallel = summarize(&parallel_samples, count);
        let gpu_cold_end_to_end = summarize(&cold_samples, count);
        let gpu_resident_amortized_per_step = summarize(&resident_samples, count);
        rows.push(Row {
            entities: count,
            gpu_cold_speedup_vs_scalar: scalar.median_ns / gpu_cold_end_to_end.median_ns,
            gpu_resident_speedup_vs_scalar: scalar.median_ns
                / gpu_resident_amortized_per_step.median_ns,
            gpu_resident_speedup_vs_cpu_parallel: cpu_parallel.median_ns
                / gpu_resident_amortized_per_step.median_ns,
            pack,
            scalar,
            cpu_parallel,
            gpu_cold_end_to_end,
            gpu_resident_amortized_per_step,
            scalar_checksum: format!("{scalar_checksum:016x}"),
            cpu_parallel_checksum: format!("{parallel_checksum:016x}"),
            cpu_exact_match: scalar_checksum == parallel_checksum,
            gpu_cold_accuracy: cold_accuracy,
            gpu_resident_accuracy: resident_accuracy,
        });
    }

    if rows.iter().any(|row| !row.cpu_exact_match) {
        return Err("scalar and CPU-parallel results diverged".into());
    }
    if rows.iter().any(|row| {
        !row.gpu_cold_accuracy.within_mixed_tolerance
            || !row.gpu_resident_accuracy.within_mixed_tolerance
    }) {
        return Err("GPU output exceeded the declared mixed absolute/relative tolerance".into());
    }
    let report = Report {
        schema: "continuum-execution-view-bench/v2",
        architecture: std::env::consts::ARCH,
        operating_system: std::env::consts::OS,
        gpu_adapter,
        rayon_threads: rayon::current_num_threads(),
        cpu_samples_per_case: CPU_SAMPLES,
        gpu_samples_per_case: GPU_SAMPLES,
        resident_dispatches_per_sample: RESIDENT_DISPATCHES,
        scalar_implementation: "safe Rust f64 SoA loop; compiler autovectorization not asserted",
        parallel_implementation: "safe Rust f64 Rayon over independent SoA lanes",
        gpu_implementation: "wgpu WGSL f32 compute over three vec4 SoA columns with host f64 position anchors; Metal backend",
        observed_parallel_crossover_entities: stable_crossover(
            &rows,
            |row| row.cpu_parallel.median_ns,
            |row| row.scalar.median_ns,
        ),
        observed_gpu_cold_crossover_vs_scalar_entities: stable_crossover(
            &rows,
            |row| row.gpu_cold_end_to_end.median_ns,
            |row| row.scalar.median_ns,
        ),
        observed_gpu_resident_crossover_vs_scalar_entities: stable_crossover(
            &rows,
            |row| row.gpu_resident_amortized_per_step.median_ns,
            |row| row.scalar.median_ns,
        ),
        observed_gpu_resident_crossover_vs_parallel_entities: stable_crossover(
            &rows,
            |row| row.gpu_resident_amortized_per_step.median_ns,
            |row| row.cpu_parallel.median_ns,
        ),
        estimates: Estimates {
            pack: estimate(&rows, |row| row.pack.median_ns),
            scalar: estimate(&rows, |row| row.scalar.median_ns),
            cpu_parallel: estimate(&rows, |row| row.cpu_parallel.median_ns),
            gpu_cold_end_to_end: estimate(&rows, |row| row.gpu_cold_end_to_end.median_ns),
            gpu_resident_amortized_per_step: estimate(&rows, |row| {
                row.gpu_resident_amortized_per_step.median_ns
            }),
        },
        rows,
    };
    continuum_numerics::write_json_exclusive(&output, &report)
}

fn measure<T>(samples: usize, mut operation: impl FnMut() -> T) -> Vec<Duration> {
    for _ in 0..WARMUPS {
        black_box(operation());
    }
    let mut elapsed = Vec::with_capacity(samples);
    for _ in 0..samples {
        let started = Instant::now();
        black_box(operation());
        elapsed.push(started.elapsed());
    }
    elapsed
}

fn measure_result<T>(
    samples: usize,
    mut operation: impl FnMut() -> Result<T, String>,
) -> Result<(Vec<Duration>, T), String> {
    for _ in 0..WARMUPS {
        black_box(operation()?);
    }
    let mut elapsed = Vec::with_capacity(samples);
    let mut result = None;
    for _ in 0..samples {
        let started = Instant::now();
        let value = operation()?;
        elapsed.push(started.elapsed());
        result = Some(value);
    }
    Ok((elapsed, result.ok_or("measurement produced no result")?))
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

fn accuracy(reference: &IntegrationView, actual: &GpuOutput, scalar_steps: u64) -> Accuracy {
    let references = [
        &reference.position_x,
        &reference.position_y,
        &reference.position_z,
        &reference.velocity_x,
        &reference.velocity_y,
        &reference.velocity_z,
    ];
    let actuals = [
        &actual.position_x,
        &actual.position_y,
        &actual.position_z,
        &actual.velocity_x,
        &actual.velocity_y,
        &actual.velocity_z,
    ];
    let mut maximum_absolute_error = 0.0_f64;
    let mut maximum_relative_error = 0.0_f64;
    let mut within_mixed_tolerance = true;
    for (reference_column, actual_column) in references.into_iter().zip(actuals) {
        for (&expected, &observed) in reference_column.iter().zip(actual_column) {
            let absolute = (expected - observed).abs();
            let relative = absolute / expected.abs().max(f64::MIN_POSITIVE);
            maximum_absolute_error = maximum_absolute_error.max(absolute);
            maximum_relative_error = maximum_relative_error.max(relative);
            within_mixed_tolerance &=
                absolute <= ABSOLUTE_TOLERANCE || relative <= RELATIVE_TOLERANCE;
        }
    }
    Accuracy {
        scalar_steps,
        gpu_precision: "f32",
        scalar_precision: "f64",
        maximum_absolute_error,
        maximum_relative_error,
        absolute_tolerance: ABSOLUTE_TOLERANCE,
        relative_tolerance: RELATIVE_TOLERANCE,
        within_mixed_tolerance,
    }
}

fn stable_crossover(
    rows: &[Row],
    contender: impl Fn(&Row) -> f64,
    reference: impl Fn(&Row) -> f64,
) -> Option<usize> {
    rows.iter().enumerate().find_map(|(index, row)| {
        (contender(row) < reference(row)
            && rows[index..]
                .iter()
                .all(|later| contender(later) < reference(later)))
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
        let accuracy = || Accuracy {
            scalar_steps: 1,
            gpu_precision: "f32",
            scalar_precision: "f64",
            maximum_absolute_error: 0.0,
            maximum_relative_error: 0.0,
            absolute_tolerance: 1.0,
            relative_tolerance: 1.0,
            within_mixed_tolerance: true,
        };
        let row = |entities, scalar, parallel| Row {
            entities,
            pack: timing(1.0),
            scalar: timing(scalar),
            cpu_parallel: timing(parallel),
            gpu_cold_end_to_end: timing(1.0),
            gpu_resident_amortized_per_step: timing(1.0),
            gpu_cold_speedup_vs_scalar: 1.0,
            gpu_resident_speedup_vs_scalar: 1.0,
            gpu_resident_speedup_vs_cpu_parallel: 1.0,
            scalar_checksum: String::new(),
            cpu_parallel_checksum: String::new(),
            cpu_exact_match: true,
            gpu_cold_accuracy: accuracy(),
            gpu_resident_accuracy: accuracy(),
        };
        let rows = vec![
            row(16, 1.0, 2.0),
            row(64, 3.0, 2.0),
            row(256, 3.0, 4.0),
            row(1024, 8.0, 4.0),
        ];
        assert_eq!(
            stable_crossover(
                &rows,
                |row| row.cpu_parallel.median_ns,
                |row| row.scalar.median_ns
            ),
            Some(1024)
        );
    }
}
