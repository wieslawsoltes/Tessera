#!/usr/bin/env python3
"""Package a dotnet publish directory without discarding Unix executable modes."""
from __future__ import annotations
import argparse
import hashlib
import json
import os
from pathlib import Path
import plistlib
import shutil
import subprocess
import tarfile
import zipfile


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument('--rid', required=True, choices=['linux-x64', 'win-x64', 'osx-arm64'])
    parser.add_argument('--input', type=Path, default=Path('artifacts/app'))
    parser.add_argument('--output', type=Path, default=Path('artifacts/packages'))
    parser.add_argument('--native-sign', action='store_true', help='Require owner certificates, signature verification and macOS notarization; never silently fall back.')
    args = parser.parse_args()
    executable = 'Tessera.exe' if args.rid.startswith('win-') else 'Tessera'
    if not (args.input / executable).is_file():
        raise SystemExit(f'Missing published app host: {args.input / executable}')
    args.output.mkdir(parents=True, exist_ok=True)
    sha = subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip()
    stage = args.output / 'stage'
    if stage.exists():
        shutil.rmtree(stage)
    package_name = f'Tessera-0.2.0-preview.1-{args.rid}'
    if args.rid.startswith('osx-'):
        root = stage / 'Tessera.app'
        payload = root / 'Contents' / 'MacOS'
        payload.parent.mkdir(parents=True, exist_ok=True)
        (root / 'Contents' / 'Resources').mkdir(parents=True, exist_ok=True)
        with (root / 'Contents' / 'Info.plist').open('wb') as stream:
            plistlib.dump({'CFBundleName': 'Tessera', 'CFBundleDisplayName': 'Tessera',
                'CFBundleIdentifier': 'io.github.wieslawsoltes.tessera', 'CFBundleExecutable': 'Tessera',
                'CFBundlePackageType': 'APPL', 'CFBundleShortVersionString': '0.2.0',
                'CFBundleVersion': '1', 'NSHighResolutionCapable': True,
                'LSMinimumSystemVersion': '13.0', 'NSPrincipalClass': 'NSApplication'}, stream)
    else:
        root = stage / package_name
        payload = root
    shutil.copytree(args.input, payload, dirs_exist_ok=True)
    if not args.rid.startswith('win-'):
        (payload / executable).chmod(0o755)
    metadata = {'product': 'Tessera', 'version': '0.2.0-preview.1', 'commit': sha,
                'runtimeIdentifier': args.rid, 'selfContained': True, 'signed': args.native_sign,
                'notarized': args.native_sign and args.rid.startswith('osx-'), 'trimmed': False}
    (payload / 'build-info.json').write_text(json.dumps(metadata, indent=2) + '\n', encoding='utf-8')
    for name in ['README.md', 'THIRD-PARTY-NOTICES.md']:
        if Path(name).is_file():
            shutil.copy2(name, payload / name)
    if args.native_sign:
        if args.rid.startswith('win-'):
            subprocess.run(['pwsh', '-NoProfile', '-File', 'eng/sign-windows.ps1', '-Directory', str(payload)], check=True)
        elif args.rid.startswith('osx-'):
            subprocess.run(['bash', 'eng/sign-macos.sh', str(root)], check=True)
        else:
            raise SystemExit('Use the signed provenance bundle for Linux; native-sign is Windows/macOS only.')
    if args.native_sign and args.rid.startswith('osx-'):
        archive = args.output / (package_name + '.zip')
        subprocess.run(['ditto', '-c', '-k', '--keepParent', str(root), str(archive)], check=True)
    elif args.rid.startswith('win-'):
        archive = args.output / (package_name + '.zip')
        with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as z:
            for file in sorted(root.rglob('*')):
                if file.is_file(): z.write(file, file.relative_to(stage))
    else:
        archive = args.output / (package_name + '.tar.gz')
        with tarfile.open(archive, 'w:gz', compresslevel=6) as tar:
            tar.add(root, arcname=root.name)
    digest = hashlib.file_digest(archive.open('rb'), 'sha256').hexdigest()
    archive.with_suffix(archive.suffix + '.sha256').write_text(f'{digest}  {archive.name}\n', encoding='utf-8')
    shutil.rmtree(stage)
    print(json.dumps({'archive': str(archive), 'sha256': digest, 'commit': sha}, indent=2))

if __name__ == '__main__':
    main()
