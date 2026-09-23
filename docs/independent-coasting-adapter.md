# Independent coasting adapter

`--continuum-coast-canary` enables a bounded KSP 1.12.5 integration canary. It first observes the same active vessel unpacked at rate zero for three updates, requests on-rails warp, then admits it only after KSP has packed it in a stable elliptic orbit at a positive warp rate. The orbit must remain above the reference body's atmosphere, remain inside its sphere of influence, and have no next patch inside the 600-second forecast.

The adapter captures one actual KSP orbit state at the current universal time. A `CoastingEngine` advances its own frontier 600 seconds on a background worker before substitution begins. KSP does not wait for that future time: each packed-orbit presentation callback samples the immutable Continuum trajectory at the current KSP universal time. Sampling a presentation time does not advance or reintegrate the engine frontier.

An optional event mode replaces the fixed 600-second frontier with an actual orbital boundary:

```text
--continuum-coast-event-radius-meters=732639.5703
--continuum-coast-event-direction=outward
--continuum-coast-event-horizon-seconds=7200
--continuum-coast-event-guard-seconds=5
```

The radius is measured from the reference body's center. Direction must be `inward` or `outward`; the horizon defaults to 7,200 seconds and the guard to five seconds. The existing analytic elliptic scheduler selects the first requested crossing in the bounded horizon, refines it through direct engine samples, and advances the Continuum frontier exactly to that event time. If the orbit is circular, the radius is outside its apsides, no crossing occurs in the horizon, or the forecast completes after the guard opens, the adapter records the reason, requests rate zero, and never claims trajectory authority.

## Clock and frame map

- `Planetarium.GetUniversalTime()` remains the global KSP clock. This canary does not give Continuum an independent whole-game clock or run a future vessel at an old planetary time.
- `CoastingEngine.Current.TimeSeconds` is Continuum's independently advanced trajectory frontier. The report records it separately from the final displayed KSP time.
- In event mode the frontier is the predicted crossing, while presentation can sample earlier KSP times from the immutable seed. The report records the crossing kind, direction, time, target-radius residual, refinement tolerance and evaluation count.
- Admission uses the packed `OrbitDriver.UpdateMode.UPDATE` path. It releases before claiming control of unpacked `TRACK_Phys`, an SOI transition, an atmosphere crossing, or another vessel.
- The engine seed and samples use KSP's orbit-native relative position and velocity returned by `Orbit.getRelativePositionAtUT` and `getOrbitalVelocityAtUT`. These are not Unity absolute world positions. KSP's driver performs its ordinary swizzle and absolute reference-body projection after the nested stock propagation call is suppressed.

The adapter seeds `Orbit.UpdateFromStateVectors`, then arms a thread-local one-shot token for the exact orbit object and bit-identical universal time. The corresponding `Orbit.UpdateFromUT` prefix consumes that token and suppresses only that duplicate propagation. The rest of `OrbitDriver.UpdateOrbit` still copies the orbit result, positions the packed vessel, handles events, and draws the orbit. A missing nested call, changed patch graph, changed body, or failed readback ends authority.

The JSON and cadence text receipts separate the bridge's synchronization cost. `stateCaptureTicks` measures the one-time
successful KSP state read and engine/reference construction after eligibility. `forecastAdmissionTicks` measures the
completed-forecast checks and authority admission, excluding background forecast time. Per successfully validated
authoritative driver callback, `evaluationTicks` measures Continuum sampling or interpolation, `publicationTicks` measures
`Orbit.UpdateFromStateVectors`, and `validationTicks` measures stock-reference, injected-orbit and driver readback plus
error gates. `driverRemainderTicks` is the nonnegative remainder of total measured callback time after those three nested
phases; it includes KSP's retained `OrbitDriver.UpdateOrbit` work between prefix and postfix as well as timer and wrapper
overhead. The total starts after the Harmony prefix has identified the owned driver and ends before timing-accounting and
completion bookkeeping. It therefore does not measure Harmony dispatch or the whole fixed step. `stopwatchFrequency`
converts ticks to seconds. Direct and Hermite runs from the same package expose whether fewer engine samples reduce
evaluation cost while publication and retained KSP driver work remain unchanged.

The per-callback phase totals contain only callbacks that consumed the exact suppression token and completed every
readback and validation step. `synchronizationMeasuredCallbacks` is that denominator and equals
`candidateDriverCalls` for a qualified run. A candidate exception, missing token, changed driver or readback exception
invalidates the run but is omitted from these phase totals because it did not reach the common timing boundary. The
separate fallback/error counters expose those omissions; phase comparisons require both to be zero.

The bounded run compares every Continuum sample with an independent stock `Orbit` initialized from the same seed. It also verifies the driver's presented position and velocity against the seeded orbit after KSP's swizzle. Any comparison outside tolerance releases immediately. Fixed-forecast mode completes after 256 accepted calls. Event mode remains admitted only until its bounded event horizon or the earlier guard, then confirms the independently advanced frontier did not move during presentation sampling, reseeds the live orbit at current KSP time while the same packed domain is still valid, and removes its Harmony patches. `--continuum-coast-quit-after-qualification` exits after either qualification path finishes.

In event mode the configured guard is an earlier completion boundary. When KSP time enters `[event time - guard, event time)`, the adapter reseeds the live orbit at the current KSP time while it still owns the packed path, releases its patches, and calls `TimeWarp.SetRate(0, true)`. If one KSP update jumps to or beyond the event, the adapter releases the current trajectory state and requests rate zero but records the run as invalid with `event-crossing-overshot`; it never labels that as a pre-event stop. The JSON records the request time and the rate index before and immediately after the request. This is a verified request to leave on-rails warp before the predicted vessel event. It does not set universal time, prove the exact wall-clock frame at which KSP finishes all warp transitions, or own the global clock.

`--continuum-coast-publication-seconds=N` selects a positive universal-time interval for exact Continuum trajectory samples; the default is two seconds. The adapter samples two endpoint states and evaluates position and velocity between them with cubic Hermite interpolation. It still injects Continuum-owned state and suppresses KSP's duplicate propagation on every admitted callback. Crossing a cadence boundary refreshes the segment endpoints; a time jump beyond the segment re-anchors it at the observed time. Neither endpoint sampling nor interpolation advances or replaces the independent engine frontier.

The adapter writes a paired `coasting-cadence-*.txt` beside its qualification JSON. It records driver callbacks, exact engine samples, and `Stopwatch` total and maximum ticks for cadence evaluation, state-vector seeding, and the measured callback remainder. Compare fresh runs using `--continuum-coast-direct-presentation` and the default Hermite cadence from the same checkpoint and warp rate. Direct mode samples the engine once per callback and exists as the same-path comparator. This separates reduced exact sampling work from interpolation, seeding, and callback work that KSP still performs; it is not a whole-game or frame-rate benchmark. The JSON remains the authority, parity, release, and cleanup record.

This establishes a narrow trajectory-evaluation and presentation seam. It does not own global time, SOI changes, atmospheric flight, contact physics, unpacked rigidbodies, other vessels, or mod compatibility. Portable builds do not qualify the installed behavior.
