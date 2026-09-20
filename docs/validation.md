# Validation receipt

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

C# tests do not exercise Unity. Synthetic bodies do not establish KSP vessel integration. Optional timing markers cannot fully attribute frame time and may be unavailable in release players. CI checks only portable analytic/report behavior because proprietary game references are not distributed.

Source integration of this research harness does not qualify a gameplay release. Continue the remaining experiment protocol before vessel takeover or claims of gameplay improvement.
