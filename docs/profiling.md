# Observational flight profiling

The flight panel's existing **Capture 300 frames of available timing markers** button now writes `ksp-continuum-markers/v2` JSON in the local `GameData/KspContinuum/PluginData` directory. Capture remains opt-in. It changes recorder enable flags temporarily; it does not change the vessel, time warp, global profiler enable state, or graphics settings.

The report contains the raw observations and their summaries:

- `frames` records coroutine boundary intervals measured with `Stopwatch`, native scene/UT, active-vessel ID, body/situation, part count, loaded/packed state, warp rate, pause state, fixed timestep, time scale and screen dimensions. Managed heap bytes and generation collection counts are observations, not allocation-rate measurements. A missing vessel has explicit status, a null identity and part count `-1`; unavailable optional context is null.
- `markers` retains elapsed nanoseconds, block counts and an `available` mask for each observation of Physics.Simulate, Physics.Processing, BehaviourFixedUpdate, BehaviourUpdate and GC.Collect. A false availability entry must never be interpreted as zero cost. Native invalid counters remain raw but are excluded from summaries. `availabilityDetail` describes setup failure or the latest read failure.
- Each marker summary distinguishes available, unavailable, positive-block and zero-block frames. Duration distributions include only available positive-block frames. No observations produce a null distribution, including a valid recorder that never emits a block. Marker times can overlap and must not be added to infer total CPU time or a physics percentage.
- `wallIntervals` contains count, minimum, maximum, mean, p50, p95 and p99 of the callback intervals. Percentiles use linear interpolation at `(count - 1) * quantile`. These intervals include scheduling, waiting, rendering cadence and probe overhead. They are not CPU execution durations or a performance improvement measurement.

Unity 2019.4's [Recorder](https://docs.unity3d.com/2019.4/Documentation/ScriptReference/Profiling.Recorder.html) reports the preceding frame's accumulated marker time and [block count](https://docs.unity3d.com/2019.4/Documentation/ScriptReference/Profiling.Recorder-sampleBlockCount.html). The probe skips its partially enabled first frame. `markerFrame` identifies the previous native frame, `observedFrame` identifies the read, and `contextFrame` identifies the earlier context boundary. `contextAligned` and the report's misaligned-frame count expose gaps; even aligned context is one boundary observation, not proof that the vessel or warp state stayed constant throughout the frame.

The report records KSP/Unity/plugin versions, platform, processor and graphics labels, target frame rate and v-sync setting. These describe this capture's environment; they do not control other processes or establish comparable workloads.

## Completion and ownership

Normal completion exports 300 observations with status `complete`. Disposing a running probe exports only its completed prefix with status `interrupted`; unobserved array tails are removed. The report callback runs at most once. Recorder enables acquired by the probe are released before export, and a failed partial-report write is logged without preventing teardown. Cleanup failures are explicit in the report. A destroyed process or failed filesystem write can still prevent a durable receipt.

Only one Continuum probe can run at a time. Unity recorders are shared engine objects, so concurrent external changes to their enable flags are not an independently owned lease; avoid changing those flags during a capture. Previously enabled recorders are left enabled by this probe, and only its own initial enables are undone.

## Qualification boundary

Portable tests cover raw-marker availability, no-block observations, distribution arithmetic, invalid readings, partial-prefix trimming and nested JSON export. Native compilation checks the installed KSP 1.12.5 / Unity 2019.4 API surface. Neither establishes marker availability or capture behavior in a release player.

Installed qualification remains open for this version: collect a normal 300-frame report, inspect missing markers explicitly, compare packed/unpacked and warp contexts, and interrupt a capture through a scene transition to verify partial export and recorder cleanup. A release player may provide no usable physics markers. That result identifies an instrumentation gap; it does not establish zero physics cost or PhysX dominance. No game-profile measurement from this implementation is claimed yet.
