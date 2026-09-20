# Record and replay input tracks

The flight panel records a vessel control vector at the tail of `OnFlyByWire`, immediately before KSP dispatches it to parts. This is a verified vessel callback boundary, not proof of every final actuator input: part consumers or patches can still change behavior afterward. Recordings include all 14 standard analog fields, four boolean fields, custom axes, stage notifications and sampled flight observations.

## Record

In an independent disposable flight, choose **Record flight inputs** before launching. Stop with **Stop recording / abort replay**. A unique `GameData/KspContinuum/PluginData/inputs-*` directory contains:

- `session.txt`: boundary, game module identity and initial vessel/topology/action-group information.
- `segment-*.csv`: editable control tracks, split into bounded blocks of at most 1,000 observed control samples, with unchanged values compressed into step keys.
- `segment-*.txt`: absolute start universal time, sample count and initial topology/action-group/control-reference identity.
- `events.csv`: session-wide event times, including events between segments and before the first control observation.
- `observations.csv`: universal time, body, situation, latitude/longitude, altitude, speeds, mass, part count and stage.
- `session-end.txt`: terminal recording status and segment count.

Packing ends a control segment; recording resumes in a new segment on unpacking. Part topology, action groups and control-reference changes are marked. Staging events mean activation began, not that separation succeeded. The passive recorder permits an existing autopilot, but stops if its observer is removed/reordered or its supported data contract is violated. A one-sample segment with no positive duration has observations but no replayable track file.

Files are local runtime artifacts. They contain neither a complete save nor all state needed for deterministic replay. Preserve the original recording before editing.

## Replay and edit

Copy a selected segment to `GameData/KspContinuum/PluginData/replay.csv`, edit it, then select **Replay PluginData file**. An example with zero controls is included as `examples/neutral-inputs.csv`. The player starts at timeline time zero on its first control callback, from the **current vessel state**. It does not restore the original launch/save or automatically chain segments.

Replay requires KSP 1.12.5's inspected assembly, an active controllable unpacked vessel, unpaused normal physics rate, no SAS or control override, and no unqualified control callbacks. Disable/remove competing controllers in the test configuration; an inactive mod can still own callbacks. The first player supports the 14 standard analog channels; recorded nonzero boolean/custom-axis controls are refused. It stops before any recorded discrete event rather than activating a stage or changing action groups. It aborts if topology, action groups, control reference, callback ownership, vessel identity or warp state changes. It clears owned controls at completion/abort and removes only its own callback and input lock. Escape also aborts replay.

Active replay also refuses loaded `DarkMultiPlayer` or `LmpClient` assemblies. This is presence detection, not connection detection, and does not cover renamed forks or other providers. Observational recording remains available.

These exclusions make analog phase tests usable without pretending that a complete multi-stage mission is replayable already. Record the full mission, then compare bounded phases under controlled initial conditions. A deterministic complete mission and checkpoint restoration remain separate work.

## Track format

The strict UTF-8 format has these ordered sections, with invariant decimal points and no quoted fields:

```text
schema,ksp-continuum-input-timeline/v1
duration,2
track,name,min,max
track,mainThrottle,0,1
key,track,time,value,mode,control1,control2
key,mainThrottle,0,0,cubic-bezier,0.2,0.8
key,mainThrottle,2,1,step,1,1
event,time,name,value
```

This one-channel excerpt illustrates a ramp; game replay additionally requires every analog channel. Each track starts at zero, with strictly increasing key times no later than duration. Modes are `step`, `linear` and `cubic-bezier`; a key defines interpolation to the next key. Bezier handles are values over linear normalized segment time, not editable time handles. All values and handles must fit the declared range. The final key holds through the timeline end. Events at equal times retain file order. A monotonic cursor emits each event once and rejects backward seeking.

Duration must be positive. Parsing is bounded to 16 MiB, 64 tracks, 32,768 keys per track, 65,536 total keys, 8,192 events, 100,000 lines and 1,024 characters per line. Invalid or unknown data fails before controls are acquired. Evaluation uses simulation time, not render frame count. Sampling at the game's control cadence cannot guarantee preservation of a pulse shorter than one control tick; such tracks need an appropriate sampling policy before they qualify a numerical comparison.

## Verification boundary

Portable tests cover interpolation, extreme finite values, immutability, event ordering, culture and serialization limits. Reference compilation verifies API availability. Actual control ownership, recording and mission behavior require game execution; see the current validation receipt. UI appearance and full-game determinism must not be inferred from these tests.
