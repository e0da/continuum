# One-tick physics substitution canary

`--continuum-live-substitution-canary` enables a destructive, disposable qualification probe. It replaces Unity's
`PhysicsFixedUpdate` node for exactly one eligible active-vessel fixed interval, computes no replacement dynamics,
captures the vessel before and after that empty callback, and restores the exact native node before another physics
interval can run. The expected visible effect is one skipped native physics tick. This does not demonstrate correct
replacement physics or improve performance.

The flag is rejected unless the same process also supplies `--continuum-survey`, `--continuum-scale-profile`,
`--continuum-writer-census`, and all three checkpoint selectors (`--continuum-checkpoint-save`,
`--continuum-checkpoint`, and `--continuum-checkpoint-sha256`). The existing scaling qualification admits only an
unpacked, loaded, unpaused, zero-throttle vessel in normal-rate orbit after ten settled seconds.

The canary records its installation and restoration outcomes, callback count, admitted vessel and topology, complete
ordered before/after body snapshots, origin generation, frame velocity, and a one-interval writer census. It fails closed if the vessel, physical topology,
floating-origin generation, or Krakensbane frame velocity changes between admission and the callback or during the
callback. Any state change in the no-dynamics callback, duplicate callback, invalid census, or inexact restoration
invalidates the containing scaling qualification.

This mode is never enabled from the in-game panel or normal gameplay. Run it only in an owned disposable KSP copy
from a verified immutable checkpoint. A process interruption can prevent the final receipt from being written; it
cannot turn the opt-in flag into a persistent configuration.
