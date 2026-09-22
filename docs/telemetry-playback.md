# Scrub recorded mission observations

Generate a standalone page from an existing mission's `mission.csv`:

```sh
dotnet run --project tools/KspContinuum.Tools -c Release -- telemetry-player artifacts/mission/mission.csv \
  --title 'CSP-0002 A006 · Minmus landing' \
  --output artifacts/playback.html
```

The output must be a new file in an existing directory. Open it in a browser or serve the containing directory. It embeds its observations, styles, script and source SHA-256; no server API or game connection is needed. Input and existing output files remain unchanged.

New [chronicle reports](chronicle.md) also include this player and link it from the mission report. Rebuilding the connected space-program site adds playback to those attempt pages, with navigation back to the report and program. Older reports remain readable; regenerate a new report from its original evidence to add playback without modifying the old rendering.

The slider and previous/next controls select exact observations. The phase selector jumps to each recorded phase boundary. Play advances using elapsed recorded wall time at 1×, 10× or 100× and holds the last available observation. Universal time remains a separately displayed recorded value: stock warp can advance it much faster. Hidden pages pause playback. Restarting playback at the end returns to the first observation.

The altitude chart connects recorded samples only as a visual guide. Missing altitudes and body changes break its line. The displayed metrics use rounded formatting while the embedded dataset retains the parsed numeric values. Missing readings show `Unavailable`. This is telemetry playback, not video, a reconstructed vessel, deterministic resimulation, or a live-control handoff. Original screenshots and game saves remain separate artifacts.

The reader accepts the existing mission telemetry schema and bounds input to 16 MiB and 100,000 rows. It rejects reversed clocks, nonfinite values, invalid throttle/part/stage values, and selected numeric magnitudes over 1e15, which is this viewer's supported range. Stage -1 is accepted as a stage sentinel. Text is escaped before embedding and inserted with textContent during playback; no source paths are embedded. The page can contain recorded vessel/controller descriptions, so choose the source deliberately before sharing it.

Portable tests exercise the actual generator, malformed clocks/values, safe embedding and output preservation. Browser verification uses real mission data and covers phase jumps, keyboard seeking, single-step controls and play/pause. Neither class of check establishes physics replay.

The altitude chart offers linear and zero-safe logarithmic scales. The latter uses `sign(h) × log(1 + |h| / 1 m)`, preserving zero and negative altitudes. Grid labels and observation readouts remain in meters; changing scale does not alter the selected observation or playback clock.
