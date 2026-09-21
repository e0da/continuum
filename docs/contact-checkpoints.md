# Contact-engine checkpoint experiment

This standalone Jolt fixture asks whether an interacting assembly can resume the same trajectory after restoration, and whether recreating its visible body state is sufficient. It builds beside the [structural benchmark](structural-benchmark.md), using the same pinned engine, compiler settings and single-threaded CPU execution. It does not run or change KSP.

## Three continuations

Two moving boxes contact a floor while a distance constraint connects their centers. Sleeping is disabled. After contact develops, the experiment captures a checkpoint and runs three continuations with the same timestep schedule:

1. Continue the original world uninterrupted.
2. Restore Jolt's full simulation state into the original world, preserving its topology and configuration, then repeat the continuation.
3. Create a new world with the same bodies, identities, shapes, materials and joint definition, initialized from the checkpoint's positions, orientations and linear/angular velocities.

The third case deliberately omits engine history. A mismatch means this reconstruction procedure cannot reproduce the original continuation. It does not identify a particular cache as the sole cause. Equality would be a valid result for this fixture and would not establish general sufficiency.

The boxes each have mass 1 kg and half extents `(0.4, 0.25, 0.4)` m. They start at `(-0.6, 0.26, 0)` and `(0.6, 0.26, 0)` m above a floor with top at zero. The local-COM distance constraint has nominal length 1.2 m. Gravity is 9.81 m/s² downward, friction 0.6, restitution and damping zero. Initial x velocities are 0.8 and 0.1 m/s; z angular velocities are +0.2 and -0.2 rad/s. The engine uses 10 velocity iterations, 2 position iterations and one collision step per update.

There are 24 warmup steps followed by 120 continuation steps. A `(0.15, 0, 0.05)` kg m/s impulse is applied to the first box before the final warmup update (zero-based index 23). Every continuation receives the same impulse before update 30. The report preserves nominal `1/120` s and the actual float timestep supplied to Jolt separately. Sample zero precedes continuation; samples 1–120 follow each update.

## Run

```sh
cmake -S tools/structural-bench -B artifacts/structural-bench/build -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build artifacts/structural-bench/build --parallel 6
ctest --test-dir artifacts/structural-bench/build --output-on-failure
```

The checkpoint executable and consumer test are registered in this existing build. The consumer recomputes comparison metrics from raw samples. Preserve reports in ignored artifacts; engine source, compiled products and local evidence do not belong in Git.

To retain a new receipt without overwriting an earlier one:

```sh
python3 - <<'PY'
import datetime, pathlib, subprocess
result = subprocess.run(['artifacts/structural-bench/build/continuum-checkpoint'], capture_output=True)
stamp = datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%S%fZ')
destination = pathlib.Path('artifacts/structural-bench') / ('checkpoint-' + stamp + '.json')
with destination.open('xb') as output:
    output.write(result.stdout)
print(destination)
if result.stderr:
    print(result.stderr.decode(), end='')
raise SystemExit(result.returncode)
PY
```

Schema `ksp-continuum-checkpoint/v1` contains configuration, checkpoint evidence and three raw trajectories. Exit 0 means the fixture gates pass, exit 2 retains a completed report that fails a gate, and exit 1 indicates an execution error whose output may not be JSON. Gates require an observed floor contact, checkpoint speed greater than `1e-5` m/s, matching full-restore bytes and sampled motion, and identical exposed initial state for cold reconstruction. The consumer additionally checks identities, active states and independently recomputes reported trajectory differences, constraint error and center displacement. Cold divergence does not fail qualification.

## First measured result

The ARM64 AppleClang 21.0.0.21000101 run on 2026-09-21 retained an 819-byte engine checkpoint while the fastest box moved at 0.03736 m/s. Full restore reproduced the checkpoint bytes and all 121 sampled states exactly. Both dynamic bodies remained active.

| Cold reconstruction difference from uninterrupted | Maximum over both bodies and all samples |
| --- | --- |
| Position distance | `3.379e-7` m |
| Linear velocity distance | `3.860e-5` m/s |
| Angular velocity distance | `3.102e-6` rad/s |
| Quaternion component difference | `0` |

These are small differences; the fixture does not establish a gameplay problem. They falsify exact continuation from this exposed-state reconstruction. The component-wise quaternion comparison is not an angular error metric. Horizontal center displacement includes the intended impulse-driven motion and is not labeled numerical drift. Constraint error is measured against nominal 1.2 m, while Jolt stores the requested length as a float.

The original setup had nearly stopped by capture, reaching only `1.29e-7` m/s, and failed the unchanged activity gate. That report was retained. The declared final-warmup impulse made the experiment discriminating without weakening the gate. Tests first rejected a placeholder receipt, then independently checked the implemented trajectories. The structural baseline and checkpoint consumer both passed CTest.

## What a checkpoint needs

Jolt records simulation-modified state, including the previous timestep, gravity, bodies, contacts and constraints. Its state recorder explicitly excludes some application configuration, including friction, restitution and motion quality. The application must preserve compatible topology, identities, settings, input phase and engine/build identity alongside the engine checkpoint. See the pinned [state-recorder contract](https://github.com/jrouwe/JoltPhysics/blob/e77f175595e64cb44218cc9d9d56fc365ad0e36a/Jolt/Physics/StateRecorder.h) and [save/restore implementation](https://github.com/jrouwe/JoltPhysics/blob/e77f175595e64cb44218cc9d9d56fc365ad0e36a/Jolt/Physics/PhysicsSystem.cpp).

Restore mutates the target in stages and can fail after earlier stages have changed. It is not an atomic publication operation. A future driver must discard an unsuccessfully restored provisional world rather than publish it as authoritative. This experiment aborts on restoration failure; it does not implement a recovery service or qualify corrupted checkpoint handling.

Contact callbacks and their external effects are another boundary. Replaying the physics does not roll back sounds, emitted records, game events or other systems. Their replay/deduplication policy remains application work.

## Interpretation limits

Exact equality here means repeatability within the tested executable environment, over the sampled continuation. It is not physical accuracy, cross-platform determinism, multicore qualification, arbitrary topology restoration, or KSP mission replay. Update timings exclude world construction, checkpoint encoding/decoding, observation, bridge traffic and game publication; they cannot establish a gameplay speedup.

The [local handoff fixture](local-handoff.md) accepts straight free flight followed by an instantaneous velocity change. Continuous gravity, friction and joint solving do not satisfy that contract. This experiment qualifies a separate richer regime before its driver interface is designed. The [KSP state-ownership map](ksp-state-ownership.md) records the remaining live integration questions.
