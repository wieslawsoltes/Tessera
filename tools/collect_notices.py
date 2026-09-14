#!/usr/bin/env python3
"""Copy upstream notices supplied by restored packages and record their metadata."""
from __future__ import annotations
import json
from pathlib import Path
import shutil
import xml.etree.ElementTree as ET

assets = Path('src/Tessera.App/obj/project.assets.json')
if not assets.exists():
    raise SystemExit('Restore/publish the native application before collecting notices.')
data = json.loads(assets.read_text(encoding='utf-8'))
folders = [Path(p) for p in data.get('packageFolders', {})]
output = Path('artifacts/app/ThirdPartyNotices')
output.mkdir(parents=True, exist_ok=True)
inventory = []
for key, library in sorted(data['libraries'].items()):
    if library.get('type') != 'package':
        continue
    relative = library.get('path', key.lower())
    package = next((base / relative for base in folders if (base / relative).is_dir()), None)
    if package is None:
        raise SystemExit(f'Restored package directory missing: {key}')
    license_value = None
    license_type = None
    project = None
    candidates = set()
    for file in package.iterdir():
        if file.is_file() and any(word in file.name.lower() for word in ['license', 'licence', 'notice', 'copyright']):
            candidates.add(file)
    for spec in package.glob('*.nuspec'):
        root = ET.parse(spec).getroot()
        for element in root.iter():
            name = element.tag.rsplit('}', 1)[-1]
            if name == 'license':
                license_value = element.text
                license_type = element.attrib.get('type')
                if license_type == 'file' and license_value:
                    file = (package / license_value).resolve()
                    if file.is_relative_to(package.resolve()) and file.is_file(): candidates.add(file)
            elif name == 'projectUrl':
                project = element.text
    copied = []
    target = output / key.replace('/', '-')
    for file in sorted(candidates):
        target.mkdir(parents=True, exist_ok=True)
        destination = target / file.name
        shutil.copy2(file, destination)
        copied.append(str(destination.relative_to(output)))
    inventory.append({'package': key, 'licenseType': license_type, 'license': license_value,
                      'projectUrl': project, 'bundledNotices': copied})
(output / 'inventory.json').write_text(json.dumps(inventory, indent=2) + '\n', encoding='utf-8')
print(f'Collected notice metadata for {len(inventory)} packages.')
