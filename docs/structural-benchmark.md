# Native structural baseline

The same pinned build also runs the [contact checkpoint experiment](contact-checkpoints.md), which compares uninterrupted simulation, full state restoration and cold reconstruction of a contacting assembly.

This standalone experiment calls Jolt's synchronous `PhysicsSystem::Update` on the host CPU. It measures a compliant two-body oscillator against analytic motion and checks a sphere dropping onto a floor. It does not load KSP, implement a native game bridge, or replace stock physics. Box3D, rocket chains, rotational compliance, docking and breakup remain later experiments.

## Build and run

Requirements: CMake 3.24+, a C++17 compiler, Python 3, and network access for the first source download. Ninja is optional if another single-configuration generator is selected. Run from the repository root:

```sh
cmake -S tools/structural-bench -B artifacts/structural-bench/build -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build artifacts/structural-bench/build --parallel 6
ctest --test-dir artifacts/structural-bench/build --output-on-failure
```

CMake downloads Jolt v5.6.0, commit `e77f175595e64cb44218cc9d9d56fc365ad0e36a`, and verifies archive SHA-256 `1f32328fb763135de10a244568d6ccb2ed9b1e6593fafe6dc6db5b2719d330bd`. Downloaded sources and compiled products remain in ignored `artifacts/`; there is no vendored source or install target. CMake rejects build directories outside the repository's artifacts directory. This pins inputs and selected options, not bit-for-bit binary reproducibility across toolchains.

The executable takes no arguments and writes JSON to stdout. Preserve a unique report without overwriting earlier evidence:

```sh
python3 - <<'PY'
import datetime, pathlib, subprocess
result = subprocess.run(['artifacts/structural-bench/build/continuum-structural'], capture_output=True)
stamp = datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%S%fZ')
destination = pathlib.Path('artifacts/structural-bench') / ('jolt-' + stamp + '.json')
with destination.open('xb') as output:
    output.write(result.stdout)
print(destination)
if result.stderr:
    print(result.stderr.decode(), end='')
raise SystemExit(result.returncode)
PY
```

Exit 0 means all three repetitions of the finest oscillator configuration and the drop meet their frozen criteria. Exit 2 means the completed report failed that qualification; coarser configurations may fail without making the whole experiment fail. Exit 1 reports an execution/configuration error on stderr. A report file from an execution error is not necessarily valid JSON. Keep failure evidence rather than interpreting every created file as a successful receipt.

## Physical fixture and oracle

Two free dynamic spheres of radius 0.1 m have masses 2 kg and 5 kg. Their centers start 2.2 m apart, with zero velocity and center of mass at the origin. A center-to-center distance constraint has equilibrium length 2 m, stiffness 25 N/m, and damping 0.5 N s/m. Gravity, body linear/angular damping and sleeping are disabled. Shapes remain separated, so the oscillator has no contacts or applied torque.

Jolt's `SpringSettings::StiffnessAndDamping` specifies the force law directly; it avoids treating the same frequency parameter as equal stiffness for different masses. See the pinned [spring settings](https://github.com/jrouwe/JoltPhysics/blob/e77f175595e64cb44218cc9d9d56fc365ad0e36a/Jolt/Physics/Constraints/SpringSettings.h) and [distance constraint](https://github.com/jrouwe/JoltPhysics/blob/e77f175595e64cb44218cc9d9d56fc365ad0e36a/Jolt/Physics/Constraints/DistanceConstraint.h).

For extension `q = separation - restLength`, reduced mass `mu = m1*m2/(m1+m2)`, decay `a = c/(2*mu)`, and damped frequency `w = sqrt(k/mu-a*a)`, the continuous reference is:

```text
q(t) = q0 * exp(-a*t) * (cos(w*t) + a/w*sin(w*t))
v(t) = -q0 * (k/mu)/w * exp(-a*t) * sin(w*t)
```

The source computes errors; the Python test independently recomputes this oracle from raw output and verifies every summary and pass flag. The chosen law includes genuine elastic motion and damping. Error means disagreement with that motion, not failure to keep the joint rigid.

Each run advances 250 nominal 0.02-second macro steps (approximately five seconds). Jolt receives a float timestep. Collision-step settings 1, 2, 4, 8 and 16 subdivide each update, keeping physical parameters and solver iterations fixed: 10 velocity iterations and 2 position iterations. All Jolt physics settings not explicitly changed retain the pinned release defaults. Positions use double precision; local solver arithmetic still uses floats. The build enables deterministic compiler settings, disables assertions, LTO, renderer/profiler and GPU backends, and uses `JobSystemSingleThreaded` with zero worker threads. This does not test multicore scaling or prove cross-platform determinism.

Each run's frozen maximum-error criteria are:

| Quantity | Bound |
| --- | --- |
| Relative extension versus analytic reference | 0.005 m |
| Relative velocity versus analytic reference | 0.02 m/s |
| Center-of-mass drift | 0.00001 m |
| Total linear momentum magnitude along the fixture axis | 0.00001 kg m/s |

The drop uses a 1 kg sphere, radius 0.25 m, initial center height 2 m, gravity 9.81 m/s², and a static box whose top is at zero. Friction and restitution are zero; motion quality is discrete. It runs 500 steps of 0.01 seconds with four collision steps. It must agree with free fall within 0.015 m for the first 0.5 seconds, never put the sampled center below 0.225 m, and remain within 0.025 m of the expected resting height with speed below 0.05 m/s throughout the final second. This is a low-speed contact sanity check, not a CCD, friction, restitution or terrain qualification. Checks sample macro-step boundaries, not the whole continuous trajectory.

## Timing and report interpretation

Schema `ksp-continuum-structural/v1` contains engine identity, target architecture, compiler, configuration, physical parameters, per-run error summaries and raw trajectory/timing samples. `timeSeconds=0` is the initial state and has `updateMilliseconds=0`; omit that row from timing statistics. All other intervals cover only the synchronous `Update` call using `steady_clock`, including its internal job scheduling. World construction, input preparation, observation, analytic validation, JSON encoding, a native/managed bridge and KSP publication are excluded. This is intentionally backend timing, not an end-to-end flight budget.

One complete oscillator run per configuration is discarded before measurement. Three measured repetitions each create new worlds; their first update includes newly initialized solver state. Configuration order rotates by two positions per repetition; it is not randomized. The drop has no discarded warmup. Timings are exploratory and can be affected by clock granularity, allocation, CPU state and system load.

## Initial native result

The first host execution on 2026-09-21 UTC built and ran as ARM64 with AppleClang 21.0.0. The initial test stub produced four expected failures for missing schema/trajectories; the native implementation passed all four outside-in tests through CTest. The raw local report is retained under ignored artifacts. No game installation or execution was involved.

The first repetition produced:

| Collision steps | Max extension error (m) | Max velocity error (m/s) | Qualified | Median Update (ms) |
| --- | --- | --- | --- | --- |
| 1 | 0.0499302 | 0.208999 | No | 0.001125 |
| 2 | 0.0296338 | 0.123874 | No | 0.001708 |
| 4 | 0.0162724 | 0.0684967 | No | 0.002875 |
| 8 | 0.00853717 | 0.0360683 | No | 0.005333 |
| 16 | 0.00437382 | 0.0185137 | Yes | 0.010125 |

All three finest runs qualified. In that first repetition, maximum center-of-mass drift was 0.000000336 m and momentum magnitude was 0.00000171 kg m/s. The drop passed, ending at height 0.249999996 m with zero velocity. Extra collision steps cost more while reducing this fixture's motion error. The result establishes one spring/contact baseline; it does not select an engine for long flexible rockets or demonstrate stock-physics speedup.

Implementation precedent: the existing portable worker benchmark supplies the raw-report/analytic-validation pattern; Jolt's pinned [HelloWorld](https://github.com/jrouwe/JoltPhysics/blob/e77f175595e64cb44218cc9d9d56fc365ad0e36a/HelloWorld/HelloWorld.cpp) supplies allocator, factory, layer/filter and world-lifecycle API precedent. No proprietary source is included.
