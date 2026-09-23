# Portable coasting engine slice

`CoastingEngine` demonstrates a Continuum-owned simulation clock without Unity or KSP. It retains an immutable initial
central-gravity state and evaluates every body directly at a requested target time with universal-variable Kepler
propagation. The engine stops exactly at the target-time event. This first event is not SOI, collision, maneuver, or
terrain event detection.

The propagator adapts Gimbal's `gimbal-orbit::TwoBodyProblem::propagate_coast` universal-anomaly and Stumpff kernel.
Continuum keeps the compact port in Core because its installed C# consumers cannot depend on Gimbal's Rust workspace
and the current native boundary does not expose orbital propagation. Gimbal fixes 100 iterations and its convergence
policy in the kernel; this slice exposes 1 through 128 iterations (default 32), a configurable positive relative
tolerance (default `1e-12`), and fails when the Newton derivative is nonfinite or effectively zero.

Independent bodies are divided into configurable parallel work batches. Presentation snapshots may be requested at
any positive time cadence, but each snapshot is an immutable evaluation of engine state and cannot feed back into the
trajectory. Tests require bitwise-identical final states across work batch sizes 1, 7, and 64 and presentation cadences
17 seconds, 311 seconds, and disabled. One advance permits at most 4,096 intermediate publications.

`SampleAt(UT)` evaluates an immutable snapshot at any finite absolute time without moving the engine frontier. An
adapter can therefore project a forecast or a historical sample without rewinding authoritative engine time.

The universal-anomaly solve defaults to relative anomaly tolerance `1e-12` and at most 32 Newton iterations. A
37-body circular-orbit fixture advances one period and requires position error below `1e-3` meters and velocity error
below `1e-6` meters per second against its analytic return state. A quarter-period sample must also reach the orthogonal
analytic position and velocity within the same bounds, excluding a no-motion implementation. For elliptic seeds, the
solver now reduces elapsed time to the signed remainder of the computed orbital period before solving the anomaly.
A circular fixture samples 10,000 years forward and backward and matches direct samples at the reduced phase within
1 mm and 1 micrometer per second. Work for one sample is independent of the number of completed revolutions. Floating
point rounding of the computed period still affects phase over long spans; this does not prove general 10,000-year
trajectory accuracy. These bounds qualify this fixture and solver setting. Hyperbolic, near-parabolic,
extreme-scale, multi-body,
perturbed, finite-burn, event-location, cancellation, and live-adapter behavior remain unqualified.

A second fixture uses the qualification station's approximate Kerbin semi-major axis (`732639.5703` m) and eccentricity
(`0.00888570`) from an analytic periapsis seed. Its half-period sample must reach analytic apoapsis within `1e-5` m and
`1e-8` m/s. This exercises a modest ellipse; live stock-orbit comparison remains an adapter qualification gate.
