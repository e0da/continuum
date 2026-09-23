# Coasting radius events

`CoastingEventScheduler.FindFirst` discovers the first inward or outward crossing of a body-centered radius within a
bounded time horizon. It scans immutable `CoastingEngine.SampleAt` snapshots, brackets a crossing, and refines that
body alone by bisection through `SampleBodyAt`. Calling `AdvanceTo(event.TimeSeconds)` then stops engine time exactly at
the predicted boundary. Neither coarse samples nor presentation publications become integration inputs.

The search admits at most 4,096 coarse intervals and 64 refinements. Its guarantee is conditional on the scan: a pair
of crossings entirely between adjacent coarse samples is not observed. This is a useful surface-altitude or configured
radial-shell boundary, not SOI, terrain, atmosphere, collision, closest-approach, or arbitrary root detection.

Tests retain that limitation as an explicit counterexample: one full-orbit scan interval starts and ends below the
configured radius while containing both an outward and inward crossing, and the search returns no event.

The analytic fixture starts at periapsis on a Kerbin ellipse with the qualification station's approximate semi-major
axis and eccentricity. Crossing the semi-major-axis radius occurs at eccentric anomaly pi/2, so the expected time is
known independently. Tests vary scan intervals, work batch sizes, and presentation cadences; all must refine the same
event time within `2e-7` seconds and radius within 2 mm. The same test executable exercises 1, 64, and 512 independent
bodies and prints diagnostic elapsed time without imposing a machine-specific performance gate.
