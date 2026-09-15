# Windows ConPTY standard-handle isolation

The two C# files in this directory are derived from RoyalTerminal at
`b740171f3d0ff6e97a1fcc1f58cc311f5dd4507f`, under the included MIT license.
`tools/update_conpty_compat.py` records and verifies the original SHA-256
hashes. It is a maintenance utility, not invoked by normal builds.

The behavioral patch sets STARTF_USESTDHANDLES with NULL stdin/stdout/stderr
in the child STARTUPINFO. Without it, a parent launched with redirected
streams (including CI and IDE test hosts) can cause ConPTY children to use
those streams instead of their pseudoconsole. bInheritHandles=false is
insufficient. No process-global handles are modified and no shell wrappers
or binary patching are used. Namespace and type visibility isolate the copy.

Primary explanation and workaround from the Windows Terminal maintainers:
https://github.com/microsoft/terminal/discussions/15814

TesseraPtyFactory supplies this implementation to the normal application
and its raw-transport regression; Unix uses the unchanged upstream factory.
Retire this compatibility copy when the pinned upstream dependency provides
and verifies the same launch policy. Keep the discovery/result-completeness
gate: a successful test process exit is not proof that every case reported.
