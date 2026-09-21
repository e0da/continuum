# Validation receipt

## Input timeline and mission trials

The combined analytic/report/timeline suite passes 123 portable assertions; optional mission policies pass 62 assertions. These do not establish whole-game determinism. The following mission attempts are retained, including failures before launch:

| Attempt | Mode | Observed result |
| --- | --- | --- |
| 1 | Headless | Preflight abort before sandbox creation: the version check confused MechJeb assembly version 2.15.0.0 with package/file version 2.15.3.0. The paired version check was corrected. |
| 2 | Rendered, 1280×720 | Stock Kerbal X reached the launchpad. Strict replay refused an existing control callback before launch. Installed MechJeb adds its core to command pods through ModuleManager; the mission had assumed it would attach that owner later. Failure telemetry, a unique sandbox save and a confirmed screenshot were preserved. |
| 3 | Rendered, 1280×720 | Intact, settled Minmus touchdown passed the declared acceptance check after launch, Kerbin orbit, transfer, correction, capture and descent. Neutral replay explicitly not run. All 77 exported input segments parsed and round-tripped. This is recording/schema and mission execution evidence, not deterministic playback or world-state reproduction. |

Attempt 2's screenshot was visually inspected and shows the craft and Continuum panel behind KSP's first-run announcement. The user dismissed that announcement; it was not the recorded replay-guard failure. Neutral replay completion remains unqualified.

Attempt 3 uses package `0.1.0-inputs.8821BD7CC20D`. Its installed DLLs matched the built bytes before launch: plugin SHA-256 `e2f31b983de38f0d3220b6ac0b35d8abad51d38ad1b50dd8b35a7be623ff2b9a`, mission SHA-256 `8643466ed8ec6d89eea3f91ccd6400578e10b3d80aab4ada90f7438914a381b7`. A subsequently reviewed ownership-cleanup correction passes portable tests and compilation but is not the binary running this attempt.

### Attempt 3 outcome and limitations

At UT 269974.00335770514 the mission reported Minmus LANDED, zero throttle, all 17 lander parts surviving and surface speed 0.00687 m/s after qualifying observations spanning 30 simulated seconds. Elapsed runner time was 1,572 seconds (about 26 minutes), much of it a normal-rate descent. The mission produced 1,559 telemetry rows, 77 input segments (15,599,319 bytes), five checkpoint saves and five confirmed milestone/launchpad PNGs. Every completed input segment passed the portable parser and serialization round-trip check. The final save exists and the landed PNG was visually inspected. The rendered game remained open after releasing mission controls.

The observer reported repeated flipping during slow powered descent and little apparent fuel use. Sampled throttle was often 0.05 and surface speed fluctuated at low values. The dark final image shows a tipped lander. Decoding the saved vessel rotation in its inspected body-relative frame places the rocket axis 96.4 degrees from outward radial, confirming it is approximately sideways; this is not a terrain-normal-relative angle. Current telemetry does not contain attitude, angular velocity or thrust direction, and current acceptance does not require an upright pose or quantify rotational settling. Record this as an intact, settled touchdown under the stated checks, not a clean upright landing. Late recorded pitch/yaw commands reach both full-deflection limits and reverse several times. Installed MechJeb settings enable a 0.05 minimum throttle; its low-speed attitude target can also change. Those are diagnostic candidates, not causal proof. The final save retains about 60.6% of both liquid fuel and oxidizer, so fuel starvation is not supported. The cause of the flipping is unresolved; add orientation, target and angular-rate observations before attributing it to an autopilot, controls, or physics defect.

Two screenshots requested in the same frame at Minmus capture did not both materialize: descent-start was confirmed and minmus-orbit was explicitly marked unconfirmed. Checkpoint saves for both milestones exist. There is no video or audio recording and no reconstructed replay of the descent.

## Minmus Survey 1

CSP-0002-A001 ran visibly at 1920×1080 using package `0.1.0-survey.6F27F6FA94B4`. All 15 installed package files matched their archive bytes before launch. The stock Kerbal X design remained unchanged. The sampled footprint had 529 zero-height observations and maximum triangle slope 0.00225°. Frozen sunlight prediction and native arrival geometry agreed within 7.55e-12 degrees. These checks cover the stated sampling/model boundaries, not arbitrary collider clearance or terrain shadows.

The survey **failed** its upright-landing contract. After the 180-second settling timeout, the craft was LANDED on Minmus with all 17 descent-stage parts preserved, 71.006 m from target, Sun elevation 44.572°, surface speed 0.00148 m/s and throttle zero. Its terrain-relative tilt was 94.984°; none of 1,711 settling observations met the 10° limit. All six milestone PNGs were confirmed at 1920×1080, and the final image shows the sideways craft in sunlight.

The targeted landing phase took about 467 wall seconds for 2,605 simulated seconds, including warp-aware deorbit/coast stages. Pathfinder's untargeted landing took about 1,080 wall seconds. Target geometry and trajectory also changed, so this is an observed pacing difference, not a controlled performance ratio. A001's final descent still lasted 296 seconds; 93.5% of its 2,823 observations recorded throttle at 0.05 while the craft repeatedly rotated.

Pinned MechJeb source shows that a final-descent request for zero throttle can be raised to the enabled 5% minimum. The capture checkpoint's approximate mass implies hover thrust below that floor. This motivates the separate `--continuum-survey-disable-throttle-floor` trial; it does not establish the cause of tipping. A001's original `min_throttle_percent` column contains normalized fractions, despite its name. Later telemetry uses `min_throttle_fraction`; the original evidence remains unchanged.

The raw receipts, saves, telemetry and screenshots remain local. The generated chronicle identifies native failure separately from editorial interpretation and retains hashes of its sources. Neither the report nor input recording establishes deterministic replay.

### A002: upright touchdown with the throttle floor disabled

CSP-0002-A002 used package `0.1.0-survey.0A24FE1F52A4`; all 15 installed files matched the archive, and the stock craft hash was unchanged. The explicit experiment disabled only MechJeb's minimum-throttle floor during owned landing control. Every Landing and Settling observation recorded the floor disabled; the terminal Done observation confirmed restoration to its previous enabled value after cleanup.

The lander touched down upright in daylight with all 17 parts intact. At the terminal receipt, terrain-relative tilt was 0.868°, Sun elevation 43.757°, surface speed 0.000176 m/s and throttle zero. The final PNG visually confirms the upright craft. Nevertheless the survey **failed**: target distance was 116.167 m, and none of 1,711 settling observations met the unchanged 100 m limit. The 180-second settling timeout was preserved. The vessel center was inside the sampled square, approximately 4.76 m from its east edge; this does not establish coverage of every leg contact.

FinalDescent took about 38 seconds versus A001's 296 seconds, with no recorded terrain-relative tilt above 90° versus 768 such observations in A001. The observer's brief rise before final descent also appears in the 1 Hz altitude trace: about 0.116 m over 16 seconds during horizontal-velocity correction, followed by decreasing sampled altitude to touchdown. This trial supports the throttle floor as a contributor to the behavior for this craft. The two fresh flights have different trajectories and entry states; repeatability and precise causal attribution remain open.

Both survey attempts have six confirmed 1920×1080 screenshots. Their connected local website links attempts, missions, vehicle, site and experiment records, including a comparison chart. Its 14 pages and 245 local links/media references were checked after the A002 rebuild; browser navigation and actual image loading were verified. The maintained catalog and derived site can be rebuilt without modifying the original reports. The Python suite has 30 passing tests.

Subsequent analysis locates A002's distance miss during final descent. The first recorded FinalDescent sample was 15.912 m from target at approximately 202 m above sea level. Latitude/longitude differences estimate about 4.1 m/s of lateral motion relative to Minmus's rotating surface. During the following 26.18 seconds before appreciable braking resumed, the ground track moved approximately 107.32 m east and 34.64 m south. It crossed the 100 m radius at approximately 55 m altitude, still at zero recorded throttle. These are sampled ground-track estimates with interpolated altitude, not direct center-of-mass velocity or proof of the cause of the controller handoff. The trace does not support attributing the observed displacement simply to viewing the moon in an inertial frame.

### A003: first checkpoint reconstruction

A003 started from A002's saved Minmus orbit, using package `0.1.0-checkpoint.0DA8AE4348FB`. All 15 installed files and both pinned external MechJeb configuration hashes were verified before launch. At the fixed acquisition epoch, the reconstructed orbit differed by 0.053742 m in position and 8.843e-6 m/s in velocity; topology and resource checks passed. The parent checkpoint's SHA-256 remained unchanged after completion.

The flight landed upright with 17 parts, terrain-relative tilt 0.995°, Sun elevation 43.774° and target distance 119.512 m. Final descent took 38.20 simulation seconds. Native horizontal speed was 4.199 m/s at entry, when the ground track was only 14.034 m from target. Thrust mode remained OFF until the first braking-mode observation at 26.12 seconds; positive throttle first appears at 26.24 seconds, approximately 111.0 m from target at 34.8 m altitude. This directly supports the observed late-drift explanation. It does not establish a universal landing-controller defect or an upstream promise of 100 m accuracy.

A003 exposed two loader integration defects. Entering flight before the delayed Main Menu GUI-ready event finished initialized the maneuver panel twice; the preserved log contains 62,848 exceptions with `AppUIInputPanel.RefreshUI` as their first stack frame, including post-terminal time. The initial empty-vessel telemetry row also used UT 0 before the saved clock loaded, falsely suggesting a large simulated interval if naively summed. Original evidence is preserved. The corrected runner waits for the native readiness event and a later frame, and begins telemetry only once the native saved clock is ready. A003 is diagnostic qualification evidence, not a clean repeatability trial.

### A004: startup corrections qualified; telemetry aborted descent

A004 used package `0.1.0-checkpoint.E57B6B144BDA` from the same A002 orbit checkpoint and pinned MechJeb settings. At the fixed acquisition epoch, topology and resource checks passed; orbital residuals were 0.053784 m and 9.861e-6 m/s. The source checkpoint remained unchanged after cleanup.

The preserved log contains one maneuver-tool initialization and no `AppUIInputPanel.RefreshUI` exceptions. The first telemetry row contains the restored Minmus vessel at UT 268881.45886477333, and the loading event uses the source epoch. This run verifies the narrow startup-order and telemetry-clock corrections. It does not establish an exception-free game session: transient stock MessageSystem and AlarmClock initialization exceptions, and a MechJeb shutdown exception, remain in the log.

The attempt **failed** at 122.084 wall seconds, immediately after the landing controller changed from DeorbitBurn to CourseCorrection. The new MechJeb step's status text was still null; the runner's CSV escaping called `Replace` on it and aborted the mission. Installed donor code confirms that a newly constructed step need not have status text before its first control update. Cleanup released the owned controller, set throttle to zero and restored the minimum-throttle setting. The vessel remained suborbital at approximately 27.1 km altitude with all 17 parts; the trial did not reach touchdown and does not qualify repeatability.

Three 1920×1080 screenshots, the failed-state checkpoint, input recording and telemetry were preserved. The corrected runner records `(status unavailable)` for a null observational status, without catching unrelated exceptions or changing control guards. A subsequent native run must cross this transition and complete the landing before that correction or repeatability can be qualified.

### A005: first completed corrected trial

A005 installed package `0.1.0-checkpoint.B627559E9FE0`, with all 15 owned files compared to archive bytes and the external MechJeb settings restored from the pinned baseline. Acquisition occurred at the same source-relative epoch; position residual was 0.053737 m and velocity residual 9.161e-6 m/s. The run traversed DeorbitBurn, CourseCorrection and the remaining landing stages without the telemetry abort. No null-status fallback was sampled, so this proves completion through the transition, not direct execution of the fallback branch. The recurring maneuver-panel exception loop was absent; transient stock UI and MechJeb shutdown exceptions remain.

The vehicle landed upright with 17 parts, target distance 120.049 m, tilt 0.995° and Sun elevation 43.775°. Final descent lasted 38.22 simulation seconds; entry horizontal speed was 4.205 m/s at 13.936 m from target. No settling sample met the unchanged 100 m distance criterion, so the receipt remains failed after the 180-second settling timeout. The throttle-floor setting was restored and the parent source hash remained unchanged. Closed-instance preservation copied and verified 76 evidence/save/configuration files. A006 uses the identical installed package and the same restored settings for the paired comparison.

### A005/A006 paired outcome

A006 used the same installed package, source checkpoint, acquisition epoch and pinned settings as A005. Both acquisition resource and settings CSV files are byte-identical. A006 acquisition residuals were 0.053756 m and 8.859e-6 m/s. Both attempts preserved the parent checkpoint hash and restored the throttle-floor setting after control release.

| Measurement | A005 | A006 |
| --- | ---: | ---: |
| Terminal target distance | 120.049 m | 119.736 m |
| Terrain-relative tilt | 0.995° | 0.994° |
| Surviving parts | 17 | 17 |
| Final-descent duration | 38.22 s | 38.22 s |
| Horizontal speed at final-descent entry | 4.205 m/s | 4.200 m/s |
| Target distance at final-descent entry | 13.936 m | 13.965 m |
| Sun elevation at terminal observation | 43.775° | 43.774° |

Both attempts completed upright daylight touchdowns and failed the unchanged 100 m target-distance criterion. Neither had a qualifying settling sample inside that radius. The target-distance difference is 0.312 m; this is a two-run observed difference, not a statistical accuracy bound. Both first positive braking-throttle observations occurred 26.22 simulation seconds after final-descent entry, already more than 111 m from target. This supports the same late-drift behavior under the reconstructed-state protocol; it does not prove deterministic replay or validate another vehicle or checkpoint.

Each attempt has three confirmed 1920×1080 images and a linked chronicle. A006's terminal mission/input evidence and game-log snapshot were copied and hash-verified (64 files), while the landed game was left open. This is not a closed-instance snapshot of its live save directory. The recurring maneuver-panel exception loop is absent in both corrected trials; the logs still contain unrelated startup exceptions.

The final automated suite passes 123 core assertions, 85 portable mission assertions and 44 Python tests. Native Release compilation has zero warnings/errors. Independent review covered restoration boundaries, saved-clock normalization and actual paired-flight measurements. These checks qualify this checkpoint-start workload, not arbitrary native save restoration or replacement physics.

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
