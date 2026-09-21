# Program branches

`PROGRAM-BRANCH-001` is a portable proof of branchable simulation history. It does not control KSP or claim durable crash recovery. It establishes the in-memory contract that a future game driver, mission director, and chronicle projector can share.

## State and history

`ProgramSnapshot` is immutable and content addressed. Its canonical binary representation includes the schema, frame, model fingerprint, topology and frame generations, tick, epoch, and bodies sorted by stable ID. Integers use little-endian encoding, doubles use their IEEE-754 bits, negative zero is normalized to zero, and non-finite values are rejected. Equal hashes establish equal encoded state, not correct physics.

Each branch has one head and an append-only, hash-chained event sequence. A fork shares its parent's immutable snapshot. Committing a child does not mutate its parent. Snapshots are indexed by content hash, while worldline tubes, forecasts, rendering, and other derived products remain outside canonical truth.

## Authority transaction

An authority lease binds a caller-supplied lease ID to one branch, exact parent snapshot hash, branch generation, supported subsystem, entity set, and logical-time interval. The initial kernel accepts only the `motion` subsystem and only one active writer per branch. Candidate computation runs outside the repository lock. Publication revalidates the exact causal parent and then installs the snapshot, event, and head under one lock.

Faults, cancellation, invalid identity or membership changes, and stale results do not publish. An external observation increments the generation even when it restores byte-identical state, so an old lease cannot revive through an ABA transition. Independent branches can compute concurrently; concurrent publication into one branch is deliberately unsupported.

## Executable experiment

Run:

```sh
dotnet run --project tests/KspContinuum.Program.Tests -c Release
python3 -m unittest tests.test_program_branch -v
dotnet run --project tools/KspContinuum.ProgramBranchToy -c Release
```

The contract tests cover canonical ordering and hashing, branch isolation, exact-head acquisition, busy and stale rejection, solver failure, malformed results, observation invalidation, content-addressed lookup, event-chain verification, replay, and serial/parallel equivalence.

The toy derives one deterministic random stream per sample from the experiment seed and stable sample index. It evaluates 256 branches serially and in parallel, compares every result hash, and emits a stable JSON receipt. The receipt is experiment evidence, not an authoritative mission attempt.

## Deliberate limits

This slice has no file-backed transaction log, recovery after process failure, merge commits, overlapping subsystem leases, topology mutation, stock save codec, live KSP publication, or multimedia projection. A chronicle adapter should consume committed receipts through the existing immutable-report manifest seam. Forecasts and rejected transactions belong in evidence attached to a committed attempt; they must not appear as authoritative mission history.
