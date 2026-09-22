# Isolated field-gravity force experiment

The field experiment is implemented in the safe Rust numerics crate. It hand-writes the vector, softened pair-force, cloud-in-cell (CIC) stencil, and assessment math. It has no numerical runtime dependency.

```sh
cargo test --manifest-path tools/numerics/Cargo.toml field
cargo run --manifest-path tools/numerics/Cargo.toml --bin field-gravity -- --output artifacts/field-gravity-new.json
```

The output path must be new. The report keeps the `ksp-continuum-field-gravity/v1` schema and reports direct and CIC-stencil acceleration at 17, 33, and 65 nodes per axis.

The candidate evaluates the bounded 8-by-8 CIC pair stencil directly. That gives a dependency-free oracle for the sampled particle-mesh operator without allocating a dense grid. It does not claim an FFT implementation or a performance win. A later optimized backend can be compared against the same Rust oracle.

The physical contract is normalized `G=1` Plummer gravity with softening `epsilon=0.6`, positions in the closed `[0,8]^3` box, and pairwise reciprocal force updates. Tests cover exact pairwise momentum conservation, one-step velocity Verlet, smooth split reconstruction, and exclusive report creation.
