# Active-physics workload adapter

`ActivePhysicsWorkloadAdapter` is the portable C# seam between a host physics snapshot, a Continuum worker backend, and transactional host publication. It does not reference Unity, install a plugin, replace `PhysicsFixedUpdate`, or authorize a live KSP run.

`TrySubmit` must run on the adapter's owning thread. It captures the complete host snapshot, requires the caller's tick, topology generation, frame generation, and frame identity to match, converts the bodies and caller-supplied forces into an immutable `SimulationBatch`, and submits that batch to `SimulationWorker`. The backend therefore computes without Unity or KSP objects.

`TryPublish` also runs on the owning thread. It rejects a result when any stamp field changed while work was pending. A matching result is converted back to an `ActivePhysicsSnapshot` while preserving each body's host identity, generation, and mass. Publication uses `TakeoverCoordinator.Prepare(expectedBefore, desired, ...)`, which requires the exact captured before-image to remain current. `Apply` then revalidates again before its first write, journals every attempted write, verifies complete readback, and compensates a failed or mismatched publication.

This closes the portable capture, compute, and checked-publication loop for one bounded constant-force workload. It does not yet solve the live ownership problems documented in [KSP state ownership](ksp-state-ownership.md): a live driver must still capture and write Rigidbody state at the qualified main-thread boundary, exclude other writers for that interval, synchronize Rigidbody and Transform state, define host recovery for an indeterminate write, and bind the workload adapter to the already-qualified bounded PlayerLoop substitution.

Run the focused proof with:

```sh
dotnet run --project tests/KspContinuum.Adapter.Tests -c Release
```

The fixture proves a nonempty gravity-like step is computed off-thread and published with verified readback. Adversarial cases change tick, topology, frame generation, frame identity, and body state while the backend is running; each is rejected before a host write.
