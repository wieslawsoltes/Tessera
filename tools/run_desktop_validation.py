"""Exercise the production desktop backend in isolated fixture and real-PTY processes.

Unlike Avalonia.Headless, this program loads Cocoa/Win32/X11 native services.
On macOS it requires NativeMenu.IsNativeMenuExported, so headless success cannot
stand in for the exporter regression. On Linux launch this runner under Xvfb.
"""
from __future__ import annotations
import argparse
import json
import os
from pathlib import Path
import sys

from run_native_validation import execute


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--project', type=Path, default=Path('tests/Tessera.Desktop.Smoke'))
    parser.add_argument('--evidence', type=Path, default=Path('artifacts/desktop-backend'))
    parser.add_argument('--mode', choices=('fixture', 'live', 'both'), default='both')
    parser.add_argument('--property', action='append', default=[])
    parser.add_argument('--expect-menu-mismatch', action='store_true',
                        help='macOS-only negative control against the explicitly pinned broken baseline')
    args = parser.parse_args()
    if args.expect_menu_mismatch and (sys.platform != 'darwin' or args.mode != 'fixture'):
        parser.error('The negative control requires macOS and --mode fixture.')
    args.evidence.mkdir(parents=True, exist_ok=True)
    properties = ['-p:' + value for value in args.property]
    build_code = execute(['dotnet', 'build', str(args.project), '-c', 'Release', *properties],
                         args.evidence / 'build.log', 300)
    problems: list[str] = []
    outcomes: list[dict] = []
    if build_code != 0:
        problems.append('Native-backend harness did not build.')
    else:
        modes = ('fixture', 'live') if args.mode == 'both' else (args.mode,)
        for mode in modes:
            report = (args.evidence / (mode + '.json')).resolve()
            report.unlink(missing_ok=True)
            command = ['dotnet', 'run', '--project', str(args.project), '-c', 'Release', *properties,
                       '--no-build', '--no-restore', '--', '--report', str(report)]
            if mode == 'live':
                command.append('--real-pty')
            code = execute(command, args.evidence / (mode + '.log'), 120)
            try:
                data = json.loads(report.read_text(encoding='utf-8'))
                if not isinstance(data, dict):
                    raise ValueError('Native report must be an object.')
                if args.expect_menu_mismatch:
                    passed = (code != 0 and data.get('passed') is False and data.get('headless') is False
                              and 'The menu being updated does not match.' in (data.get('error') or ''))
                else:
                    checks = data.get('checks')
                    passed = (code == 0 and data.get('passed') is True and data.get('headless') is False
                              and bool(data.get('nativeHandle')) and isinstance(checks, list)
                              and len(checks) >= 50 and data.get('error') is None
                              and (sys.platform != 'darwin' or data.get('nativeMenuExported') is True))
                outcomes.append({'mode': mode, 'exitCode': code, 'passed': passed, 'report': report.name})
                if not passed:
                    problems.append(f'{mode}: incomplete native-backend evidence; inspect {report.name}.')
            except (OSError, UnicodeError, json.JSONDecodeError, ValueError, TypeError) as error:
                problems.append(f'{mode}: {error}')
    manifest = {'schemaVersion': 1, 'passed': not problems, 'sourceCommit': os.environ.get('GITHUB_SHA'),
                'nativeBackend': True, 'expectedBaselineFailure': args.expect_menu_mismatch,
                'physicalHardwareAudited': False, 'buildExitCode': build_code, 'scenarios': outcomes, 'problems': problems}
    (args.evidence / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(manifest, indent=2))
    return 0 if manifest['passed'] else 1


if __name__ == '__main__':
    sys.exit(main())
