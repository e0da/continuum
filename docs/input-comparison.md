# Comparing recorded inputs

`scripts/compare_inputs.py` compares two Continuum input timeline segment files channel by channel. It is intended for inspecting recorded control differences between trials. It does not compare vessel state, physics results, diagnostic events, or replay determinism.

Run it with an explicit aligned interval, sample count, and absolute tolerance:

```sh
python3 scripts/compare_inputs.py \
  artifacts/run-a/segment-00005.csv \
  artifacts/run-b/segment-00005.csv \
  --start 0 \
  --end 19.98 \
  --samples 1000 \
  --tolerance 0.001 \
  --output artifacts/input-comparison.json
```

The output records each source file's basename and SHA-256 digest. For every channel it reports the largest sampled absolute deviation, sampled RMS deviation, first sample whose deviation is greater than the tolerance, and the number of divergent samples. The overall result summarizes the same sampled values across all channels. `eventsCompared` is always `false`; event rows are parsed and validated as part of the input format but are outside this comparison.

The two segments must have exactly the same declared duration, channel names, and channel ranges. The requested interval must have positive width and fit within that duration. A duration mismatch fails instead of truncating either recording. Compare separate segment pairs explicitly; the tool does not chain segments or align their external start times.

Evaluation follows the core timeline value rules: a key's mode controls its outgoing segment, the final key is held through the duration, linear interpolation uses normalized time, and cubic Bezier values use stable de Casteljau interpolation. The parser requires the versioned headers and section order used by `TimelineCsv`, invariant-culture finite numbers, safe identifiers, bounded counts, and bounded UTF-8 input.

The result is sampled, not continuous. The declared grid includes both interval endpoints. The tool also evaluates every key time from both recordings inside the interval so a narrow step or key discontinuity is not skipped merely because it falls between regular grid points. Curved segments can still reach an interior deviation between samples, and the RMS is an unweighted mean over the evaluated sample set rather than a time integral. Increase `--samples` when the required time resolution is finer. The JSON records both declared and evaluated sample counts and sets `sampledNotContinuous` to `true`.

The tool refuses missing channels, changed ranges, malformed timelines, non-finite deviations, excessive input or sample counts, and an existing output path. It creates a new result with exclusive file creation. A matching comparison only establishes agreement of sampled input-channel values within the chosen tolerance; it does not establish equivalent game state, trajectory, or outcome.
