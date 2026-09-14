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
