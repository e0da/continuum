use continuum_numerics::execution_view::{Body, IntegrationView, fixture};
use continuum_numerics::persistent_execution_view::{
    CanonicalWorld, EntityKey, PersistentIntegrationView, fixture_world,
};
use serde::Serialize;
use std::hint::black_box;
use std::time::{Duration, Instant};

const SIZES: &[usize] = &[1_024, 16_384, 262_144, 1_048_576];
const DIRTY_PER_MILLE: &[usize] = &[0, 1, 10, 100, 1_000];
const SAMPLES: usize = 21;
const VALIDATION_STEPS: usize = 8;
const DT: f64 = 1.0 / 60.0;

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Timing {
    median_ns: u128,
    p95_ns: u128,
    ns_per_dirty_entity_at_median: Option<f64>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Row {
    entities: usize,
    dirty_per_mille: usize,
    dirty_entities: usize,
    full_repack: Timing,
    incremental_refresh: Timing,
    incremental_speedup: f64,
    exact_before_kernel: bool,
    exact_after_scalar_steps: bool,
    scalar_parallel_exact: bool,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Crossover {
    entities: usize,
    largest_measured_dirty_per_mille_where_incremental_wins: Option<usize>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Report {
    schema: &'static str,
    architecture: &'static str,
    operating_system: &'static str,
    samples_per_case: usize,
    validation_steps: usize,
    rayon_threads: usize,
    rows: Vec<Row>,
    crossovers: Vec<Crossover>,
}

fn main() {
    if let Err(error) = run() {
        eprintln!("persistent-execution-view-bench: {error}");
        std::process::exit(1);
    }
}

fn run() -> Result<(), String> {
    let output = continuum_numerics::output_argument()?;
    let mut rows = Vec::new();
    for &count in SIZES {
        for &dirty_per_mille in DIRTY_PER_MILLE {
            let mut world = fixture_world(count);
            let mut canonical_bodies = fixture(count);
            let mut cached = PersistentIntegrationView::pack(&world);
            let dirty = mutate(
                &mut world,
                &mut canonical_bodies,
                cached.keys(),
                dirty_per_mille,
            );

            let repack_samples = measure(|| IntegrationView::pack(black_box(&canonical_bodies)));
            let refresh_samples = measure(|| {
                cached
                    .refresh(black_box(&world), black_box(&dirty))
                    .expect("fixture keys must remain current")
            });

            let rebuilt = PersistentIntegrationView::pack(&world);
            let exact_before_kernel = exact(&cached, &rebuilt)
                && exact_view(&cached.view, &IntegrationView::pack(&canonical_bodies));
            let mut scalar_cached = cached.clone();
            let mut scalar_rebuilt = rebuilt.clone();
            let mut parallel_cached = cached.clone();
            for _ in 0..VALIDATION_STEPS {
                scalar_cached.view.integrate_scalar(DT);
                scalar_rebuilt.view.integrate_scalar(DT);
                parallel_cached.view.integrate_parallel(DT);
            }
            let exact_after_scalar_steps = exact(&scalar_cached, &scalar_rebuilt);
            let scalar_parallel_exact = exact(&scalar_cached, &parallel_cached);
            if !(exact_before_kernel && exact_after_scalar_steps && scalar_parallel_exact) {
                return Err(format!(
                    "exact validation failed for {count} entities at {dirty_per_mille} per mille dirty"
                ));
            }

            let full_repack = summarize(&repack_samples, dirty.len());
            let incremental_refresh = summarize(&refresh_samples, dirty.len());
            rows.push(Row {
                entities: count,
                dirty_per_mille,
                dirty_entities: dirty.len(),
                incremental_speedup: full_repack.median_ns as f64
                    / incremental_refresh.median_ns.max(1) as f64,
                full_repack,
                incremental_refresh,
                exact_before_kernel,
                exact_after_scalar_steps,
                scalar_parallel_exact,
            });
        }
    }

    let crossovers = SIZES
        .iter()
        .map(|&entities| Crossover {
            entities,
            largest_measured_dirty_per_mille_where_incremental_wins: rows
                .iter()
                .filter(|row| {
                    row.entities == entities
                        && row.incremental_refresh.median_ns < row.full_repack.median_ns
                })
                .map(|row| row.dirty_per_mille)
                .max(),
        })
        .collect();
    let report = Report {
        schema: "continuum-persistent-execution-view-bench/v1",
        architecture: std::env::consts::ARCH,
        operating_system: std::env::consts::OS,
        samples_per_case: SAMPLES,
        validation_steps: VALIDATION_STEPS,
        rayon_threads: rayon::current_num_threads(),
        rows,
        crossovers,
    };
    continuum_numerics::write_json_exclusive(&output, &report)
}

fn mutate(
    world: &mut CanonicalWorld,
    canonical_bodies: &mut [Body],
    keys: &[EntityKey],
    dirty_per_mille: usize,
) -> Vec<EntityKey> {
    let dirty_count = if dirty_per_mille == 0 {
        0
    } else {
        (keys.len() * dirty_per_mille).div_ceil(1_000)
    };
    if dirty_count == 0 {
        return Vec::new();
    }
    let mut dirty = Vec::with_capacity(dirty_count);
    for ordinal in 0..dirty_count {
        let index = ordinal * keys.len() / dirty_count;
        let key = keys[index];
        let body = world.get_mut(key).expect("fixture key must exist");
        body.force[0] += (ordinal + 1) as f64 * 0.000_001;
        body.velocity[2] -= (key.slot + 1) as f64 * 0.000_000_1;
        canonical_bodies[index] = *body;
        dirty.push(key);
    }
    dirty
}

fn exact(left: &PersistentIntegrationView, right: &PersistentIntegrationView) -> bool {
    left.keys() == right.keys() && exact_view(&left.view, &right.view)
}

fn exact_view(left: &IntegrationView, right: &IntegrationView) -> bool {
    left.position_x == right.position_x
        && left.position_y == right.position_y
        && left.position_z == right.position_z
        && left.velocity_x == right.velocity_x
        && left.velocity_y == right.velocity_y
        && left.velocity_z == right.velocity_z
        && left.force_x == right.force_x
        && left.force_y == right.force_y
        && left.force_z == right.force_z
        && left.inverse_mass == right.inverse_mass
}

fn measure<T>(mut operation: impl FnMut() -> T) -> Vec<Duration> {
    for _ in 0..3 {
        black_box(operation());
    }
    (0..SAMPLES)
        .map(|_| {
            let started = Instant::now();
            black_box(operation());
            started.elapsed()
        })
        .collect()
}

fn summarize(samples: &[Duration], dirty_entities: usize) -> Timing {
    let mut nanoseconds: Vec<u128> = samples.iter().map(Duration::as_nanos).collect();
    nanoseconds.sort_unstable();
    let median = nanoseconds[nanoseconds.len() / 2];
    let p95 = nanoseconds
        [((nanoseconds.len() as f64 * 0.95).ceil() as usize - 1).min(nanoseconds.len() - 1)];
    Timing {
        median_ns: median,
        p95_ns: p95,
        ns_per_dirty_entity_at_median: (dirty_entities != 0)
            .then_some(median as f64 / dirty_entities as f64),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn mutation_samples_unique_evenly_spaced_keys() {
        let mut world = fixture_world(10_000);
        let mut canonical_bodies = fixture(10_000);
        let view = PersistentIntegrationView::pack(&world);
        let dirty = mutate(&mut world, &mut canonical_bodies, view.keys(), 1);
        assert_eq!(dirty.len(), 10);
        assert_eq!(dirty[0].slot, 0);
        assert_eq!(dirty[9].slot, 9_000);
    }
}
