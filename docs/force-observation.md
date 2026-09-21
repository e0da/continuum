# Force observation boundary

Continuum has an opt-in component observer, enabled with `--continuum-part-forces` during an existing Probe capture. It copies raw `Part.force`, `Part.torque` and `Part.forces` entries at `FashionablyLate`. Source tests and compilation do not qualify installed callback ordering or compatibility. No aggregate-force or solver-replacement claim follows from this capture. The separate flight-shadow worker still uses synthetic zero forces for its transport check.

## Implemented experimental seam

`PartForceObservation` owns a read-only main-thread callback and copies values into immutable Core batches. The receipt records provider identity and native assembly MVID; batches link to its session and record capture time, vessel and topology identity, a provider-local physics epoch, frame generations and per-part observations. Unity and KSP objects never enter the portable batches.

The experimental provider copies the public `Part.force`, `Part.torque` and `Part.forces` census at `TimingManager.TimingStage.FashionablyLate`, before the flight-integrator stage; relative ordering among callbacks at the same stage is not a completeness guarantee. [Principia uses this named stage](https://github.com/mockingbirdnest/Principia/blob/dcf1fb949d792be966e5a7a162a1073ddfa1f9c1/ksp_plugin_adapter/ksp_plugin_adapter.cs) to collect nonconservative part forces before the integrator clears them. The owned KSP 1.12.5 assembly identified in [the integration map](integration-map.md) exposes those fields, `Part.AddForce`, `Part.AddForceAtPosition`, `Part.AddTorque`, and the named timing stages.

The receipt reports availability separately for:

- the `Part` force and torque census;
- stock aerodynamics;
- gravity;
- contacts and constraints;
- direct `Rigidbody` writes.

Only the part census can be reported as captured. An unavailable component has no numeric substitute. Context matching covers session, vessel, topology, physics epoch, frame and capture time. These epochs are local to the provider; equal counter values in the separate Shadow receipt do not join the captures. A future worker bridge needs physical state captured at this same boundary.

The observer copies each logical part's own storage exactly once. `rigidBodyPartFlightId` exposes redirection to a physical part without copying that owner's census again. This does not attribute entries to individual force-producing mods. Positioned forces preserve world position and, where a native body exists, same-callback center of mass and lever arm. Values retain raw KSP units; there is no SI conversion or total-force reconstruction. Impulse deposits may already have been divided by the fixed step by KSP.

Retention is bounded to 16 batches, 512 parts per batch, 64 positioned forces per part, 2048 retained part records and 4096 retained positioned-force entries. Overflow rejects the prospective batch whole and reports `bounded`; no partial census is called complete. `interrupted` retains a valid prefix. `unavailable` and `invalid` retain explicit failure states. Report pages distinguish no samples and unqualified cleanup from useful component observations.

Registration checks that the public named-stage API actually installed exactly one owned callback in a unique live `Timing3`; the native API can silently return without registering. Audits reject callback loss/duplication or stage replacement. Cleanup removes owned delegates from the retained stage, preserves foreign callbacks, unregisters the origin observer and disables capture before removal. A destroyed stage is reported as `owner-destroyed`, not verified removal. Callback faults are caught so later subscribers can continue. Audits and defensive copies have overhead; this is bounded instrumentation, not a hot-loop performance improvement.

## Why the census is incomplete

Principia has a separate flight-integrator-stage path that reconstructs stock lift and drag because stock aerodynamics does not publish through `Part.AddForce`. PhysX contacts and constraints, direct `Rigidbody` calls, provider-internal terms and later writes are also outside the proposed census. Post-step velocity change cannot recover their ownership: contacts, constraints, frame motion and provider corrections are mixed together.

The resulting receipt is a component observation. It is not total force, a stock trajectory prediction, a deterministic replay input or proof that every force provider was observed.

## Existing hook owners

[ModularFlightIntegrator (MFI)](https://github.com/sarbian/ModularFlightIntegrator/blob/03f07cd6e498ed003f84ee97ce976430e6447256/ModularFlightIntegrator.cs) replaces the stock integrator wrapper through its [manager](https://github.com/sarbian/ModularFlightIntegrator/blob/03f07cd6e498ed003f84ee97ce976430e6447256/MFIManager.cs). Its principal integration and aerodynamics registrations are single static delegate slots that refuse a second owner, and the inspected API has no unregister operation. Continuum must not occupy `Integrate` or `UpdateAerodynamics` merely to observe forces. A future owner adapter must check registration success and abstain when another owner already holds a slot.

[FAR registers MFI's aerodynamic override](https://github.com/dkavolis/Ferram-Aerospace-Research/blob/c769cbd3d23ec9fd22538c1eb8b57fd2fcd025c6/FerramAerospaceResearch/FARAeroComponents/ModularFlightIntegratorRegisterer.cs). In the pinned source, FAR accumulates local aerodynamic force and torque, converts them to world space, then normally publishes them with [`Part.AddForce` and `Part.AddTorque`](https://github.com/dkavolis/Ferram-Aerospace-Research/blob/c769cbd3d23ec9fd22538c1eb8b57fd2fcd025c6/FerramAerospaceResearch/FARAeroComponents/FARAeroPartModule.cs). This makes FAR deposits plausible census inputs. Water, fallback and version-specific paths still need installed tests; Continuum must leave FAR's MFI slot untouched.

Principia reads the same part census, then performs separate gravity and motion work. A Continuum callback could copy that raw channel without mutation, but same-stage coexistence, epoch linkage and installed ordering remain unqualified. It must not disable gravity, correct positions, publish vessel state or describe the census as Principia's total force.

No inspected local instance contained MFI, FAR or Principia. Source evidence establishes candidate APIs and conflicts, not installed compatibility.

## Qualification order

1. In a disposable stock instance, apply known `Part.AddForce`, `AddForceAtPosition` and `AddTorque` sentinels to an isolated body. Verify the copied entries before the integrator clears them, bounds, epoch linkage and callback removal. Keep stock aero unavailable.
2. Pin FAR and MFI releases. Verify occupied MFI slots, then compare FAR's source-visible published force and torque with the part census during bounded atmospheric windows. Test special paths separately.
3. Pin Principia. Verify that both observers see the same sentinel census while Continuum performs no ownership calls. Exercise packing, origin shifts and teardown. Trajectory agreement is not force-completeness evidence.

Qualify FAR and Principia separately before any combined claim. If provider identity, callback placement or lifecycle cleanup cannot be established, the adapter must refuse active capture rather than emit an ambiguous batch.
