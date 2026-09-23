# Sleeping-island fixed-step experiment

`--continuum-sleeping-island` adds one opt-in strategy to the existing settled station profiler. It captures the active
vessel's current rigidbody, joint, and collider graph without changing that topology. Immediately before each native
`PhysicsFixedUpdate`, it calls `Sleep` on every captured dynamic body. This asks whether a quiet vacuum vessel can be
represented as one inactive physics island while KSP continues running its ordinary scripts, modules, rendering, and
global clock.

The strategy admits only an unpacked active vessel in vacuum orbit at normal time with zero throttle, inactive engines,
SAS off, and RCS off. It rejects missing or changed bodies, joints, parts, vessel identity, or flight regime. It samples
all body centers in the root body's rotating frame on admission and periodically after native physics; more than 1 mm
of internal drift invalidates the run. Cleanup restores the captured awake/asleep state and verifies that all captured
joints remain present. The receipt is embedded as `sleepingIsland` in the ordinary `markers/v2` report.

Run a stock capture and a candidate capture from separate fresh processes and the same immutable station checkpoint.
The candidate adds `--continuum-sleeping-island`; both runs retain `--continuum-scale-profile --continuum-playerloop`
and the same part-count/checkpoint flags. Alternate process order across at least three pairs. Compare the complete
FixedUpdate parent and native `PhysicsFixedUpdate`, with unchanged part, rigidbody, joint, collider, and loaded-vessel
counts. A lower native child plus lower complete parent is the performance result; callback cadence alone is not.

This experiment does not create a compound body, delete joints, integrate forces, support contacts, or preserve thrust,
staging, docking, robotics, wheel, damage, or arbitrary module behavior. It deliberately suppresses native motion for a
settled quiet island and restores the graph after the bounded capture. A win would justify implementing Continuum-owned
sleep/wake state and then a semantic compound cluster. It would not establish a general vessel replacement or predict
the synthetic compound fixture's speedup.
