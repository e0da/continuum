# Experiment protocol

This bench tests whether fewer rigid bodies reduce solver work while retaining the same shapes. It does not test stock vessels, performance relative to Kerbal Joint Reinforcement, aerodynamic forces, fuel flow, heating, buoyancy, docking, saves, or other mods.

## Controlled comparison

Each run creates a new local Unity Physics3D scene. The assembly contains 8, 32, or 128 unit boxes, spaced 1.1 units along x, with repeating masses 1, 2, 3. The jointed case uses fixed joints with unbounded break force. The compound case retains every collider and uses the analytic mass, center of mass, and inertia. This is a comparison of representation cost, not a tuning study of KSP's configurable joints.

Both cases disable gravity, drag, and sleeping and use six position iterations, one velocity iteration, and a fixed 0.02-second step. A unit upward force acts at the first box on each step. There are 50 warmup steps, followed by 200 measured steps. Timings include the force call and synchronous scene simulation, but exclude object creation, raycasts, JSON output, and cleanup. Six pairs alternate order for each size. Compare paired results and variability; do not publish only the fastest sample. The plugin can pause the menu briefly while a sample runs.

Collider rays are checked before forcing. Spacing error is recorded after simulation. A separate three-box bench tests compound-body floor contact. Separation starts with a rotated three-box assembly and checks linear and angular momentum immediately after transferring each child's center-of-mass velocity. It does not yet test post-separation contact or a real KSP decoupler.

## Acceptance sequence

1. Compile against an owned KSP 1.12.5 copy and run analytic tests.
2. Once exclusive test-instance ownership is available, use CKAN packaging to install in an independent disposable copy. Never alter the active playable copy or clone while KSP/CKAN is open.
3. Launch to the main menu. Run the benchmark twice. Check both reports contain 36 samples, complete collider hits, finite positive timings, passing split/contact checks, and near-zero compound spacing error. Inspect the game log for exceptions and verify the temporary scenes are unloaded.
4. Compare per-size paired timing distributions across runs. A failure to improve rejects the compound-performance hypothesis for this workload. A win does not establish stock-vessel speedup.
5. Load a disposable sandbox vessel. Capture the inventory and markers. Verify scene transitions and unload do not leave a profiler recorder enabled or a benchmark scene alive. Marker availability and scope vary in release players; absent markers require another profiling route.
6. Profile a representative stock and modded vessel before deciding which production hot loop to replace. KSP Community Fixes should be a separately recorded comparison, not silently added between measurements.

Never turn a failed invariant into a performance success. There is no minimum required speedup; report the measurement even when negative.

## Rejected shortcut

Read-only inspection of the installed 1.12.5 API and implementation found `Part.DemoteToPhysicslessPart`, `Part.PromoteToPhysicalPart`, and `FlightIntegrator.UpdateMassStats`. Demotion removes the independent body, attachment joint, and several collision/buoyancy helpers. The mass update includes physicsless child mass but writes the parent's `CoMOffset` as the body's center of mass. Calling demotion is therefore not a demonstrated mass-and-inertia-preserving replacement. No proprietary implementation or assembly is included in this repository.

## Prior art

- [KSP Community Fixes](https://github.com/KSPModdingLibs/KSPCommunityFixes): managed patches, flight-integration optimizations, and existing correctness fixes.
- [Autostrut deformation diagnosis](https://github.com/KSPModdingLibs/KSPCommunityFixes/issues/21): existing constraints can preserve deformed rather than original geometry.
- [Kerbal Joint Reinforcement Continued](https://github.com/KSP-RO/Kerbal-Joint-Reinforcement-Continued): an essential future comparison for joint stability.
- [UbioZur Welding](https://github.com/UbioWeldingLtd/UbioWeldContinued): part merging is prior art, but does not establish transparent runtime aggregation.

The implementation here is original; these projects inform the experiment rather than supplying copied source.
