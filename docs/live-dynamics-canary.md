# Bounded live-dynamics candidate

Continuum now has a portable rigid-cluster step suitable for a contact-free orbital candidate. `RigidCluster6Dof.AdvanceFrozenAcceleration` applies a half impulse at the center of mass, advances translation and rotation for one fixed interval, then applies the second half impulse. This preserves rigidity and angular momentum while producing the constant-acceleration position and velocity update.

The candidate is deliberately not connected to a KSP flag. Replacing Unity's global `PhysicsFixedUpdate` node suppresses native integration, but it does not suppress KSP or mod callbacks that queued forces before that node. Unity does not expose those native accumulators through the current adapter. Writing a plausible pose and restoring native physics on the next frame would therefore risk applying deferred work after the Continuum step.

## A023 deferred-impulse result

The existing `CSP-0002-A023` receipt supplies direct installed evidence. It skipped two cached native physics callbacks in render frame `13827` and restored the exact native node. Both skipped intervals reported zero state change. The first restored native physics interval in frame `13828` then reported a maximum normalized velocity change of `0.00768149287685242 m/s`.

Across the other 125 nonzero native physics intervals in the same bounded receipt, excluding that recovery frame, the largest change was `0.0038697041186702783 m/s`; frame `13831` measured `0.0038685197461813471 m/s`. The recovery interval was therefore about 1.99 times the largest ordinary interval. This is evidence that work queued during the two skipped callbacks survived and was applied when native physics resumed. The receipt does not identify each force provider, but it falsifies the assumption that replacing the native node makes its preceding inputs disappear.

## Required ownership gate

A live dynamics candidate must own or explicitly drain every input that otherwise reaches the native solver. At minimum, the gate must account for stock `FlightIntegrator`, part force and torque deposits, direct `Rigidbody` force calls, aerodynamics, joints, contacts, frame corrections, and installed integration mods. It must also bound every loaded body affected by the global physics node. Merely restricting the active vessel to vacuum orbit does not satisfy this contract.

The next useful live substitution should target a narrower provider with a real ownership seam, such as the independently reconstructed stock aerodynamic calculation, while leaving native integration active. Live rigid-cluster publication can resume when an earlier force-provider boundary or an isolated physics world gives Continuum exclusive ownership without deferred native work.

Portable tests verify the kick-drift-kick arithmetic, rigidity, linear impulse, angular-momentum preservation, covariance, and deterministic replay. They do not exercise Unity or KSP publication.
