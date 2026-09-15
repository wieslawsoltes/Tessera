# Windows pseudoconsole stream isolation

Tessera uses RoyalTerminal's terminal control, VT processors, session abstractions and transport providers. For the Windows PTY implementation, `TesseraPtyFactory` selects a small compatibility copy of the pinned RoyalTerminal Windows PTY sources. Unix still uses the upstream default factory. The copy's source revision, original SHA-256 hashes and MIT license are retained in `Services/Compatibility` and `tools/update_conpty_compat.py`. The updater is a maintenance operation, not part of normal build or startup.

## The launch-policy correction

When the terminal host's standard streams are redirected, Windows can duplicate those stream handles into a ConPTY child even though `CreateProcessW` specifies `bInheritHandles=false`. That can divert shell output away from the pseudoconsole and interfere with the host's own redirected input/output. A test host and an IDE launcher are examples; it is not exclusively a testing problem.

The corrected `STARTUPINFOEX.StartupInfo` explicitly sets `STARTF_USESTDHANDLES` while leaving `hStdInput`, `hStdOutput` and `hStdError` NULL. This requests fresh standard handles bound to the child's pseudoconsole instead of automatically copying the parent's redirected handles. The process creation still disables general handle inheritance. No global `SetStdHandle` calls, command wrappers, package binary modifications or shell-level `CONOUT$` redirects are involved.

This behavior and the NULL-handle workaround are described by Windows Terminal maintainers in https://github.com/microsoft/terminal/discussions/15814 . Besides namespace and internal visibility, this launch flag is the only behavioral change in the compatibility copy. Keep that delta small and retire the copy once the pinned upstream package implements and passes equivalent isolation tests.

## Regressions and evidence

`PtyTransportTests.ChildStandardOutputReachesThePseudoTerminalWithoutUiOrParser` starts a real child shell and requires its normal stdout to arrive through the application-selected PTY factory, without Avalonia or a VT parser. The CI runner itself captures process stdout/stderr, exercising redirected-parent launch conditions.

The native UI tests additionally require the child readiness marker, actual command output that is not present in its echoed input, preservation across native window transfers, and asynchronous shutdown. An isolated Windows batch script emits readiness explicitly rather than relying on the interactive prompt; that fixture detail alone does not solve the stream-isolation bug.

The acceptance runner compares discovery with TRX records and rejects missing cases. This is mandatory because the original redirected-stream failure was accompanied by a green VSTest exit without results for both timed-out live-PTY tests. The runner never treats a missing record as a skipped or passed case. See `TESTING.md` for local commands, fixture skip policy and machine-readable evidence.
