# Bounded live-dynamics canary

`--continuum-live-dynamics-canary` is the first Continuum probe that computes and publishes nonempty vessel dynamics while stock `PhysicsFixedUpdate` is absent. It is a destructive qualification mode for an owned disposable KSP copy, not a gameplay setting.

The canary admits only a loaded, unpacked, unheld, unpaused, zero-throttle active vessel in normal-rate orbit after the existing ten-second scaling qualification. It requires the same survey, immutable checkpoint, scale-profile, PlayerLoop, and writer-census controls as the empty substitution canary. The two substitution flags are mutually exclusive.

For each native physics callback already cached in the first qualifying render-frame batch, the candidate:

1. deduplicates the active vessel's physical `Rigidbody` owners and captures their mass, center of mass, principal inertia frame, pose, and velocity;
2. constructs one six-degree-of-freedom rigid cluster;
3. samples KSP's central gravitational acceleration at the cluster center of mass;
4. advances the cluster for `Time.fixedDeltaTime` with a frozen-acceleration kick-drift-kick step;
5. reconstructs every body pose and velocity, publishes them, calls `Physics.SyncTransforms`, and reads them all back;
6. restores the exact native PlayerLoop node for the following render frame.

Unity can cache more than one fixed callback in a render frame. The candidate permits one through four callbacks only in the first frame and computes one dynamics step for each callback. This matches the bounded-frame ownership discovered by `CSP-0002-A023`; it does not silently discard cached physical time.

The receipt records callback count and frame, body count, timestep, sampled acceleration, compute/publication/whole-callback time, maximum readback errors, before/after writer snapshots, and exact native-node restoration. Publication exceptions attempt a complete before-image restoration. A failed restoration is reported as indeterminate. Context or topology drift, an unverified write, no observable dynamics, a callback outside the bounded frame, or inexact PlayerLoop restoration invalidates the run.

## Frozen experiment boundary

This candidate deliberately approximates the whole active vessel as one rigid cluster. It excludes thrust, aerodynamics, contacts, robotics, joint flex and breakage, other loaded vessels, and sustained trajectory ownership. Central gravity is held constant within each step. Its current readback gates are `1e-4 m` position, `1e-5 m/s` linear velocity, `1e-4` degrees attitude, and `1e-5 rad/s` angular velocity. These gates prove that the values written reached Unity; they are not stock-trajectory tolerances.

The first installed experiment must use paired immutable-checkpoint runs:

- stock physics, retaining full per-frame physics and PlayerLoop timings;
- the bounded Continuum candidate, retaining the same timings plus its callback receipt.

Run each side repeatedly and preserve run order. Compare the physical state after the same number of fixed intervals, including center-of-mass position/velocity, vessel attitude/angular velocity, and internal-body deformation. Report complete tick and rendered-frame distributions rather than only candidate kernel time. One bounded batch can establish useful live dynamics and measure immediate cost; it cannot establish sustained orbital parity, ordinary-craft FPS improvement, or compatibility with other integrator owners.

Portable coverage verifies the kick-drift-kick rigid-cluster step, lifecycle rejection, nonempty dynamics requirement, publication receipt, and report serialization. Native addon compilation verifies the KSP/Unity API surface. Installed qualification remains pending.
