# Transactional local-physics handoff fixture

This standalone slice connects an immutable sphere model, encounter planning, a local analytic contact solver and an in-memory publication owner. KSP stays authoritative in the game; this fixture does not publish to Unity or take over a vessel.

## Layers and authority

| Component | Responsibility |
| --- | --- |
| `HandoffBody`, `HandoffSnapshot` | Stable sphere identities, mass/radius, position/velocity, frame identity, split physical time |
| `HandoffWorld` | In-memory state owner and driver: capture, replace/restore, plan, admit work, validate and atomically publish |
| `HandoffPlan` | Bind an encounter plan to its captured snapshot and authority revision |
| `SphereContactSolver` | Pure two-sphere first-contact computation with frictionless elastic response |
| Console adapter | Convert selected bodies to a nearby translating frame and convert response velocities back |
| Receipt | Before/after state, exact inputs, contact time, normalized results, conservation, replay and rejection checks |

The model contains no Unity objects. Definitions/state/predictions are conceptually distinct; this fixture implements only the fields needed for spherical motion. It does not introduce a universal ECS, message bus or out-of-process service. Existing immutable worker columns, encounter groups and epoch invalidation supplied the repository patterns. A worker result is provisional until the owner accepts it.

`HandoffWorld` combines the in-memory driver and authority boundary deliberately: one snapshot reference can be replaced atomically under its lock. A future external driver must state its own publication guarantee. Several Unity API writes cannot become atomic just because our internal model uses a lock.

## Time and event boundary

Snapshot time is `(BaseEpoch, Offset)`. Keep the pair instead of adding small offsets to a large binary64 epoch. Encounter times are relative to the captured snapshot; a plan must be interpreted with that snapshot. The frame is a declared nonrotating inertial frame. A name alone does not prove a transform.

The first implementation uses one common-time publication barrier and one active transaction. The selected group receives detailed solving; other bodies coast under their declared exact linear motion. The admitted cap is no later than the requested target, the plan horizon, or another group's unresolved boundary.

The solver stops at its first confirmed contact, changes velocity at that instant, and returns. The owner publishes the entire new snapshot and invalidates all previous plans. There is no post-impact coasting before replanning: changed trajectories could encounter objects that the old plan considered clear. A possible-contact interval's lower boundary is a refinement boundary, not the confirmed impact time.

This is a conservative baseline, not independently advancing island clocks. Quiet bodies bypass the detailed solver but still advance to the common publication time. The earlier planner's independent horizon results remain separate evidence.

## Admission and publication

Admission requires a Complete plan from this world and its current revision, an existing group, a valid target and an available transaction. Inputs are immutable copies of the selected group. The solver callback executes outside the authority lock.

Publication rechecks cancellation and the world revision, then validates frame, split time, ordered membership, body generations, masses, radii and finite values. Selected positions must exactly match the declared free-flight arithmetic up to the returned event time. Only endpoint velocities may change. It constructs all quiet-body results and the complete next snapshot before changing authoritative state.

The callback is a trusted implementation of the first-event model. Structural validation does not prove its collision time or impulse is physically correct; analytic tests and independent numerical review qualify the supplied solver. Arbitrary continuous-force or multi-event backends do not satisfy this contract.

Replacement, including checkpoint restoration or changing then returning to an earlier state, advances the authority revision. Old plans never revive. Solver faults, cancellation, stale results and invalid output do not publish any of the provisional bodies. Legitimate external replacement during a solve remains authoritative.

## Numerical scope and replay

The contact backend solves two positive-radius, positive-mass spheres in straight free flight, with one frictionless elastic impact. It rejects initial overlap and detected arithmetic failure. It is a binary64 analytic fixture, not certified continuous collision detection. A distance/radius ratio above `1e8` is rejected; this guard is a declared conditioning limit, not a general error proof.

The adapter subtracts a chosen origin and common velocity before solving. The benchmark uses exactly representable initial positions/velocities at its selected translations and boosts. It cannot recover local detail already rounded out of a global coordinate. Rotating frames, joint/contact caches, terrain, thrust, gravity and fragmentation are outside this model.

Checkpoint replay restores the complete state needed by this small memoryless sphere model. Comparison is exact serialized state within the same executable environment, excluding the deliberately different authority revision. This does not qualify cross-platform determinism, a KSP save replay, or reconstruction of an engine with hidden contact history.

## Run

```sh
dotnet run --project tests/KspContinuum.Handoff.Tests -c Release
dotnet run --project tests/KspContinuum.Contact.Tests -c Release
python3 -m unittest discover -s tests -p test_handoff_bench.py -v
dotnet run --project tools/KspContinuum.HandoffBench -c Release -- \
  --output artifacts/handoff-NEW.json
```

The receipt path must be new. Exit 0 means the declared experiment gates passed; exit 2 retains a report with failed gates; exit 1 means argument or execution failure. The console fixture commits contact at offset 9 s, creates a fresh plan, then coasts to offset 11 s without another impulse. It separately restores the initial checkpoint and reproduces the first-contact state. No performance claim follows from this fixture.

## Integration findings and next gate

Turning off Unity automatic physics does not turn off `FixedUpdate`; it cannot alone establish exclusive ownership of KSP/mod state. [Unity 2019.4 documentation](https://docs.unity3d.com/2019.4/Documentation/ScriptReference/Physics.Simulate.html).

Jolt supports multiple worlds and state recording, but externally changed settings/topology and stable identity remain application responsibilities. Recreating a body in another world is not proof of lossless contact-cache transfer. [Jolt architecture](https://jrouwe.github.io/JoltPhysics/).

The [contact checkpoint experiment](contact-checkpoints.md) compares a persistent isolated contact-engine world with checkpoint restore, measuring cold-reconstruction differences separately. A live KSP adapter still needs observed callback ordering, exclusive writer ownership and explicit partial-publication recovery; see the [state-ownership contract](ksp-state-ownership.md). The [interaction-regime contract](interaction-regimes.md) and [trajectory representation experiments](trajectory-representations.md) remain relevant; richer orbital models do not remove the ownership boundary.
