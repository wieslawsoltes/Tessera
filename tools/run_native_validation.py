"""Build/discover, execute, and account for every native acceptance case."""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import xml.etree.ElementTree as ET

from verify_native_results import EXTERNAL, REQUIRED, SFTP, VAULT, discovery_names, verify


def execute(command: list[str], log: Path, timeout: int) -> int:
    print('Running: ' + subprocess.list2cmdline(command), flush=True)
    environment = dict(os.environ, DOTNET_CLI_UI_LANGUAGE='en-US', NO_COLOR='1')
    try:
        result = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                encoding='utf-8', errors='replace', env=environment,
                                timeout=timeout, check=False)
        output, code = result.stdout, result.returncode
    except subprocess.TimeoutExpired as error:
        output = error.stdout or b''
        if isinstance(output, bytes):
            output = output.decode('utf-8', errors='replace')
        output += '\nAcceptance runner exceeded its wall-clock timeout.\n'
        code = 1
    except OSError as error:
        output, code = str(error), 1
    log.write_text(output, encoding='utf-8')
    print(output, flush=True)
    return code


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--mode', choices=('native', 'integration'), default='native')
    parser.add_argument('--property', action='append', default=[], help='Additional MSBuild property, e.g. Platform=Any CPU')
    args = parser.parse_args()
    root = Path('artifacts/test-evidence') / args.mode
    root.mkdir(parents=True, exist_ok=True)
    trx = root / 'results.trx'
    trx.unlink(missing_ok=True)
    report = root / 'completeness.json'
    report.unlink(missing_ok=True)
    base = ['dotnet', 'test', 'tests/Tessera.App.Tests', '-c', 'Release']
    base += ['-p:' + item for item in args.property]
    discovery = root / 'discovery.txt'
    discovery_code = execute(base + ['--list-tests'], discovery, 240)
    run_code = 1
    if discovery_code == 0:
        command = base + ['--no-build', '--no-restore', '--logger', 'trx;LogFileName=results.trx',
                          '--results-directory', str(root.resolve()), '--blame-hang-timeout', '2m']
        if args.mode == 'integration':
            command += ['--filter', 'FullyQualifiedName~ExternalAcceptanceTests']
        run_code = execute(command, root / 'execution.txt', 300)
    try:
        names = discovery_names(discovery.read_text(encoding='utf-8'))
        if args.mode == 'integration':
            names = [name for name in names if name.startswith(EXTERNAL)]
            required, allowed = SFTP | {VAULT}, set()
        else:
            required = REQUIRED
            allowed = SFTP | ({VAULT} if os.environ.get('TESSERA_VAULT_TESTS') != '1' else set())
        evidence = verify(names, trx.read_bytes(), allowed, required)
    except (OSError, UnicodeError, ET.ParseError, ValueError) as error:
        evidence = {'schemaVersion': 1, 'passed': False, 'problems': [str(error)]}
    evidence.update(mode=args.mode, sourceCommit=os.environ.get('GITHUB_SHA'),
                    discoveryExitCode=discovery_code, executionExitCode=run_code)
    if discovery_code != 0 or run_code != 0:
        evidence['passed'] = False
        evidence['problems'].append('Discovery or execution returned a non-zero exit code.')
    report.write_text(json.dumps(evidence, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
    print(json.dumps(evidence, indent=2, ensure_ascii=True), flush=True)
    return 0 if evidence['passed'] else 1


if __name__ == '__main__':
    sys.exit(main())
