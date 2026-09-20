# Validation receipt

## Input timeline and mission trials

The combined analytic/report/timeline suite passes 123 portable assertions; optional mission policies pass 36 assertions. These do not establish whole-game determinism. The following mission attempts are retained, including failures before launch:

| Attempt | Mode | Observed result |
| --- | --- | --- |
| 1 | Headless | Preflight abort before sandbox creation: the version check confused MechJeb assembly version 2.15.0.0 with package/file version 2.15.3.0. The paired version check was corrected. |
| 2 | Rendered, 1280×720 | Stock Kerbal X reached the launchpad. Strict replay refused an existing control callback before launch. Installed MechJeb adds its core to command pods through ModuleManager; the mission had assumed it would attach that owner later. Failure telemetry, a unique sandbox save and a confirmed screenshot were preserved. |
| 3 | Rendered, 1280×720 | Intact, settled Minmus touchdown passed the declared acceptance check after launch, Kerbin orbit, transfer, correction, capture and descent. Neutral replay explicitly not run. All 77 exported input segments parsed and round-tripped. This is recording/schema and mission execution evidence, not deterministic playback or world-state reproduction. |

Attempt 2's screenshot was visually inspected and shows the craft and Continuum panel behind KSP's first-run announcement. The user dismissed that announcement; it was not the recorded replay-guard failure. Neutral replay completion remains unqualified.

Attempt 3 uses package `0.1.0-inputs.8821BD7CC20D`. Its installed DLLs matched the built bytes before launch: plugin SHA-256 `e2f31b983de38f0d3220b6ac0b35d8abad51d38ad1b50dd8b35a7be623ff2b9a`, mission SHA-256 `8643466ed8ec6d89eea3f91ccd6400578e10b3d80aab4ada90f7438914a381b7`. A subsequently reviewed ownership-cleanup correction passes portable tests and compilation but is not the binary running this attempt.

### Attempt 3 outcome and limitations

At UT 269974.00335770514 the mission reported Minmus LANDED, zero throttle, all 17 lander parts surviving and surface speed 0.00687 m/s after qualifying observations spanning 30 simulated seconds. Elapsed runner time was 1,572 seconds (about 26 minutes), much of it a normal-rate descent. The mission produced 1,559 telemetry rows, 77 input segments (15,599,319 bytes), five checkpoint saves and five confirmed milestone/launchpad PNGs. Every completed input segment passed the portable parser and serialization round-trip check. The final save exists and the landed PNG was visually inspected. The rendered game remained open after releasing mission controls.

The observer reported repeated flipping during slow powered descent and little apparent fuel use. Sampled throttle was often 0.05 and surface speed fluctuated at low values. The dark final image appears to show a tipped lander. Current telemetry does not contain attitude, angular velocity or thrust direction, and current acceptance does not require an upright pose or quantify rotational settling. Record this as an intact, settled touchdown under the stated checks, not a clean upright landing. The cause of the flipping is unresolved; add orientation and angular-rate observations before attributing it to an autopilot, controls, or physics defect.

Two screenshots requested in the same frame at Minmus capture did not both materialize: descent-start was confirmed and minmus-orbit was explicitly marked unconfirmed. Checkpoint saves for both milestones exist. There is no video or audio recording and no reconstructed replay of the descent.

## Earlier isolated benchmark qualification

The isolated benchmark ran in an independent stock-derived KSP 1.12.5 Mac test copy on 2026-09-20, on Apple M4 Max (arm64 host, x86_64 game executable), using Unity 2019.4.18f1 and `-batchmode -nographics --continuum-bench`. Existing playable and mod-pack verification copies were preserved. The test copy was prepared only after KSP, CKAN and Steam were closed; installation used CKAN.

## Source and build checks

- 37 analytic/report assertions cover aggregate mass/inertia, split momentum, invalid inputs, nested report arrays, escaped strings, invariant numeric formatting and exact 64-bit integer export.
- Release compilation against owned game references passes with zero warnings/errors. The package contains only the project DLL and documentation; game assemblies are not copied.
- Independent runtime and numerics reviewers found a rotated-hierarchy construction defect. The fix sets the Transform pose before creating the Rigidbody and checks initial child positions explicitly.
- The first engine run exposed missing nested arrays in Unity JSON output despite passing numerical checks. A narrow report writer now exports the supported DTO fields and rejects nonfinite numbers; tests parse the output independently with System.Text.Json. The corrected runtime artifact includes every timing sample.

## Engine evidence

The installed DLL was compared byte-for-byte with the built DLL before testing. Its SHA-256 is `34d2d52fdce8834c3b334a9c8739ad6a3750d478dfa075af665ce8755577696a`. Raw reports remain local runtime artifacts. The test uses 50 warmup and 200 measured steps, six alternating pairs per size, and a 0.02-second timestep.

Both corrected runs exited 0, each exported 36 samples, and passed complete collider ray hits, finite positive timings, compound spacing bounds, initial rotated pose, separation momentum and floor-contact checks. Initial pose error was 2.67e-7; relative linear momentum error was zero and relative angular momentum error was 4.67e-6, below the 1e-5 acceptance bound. Each local scene unload is awaited before the next experiment and report completion. The game log contained no matching exception/error entries; headless shader warnings are not graphics validation.

Median milliseconds per step; each entry uses six samples. Ratio is the median of the six paired jointed/compound ratios, with full paired range in parentheses.

| Boxes | Run 1 jointed / compound | Run 1 ratio | Run 2 jointed / compound | Run 2 ratio |
| --- | --- | --- | --- | --- |
| 8 | 0.1847 / 0.1590 | 1.158 (1.113–1.181) | 0.1803 / 0.1591 | 1.139 (1.093–1.199) |
| 32 | 0.1912 / 0.1578 | 1.220 (1.200–1.288) | 0.2009 / 0.1610 | 1.252 (1.211–1.287) |
| 128 | 0.2539 / 0.1611 | 1.583 (1.526–1.677) | 0.2590 / 0.1633 | 1.596 (1.555–1.604) |

This supports reduced representation cost for this particular isolated workload. It does not measure stock-vessel or whole-game improvement. Runs occurred on a shared workstation with uncontrolled background load and no rendered game view. They do not predict another host's performance.

Local raw report SHA-256 values: `81b418b42762cd72aa2e50f7b6e193a46b76ec394b4fbe89173b8ade48069c4a` and `d35fa7b7b340bb5a01622819a0f719b183b1842e4970960e31b00d0beb1ad508`.

## Qualification still open

Visible panels, live-vessel inventory, profiler marker availability and recorder cleanup through scene transitions, real docking/decoupling, stock-vessel performance, and mod compatibility are not verified. There is no demonstrated in-engine failing-before/passing-after result for the rotation fix. The numerical regression is source-reviewed and the corrected state has run in Unity.

C# tests do not exercise Unity. Synthetic bodies do not establish KSP vessel integration. Optional timing markers cannot fully attribute frame time and may be unavailable in release players. CI checks portable analytic/report/timeline behavior and mission policies because proprietary game references are not distributed.

Source integration of this research harness does not qualify a gameplay release. Continue the remaining experiment protocol before vessel takeover or claims of gameplay improvement.
