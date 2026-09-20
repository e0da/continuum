#!/usr/bin/env python3
"""Package only project-owned outputs; never install into a game copy."""
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile

root = Path(__file__).resolve().parents[1]
plugin = root / 'src/KspContinuum.Plugin/bin/Release/net472/KspContinuum.dll'
if not plugin.is_file():
    raise SystemExit('Build the Release plugin first.')
output = root / 'artifacts/ksp-continuum-0.1.0-experiment.zip'
output.parent.mkdir(exist_ok=True)
with ZipFile(output, 'w', ZIP_DEFLATED) as archive:
    archive.write(plugin, 'GameData/KspContinuum/Plugins/KspContinuum.dll')
    archive.write(root / 'README.md', 'GameData/KspContinuum/README.md')
    for name in ('experiment.md', 'replacement.md', 'validation.md'):
        archive.write(root / 'docs' / name, 'GameData/KspContinuum/docs/' + name)
print(output.name)
