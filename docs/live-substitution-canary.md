# Bounded-frame physics substitution canary

`--continuum-live-substitution-canary` enables a destructive, disposable qualification probe. It replaces Unity's
`PhysicsFixedUpdate` node for one eligible active-vessel render-frame batch, computes no replacement dynamics,
captures the vessel around the first empty callback, and restores the exact native node for the next PlayerLoop frame.
Unity may already have cached more than one fixed interval for the current render frame; the canary permits and records
one through four candidate callbacks only when they share the first callback's frame. The expected visible effect is
that bounded number of skipped native physics ticks. This does not demonstrate correct
replacement physics or improve performance.

The flag is rejected unless the same process also supplies `--continuum-survey`, `--continuum-scale-profile`,
`--continuum-writer-census`, and all three checkpoint selectors (`--continuum-checkpoint-save`,
`--continuum-checkpoint`, and `--continuum-checkpoint-sha256`). The existing scaling qualification admits only an
unpacked, loaded, unpaused, zero-throttle vessel in normal-rate orbit after ten settled seconds.

The canary records its installation and restoration outcomes, callback count, admitted vessel and topology, complete
ordered before/after body snapshots, origin generation, frame velocity, and a census around the first skipped interval. The snapshots
come from the existing PlayerLoop hooks immediately outside the substituted `PhysicsFixedUpdate` node. Admission and
bracket entry require the same vessel and ordered physical membership while allowing ordinary origin/frame changes
before the interval. The actual before/after bracket requires exact topology, floating-origin generation, and
Krakensbane frame velocity. Any state change in the measured no-dynamics interval, callback outside the bounded frame,
more than four cached callbacks, invalid census, or inexact restoration
invalidates the containing scaling qualification.

This mode is never enabled from the in-game panel or normal gameplay. Run it only in an owned disposable KSP copy
from a verified immutable checkpoint. A process interruption can prevent the final receipt from being written; it
cannot turn the opt-in flag into a persistent configuration.
