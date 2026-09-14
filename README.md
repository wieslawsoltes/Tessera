# Tessera

**Your command line, composed.**

A native cross-platform terminal workspace built in C# with Avalonia and RoyalTerminal. The implementation follows the Tessera designer's Obsidian, Porcelain and Blueprint themes, compact chrome, independent tab groups and docked session tools.

> Alpha implementation. Native CI builds Windows, macOS and Linux, executes real PTY and headless UI tests, and captures all three themes. Check the latest run before treating a build as verified. This is not the earlier xterm.js browser simulator.

## Run

Install the .NET 10 SDK, then:

```sh
git clone https://github.com/wieslawsoltes/Tessera.git
cd Tessera
dotnet run --project src/Tessera.App
```

Normal startup opens a real local terminal through RoyalTerminal (ConPTY on Windows, Unix PTY on macOS/Linux). Connections and workspaces are stored below the current user's application-data directory, in `Tessera/`.

To inspect the original multi-pane visual composition without launching processes or connecting to remote systems:

```sh
dotnet run --project src/Tessera.App -- --design-mode
```

Design mode is explicitly labeled in the status bar and uses deterministic rendering fixtures. It never writes the user's workspace files.

## Workspace interactions

Create independent workspaces and terminal tabs. Drag a tab onto another pane's center to join its group, or onto an edge to create a nested split. Native splitters support pointer and keyboard resizing. Six arrangement presets preserve open session identities. Focus mode, tool-panel docking, floating terminal windows, pinning, layout undo/redo and saved arrangements are available from the menus and command palette.

The palette is `Cmd+K` on macOS and `Ctrl+K` on Windows/Linux. `Ctrl+Shift+P` is also available. Keyboard bindings can be edited under Preferences. Menus, the palette and toolbar route through the same command registry. Terminal `Ctrl+C`, `Ctrl+D`, completion and normal shell input remain shell-owned.

The reusable command library inserts text without pressing Enter. The Files tool edits actual local text files, with explicit write confirmation. Notes are scoped to each workspace. History uses RoyalTerminal shell-integration lifecycle events, not intercepted keystrokes. Native recording/replay uses RoyalTerminal JSON and asciicast v3.

## Connection and safety model

The native RoyalTerminal profile editor exposes PTY, SSH, Pipe, Raw TCP, Telnet and Serial connection forms. Unknown SSH host keys are never automatically trusted. Passwords and proxy passwords are kept in memory only and are not written into workspace or profile documents. Use SSH-agent authentication for keys that need a passphrase.

Production profiles reconnect read-only and are never broadcast targets. Broadcast requires explicit selection of the active source and at least one other connected non-production session; Escape and workspace switching disarm it. Capture requires a warning acknowledgement because terminal input and output may include sensitive data. A persisted replay tab is a read-only slot and cannot automatically start a live shell.

Saved arrangements are connection **slots**, not process snapshots. Restoring a layout closes the replaced workspace's sessions after confirmation. Remote sessions are not silently reconnected during startup.

## Build and test

```sh
dotnet build Tessera.slnx -c Release
dotnet test tests/Tessera.Tests -c Release
dotnet test tests/Tessera.App.Tests -c Release
```

CI publishes self-contained **unsigned** `linux-x64`, `win-x64` and `osx-arm64` artifacts. Keep all files in the published directory together; the terminal stack includes native libraries. Apple signing/notarization and platform installers are not configured.

## Architecture

- `Tessera.Core`: validated immutable docking trees, document identities, safety predicates, atomic workspace storage and layout history.
- `Tessera.App`: the native design shell, command registry, session ownership, native RoyalTerminal controls, profile editor and tools.
- `Tessera.Tests`: core unit tests, randomized docking invariants and atomic persistence.
- `Tessera.App.Tests`: native headless interactions, theme captures, real PTY execution and session-lifetime regressions.

A terminal's process lifetime belongs to `SessionRuntime`, not to the visual tree. Tabs, groups, focus mode and floating windows borrow the existing control; they do not clone its process.

## Scope boundaries

The Files tool is currently local, not SFTP. Floating windows use explicit float/return commands, not cross-window drag docking. OS credential-vault persistence, GPU texture interop, remote reconnect orchestration, advanced profile logging policy, full accessibility/hardware evaluation, and exact screenshot-by-screenshot visual acceptance are separate acceptance items. Do not interpret the presence of the complete upstream settings editor as proof that every upstream configuration option is connected to Tessera's runtime. The implementation ledger in `docs/IMPLEMENTATION.md` tracks verified coverage and remaining work.
