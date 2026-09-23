# Independent coasting adapter

`--continuum-coast-canary` enables a bounded KSP 1.12.5 integration canary. It first observes the same active vessel unpacked at rate zero for three updates, requests on-rails warp, then admits it only after KSP has packed it in a stable elliptic orbit at a positive warp rate. The orbit must remain above the reference body's atmosphere, remain inside its sphere of influence, and have no next patch inside the 600-second forecast.

The adapter captures one actual KSP orbit state at the current universal time. A `CoastingEngine` advances its own frontier 600 seconds on a background worker before substitution begins. KSP does not wait for that future time: each packed-orbit presentation callback samples the immutable Continuum trajectory at the current KSP universal time. Sampling a presentation time does not advance or reintegrate the engine frontier.

## Clock and frame map

- `Planetarium.GetUniversalTime()` remains the global KSP clock. This canary does not give Continuum an independent whole-game clock or run a future vessel at an old planetary time.
- `CoastingEngine.Current.TimeSeconds` is Continuum's independently advanced trajectory frontier. The report records it separately from the final displayed KSP time.
- Admission uses the packed `OrbitDriver.UpdateMode.UPDATE` path. It releases before claiming control of unpacked `TRACK_Phys`, an SOI transition, an atmosphere crossing, or another vessel.
- The engine seed and samples use KSP's orbit-native relative position and velocity returned by `Orbit.getRelativePositionAtUT` and `getOrbitalVelocityAtUT`. These are not Unity absolute world positions. KSP's driver performs its ordinary swizzle and absolute reference-body projection after the nested stock propagation call is suppressed.

The adapter seeds `Orbit.UpdateFromStateVectors`, then arms a thread-local one-shot token for the exact orbit object and bit-identical universal time. The corresponding `Orbit.UpdateFromUT` prefix consumes that token and suppresses only that duplicate propagation. The rest of `OrbitDriver.UpdateOrbit` still copies the orbit result, positions the packed vessel, handles events, and draws the orbit. A missing nested call, changed patch graph, changed body, or failed readback ends authority.

The bounded run compares every Continuum sample with an independent stock `Orbit` initialized from the same seed. It also verifies the driver's presented position and velocity against the seeded orbit after KSP's swizzle. After 256 accepted calls it publishes maximum differences, confirms the independently advanced frontier did not move during presentation sampling, reseeds the live orbit at current KSP time while the same packed domain is still valid, removes its Harmony patches, and optionally returns to rate zero and quits with `--continuum-coast-quit-after-qualification`.

`--continuum-coast-publication-seconds=N` selects a positive universal-time interval for fresh Continuum presentation samples; the default is two seconds. The first owned callback publishes immediately. On later callbacks before the next boundary, the adapter lets KSP propagate the previously injected orbit for presentation. Those display-only steps do not advance or replace the Continuum engine frontier. When the cadence boundary arrives, Continuum samples the immutable trajectory at the current KSP time, injects that state, and suppresses only KSP's duplicate propagation for that callback.

The adapter writes a paired `coasting-cadence-*.txt` beside its qualification JSON. It records driver callbacks, published samples, and `Stopwatch` total and maximum ticks for engine sampling, state-vector seeding, and the measured callback remainder. Compare fresh runs of a dense interval such as `0.000001` and the default sparse interval from the same checkpoint and warp rate. This separates reduced sampling/seeding work from callback work that KSP still performs; it is not a whole-game or frame-rate benchmark. The JSON remains the authority, parity, release, and cleanup record.

This establishes a narrow trajectory-evaluation and presentation seam. It does not own global time, SOI changes, atmospheric flight, contact physics, unpacked rigidbodies, other vessels, or mod compatibility. Portable builds do not qualify the installed behavior.
