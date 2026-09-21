#!/usr/bin/env python3
"""Package only project-owned outputs; never install into a game copy."""
import argparse
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--mission', action='store_true', help='Include the optional MechJeb 2.15.3 mission addon')
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
plugin = root / 'src/KspContinuum.Plugin/bin/Release/net472/KspContinuum.dll'
if not plugin.is_file():
    raise SystemExit('Build the Release plugin first.')
mission = root / 'src/KspContinuum.Mission/bin/Release/net48/KspContinuum.Mission.dll'
if args.mission and not mission.is_file():
    raise SystemExit('Build the Release mission addon first.')
output = root / ('artifacts/ksp-continuum-0.1.0-mission.zip' if args.mission else 'artifacts/ksp-continuum-0.1.0-experiment.zip')
output.parent.mkdir(exist_ok=True)
with ZipFile(output, 'w', ZIP_DEFLATED) as archive:
    archive.write(plugin, 'GameData/KspContinuum/Plugins/KspContinuum.dll')
    if args.mission:
        archive.write(mission, 'GameData/KspContinuum/Plugins/KspContinuum.Mission.dll')
    archive.write(root / 'README.md', 'GameData/KspContinuum/README.md')
    for name in ('experiment.md', 'replacement.md', 'validation.md', 'compatibility.md', 'input-timeline.md', 'integration-map.md', 'minmus-mission.md',
                 'space-program.md', 'naming.md', 'wiki-templates.md', 'chronicle.md',
                 'simulation-worker.md', 'profiling.md', 'input-comparison.md', 'worker-benchmark.md', 'structural-benchmark.md',
                 'orbital-fixture.md', 'layout-benchmark.md', 'telemetry-playback.md',
                 'shadow-worker.md', 'field-gravity.md', 'field-trajectory.md', 'interaction-regimes.md', 'force-observation.md', 'qualification-report.md'):
        archive.write(root / 'docs' / name, 'GameData/KspContinuum/docs/' + name)
    archive.write(root / 'examples/neutral-inputs.csv', 'GameData/KspContinuum/examples/neutral-inputs.csv')
print(output.name)
