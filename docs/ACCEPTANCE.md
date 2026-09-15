# Native acceptance: 0.2.0-preview.1

Tessera remains a native Avalonia/RoyalTerminal application. This revision implements the software acceptance items identified after the initial native alpha. Test results belong to the exact commit and run; the presence of a test is not evidence that it ran. Environmental tests are explicitly skipped outside their isolated fixture, never reported as a pass by returning early.

## Implemented workflows

| Acceptance item | Implementation and verification contract |
|---|---|
| Cross-window drag docking | Native Avalonia drag/drop between the main window and independently split floating windows. Opaque, one-use process-local capabilities prevent external drags from supplying trusted document IDs. Drag onto a tab header to insert at that position; edge drops split and center drops join. The same terminal/control/PTY survives. Floating layout and window dimensions persist. Arrow navigation is spatial inside the active window; next/previous traverses groups. |
| SFTP | Real SSH.NET connection through the same host verification, encrypted-key, agent, proxy and keyboard-interactive authentication services. Browse, staged upload/download, cancel, create directory, rename, delete, UTF-8 editing and write confirmation. Connections start read-only. Inline edits carry metadata plus SHA-256 content versions. Symlink rename/delete acts on the directory entry, not its target. Overwrite requires the server's atomic POSIX rename extension; no delete-then-rename fallback. |
| Persistent ranked history | Completed semantic shell events only; persistence defaults off. Opt-in retention, frequency/recency/profile/directory/success ranking, secret-like-command filtering, bounded storage and explicit clearing. History inserts text without executing it. |
| Advanced search | Native buffer snapshots with regex, case and Unicode whole-word controls. Bounded regex evaluation and result counts, native match navigation and stale-buffer detection. Zero-width matches are omitted; multiline matches navigate to the first visible line. |
| Host-key onboarding | Independent native modal fingerprint review: reject, trust once, trust and save. Changed and revoked keys fail closed without an override action. Pinning does not bypass revoked-key policy. OpenSSH known_hosts plus Tessera's private known_hosts are checked. Trust is checked again after the dialog returns, so a revocation or key rotation observed during the prompt invalidates the stale acceptance decision. |
| MFA and private keys | Encrypted key passphrase prompts, SSH agent contribution and keyboard-interactive challenges, including public-key plus challenge authentication. Echo-sensitive fields, cancellation, connection-scoped lifetimes, bounded rounds/prompts and no persistence of passphrases/MFA responses. |
| OS credential storage | Explicit per-profile opt-in: Windows Credential Manager, macOS Keychain, or Freedesktop Secret Service through secret-tool. Endpoint-scoped identifiers; forgetting deletes the stored credential. No plaintext fallback. Linux requires libsecret-tools and an unlocked Secret Service collection. |
| Profile and logging wiring | All native profile fields remain available in the Advanced JSON editor. Compact profile edits preserve additional forwards, keys, arguments, environments and unrepresented fields, including after switching edited profiles. Backspace Ctrl-H, font/shaping/ligatures, Sixel, highlights, scrollback, proxy exclusion, session logging and event-log selection are wired. Logging is output-only, bounded and asynchronous, with rotation and durable close. |
| Cursor and line spacing | Persistent block/bar/underline and blink preferences; native cell-height adjustment without scaling glyphs. Profile font settings remain authoritative unless global override is enabled. Native programs may change their cursor through VT; reconnect reapplies the preference. |
| Persisted shaders | Per-document shader source, language and animation settings, validated/compiled before apply and on restoration. Reduced-motion disables continuous animation. Broken storage is not silently overwritten. Sources have per-pass and aggregate limits. |
| Unsaved-capture recovery | Opt-in recording with encrypted two-second checkpoints, checkpoint on stop/close/replacement, retained generations, recovery list, read-only replay, export and explicit discard. Raw input events are excluded from all saved recordings. Output may still contain echoed secrets. AES-256-GCM keys use the OS vault or an explicit passphrase-wrapped fallback, never an adjacent plaintext key. |
| Accessibility | Named native controls, modal focus cycling and Escape, echo-sensitive secret fields, keyboard docking/navigation, and a bounded read-only terminal document automation provider. Automation SetValue cannot execute commands. Actual assistive-technology and hardware certification remains an evidence gate below. |
| Signed distribution | CI signs source and application archive provenance using OIDC/Sigstore and verifies the bundles. A separate protected workflow performs Authenticode or Developer ID/notarization/stapling using owner-provided credentials. Missing certificates fail the native-sign job instead of emitting a falsely signed artifact. |

## Limits and explicit unsupported backend paths

The pinned RoyalTerminal SSH.NET backend rejects X11 forwarding. The complete profile editor preserves its configuration but does not pretend it is active. Command-based proxies are also rejected explicitly; HTTP, SOCKS4 and SOCKS5 proxies are mapped. This is a backend capability boundary, not silently ignored UI state. SSH agent behavior depends on the platform agent being available.

SFTP version checks are optimistic concurrency checks, not a server-side compare-and-swap transaction. SFTP v3 cannot guarantee protection against another writer changing a path between the final check and rename. Parent directory symlinks are not a sandbox boundary. Directories are not recursively deleted; a non-empty-directory rejection is intentional. A lost connection may leave a clearly named `.tessera-*.part` file on the server. It never triggers deletion of the existing destination.

History filtering is conservative rather than a universal secret detector. Keep persistent history off when policy requires that commands never reach disk. Raw input is removed before capture serialization, but a password printed by a program is still output. Exports are deliberately unencrypted; recovery files are encrypted. A sudden crash may lose the interval since the latest two-second checkpoint. A missing vault key or forgotten recovery passphrase cannot be bypassed.

Search limits: 2,048-character pattern, 8 MiB-character snapshot ceiling, 5,000 matches, regex timeout and total evaluation budget. Transfer editor limit: 2 MiB UTF-8. Directory listing limit: 5,000 entries. Capture memory budget: 32 MiB / 100,000 events checked at checkpoints; recovery file 64 MiB, total 256 MiB, 512 generations. Stop/overflow errors are surfaced, not silently treated as successful recording.

Workspace layout, command history, profiles and shaders remain separate versioned stores. Atomic replacement protects each file; this is not a multi-file transactional database. Credentials, production unlocks and broadcast membership are not workspace fields. Floating windows are restored geometrically; remote connections still require explicit reconnect.

## Automated evidence

`AcceptanceCoreTests` supplements the original core tests with 2,000 mixed-window layout operations, identity checks, bounded search, UTF-8 stream decoding, ranked history and shader persistence. `AcceptanceServiceTests` covers trust/revocation/pins, encrypted recovery/tampering, credential scoping, profile roundtrips and logging. `AcceptanceUiTests` exercises native windows, actual PTY movement, search, cursor metrics, secure dialogs and automation. `HostOnboardingRaceTests` verifies revocation, rotation and concurrent matching trust while the onboarding prompt is open.

`ExternalAcceptanceTests` is enabled only against isolated CI fixtures. It verifies OS vault write/read/replace/delete, public-key plus keyboard-interactive authentication with an encrypted client key, actual SFTP operations and symlink safety. The fixture binds only `127.0.0.1:23781`, creates an ephemeral unprivileged test user and removes it afterward. The native suite runs on Windows x64, Linux x64 and macOS arm64. X11 smoke uses the real desktop backend and synthetic X11 keyboard and pointer input, not Avalonia.Headless. It floats a terminal, drives a native Xdnd drag back to the main workspace, verifies the exact floating window ID disappears, and opens the SFTP workspace. Before/during/after captures and a machine-readable outcome are retained. PTY command assertions use isolated shell startup, wait for a marker actually produced by the child, and require output that cannot be satisfied by echoed input on any platform.

CI uploads TRX files, native screenshots, X11 captures and package checksums. A Sigstore bundle is generated only after core, native and integration jobs succeed. It attests origin and digests; it does not certify the absence of defects or replace an OS-native executable signature.

## Physical-device and assistive-technology signoff

These are real execution gates, not tasks that source code or screenshots can certify. Record the exact build hash, OS version, device, display scale, input method, assistive technology and result for each session in a release issue. No representative-device pass is claimed merely because CI passed.

| Matrix | Required observations |
|---|---|
| macOS Apple Silicon / VoiceOver | Command palette, profile/host-key/MFA focus, terminal document output, floating-window navigation, password announcement suppression, clipboard confirmation and cancellation. |
| Windows x64 / Narrator and NVDA | Same flow; ConPTY Ctrl-C/D/Z, dead keys, IME, UI Automation document value, 125/150/200% scaling and mixed-monitor moves. |
| Linux X11 and Wayland / Orca | Same flow; unlocked/locked/missing Secret Service, native drag across windows, compositor scaling and clipboard ownership. |
| Input and hardware | Japanese/Chinese IME composition, Polish dead keys, keyboard-only operation, touch/trackpad scrolling, physical serial devices, disconnected/reconnected SSH links and agent prompts. |
| GPU/display | Integrated/discrete GPUs, sustained output while resizing, monitor migration, sleep/wake, reduced motion and static/animated shaders. |

Apple Developer ID/notary and Windows signing secrets are not part of this repository. `signed-release.yml` will only run on main, requires successful CI for the exact revision, uses the protected `production-signing` environment and deletes temporary signing material. Actual native signing/notarization requires the repository owner to configure those credentials and environment reviewers.
