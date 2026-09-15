# Tessera implementation contract

- Target .NET 10 and Avalonia 12.1.1. Pin RoyalTerminal 0.5.2; never fabricate its APIs.
- Match the Tessera designer's Obsidian/Porcelain/Blueprint tokens, 55px titlebar, 52px rail, 226px sidebar, 81px workspace header, 35px contextbar, 184px tool dock and compact status bar.
- Native application code belongs in src/Tessera.App. Do not replace RoyalTerminal with a WebView, xterm simulator, or hand-written VT parser.
- Core docking/persistence has no Avalonia or transport dependency. A document has exactly one owner. Dock/reparent/focus never implicitly start or dispose a session.
- Never persist plaintext credentials, keystrokes, production unlocks or broadcast targets. Never trust unknown SSH host keys automatically.
- Normal startup is real PTY; visual fixtures must be explicitly --design-mode or test-only.
- Run core tests, native headless tests, capture all themes and publish unsigned per-platform artifacts in CI. Do not claim tests passed without a recorded result.
- Use one command registry for menus, palette, shortcuts and toolbar actions. Preserve shell shortcuts and IME composition.
- Track remaining acceptance gaps honestly in docs/IMPLEMENTATION.md.

## Native menus and regression evidence
- Attach one NativeMenu root per top-level terminal window. Never replace/clear the root on shell redraw or disposal; update item properties in place. Main/floating windows must not share menu nodes.
- Use the shared AppCommand instances in both native and managed menus. Preserve pending chords when effective bindings have not changed.
- Keep headless and native-backend coverage distinct. Avalonia.Headless on macOS is not Cocoa export coverage. Run the separate desktop smoke before packaging.
- A fixture supplies only picker decisions and explicit sample terminal content. Real file IO, recording formats, encryption and command effects must remain production code.
- Keep discovery/TRX completeness gates and the exact platform/mode in reports; never claim added tests or workflow configuration were executed without evidence.
