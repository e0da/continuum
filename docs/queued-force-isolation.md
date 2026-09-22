# Queued-force isolation probe

Skipping Unity's native physics node leaves previously queued PhysX forces pending. The first restored native step can therefore apply more than one interval of work. A live Continuum dynamics callback must not publish replacement motion until it owns or safely isolates those queued forces.

The isolated engine benchmark now records four same-build cases after `Rigidbody.AddForce` and before an explicit local `PhysicsScene.Simulate`:

- an unchanged baseline;
- an `isKinematic` true/false transition;
- a sleep/wake transition;
- rewriting the current velocity.

Every case uses a fresh isolated physics scene and reports expected and observed velocity change plus `retained`, `cleared`, or `changed`. The probe does not assert that an operation safe in an isolated scene is safe for a KSP vessel. A cleared result selects a separate disposable-instance experiment that must verify pose, velocity, joint, collider, lifecycle, queued-force, and exact restoration behavior before reconnecting live dynamics. A retained or changed result rules that strategy out for force ownership.
