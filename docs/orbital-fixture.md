# Orbital and event fixture

`tools/orbit-fixture/run.py` exercises an existing, external Gimbal coast implementation against independent closed-form orbital states. It also demonstrates event crossings that endpoint sampling misses. It does not install a solver in KSP or introduce a new general orbital solver.

## Run

An existing clean Gimbal checkout, its cached Cargo dependencies, and an installed Rust toolchain are required. No Gimbal implementation code is copied into this public repository. Supply the checkout location explicitly and pin the full expected Git revision:

```sh
python3 tools/orbit-fixture/run.py \
  --gimbal-root /path/to/gimbal \
  --expected-revision a4ca52a45921bbbca6180a8d5882e44354df05a6 \
  --output /tmp/orbit-fixture.json
```

The output must be new; existing files and symlinks are refused. The parent directory must already exist. Cargo is found on `PATH`, then at `~/.cargo/bin/cargo`. If that launcher is unavailable, pass an existing concrete toolchain binary:

```sh
python3 tools/orbit-fixture/run.py \
  --gimbal-root /path/to/gimbal \
  --expected-revision a4ca52a45921bbbca6180a8d5882e44354df05a6 \
  --cargo "$HOME/.rustup/toolchains/1.92.0-aarch64-apple-darwin/bin/cargo" \
  --output /tmp/orbit-fixture-toolchain.json
```

The concrete toolchain example is platform-specific. When a sibling `rustc` exists, the runner uses it with Cargo. It creates a temporary Cargo project with a path dependency on `gimbal-orbit`, copies the donor lockfile into that temporary project, and builds offline with a temporary target directory. Cargo may update the temporary lockfile to include the adapter; both lockfile hashes are recorded. Missing toolchains/dependencies fail instead of being installed. Build and adapter execution have 180-second and 30-second timeouts. Temporary project/build outputs are removed afterward. This is a trusted local dependency invocation, not a sandbox for arbitrary Cargo build scripts.

The runner checks the donor revision and clean tracked/untracked status before and after execution, together with source and source-lock hashes. It never modifies the Gimbal checkout. Ignored files are outside Git's clean-status check. The report contains donor revision, relevant hashes, compiler versions, input and expected states, returned states, numeric residuals, budgets, event results, and explicit limitations; it omits the local donor path.

Exit status `0` means every declared fixture passed, `2` means a completed report contains a failed qualification case, and `1` means setup/protocol/output failed. A donor panic or nonfinite state becomes a failed orbital case. A build or process failure prevents a qualification report. No fallback solver silently replaces a failed donor result.

## Independent orbital oracle

The adapter calls Gimbal's public `TwoBodyProblem::propagate_coast`. The reference starts at periapsis and selects a known eccentric anomaly `E`, rather than solving the donor's universal-anomaly equation again. With semimajor axis `a`, eccentricity `e`, and `n = sqrt(mu/a^3)`:

```text
dt = (E - e*sin(E) + 2*pi*revolutions) / n
x  = a*(cos(E) - e)
y  = a*sqrt(1-e^2)*sin(E)
vx = -a*n*sin(E)/(1-e*cos(E))
vy = a*n*sqrt(1-e^2)*cos(E)/(1-e*cos(E))
```

Circular quarter/half turns reduce to elementary geometric states; eccentric apocentre is also checked independently using the vis-viva speed. The 12 integration cases cover circular, `e=0.6`, and `e=0.95` ellipses, outbound/inbound states, negative elapsed time, and 8/16 completed turns. Every case uses `a=10^7 m` and `mu=3.986004418e14 m^3/s^2`. This is a point-mass mathematical model, including cases that would pass inside an Earth-sized body's surface; no terrain or finite-radius central-body physics is claimed.

The fixed acceptance limits, declared before the first donor run, are Euclidean position error at most **0.001 m** and velocity error at most **0.000001 m/s**. The reference shares the Newtonian two-body model but uses independently expressed orbital laws and no Kepler root solve. It is a double-precision numerical check, not exact arithmetic or independent validation of the force model itself.

## Bounded event adversaries

The event fixture has a separate, explicit contract: a point follows `r(t)=r0+v*t` with constant relative velocity against a stationary sphere. It solves the quadratic boundary equation on the inclusive interval `[0, duration]`, using the stable quadratic-root form where there are two distinct roots. Four conditioned fixtures check:

- two crossings at 0.25 and 0.75 seconds while both endpoints are outside;
- one grazing contact at 0.5 seconds without a sign change;
- a true miss;
- one exit after starting inside the sphere.

The report requires endpoint-sign sampling to miss the first two cases. Those are **expected failures of the endpoint baseline**, not failures hidden from the qualification result. The swept calculation must recover the explicitly known event times. Additional unit tests separate a near-grazing hit from a near-grazing miss and reject a stationary boundary with infinitely many roots.

This event computation does **not** locate events along Gimbal's curved orbital trajectory. It supplies no certified floating-point interval bound near arbitrary degeneracies, no moving/accelerating sphere model, no event scheduler, and no ownership rule for events shared by consecutive intervals. A production warp policy still needs those contracts, plus the force/trajectory error bound used during event location.

## Evidence and limits

Run the dependency-free Python tests separately:

```sh
python3 -B -m unittest discover -s tools/orbit-fixture -v
```

The test-first reference scaffold produced a witnessed assertion failure on the circular quarter-period time; the implemented suite then passed seven tests. It includes a deliberately perturbed 1 cm returned position that fails the 1 mm gate, nonfinite-output rejection, event misses/hits, invalid domains, exclusive output, and a missing-donor failure.

The first live offline run against Gimbal revision `a4ca52a45921bbbca6180a8d5882e44354df05a6`, using Rust/Cargo 1.92.0 on the local ARM host, passed all 12 orbital cases and four event cases. Its largest position residual was approximately `1.94e-7 m`; its largest velocity residual was approximately `1.68e-10 m/s`. The report is the authoritative evidence for a particular invocation; these observations do not define new acceptance thresholds.

The recorded build/process wall times are diagnostic observations, including process startup and I/O, not a solver throughput benchmark. This fixture establishes no contact/joint behavior, stock-force capture, burn/mass-flow handling, parabolic/hyperbolic/N-body support, deterministic restart, millennial accuracy, or speedup. Those need separate experiments.
