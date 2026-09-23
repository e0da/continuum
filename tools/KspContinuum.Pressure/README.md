# Simulation pressure from an installed profile

Run `dotnet run --project tools/KspContinuum.Pressure -c Release -- --target-step-seconds 0.02 PATH/markers.json`.
The tool reads existing `--continuum-scale-profile --continuum-playerloop` receipts; it does not launch or change KSP.
Pass several receipts to compare craft and regimes. It rejects pressure estimates for changing contexts, packed vessels,
pauses, or mismatched fixed-child sample counts while retaining the measured clock and factors.

`achievedWarp` is the change in KSP universal time divided by capture wall time. `warpFulfillment` compares that to the
requested factor. `simulatedTimeDebtSecondsPerWallSecond` is the shortfall, clamped at zero. Short captures can report
slightly more than requested warp because their start and end boundaries do not align with fixed ticks. A longer run
is needed to identify sustained debt; this is not KSP's UI clock-color signal.

For a loaded, unpacked, stable physics context, the pressure estimate sums the *mean* script and native physics child
scope durations per fixed step and multiplies by requested simulated seconds per wall second divided by the selected
simulated step size. This gives the fraction of wall time those observed scopes would occupy on their current
dependency path. It is a **partial lower bound**, not total frame CPU, available parallel speedup, a prediction of
solver stability, or a measured result at another step size. Rendering, input, Update, loading, unmeasured fixed work,
and scheduler overhead also compete for the deadline. Logical processor count is context, not a multiplier applied to
the current serial path. A later Continuum profile should report each island's total work and longest dependency path
separately to show where parallel scheduling helps.

The existing 196-part station sample has 110 native bodies, 144 joints and 328 colliders. Its 1x capture achieved
1.003x over 2.95 wall seconds; measured fixed children averaged 4.084 ms per step. At 20 ms simulated steps that is
about 20.4% of the wall-time budget at 1x, and a hypothetical 81.7% at 4x with the same measured per-step cost.
Those values are calculations from one headless observation, not a tested 4x result.
