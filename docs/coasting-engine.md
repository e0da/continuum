# Portable coasting engine slice

`CoastingEngine` demonstrates a Continuum-owned simulation clock without Unity or KSP. It retains an immutable initial
central-gravity state and evaluates every body directly at a requested target time with universal-variable Kepler
propagation. The engine stops exactly at the target-time event. This first event is not SOI, collision, maneuver, or
terrain event detection.

The propagator is a C# port of Gimbal's `gimbal-orbit::TwoBodyProblem::propagate_coast` universal-anomaly and Stumpff
kernel. Continuum keeps the compact port in Core because its installed C# consumers cannot depend on Gimbal's Rust
workspace and the current native boundary does not expose orbital propagation.

Independent bodies are divided into configurable parallel work batches. Presentation snapshots may be requested at
any positive time cadence, but each snapshot is an immutable evaluation of engine state and cannot feed back into the
trajectory. Tests require bitwise-identical final states across work batch sizes 1, 7, and 64 and presentation cadences
17 seconds, 311 seconds, and disabled. One advance permits at most 4,096 intermediate publications.

`SampleAt(UT)` evaluates an immutable snapshot at any finite absolute time without moving the engine frontier. An
adapter can therefore project a forecast or a historical sample without rewinding authoritative engine time.

The universal-anomaly solve defaults to relative anomaly tolerance `1e-12` and at most 32 Newton iterations. A
37-body circular-orbit fixture advances one period and requires position error below `1e-3` meters and velocity error
below `1e-6` meters per second against its analytic return state. Those bounds qualify this fixture and solver setting;
they are not a general long-horizon orbit accuracy guarantee. Hyperbolic, near-parabolic, extreme-scale, multi-body,
perturbed, finite-burn, event-location, cancellation, and live-adapter behavior remain unqualified.
