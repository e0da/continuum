# Active-physics takeover canary

This portable C# fixture proves the publication protocol needed immediately before a live KSP adapter. It does not reference Unity, install a plugin, suppress stock physics, or mutate a game instance.

`TakeoverCoordinator` grants one opaque `TakeoverAuthority` token for one prepared transaction. Preparation captures an immutable before-image, checks the caller's tick, topology generation, frame generation and frame identity, validates body identity/generation/mass, and constructs a per-body write journal. A second preparation is busy until the active transaction reaches a terminal state.

Application revalidates the complete before-image before the first write. A stale tick, topology change, frame change, or same-stamp state change aborts without calling the driver. The transaction then moves through these explicit phases:

| Phase | Meaning |
| --- | --- |
| `Prepared` | Exclusive authority exists; no write has been attempted. |
| `Applying` | One or more journaled driver writes may have been attempted. |
| `Verified` | A complete readback exactly matched the desired snapshot. |
| `Aborted` | No write occurred, or every attempted write was restored and the complete before-image was read back exactly. |
| `Indeterminate` | A write may have occurred and exact before-image restoration could not be verified. |

The driver returns a success flag for each write, but that flag is not treated as proof. Every successful path requires a full readback. A failed write is included in compensation because an external API may change state before reporting failure. Compensation runs the before-image journal in reverse and then checks the entire snapshot. A readback exception after writing follows the same recovery path. The coordinator never reports `Verified` from write return values alone.

This gives all-or-none observable behavior for the in-memory test driver when compensation can be proven. It does not establish that replaying a before-image is safe in KSP. Unity callbacks, contacts, destroyed objects, resources, or other readers may observe effects between writes. A live adapter must first qualify one callback boundary, writer exclusion, object lifetime checks, synchronization, and a host recovery action. Until then, `IActivePhysicsDriver` is a portable seam only and the game remains authoritative.

Run the focused proof with:

```sh
dotnet run --project tests/KspContinuum.Takeover.Tests -c Release
```

The adversarial cases cover exclusive tokens, explicit abort, stale/topology/frame rejection before writes, same-stamp mutation, verified application, partial writes, write failure after mutation, mismatched readback, readback exception, verified compensation, and unverifiable compensation.
