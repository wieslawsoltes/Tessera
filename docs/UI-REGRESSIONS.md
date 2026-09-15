# UI regression coverage and execution boundaries

The full application test project uses `Avalonia.Headless.XUnit` with the actual Tessera application resources, Skia drawing, HarfBuzz and RoyalTerminal controls. Each new UI fixture has isolated temporary data, a dispatcher-bound application lifetime, bounded polling, cancellation and asynchronous cleanup. Design fixtures are explicitly marked; real-PTY regressions independently start actual child processes.

Headless UI tests exercise commands, native/managed menu models, real buttons, text fields, list selection, routed keyboard events, overlays, security dialogs and filesystem operations. They assert resulting state, identity, output or file content. Screenshots are supplemental rendered evidence, **not image-diff certification or a substitute for behavior assertions**.

## Feature matrix

| Surface / workflow | Behavioral coverage |
|---|---|
| Native and managed menu ownership | `MenuLifecycleRegressionTests`: initial attachment, root/submenu/item identity, every registered command exposed exactly once, current command enablement via exporter callbacks, single/chord/empty/invalid bindings, independent main/floating ownership, close/reopen, unchanged chord across redraw. |
| Top-level overlays | `FullUiRegressionTests.EveryOverlayOpensThroughItsCommandAndEscapeRestoresFocus`: palette, profiles, preferences, layouts, bindings, shaders, search, broadcast, diagnostics, about, and empty recovery. Real command execution, title/content, focus restoration, Escape and menu stability. |
| Tabs and workspaces | Create, rename, duplicate, pin, cancelled/confirmed close, workspace create/rename/remove, saved arrangement creation; outstanding confirmation cancels on owner close. New terminal slots are distinct and existing terminal instances survive. |
| Layouts and docking | All six preset cards via real buttons with undo/redo; native-floating lifecycle; original cross-window capability tests and 2,000 randomized layout operations; split-right/down, next/previous tab/pane, directional focus, keyboard resize. Existing X11 integration tests OS-delivered drag separately. |
| Appearance | All three theme cards, 840/1050/1450-DIP responsive chrome, compact mode, reduced motion, font override/size, native line height, cursor style/blink, history preferences, serialization roundtrip, zoom actions. |
| Palette and menus | Filter/navigation/Enter dispatch, empty-list recycling, shared command identities, toolbar actions, disabled-command behavior and binding conflict validation. Native Cocoa export itself is tested by the separate desktop harness. |
| Command library and history | Snippet create/edit/remove from real UI; completed-history population, filtering, cancel/confirm clearing; existing core ranking, retention and secret-filter tests. Fixtures do not pretend their sample commands were executed remotely. |
| Notes and local files | Notes survive docking/focus/tool switches. Choose folder, browse/open, Unicode editing, confirmed save, cancellation, binary/size rejection and external-write conflicts—including same-size changes with restored timestamps while confirmation is open. Text exports use the actual command and real file IO. |
| Search | Regex/case/whole-word form toggles, result selection, next/previous, malformed expressions, cancellation, stale snapshots and bounded evaluation in the existing service/core tests. |
| Shaders | Real native shader editor compilation, preset selection, cancelled source picker, malformed source cannot replace the valid pipeline; existing persistence and restoration validation. |
| Input / clipboard | Select/copy actual terminal text through the headless clipboard service; locked paste rejection; native-key/text and direct-paste guards retained; cancelled capture/bootstrap produce no input; real child-stdout and command-output tests cannot pass on echoed input. |
| Capture and replay | JSON/asciicast v3 file imports via the real command, play/pause/seek/stop, no process or input on replay, exports of both formats stripping raw input, cancelled/corrupt imports cannot create a tab. Cancelled recording does not create a recovery key. |
| Encrypted recovery | Actual passphrase-wrapped encrypted checkpoint provision, native passphrase dialog, read-only replay, export preserving encrypted original, cancelled and confirmed discard. Only fixture vault availability is substituted; cryptography and storage are real. Existing service tests cover tampering and missing/wrong keys. |
| SSH trust and MFA dialogs | Explicit reject/trust-once/trust-and-save decisions; immutable displayed fingerprint; profile overlay preserved; echo-aware challenge fields; response clearing on submit/Escape/owner close; queued prompts cancel; no credential-file persistence. Existing host validation/revocation/rotation/race tests remain. |
| Connection profiles | Native settings surface, compact/advanced preservation tests, Advanced JSON immutable-ID rejection and actual successful disk write. Private-key/agent/proxy/transport mapping remains covered by service/integration tests rather than simulated remote connections. |
| SFTP | Disconnected workspace is singleton/read-only; precondition errors do not open pickers or modify files. Existing isolated OpenSSH integration covers encrypted-key plus interactive MFA, real transfers, conflicts and symlink safety. UI tests do not claim to click through live SFTP transfer dialogs or every upstream profile-editor option. |
| Lifecycle and shutdown | Quit closes owned windows, retains native root identity and disposes sessions. Existing live-PTY tests verify native-window transfer, async disposal and input policy. Isolated backend smoke covers initialization, queued native exporter updates and shutdown. |
| Accessibility | Labels, secret masking, focus restoration/cycling, Escape and read-only terminal automation provider. Actual VoiceOver/Narrator/NVDA/Orca behavior remains a separate release gate. |

## Native pickers versus real file operations

`IWorkspaceFileDialogs` is an application-owned boundary. Production delegates to `Window.StorageProvider` and disposes picker results. Tests supply only user picker choices (paths or cancellation) through `ScriptedFileDialogs`. File validation, command handlers, encryption, recording serializers, editor conflict checks and writes are the production implementation. The tests do not reimplement Avalonia interfaces marked `NotClientImplementable`, launch OS picker windows, or mock a filesystem.

Native picker UI and sandbox/security-scoped file behavior need native-system testing. Local-file save conflict checks are optimistic and do not claim server-side or cross-process compare-and-swap atomicity. The recording import uses RoyalTerminal's registered format probing rather than the JSON-only overload, so exported `.cast` files can be opened again.

## Evidence is enforced

`python tools/run_native_validation.py` builds/discovers the suite, executes that exact build, then checks discovery/TRX parity. Root-identity, first-export and command-inventory regressions are independently required alongside live PTY tests. No disappearing native test can turn the job green. External service cases skip only outside the documented fixture; the integration job permits no skips.

`python tools/run_desktop_validation.py` independently records native backend handles and complete scenario manifests. On macOS a passing report must also say `nativeMenuExported: true`. CI runs a negative control against the pinned old code and the positive native scenarios before publishing packages.

All tests and workflow definitions are supplied as source. Actual local and CI results belong to their exact revision and platform. Adding a workflow is not evidence that it was run. See `TESTING.md`, `MACOS-MENU-LIFECYCLE.md` and release gates #2/#3 for the remaining environmental boundaries.
