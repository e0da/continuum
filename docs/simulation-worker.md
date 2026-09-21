# Bounded simulation worker

`src/KspContinuum.Core/SimulationWorker.cs` implements an offline compute boundary with one background compute thread. It has no Unity references or game integration. `ConstantForceBackend` exercises actual independent-body translation; it is not a replacement rigid-body solver.

## Input and output contract

`WorkStamp(long tick, long topologyGeneration, long frameGeneration)` identifies the captured input. All three values are nonnegative. Accepted ticks must increase globally for the lifetime of a worker, including across topology and frame changes. A new simulation session should create a new worker.

`SimulationBody(int id, double mass, Vec position, Vec velocity, Vec force)` is immutable. IDs are nonnegative, mass is finite and positive, and all vector components are finite. Existing `Vec` values are returned by value, so changing a retrieved vector cannot change the body. Use a consistent unit system; the reference example can use kg, metres, seconds, and newtons.

`SimulationBatch(WorkStamp stamp, double stepSeconds, IEnumerable<SimulationBody> bodies)` copies its collection, rejects duplicate IDs and null entries, and accepts 1–4096 bodies. Its timestep is finite and positive. The collection is read-only and the contained bodies and stamp are sealed immutable objects. Construct batches on the capture side; enumeration and validation occur synchronously. The caller must not mutate its source collection concurrently with capture.

### Opt-in column capture

`SimulationBatch.FromColumns(stamp, stepSeconds, ids, masses, positions, velocities, forces)` accepts an `int[]`, `double[]`, and three `Vec[]` arrays of equal length. It defensively captures their values into privately owned arrays, with separate position, velocity and force component arrays. The same 1–4096 body bound, unique nonnegative IDs, finite values, positive masses/timestep and nonnull stamp requirements apply. The caller must not mutate the source arrays during capture; changes afterward cannot alter the batch.

Use `Count`, `GetId(index)`, `GetMass(index)`, `GetPosition(index)`, `GetVelocity(index)` and `GetForce(index)` for reads without creating body objects. Vector reads return copies. `UsesColumnStorage` identifies this opt-in representation. Existing constructors and backends remain supported, and the original object constructor keeps its original representation.

`Bodies` remains a compatibility view. On a column batch, its first access creates a complete immutable `SimulationBody` view, cached with thread-safe lazy initialization. Even reading `Bodies.Count` triggers that materialization; use `Count` when it is unnecessary. A backend that reads `Bodies` remains correct but pays for this view, so the column representation is not a promise that every existing backend will allocate less.

`ConstantForceBackend` has a column path that allocates new motion arrays, validates all computed values, and checks cancellation. Output can share its input's private immutable ID, mass and force arrays; it does not retain or mutate the input's motion arrays. These arrays are never exposed or pooled. Worker validation uses indexed reads and preserves the same stamp, ID/order, mass, force and timestep checks without creating compatibility views. Custom backends can return either representation, subject to that envelope.

This adds a sealed capture and result representation, not a reusable mutable buffer lease, native bridge, SIMD kernel or game-state owner. The separate [handoff comparison](worker-benchmark.md#compare-handoff-layouts) measures the actual worker boundary before any default storage change.

`ISimulationBackend.Compute(SimulationBatch batch, CancellationToken cancellation)` returns a new batch representing the end of that interval. The output retains the **input stamp**, exact timestep, body count, ordering, IDs, masses, and forces. Positions and velocities may change. The worker validates this envelope before publishing. A null result, changed envelope, backend exception, or invalid/nonfinite output construction faults the worker permanently. `Fault` retains the exception for diagnosis; it is not thrown on the submitting thread.

Backends may access only captured data and their own thread-safe state. No Unity objects, KSP callbacks, or main-thread API calls belong inside `Compute`. The worker does not own or dispose the supplied backend. A caller that shares one backend across workers must supply its own backend synchronization.

## Submission and collection

```csharp
using (var worker = new SimulationWorker(new ConstantForceBackend()))
{
    var stamp = new WorkStamp(10, 2, 3);
    var input = new SimulationBatch(stamp, 0.02, new[] {
        new SimulationBody(7, 2, new Vec(), new Vec(), new Vec(2, 0, 0))
    });
    SubmitStatus submitted = worker.TrySubmit(input);
    // Later, at an owner-controlled boundary:
    SimulationBatch output;
    ResultStatus result = worker.TryTake(stamp, out output);
}
```

The capacity is **one outstanding batch total**, including pending, executing, and completed-but-uncollected work. There is no growing backlog and no overwritten result. `TrySubmit` returns `Busy` while that slot is occupied. When idle, null input or a repeated/backward tick returns `Rejected` without changing state. `Disposed` and `Faulted` take precedence over all other submission statuses; `Busy` takes precedence over input rejection. Construction errors throw `ArgumentException` before submission.

`TryTake` returns `Empty`, `Pending`, `Ready`, `Stale`, `Faulted`, or `Disposed`. Only `Ready` supplies a nonnull output. A completed batch is consumed once. If any of its three stamp fields differs from the caller's **current expected input stamp**, collection returns `Stale`, discards that result, and frees the slot. A stale discard does not reset the accepted-tick watermark. Passing a null expected stamp throws `ArgumentException` without consuming work.

The caller must synchronize stamp selection, collection, and publication against world transitions. A `Ready` result is not permission to apply it after a subsequent topology, frame, or tick change. This API neither mutates a world nor performs an atomic game-state commit. It does not transform coordinates across a frame shift, cancel an old batch merely because the world changed, or permit one-tick-late feedback by default.

## Disposal and faults

`Dispose` is idempotent. It immediately closes submission and collection and discards pending/completed work. Cancellation is dispatched through the thread pool so backend cancellation callbacks cannot block the disposing thread. Compute itself remains on the one dedicated background thread. No thread is forcibly aborted and disposal does not join it. A cooperative backend observes cancellation and exits; a backend that ignores cancellation or blocks forever can retain its thread and resources indefinitely. Its late result is always discarded. The cancellation source is released after both compute-thread exit and cancellation dispatch finish. The worker must be disposed even after a fault.

This is process-local exception containment, not a sandbox or a CPU/memory deadline for arbitrary backend code. The 4096-body and one-slot bounds constrain accepted data and queue depth, not allocations or work performed by a custom backend. Creating arbitrarily many workers creates arbitrarily many threads.

## Reference physics and evidence

The reference backend integrates constant force in a fixed, nonrotating Cartesian frame:

`v1 = v0 + (force / mass) * dt`

`x1 = x0 + v0 * dt + 0.5 * (force / mass) * dt * dt`

It preserves the force in its output to permit another constant-force step. It has no orientation, angular momentum, torque, contacts, joints, terrain, force capture, orbit model, adaptive timestep, resource simulation, or ownership handoff to stock physics. There is no gravity unless explicitly supplied as constant force. Double precision and finite-output checks do not establish an error bound for extreme scales or arbitrary trajectories; overflowing intermediate arithmetic fails closed instead of publishing a partial batch.

Run the independent executable tests with:

```sh
dotnet run --project tests/KspContinuum.Worker.Tests
```

The initial test-first API scaffold produced a witnessed runtime RED (`NotImplementedException: Worker body contract not implemented`). Implementation then passed the outside-in suite. Tests cover an analytic three-axis trajectory and subdivided-step agreement, input immutability and validation, off-thread execution, backpressure, tick ordering, each stale-stamp dimension, invalid backend envelopes, faults, numeric overflow, cooperative cancellation, and disposal while a backend ignores cancellation. The test project is separate from the existing analytic/timeline test executable.

These checks establish the offline boundary for the declared free-body experiment. They establish no KSP integration, replacement-solver fidelity, deterministic native replay, or speedup. A game adapter still needs the capture and commit contract described in [replacement.md](replacement.md).

The [worker benchmark](worker-benchmark.md) is a runnable CLI consumer that compares backend strategies and measures packing, handoff, compute, and collection together.
