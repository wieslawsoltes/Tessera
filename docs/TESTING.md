# Native acceptance evidence

## Run the checks

From the repository root with the .NET 10 SDK and Python 3.10 or later:

```sh
python -m unittest discover -s tools -p 'test_*.py' -v
dotnet test tests/Tessera.Tests -c Release --logger trx
python tools/run_native_validation.py
```

`run_native_validation.py` first builds and discovers the complete native inventory, then runs that exact build. It saves UTF-8 discovery/execution logs, `results.trx` and `completeness.json` under `artifacts/test-evidence/native`. It deletes old result files before execution, checks the process exit codes and compares discovered display names with actual TRX records. Missing, extra, duplicated, failed, unexpectedly skipped and inconsistent results all fail the command. Both live-PTY cases are independently mandatory even if discovery itself omits them.

Repeated `--property` options pass MSBuild properties, for example `--property 'Platform=Any CPU'`. This is useful in environments that predefine an unrelated `Platform` variable.

## Real platform services and conditional skips

The normal native suite explicitly skips the two SFTP fixture cases. The OS vault case runs only when `TESSERA_VAULT_TESTS=1`; otherwise that one additional skip is permitted. All other discovered cases must pass. On Windows/macOS CI, the vault test is enabled. macOS CI uses an isolated temporary keychain. Linux tests requiring Secret Service run in the separate integration job.

After provisioning the isolated OpenSSH and Secret Service fixtures, run:

```sh
python tools/run_native_validation.py --mode integration
```

That mode requires every `ExternalAcceptanceTests` case to be discovered and reported, including real SFTP transfers/symlink operations, encrypted client keys plus keyboard-interactive authentication, and the vault roundtrip. It permits **no skips**. The fixture is CI-only: it creates an ephemeral unprivileged user, binds the SSH listener to loopback and deletes the fixture afterward. Do not run its provisioning script against a production host.

`eng/desktop-smoke.sh` separately drives real X11 keyboard/pointer delivery through native cross-window drag/drop. Its images and `smoke.json` are evidence of that desktop-backend execution, not physical device or assistive-technology certification.

## PTY assertions cannot pass on input echo

`PtyTestFixture` starts an isolated child shell and waits for child-generated readiness before sending a command. Windows emits that readiness explicitly from a startup script: an echo-disabled `cmd.exe` prompt is not a readiness signal. The subsequent command constructs a unique output marker from separate input fragments. The complete marker must not occur in the input bytes, so terminal echo alone cannot satisfy the test. Moving a live terminal between native windows must preserve its endpoint and still execute that command.

On timeout, JSON-escaped evidence includes transport state, raw output and a rendered snapshot. Escape/control characters are never copied directly into assertion messages. This diagnostic fixture uses isolated, nonsensitive test data; the raw snapshot mechanism is not a production logging policy.

## Why a green process exit is not enough

During the acceptance audit, a Windows VSTest run discovered 63 native cases but emitted only 61 result records. Two live-PTY cases had started and timed out without a recorded outcome, while the process returned success. The result verifier has a regression for that exact failure mode and rejects it before packaging. Test totals are derived from records, not inferred from workflow step names or screenshot content. Explicit fixture skips are listed separately, never counted as passed.

CI runs the verifier before publishing application packages. Archive provenance is emitted only when all required core/native/integration jobs succeed. Provenance establishes origin and integrity; it does not certify hardware coverage or replace Authenticode, Developer ID or notarization. Those release gates remain documented in `ACCEPTANCE.md`, `SIGNING.md`, and repository issues #2 and #3.
