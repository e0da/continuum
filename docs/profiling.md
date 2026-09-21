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

A007 completed three normal 300-frame captures with no physics/update marker samples, as recorded below. Installed qualification remains open for packed/warp transitions and interruption through a scene transition, including partial export and recorder cleanup. Missing samples identify an instrumentation gap; they do not establish zero physics cost or PhysX dominance.

## Controlled qualification workload

The opt-in `--continuum-qualify` flight addon observes an externally launched
flight. It starts a 300-frame marker capture and a separate shadow-worker capture
at each of three sequential conditions: airborne with throttle command below
0.01, airborne with throttle command above 0.05, and landed or splashed. The
flight mission or player supplies those states; this addon does not steer the
vessel. Each frame now includes nullable `throttleCommand` so the report can
show whether the window remained in its starting context. A throttle command
is not proof of thrust, and a landed situation is not collision-cost attribution.

Receipts are written beneath a new `PluginData/qualification-*` directory. Each
window has a start-context text receipt, marker JSON and the completed shadow
JSON. The addon exits the process after all windows or a 600-second flight
limit. Use it only in a disposable test launch: it is intentionally an automatic
exit harness. `complete` means the three receipt windows completed, not that
all shadow samples were accepted, markers were available, or physics matched.
Marker timing includes the running shadow observer and other addons; it is not
an instrumentation-free baseline. Inspect source frames and shadow acceptance
before drawing conclusions.

## Native qualification observation: CSP-0002-A007

An isolated KSP 1.12.5 instance ran the installed qualification package from the preserved Minmus-orbit checkpoint at 1920 × 1080. The harness completed all three windows, exited with code 0, and the closed-instance receipts were preserved. This is a collection result, not a replacement-physics qualification.

| Start-classified window | Profiler frames | Accepted / submitted shadow batches | First accepted dynamic bodies | Frame-interval median / p95 (ms) |
| --- | ---: | ---: | ---: | ---: |
| Coast | 300 | 120 / 120 | 10 | 4.500 / 8.396 |
| Powered | 300 | 120 / 120 | 10 | 4.347 / 10.332 |
| Contact | 300 | 120 / 120 | 10 | 7.894 / 13.279 |

The four physics/update markers were `available-no-samples` in all three windows: 300 available frames and zero observed blocks per marker per window. `GC.Collect` was observed in two powered frames (three blocks) and two contact frames (two blocks), with median observed durations 2.654 and 2.475 ms; coast had no GC samples. These observations did not measure physics cost. The callback intervals include the instrumented workload and are not an uninstrumented FPS benchmark or a causal attribution. The powered window contained 143 frames with positive throttle command and 157 near-zero frames; its start label does not imply continuous thrust.

The worker reported no stale results in these windows. It used zero force and a constant-velocity oracle, proving live capture/transport and freshness checks under this workload, not captured stock forces or trajectory agreement. Stale-result rejection has separate synthetic epoch-transition tests. Median capture cost was 0.102, 0.090 and 0.111 ms; median handoff latency was 0.621, 0.652 and 0.939 ms. Those scopes overlap and must not be added as a total simulation cost.

Qualification quit after the landed capture, before the mission's settling acceptance completed. The preserved mission receipt still says `running`; it is an interrupted mission attempt with no terminal pass/fail verdict. The independent qualification receipt says `complete`. Neither should overwrite the other. On application teardown, the pre-existing mission cleanup logged two `FlightGlobals.ActiveVessel` null-reference failures after the flight singleton was destroyed. Capture receipts were already complete, but this run does not prove graceful mission cleanup or complete settings restoration.

Package SHA-256: `bd2680ab2e17fdc1c6a9f7c905658cfa316bf1c7ddc8055fb5837b5ae295b64c`. Parent checkpoint SHA-256: `6993575ee97eb6f9529d6f91187e63909dc2aad424e477905a4a86a063445240`, verified unchanged after exit. Native receipts do not themselves bind the package or attempt; a separate local capture/install record preserves that association. The [qualification report tool](qualification-report.md) renders the raw window receipts without promoting collection completion into a physics claim.

Next: qualify a supported timing source for actual force callbacks and native solving before selecting a replacement bottleneck. More landing precision tuning is not a prerequisite.
