# Validation receipt

The initial prototype is compiled and analytically checked. **Live validation is HOLD.** The existing KSP and CKAN session is reserved for independent mod-pack verification; this work did not launch, close, clone, install into, or modify any game instance.

## Completed locally

- The analytic runner passed 29 assertions covering unequal masses, center of mass, box inertia, the parallel-axis contribution, translation invariance, split linear/angular momentum, and invalid inputs.
- The addon compiled in Release against owned KSP 1.12.5 Mac reference assemblies, targeting .NET Framework 4.7.2, with zero warnings and zero errors.
- Build output contains only `KspRigid.dll`; no game or Unity assemblies are copied.
- The packaging script creates a local ZIP containing the project DLL and documentation only. It does not install or publish a release.

The analytic runner first failed because the model was absent, then passed after implementation. A missing Unity JSON module reference was found by the real-reference build and corrected.

## Not demonstrated

The addon has not been loaded by KSP. No UI, isolated-scene execution/cleanup, profiler marker availability, collision result, in-engine separation result, timing improvement, stock-vessel behavior, or mod compatibility has been verified. The checked-in engine experiment is a runnable hypothesis, not measured evidence.

The C# model tests do not exercise Unity. The synthetic Unity benchmark does not exercise KSP vessel integration. The optional profiler markers do not provide a complete attribution of frame time and may be unavailable in the release player. CI runs only the analytic model because proprietary game references are not distributed.

Follow the experiment protocol once an independent test instance is available. Keep the implementation in a draft PR until that evidence exists.
