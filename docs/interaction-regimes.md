# Interaction horizons and simulation regimes

Continuum should schedule work by conservative interaction horizons and explicit error budgets, rather than putting every object through a global fine timestep. This is an architecture recommendation, not an implemented scheduler or a performance guarantee. The existing worker, orbital fixture and force observations provide pieces of its test harness. The primary target uses prescribed celestial ephemerides and independent vessel propagation. Live n-body gravity is an optional curiosity, not a dependency or a priority for this architecture.

## What independence means

For a chosen interval, two systems are independent only if no modeled dependency can cross between them before that interval ends. Contact is one dependency; joints, gravity, thrust commands, resource transfer, communications, provider callbacks and multiplayer authority can add others. A known, immutable planetary ephemeris can be shared input without requiring mutual synchronization between test-particle vessels. Mutual gravitational backreaction changes that model and requires coupled integration or a quantified approximation.

The useful horizon is not a relativistic light cone. Newtonian gravity and rigid constraints do not provide a finite signal-speed bound. It is a bound on when the selected simulation model could require coupled work. A distant vessel can still have local attitude, resource or thermal dynamics that need computation; spatial isolation only removes some dependencies.

Warp scales simulated time per wall-clock second, not physical velocity. Opposing 5 km/s trajectories have 10 km/s relative speed at any warp. An endpoint-only check over ten simulated seconds can miss their entire encounter.

## Proposed regimes

| Regime | Representation and work | Trigger to refine or wake |
| --- | --- | --- |
| Supported and quiet | Body-fixed pose or sleeping contact island; scheduled resource/thermal events | Support moves, contact approaches, topology/control changes, error budget expires |
| Isolated coast | Center-of-mass orbital propagation; separately bounded attitude/internal state | Swept-path encounter, maneuver, atmosphere/terrain crossing, perturbation/error threshold |
| Isolated powered flight | Adaptive translation, attitude, mass flow and force-provider updates | Encounter, staging, contact, discontinuous input, bound violation |
| Encounter preparation | Common epoch; conservative swept volumes, relative motion, finer event localization | Potential interaction cannot be excluded within the current horizon |
| Coupled interaction | Shared contact/joint island in a local frame; CCD and appropriate fine steps | Contacts/constraints disappear and a new safe separation horizon is established |

These are composable work modes, not a single flag for a whole vessel: an orbit can be cheap while a centrifuge or robotic arm remains active. A single intricate station is also not automatically independent pieces; its constraints connect them. Static terrain should not connect every landed craft into one dynamic island when the terrain has no dynamic response.

## Conservative encounter screening

Each propagator should return a trajectory segment, a conservative spatial envelope, its validity interval and its error assumptions. Geometry needs rotational coverage too: a sphere enclosing the whole craft is loose but safer than a center-only path. A moving elongated craft cannot be bounded by untranslated endpoint boxes alone.

Use a common nonrotating frame for screening, with `t=0` at a shared epoch and initial position/velocity error bounds. A rotating predictor must also bound Coriolis, centrifugal and Euler terms and transform error. For a simple local predictor beginning at a known state, a sufficient center-position envelope around `x0 + v0*t` is inflated by `positionError + velocityError*t + 0.5*accelerationBound*t*t`. This requires a valid bound on acceleration relative to that predictor over the entire interval, including possible commands. Add the object's bounding radius and collision tolerance. For an orbital predictor, substitute a justified bound on its residual error. If the bound is unknown, shorten the horizon or retain coupled work; never silently assume zero acceleration.

Index these swept envelopes in a BVH or sweep-and-prune broad phase. Disjoint envelopes exclude contact within their validity interval. Overlap produces candidates, not confirmed collisions. Refine candidates with trajectory subdivision and continuous collision detection/time-of-impact methods. Curved paths, grazing contact and an entry followed by exit can defeat an endpoint sign-change test. Exhausting a search budget returns uncertain and forces refinement, not a clean bill of health. Box2D documents both [standalone collision routines](https://box2d.org/documentation/md_collision.html) and [time-of-impact handling](https://box2d.org/files/ErinCatto_ContinuousCollision_GDC2013.pdf).

Predictions expire on input changes, staging, terrain changes, frame changes, model/provider changes or dependency updates. Generation-stamped queued events prevent an old predicted encounter from mutating newer state. Spatial indices must include newly created debris before any further independent advance.

## Scheduler and transition contract

Use conservative scheduling first. Each island may advance only to the earliest of its validated horizon, next external event, internal error limit and requested observation time. Independent work can run in parallel up to those boundaries. Every incoming dependency must supply a lower-bound timestamp for its next possible effect; advance only to the minimum incoming bound. Undeclared callbacks or provider effects have zero lookahead and require synchronized execution. Other islands must not introduce earlier messages into already committed history. ROSS describes [conservative lookahead and optimistic rollback](https://ross-org.github.io/feature/schedulers.html); applying the former here is our recommendation. Optimistic execution is a later option once state, random choices, provider effects and event outputs can all be rolled back.

On promotion to an interacting regime:

1. Invalidate pending results and predictions dependent on the old generations.
2. Bring affected participants to one event epoch without advancing past an unresolved encounter.
3. Convert position, velocity, attitude and angular velocity into a declared local frame. A rotating frame requires its velocity terms and fictitious forces; subtracting a world origin alone is insufficient.
4. Transfer ownership once. Keep a transition ledger assigning each force and state term to exactly one owner before and after the event, so gravity, thrust, resources and provider work cannot be omitted or double-applied. Preserve masses, momentum, joint/contact state and provider state needed by the receiving solver. Unavailable internal state prevents a lossless handoff claim.
5. Resolve/refine the event, publish the new generation, and record the transition and its conservation/error measurements.

No participant may commit beyond an unresolved shared event. Order simultaneous events deterministically by time, declared priority, stable participant IDs and generation; changes invalidate and recompute dependent events at that epoch.

Demotion requires more than current separation: no connecting dynamic constraints, separation throughout a fresh validated horizon, acceptable internal residuals and a wider exit threshold or dwell condition to avoid switching repeatedly. Do not discard flex or spin energy when replacing a station by one rigid body. Retain internal state or measure the approximation loss. Checkpoint-and-replay can reproduce dissipative collisions; integrating backwards generally cannot undo them.

The committed event ledger has a shared time meaning even if computation runs ahead independently. Rendering can interpolate committed snapshots. Live input limits how far an interactive island can commit. Multiplayer needs an explicit authority and synchronization policy; asynchronous local computation alone does not permit players to meet at inconsistent dates.

## Reuse and build boundary

- **Reuse a contact engine.** Jolt already provides multicore-oriented body access and solver infrastructure; our structural harness makes it a practical candidate to test. Its [architecture documentation](https://jrouwe.github.io/JoltPhysics/) is the API reference. Box2D's [island design](https://box2d.org/posts/2023/10/simulation-islands/) supplies useful algorithmic precedent; it is a 2D library, not a 3D KSP replacement.
- **Keep richer gravity optional.** Prescribed celestial motion can be generated offline and sampled from versioned ephemerides. New vessel trajectories still respond to maneuvers and encounters; precomputing planets does not precompute every future flight. For an optional richer model, REBOUND's [TRACE](https://rebound.hanno-rein.de/integrators/trace/) and [hybrid example](https://rebound.hanno-rein.de/ipython_examples/HybridIntegrationsWithTRACE/) address occasional close encounters in centrally dominated systems. They are candidates or references, not proof for thrusting, colliding, arbitrary modded vessels. Orekit separates [event check intervals from event-time tolerances](https://www.orekit.org/site-orekit-latest/apidocs/org/orekit/propagation/events/EventDetectionSettings.html), a useful distinction for warp controls.
- **Build Continuum's boundary.** Own dependency declarations, validity/error contracts, event ordering, generation checks, frame transforms, provider adapters and mission-level replay receipts. These connect domains that an ordinary contact engine does not own.
- **Select hardware from workload measurements.** Batch orbital propagation, broad-phase candidates and independent islands. Measure capture, scheduling, transfer, solve and publication separately. GPU launch/transfer overhead can outweigh small jobs. PhysX's documented [GPU rigid-body path](https://nvidia-omniverse.github.io/PhysX/physx/5.1.0/docs/GPURigidBodies.html) is CUDA/Linux/Windows, not a ready-made Mac acceleration route. Any Metal or portable compute backend needs its own cost and correctness evidence.

## First executable experiment

The [portable encounter planner](encounter-scheduler.md) now implements the translating-sphere planning subset. It leaves integration, curved trajectories and actual solver handoff unqualified.

Build a standalone encounter scheduler against an always-fine reference before taking over KSP time. Begin with translating spheres and declared acceleration bounds, then add curved orbital segments. Freeze the following cases: opposing 5 km/s craft, a grazing miss, collision entirely between endpoints, acceleration after prediction, staging debris, an orbit/terrain crossing, origin rebasing, quiet resting probes and a dense debris cloud.

Require no missed reference encounters, bounded event-time/state error, deterministic event order under worker-count changes, no stale event application, and measured handoff conservation. Dense sampled reference results are evidence, not a proof that all curved-path encounters are excluded. Use analytic cases or interval bounds for the no-miss contract. Benchmark quiet-object scaling separately from candidate-pair count, largest island size and synchronization cost. Run mixed workloads: one hard encounter must not force thousands of unrelated quiet probes into fine stepping.

## Open questions and decision boundary

Unknowns: which KSP/mod effects can declare useful horizons; how much PartModule work remains on the main thread; which internal states can survive regime transfer; whether real missions provide enough parallel work to amortize batching; and how well a chosen broad phase handles long swept orbits. Sparse scenes should be much cheaper under this architecture, but worst-case dense interactions and uncontrolled callbacks remain expensive. Broad-phase output and pair-event storage can both grow as O(N²); a dense system may form one island containing all N objects. Zero lookahead can remove island-level time parallelism. Capacity exhaustion must stop speculative advancement or fall back to bounded synchronized work, never discard candidates to preserve speed.

Proceed with the bounded scheduler fixture alongside the force-observation delivery; the optional spatial-gravity experiment is not a prerequisite. Full time ownership, live solver replacement and multiplayer changes require separate implementation and qualification. The strongest simpler alternative is a single global timestep with parallel islands; keep it as the baseline, because it may win when horizons are short or synchronization dominates.
