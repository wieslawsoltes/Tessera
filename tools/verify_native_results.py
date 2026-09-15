"""Fail CI on missing, duplicated, failed, or unexpectedly skipped native tests.

VSTest's process exit code alone is insufficient: discovery and the TRX must
account for the same test cases. This verifier also pins critical PTY methods so
a discovery failure cannot silently remove them from both sides of the check.
"""
from __future__ import annotations

import argparse
from collections import Counter
import json
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
PREFIX = "Tessera.NativeTests."
EXTERNAL = PREFIX + "ExternalAcceptanceTests."
SFTP = {
    EXTERNAL + "RealSftpSymlinkOperationsDoNotDeleteOrOverwriteTheirTarget",
    EXTERNAL + "RealSftpUsesEncryptedKeyAndInteractiveChallengeThenTransfersAndDetectsConflicts",
}
VAULT = EXTERNAL + "OperatingSystemVaultRoundTripsReplacesAndDeletesUnicodeSecret"
REQUIRED = {
    PREFIX + "NativeUiTests.LocalTerminalExecutesAnActualPtyCommand",
    PREFIX + "AcceptanceUiTests.LivePtySurvivesTwoNativeWindowTransfersAndAsyncDisposal",
}


def discovery_names(text: str) -> list[str]:
    """Read VSTest's fully-qualified display names, retaining theory arguments."""
    text = re.sub(r"\x1b\[[0-?]*[ -/]*[@-~]", "", text)
    return [line.strip() for line in text.splitlines()
            if line.strip().startswith(PREFIX)]


def verify(discovered: list[str], xml: bytes, allowed_skips: set[str],
           required: set[str] | None = None) -> dict:
    problems: list[str] = []
    required = REQUIRED if required is None else required
    expected = Counter(discovered)
    if not expected:
        problems.append("Discovery returned no native test cases.")
    for name, count in expected.items():
        if count != 1:
            problems.append(f"Ambiguous discovery: {name} occurred {count} times.")
    for name in sorted(required - expected.keys()):
        problems.append(f"Required acceptance case was not discovered: {name}")
    root = ET.fromstring(xml)
    results = root.findall("./t:Results/t:UnitTestResult", NS)
    actual = Counter(item.get("testName", "") for item in results)
    for name in sorted(expected.keys() - actual.keys()):
        problems.append(f"Missing result: {name}")
    for name in sorted(actual.keys() - expected.keys()):
        problems.append(f"Undiscovered result: {name}")
    for name, count in actual.items():
        if count != 1:
            problems.append(f"Duplicate result: {name} occurred {count} times.")
    outcomes = Counter(item.get("outcome", "") for item in results)
    skipped: list[str] = []
    for item in results:
        name, outcome = item.get("testName", ""), item.get("outcome", "")
        if outcome == "NotExecuted" and name in allowed_skips and name not in required:
            skipped.append(name)
        elif outcome != "Passed":
            problems.append(f"Unacceptable outcome {outcome!r}: {name}")
    summary = root.find("./t:ResultSummary", NS)
    if summary is None or summary.get("outcome") != "Completed":
        problems.append("TRX run did not complete normally.")
    counters = root.find("./t:ResultSummary/t:Counters", NS)
    if counters is None:
        problems.append("TRX has no result counters.")
    else:
        # VSTest reports dynamic xUnit skips as NotExecuted but may leave its
        # notExecuted counter at zero. Validate the authoritative case records.
        for field, value in (("total", len(results)),
                             ("executed", len(results) - outcomes["NotExecuted"]),
                             ("passed", outcomes["Passed"]),
                             ("failed", outcomes["Failed"])):
            if counters.get(field) != str(value):
                problems.append(f"Counter {field}={counters.get(field)!r}; observed {value} records.")
    return {"schemaVersion": 1, "passed": not problems,
            "discovered": len(discovered), "reported": len(results),
            "passedCases": outcomes["Passed"], "skippedCases": sorted(skipped),
            "requiredPassed": sorted(name for name in required
                                      if any(r.get("testName") == name and r.get("outcome") == "Passed" for r in results)),
            "problems": problems}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--discovery", type=Path, required=True)
    parser.add_argument("--trx", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--mode", choices=("native", "integration"), default="native")
    parser.add_argument("--vault", choices=("enabled", "disabled"), default="enabled")
    args = parser.parse_args()
    try:
        names = discovery_names(args.discovery.read_text(encoding="utf-8-sig"))
        if args.mode == "integration":
            names = [name for name in names if name.startswith(EXTERNAL)]
            allowed: set[str] = set()
            required = SFTP | {VAULT}
        else:
            allowed = SFTP | ({VAULT} if args.vault == "disabled" else set())
            required = REQUIRED
        report = verify(names, args.trx.read_bytes(), allowed, required)
    except (OSError, UnicodeError, ET.ParseError, ValueError) as error:
        report = {"schemaVersion": 1, "passed": False, "problems": [str(error)]}
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2, ensure_ascii=True))
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    sys.exit(main())
