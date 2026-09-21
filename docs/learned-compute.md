# Learned and stochastic compute

Continuum can use machine learning and Monte Carlo without making learned output authoritative. The useful pattern is a verified analytic path, a recorded truth-producing path, and optional candidates that compete on the same frozen workloads.

## First learned experiment: frame residual

The first candidate should learn only the residual left by the independent Krakensbane frame forecast:

```text
frame delta = analytic persistence forecast + learned residual
```

The shadow receipt already records the label as the observed endpoint frame velocity minus the captured frame velocity. Collection should span complete flight attempts, different craft, and distinct regimes. Train, validation, and test splits must be made by attempt and craft rather than by adjacent samples; neighboring physics steps are nearly duplicates.

Start with ridge regression, a small boosted tree, and a tiny multilayer perceptron. Candidate inputs may include timestep, body-relative position and velocity, central acceleration, current frame velocity, captured last correction, vessel mean velocity, altitude, situation, and warp/packed state. Add inputs only when an ablation shows value.

A learned candidate remains shadow-only unless it:

- reduces held-out frame-delta RMS by at least 25% versus analytic persistence;
- reduces final velocity RMS on complete held-out attempts without degrading the 99.9th percentile;
- returns finite output deterministically from frozen weights and preprocessing;
- abstains outside its declared feature domain;
- includes model, dataset-manifest, feature-schema, preprocessing, training-code, and validation hashes;
- costs less than the benefit it creates under the actual batch shape.

The first live coast contains enough samples to test the data path, not enough independent missions to train a trustworthy model. Online training during gameplay is out of scope. CPU inference should be benchmarked before GPU, Metal, Core ML, or Neural Engine dispatch because a tiny one-step model may cost less than accelerator scheduling.

## Monte Carlo experiments

Monte Carlo first belongs in two offline workflows.

`MC-CONTROL-001` perturbs checkpoint state, mass, thrust, ignition delay, horizontal velocity, target slope, and controller parameters. Each run records a seed, checkpoint hash, configuration hash, and controller revision. It measures landing success, touchdown velocity and tilt, fuel, target error, peak acceleration, oscillation, and tail sensitivity. Bayesian optimization may select controller settings, followed by validation on unseen seeds and live KSP.

`MC-RISK-001` runs seeded ensembles through the ordinary propagator for maneuver error, mass/thrust uncertainty, controller timing, terrain, and atmospheric uncertainty. Reports retain closest approach, fuel reserve, event time, touchdown state, constraint violations, and representative worst cases. A surrogate becomes useful only after profiling shows propagation dominates ensemble cost, and it must preserve tail events rather than merely mean position.

## Boundaries

Learned models may prioritize conservative encounter candidates or request refinement. They may not suppress an analytically unresolved collision or encounter. Spatial indexes, swept bounds, continuous collision detection, and deterministic island construction remain the collision broad phase. Monte Carlo may generate fragment distributions, blast envelopes, and adversarial workloads; it does not replace authoritative contact detection.

The development loop is:

1. establish the analytic baseline and immutable workload;
2. collect labels and preserve provenance;
3. compare analytic, stochastic, and learned strategies through the same interface;
4. reject candidates that miss accuracy, tail, determinism, domain, or cost gates;
5. publish only qualified strategies, with exact fallback always available.

An M4 Max with 128 GB is ample for these small models and large local ensembles. Representative data and honest validation are expected to be harder than training.
