# Tessera implementation ledger

**Current acceptance revision: 0.2.0-preview.1.** The software acceptance work is recorded in [ACCEPTANCE.md](ACCEPTANCE.md); distribution verification is in [SIGNING.md](SIGNING.md). The following section is retained as historical context for the initial 0.1.0 alpha and must not be read as the current feature status.

<details><summary>Historical initial-alpha ledger</summary>

# Tessera native implementation ledger

## Product and implementation boundary

This is a native C#/.NET 10 application using Avalonia 12.1.1 and RoyalTerminal 0.5.2. The browser prototype is the design reference, not the runtime. A normal launch opens an actual local PTY. `--design-mode` is a separately labeled, non-networked visual fixture and does not save user settings.

The design target remains full Tessera workflow parity and the broad RoyalTerminal capability surface. The current source is a working native alpha, not a declaration of complete terminal-market feature parity or final pixel-perfect acceptance.

## Design contract

The designer's three palettes are reproduced by semantic role in `ThemeManager.Palettes`: Obsidian, Porcelain and Blueprint. The native shell uses the same terminal-first spatial composition: 55 DIP title band, 52 DIP navigation rail, 226 DIP workspace sidebar, 81 DIP workspace header, 35 DIP context band, 184 DIP default tool area, and 28 DIP application status bar. Compact density changes the title and workspace header heights deliberately.

The visual fixture uses independent terminal tab groups in a 62/38 split, with a vertical split in the right column. Ordinary startup is intentionally one real local session rather than a dashboard of fake remote connections. Terminal ANSI output, document titles, platform decorations and connection metadata can therefore differ from a browser reference screenshot without changing the shell's design contract.

The UI font is the embedded Avalonia Inter family. Terminal fonts remain independently configurable. Colors are applied to both chrome and native terminal rendering. Tool cards keep bounded heights; the narrow right dock uses a tool selector instead of overflowing horizontal tabs.

## Implemented host features

| Area | Native implementation | Validation / qualification |
|---|---|---|
| Workspaces | Add, rename, remove, switch; durable notes and active workspace | Core model validation and serialization tests |
| Tabs | New, duplicate connection, rename, pin, close, switch, context actions | Native toolbar executes once; document ownership tested |
| Docking | Arbitrary nested two-child splits, edge/center tab drop, splitter resizing, presets | 500 randomized layout transitions plus directed ownership tests |
| Layout lifetime | Existing controls and processes survive reparenting | Native identity tests for docking, focus mode and floating windows |
| Layout persistence | Atomic versioned JSON, backup, bounded input validation, fresh slots for restored templates | Serialization and atomic storage tests |
| Focus / tools | Focus one group, hide sidebar/tools, bottom/right tool docking | Native UI tests and screenshots |
| Floating windows | Explicit float and return commands, same terminal instance | Native round-trip test; cross-window dragging is not implemented |
| Command system | One command registry for menus, native menus, palette, toolbar and shortcuts | Duplicate execution and binding validation regressions |
| Palette | Search commands, connections and workspaces; keyboard navigation | Includes repeated typing, empty-result and recycled-container tests |
| Keybindings | Editable one- and two-stroke gestures, prefix conflict checks, invalid-import fallback | Registry unit tests; shell Control-C/D/Z are reserved |
| Native terminal | Actual RoyalTerminal control, session service and PTY runtime | Real PTY integration test in the native test suite |
| VT engine | Explicit managed and Ghostty provider composition; Auto policy for real sessions | Actual library availability decides native use; no fake GPU performance claims |
| Connection profiles | Native editor for PTY, SSH, pipe, raw TCP, Telnet, serial; profile CRUD | Uses RoyalTerminal profile serializer and transport mapper; remote/hardware test matrix remains separate |
| Appearance | Font family/source/file, rendering flags, font sizing, scrollback, reflow, Sixel, shaping/ligatures, row regex highlights | Connected to control and renderer properties, subject to selected VT capabilities |
| Shaders | Native source editor, source-language selection, preset/custom source, compile diagnostics, per-session apply/disable | Native preset compilation/apply regression; per-session shader sources are not persisted |
| Shell integration | Explicit generated Bash/Zsh/Fish/PowerShell bootstrap with confirmation | Uses RoyalTerminal builder; does not modify shell dotfiles |
| Command history | Completed command lifecycle events with host/profile context | In-memory bounded list; not a keystroke logger |
| Snippets | Add, edit, delete, insert one safe command line without Enter | Inserts only; user executes explicitly |
| Search | Native buffer literal search and match navigation | Regex/case-sensitive search UI from the browser prototype remains to be ported |
| Local files | Pick folder, traverse, bounded UTF-8 editor, write confirmation, external-change check, atomic replace | Actual local files, not a virtual demo filesystem; SFTP is not implemented |
| Capture | Explicit opt-in input/output/resize capture, JSON and asciicast v3 save | Native RoyalTerminal capture APIs, not the browser's simulator format |
| Replay | Play, pause, stop, seek, read-only terminal, persisted replay identity | A restored replay slot cannot autostart a shell |
| Diagnostics | Session state, errors, dimensions, actual VT selection and bounded application events | No raw input logging in diagnostics |
| Production safety | Read-only on reconnect; runtime-only unlock; production always excluded from broadcast | Programmatic and native input-event regressions |
| Broadcast | Explicit active source and targets; connected/unlocked/non-production only; workspace/Escape disarm | Byte fan-out with recursion guard; physical multi-host validation still required |
| Secrets / trust | In-memory passwords and proxy passwords, secret references on disk, known-host validation | OS credential vault persistence and interactive unknown-host trust UI remain separate work |

## Architecture and invariants

`Tessera.Core` is independent of Avalonia, terminal parsing and transport implementations. A `Workspace` contains a validated immutable dock tree and stable terminal documents. Each document appears in exactly one tab group; each group has a valid active tab; splits have finite bounded ratios. Document limits and tree-depth limits apply at the persistence boundary.

`SessionRegistry` owns `SessionRuntime` instances by document ID. A runtime owns one `TerminalControl`, one capture runtime and one cancellable serialized transport lifecycle. A visual tab or pane borrows the control. It does not own the process. Visual detach/reattach is distinct from session shutdown; reconnect is an explicit command. Capture/replay state is not confused with a live transport slot.

`ShellController` owns application state and workspace operations. `MainWindow` and its partial view-composition files bind commands, native storage dialogs, focus routing and platform windows. The current implementation is not a completed view-model-per-dialog refactor: view composition still contains orchestration code. The core boundaries already support a later extraction without changing saved workspace identity semantics.

The command registry is authoritative for application actions. A disabled command does not swallow a shell key. Shortcuts are validated before registration and imported malformed gestures fall back to defaults. Two-stroke sequences are bounded by a timeout; conflicting one-stroke prefixes are rejected by the editor.

## Safety and persistence semantics

Production unlocks, broadcast membership, passwords and proxy passwords are never workspace fields. New production connections lock input again. Replays are permanently read-only. Save-layout means save an arrangement of connection slots, not freeze or revive processes. Restoring a replacement layout requires confirmation and closes the replaced sessions.

Profile files use RoyalTerminal's serializer. Workspace files use Tessera's schema. A damaged workspace is reported and is not overwritten by autosave. Workspace writes use a temporary file, flush, atomic replacement and a backup. Files are user-read/write on Unix. The current backup is retained for recovery; automated recovery choice UI and a process crash journal are not implemented.

Capture is separately opt-in because recordings contain input as well as output. They can include secrets even though ordinary history is semantic rather than raw input. Do not record credentials. Unsaved recording retention after every possible close/crash path needs an additional product acceptance pass.

## Reproducible verification

The ordinary `Native CI` workflow builds and tests Windows x64, Linux x64 and macOS arm64. The native suite uses Avalonia Headless with real Skia/HarfBuzz rendering, not mocked text rectangles. It also starts an actual PTY. The core suite tests docking, validation, serialization and atomic persistence.

Each native job uploads TRX results and 11 view captures: all three themes, command palette, preferences, profile editor, layout chooser, production read-only state, focus mode, right-docked tools and compact workspace. These screenshots establish actual rendered output, but do not constitute a completed automated pixel-diff acceptance suite against every browser reference.

At commit `d2df28571093f6b04f568733e187cb51aa7c1ba6`, native build, UI and PTY tests passed on all three platforms in run `34900796620`. Subsequent commits add visual refinement, shader tests, keyboard safety tests and palette recycling coverage; consult the attached run for the exact commit rather than treating this historical result as proof for later changes.

A self-contained Linux artifact was also run under X11/Xvfb. Actual typing uncovered a recycled palette-template null dereference that open-only headless checks had missed. The fix is accompanied by a filtering regression; this is why interactive acceptance is kept distinct from successful compilation.

## Remaining acceptance work

Full parity still includes cross-window drag docking and OS drag/drop, arbitrary tab insertion order, spatial rather than sequential pane navigation, SFTP and remote file workflows, history persistence/ranking, case/regex buffer search controls, host-key onboarding/MFA workflows, OS keychain integration, complete logging/backspace/profile-behavior mapping, cursor/line-height preferences, saved shader sources, and robust unsaved-capture recovery. The native settings editor exposes upstream fields; that alone is not a claim that every field has a Tessera runtime policy.

GPU texture interoperability is not the current renderer; the shader pipeline operates on RoyalTerminal's default Skia path. Native VT support does not imply a completed graphics-backend compatibility or performance certification.

Touch/gesture delivery, IME across representative systems, VoiceOver/Narrator/Orca auditing, multi-monitor DPI migration and physical serial/SSH interoperability need representative hardware and real services. Windows/macOS signing, Apple notarization, installers, automatic updates and application-store distribution are not configured. Current packages are explicitly unsigned alpha builds.

</details>
