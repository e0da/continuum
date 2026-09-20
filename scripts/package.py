#!/usr/bin/env python3
"""Package only project-owned outputs; never install into a game copy."""
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile

root = Path(__file__).resolve().parents[1]
plugin = root / 'src/KspRigid.Plugin/bin/Release/net472/KspRigid.dll'
if not plugin.is_file():
    raise SystemExit('Build the Release plugin first.')
output = root / 'artifacts/ksp-rigid-0.1.0-experiment.zip'
output.parent.mkdir(exist_ok=True)
with ZipFile(output, 'w', ZIP_DEFLATED) as archive:
    archive.write(plugin, 'GameData/KspRigid/Plugins/KspRigid.dll')
    archive.write(root / 'README.md', 'GameData/KspRigid/README.md')
    for name in ('experiment.md', 'replacement.md', 'validation.md'):
        archive.write(root / 'docs' / name, 'GameData/KspRigid/docs/' + name)
print(output.name)
