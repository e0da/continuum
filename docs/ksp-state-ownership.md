# KSP state ownership contract

Continuum currently observes live KSP motion and tests publication in a separate [in-memory fixture](local-handoff.md). It has not established exclusive ownership of a live vessel. The next game-facing step should measure callback ordering and invalidation without changing physics ownership.

This contract distinguishes **observed source behavior**, **proposed design**, and **unknown installed behavior**. Pinned community source establishes a candidate integration seam, not compatibility with an installed combination. The [integration map](integration-map.md) identifies the owned KSP assembly previously inspected; no proprietary code or assemblies belong in this repository.

## Ownership by state domain

The live owner remains KSP and its installed providers in every row. The final column is a requirement for a future adapter, not an implemented guarantee.

| Domain | Observed source behavior and current capture | Required boundary before takeover |
| --- | --- | --- |
| Position, attitude, linear/angular velocity | [ShadowCapture](../src/KspContinuum.Plugin/ShadowCapture.cs) copies dynamic part Rigidbody state, deduplicates shared bodies, and never writes results back. Raw Unity frame coordinates are not a complete inertial transform. | Name one motion integrator and every permitted direct writer. Publish pose and velocity at a common physical epoch before qualified readers resume. Specify Rigidbody/Transform synchronization and COM conventions. |
| Part force and torque deposits | [PartForceObservation](../src/KspContinuum.Plugin/PartForceObservation.cs) observes `Part.force`, `Part.torque`, and positioned deposits at `FashionablyLate`. Capture explicitly leaves gravity, stock aerodynamics, direct Rigidbody writes and contacts unavailable. | Assign each force channel exactly once; preserve units, impulse-versus-force meaning, timestep and application lever arm. No unavailable channel may silently become zero. |
| Gravity and aerodynamics integration | [MFI's pinned override](https://github.com/sarbian/ModularFlightIntegrator/blob/03f07cd6e498ed003f84ee97ce976430e6447256/ModularFlightIntegrator.cs#L362-L400) admits one `Integrate` delegate; another registration fails. Its base method remains callable. | Negotiate the actual integrator owner and verify registration. An MFI slot is not authority over every part module, native solver, or patched method. Do not occupy an override to observe. |
| Shapes, mass/COM/inertia, joints and contacts | Shadow physical capture contains mass properties and body constraints, but not the collider/constraint graph or native contact history. [Bench](../src/KspContinuum.Plugin/Bench.cs) owns isolated synthetic physics scenes. | Inventory all collision geometry and joints, including mod-created constraints. Transfer or explicitly approximate hidden state. Recreating native bodies is a different operation from continuing an existing solver. |
| Vessel identity and topology | Shadow capture audits vessel, ordered part/parent/body identities and kinematic state. Logical parts may share a physical body. [Existing control inspection](integration-map.md) places stage notification before completed staging bookkeeping. | Maintain persistent model IDs separately from run-local native IDs. Re-audit actual topology after notification and before publication; docking, breakage and staging invalidate pending work. |
| Physical time, packing and scene lifecycle | Capture excludes packed, paused, held-physics and non-normal-rate states. Its [panel](../src/KspContinuum.Plugin/Addon.cs) supplies an observed physics-boundary counter. | Define time and callback ordering explicitly. Pack/unpack, scene change, load/restore and ownership loss invalidate work. Remove only owned callbacks; inability to confirm cleanup is a reported failure. |
| Reference frame and origin | Shadow capture subscribes to `onFloatingOriginShift` and audits exact Krakensbane frame velocity. These are conservative invalidators, not a full rotating-frame transform. | Keep a frame identity and generation alongside state; include origin velocity and, when applicable, rotation/angular velocity. Do not combine force and body samples whose epochs merely have equal numeric counters. |
| Resources, controls, thermal and external events | The current motion fixture does not capture these providers' complete state. [Compatibility boundaries](compatibility.md) identify existing controller/integrator owners. | Keep them with their existing owner until an adapter declares inputs, outputs, timing and replay behavior. No motion handoff implies authority over fuel, guidance or multiplayer locks. |

## What the available switches do not prove

Unity 2019.4 documents that `Physics.Simulate` performs native integration and physics callbacks, while `FixedUpdate` continues when automatic simulation is off. Disabling automatic simulation therefore cannot alone suspend KSP and mod writers. This is an inference from the [documented scheduling contract](https://docs.unity3d.com/2019.4/Documentation/ScriptReference/Physics.Simulate.html), not an installed takeover experiment.

Setting `isKinematic` changes how a body responds to forces and constraints, but that body can still affect others through joints and collisions. It is not an isolation switch. [Unity Rigidbody documentation](https://docs.unity3d.com/2019.4/Documentation/ScriptReference/Rigidbody-isKinematic.html).

Transform writes and physics state also have a synchronization boundary: `SyncTransforms` flushes transform changes into the physics engine. It does not make multiple game-state writes transactional or stop other systems from observing them. [Unity synchronization documentation](https://docs.unity3d.com/2019.4/Documentation/ScriptReference/Physics.SyncTransforms.html).

Principia's pinned adapter supplies two concrete warnings. Its force-holder workaround stores COM-relative lever arms because an origin shift can invalidate application positions between capture and consumption. Its post-physics path also accounts for parts destroyed during the native step. The adapter uses multiple named stages and a `WaitForFixedUpdate` continuation; copying only its force-capture hook does not copy its ownership protocol. [Force/frame handling](https://github.com/mockingbirdnest/Principia/blob/dcf1fb949d792be966e5a7a162a1073ddfa1f9c1/ksp_plugin_adapter/ksp_plugin_adapter.cs#L191-L227), [post-step handling](https://github.com/mockingbirdnest/Principia/blob/dcf1fb949d792be966e5a7a162a1073ddfa1f9c1/ksp_plugin_adapter/ksp_plugin_adapter.cs#L1235-L1259).

## Minimal read-only trace canary

**Proposed experiment:** determine whether a captured input batch has a reproducible lifecycle boundary in one pinned stock instance. Keep stock integration active throughout. The tracer registers observational callbacks and writes a bounded local receipt; it does not inject forces, toggle physics settings, move bodies, invoke an extra physics step, command warp or stage a craft. Controlled disturbances belong to a separately authorized operator or mission script, recorded as external events.

Start with one loaded unpacked vessel at normal rate. Retain at most 120 observed physics cycles, 512 bodies per sample and 8,192 total event records, with a 30-second wall-time stop once capture begins. Reuse the existing force observer's per-batch deposit bounds. Stop with an explicit partial or overflow status rather than truncate a claimed complete capture; paused physics must not leave an unbounded observer running. Use one session-wide monotonically increasing event sequence and main-thread identity, alongside named callback stage, Unity frame/fixed time, KSP UT, actual fixed-step duration, pause/warp/packing state, vessel/body IDs, topology and frame generations. Counters from separate observers must not be joined by coincidence.

Observe candidate seams at `FashionablyLate`, `FlightIntegrator`, `BetterLateThanNever`, a resumed `WaitForFixedUpdate`, and the next ordinary observation. These names are source-supported candidates, not asserted runtime order. Record pose/velocity and the available force census at each relevant seam; distinguish absent samples, unavailable channels and explicit zeros. Capture field reads into owned data before leaving the main thread. Record callback registration and removal readback, KSP/Unity/plugin versions, assembly identities, and known provider/patch inventory. Inventory is not proof there are no hidden writers.

The question has competing explanations:

- **Stable boundary:** observed callbacks yield repeatable ordering and epoch-linked inputs. A single contrary ordering or unexplained mid-boundary identity change falsifies this for the tested configuration.
- **Incomplete boundary:** later writes, origin shifts or topology changes invalidate an earlier capture. Seeing this justifies a later boundary or a wider dependency contract; it does not justify relaxing stale-result rejection.

Use bounded windows for ordinary coast, an externally initiated topology change, a frame shift, and pause/packing/scene exit. Cases that do not occur remain unobserved. Pass requires valid ordered receipts, invalidation of all outstanding shadow work on observed transitions, and verified owned-callback cleanup. A trace cannot establish exhaustive writer detection, force completeness or exclusive ownership. A sentinel-force injection would be a separate mutating experiment, not part of this read-only canary.

## Publication and failure recovery

**Proposed design:** a live adapter must expose publication states explicitly: prepared, applying, verified, aborted-before-write, and indeterminate-after-write. The in-memory owner can swap one snapshot; the KSP adapter cannot assume equivalent atomicity.

Before the first write, validate the authority token, topology/frame/time stamp, participant membership and required host objects on the main thread. Preserve a before-image and a write journal for the exact owned fields. No pending worker result may survive an ownership or context change.

After applying, perform bounded readback in the same qualified execution boundary. A complete matching readback may acknowledge the publication; subsequent dependent reads must be covered by the ordering contract. Rendering/interpolation and native physics synchronization require their own explicit handling.

If a write throws or readback disagrees after any effect, mark the publication indeterminate, invalidate all derived predictions, stop further Continuum writes and retain the journal. A rollback is permitted only when the adapter can establish that identities and ownership remain valid and that no intervening side effects must also be undone. Replaying the before-image is not automatically safe: collision callbacks, destroyed parts or resource consumption may already have escaped. Do not automatically reactivate an old integrator onto half-published state. The qualification instance needs a tested pause/reload or other host-specific recovery path before active publication is enabled; that path remains unknown.

## Contact-engine checkpoint comparison

Jolt's pinned rollback documentation requires the application to restore externally modified settings and topology, preserve body IDs and consistent operation ordering. Its multiple-world API reconstructs bodies from creation settings; this is not a claim that world constraint/contact history transfers. [Pinned Jolt architecture](https://github.com/jrouwe/JoltPhysics/blob/e77f175595e64cb44218cc9d9d56fc365ad0e36a/Docs/Architecture.md#rolling-back-a-simulation).

The decisive comparison should therefore separate continuation, same-world restore and cold reconstruction. Checkpoint while contacts or constraints are active, not only before first impact. Freeze timestep, topology, solver settings, build and input order. Preserve callback output as experiment data rather than allowing replayed callbacks to cause untracked effects. Exact same-build replay, state continuity, and physically acceptable cold-reconstruction error are different outcomes. None establishes KSP takeover or cross-platform replay.

## Remaining decisions

The immediate recommendation is the read-only ordering trace, after the closed-game checkpoint experiment. Stronger alternatives—global native-physics suppression or moving live parts into isolated scenes—have broader effects without an established exclusive-writer protocol and are not the next canary.

Unknowns remain the installed callback order and patch interactions, complete force/provider inventory, native solver history needed for continuation, and recoverable host publication boundary. This document authorizes no runtime action. Live qualification needs its own executable implementation and the session's authorization to run the disposable instance; the current research leaves KSP closed.
