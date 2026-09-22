# Live rigid-cluster authority canary

The repository now has the reversible PlayerLoop operation needed by a live rigid-cluster canary: `PhysicsBoundarySubstitution` replaces the single native `PhysicsFixedUpdate` node with one owned managed callback, audits unique ownership, and restores only that node from its before-image. It never overwrites the whole PlayerLoop. Restoration requires exact candidate ownership and exact native-node readback; ambiguity fails closed as `cleanup-error`.

This is deliberately not wired to a flight command yet. Replacing the native node would stop PhysX integration, but it would not stop KSP or mods from writing part rigidbodies in `ScriptRunBehaviourFixedUpdate` and other callbacks. The current API also has no proved transaction for changing the part rigidbody graph while retaining KSP's collider, joint, destruction, and callback semantics. Therefore a live vessel mutation cannot currently satisfy the required only-writer invariant.

The next installed evidence gate is a disposable qualification instance with one settled vessel in vacuum orbit, engines shut down, SAS and RCS off, normal time, no contacts, and unchanged topology. Capture the existing lifecycle trace and five-scope PlayerLoop timing from a preserved checkpoint. The trace must show every observed pose/velocity writer and establish a suppressible interval around the native node. Only then should an opt-in candidate callback capture `RigidCluster6Dof`, retain existing colliders, publish reconstructed member poses for a short bounded window, restore the native node, and verify the complete dynamic and PlayerLoop readback.

Use two fresh launches from the same preserved checkpoint:

1. Stock: `KSP --continuum-scale-profile --continuum-playerloop --continuum-scale-parts=N`
2. Writer census: `KSP --continuum-lifecycle-trace --continuum-playerloop`

Do not combine the captures or compare rendered FPS. Compare the `FixedUpdate` and `PhysicsFixedUpdate` raw distributions only after both receipts report verified hook cleanup and identical vessel/topology context. No candidate performance claim is possible until the writer-census gate passes and a separately named `--continuum-rigid-cluster-canary` mode exists.
