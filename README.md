# Tessera

**Your command line, composed.** Native .NET 10 / Avalonia 12 / RoyalTerminal terminal workspace for Windows, macOS and Linux.

## 0.2.0-preview.1 acceptance implementation

Cross-window native drag docking with tab insertion ordering and split floating windows; spatial keyboard navigation; real SFTP browsing, transfers and editing; SSH trust onboarding, encrypted keys, agent and keyboard-interactive MFA; opt-in OS credential storage; ranked persistent command history; regex/case/whole-word native-buffer search; output-only logging; complete profile-document editing; cursor and line-height preferences; persistent shaders; encrypted unsaved-capture recovery.

The original Obsidian, Porcelain and Blueprint design is preserved. Normal launch opens a real local PTY. `--design-mode` is an explicitly non-networked visual fixture, not a simulated production terminal.

```bash
dotnet run --project src/Tessera.App
dotnet test tests/Tessera.Tests -c Release
dotnet test tests/Tessera.App.Tests -c Release
```

The **Native CI** workflow builds all three platforms, tests real PTYs and native controls, runs isolated OpenSSH/SFTP/MFA and OS-vault integration checks, and signs source/application archive provenance with OIDC/Sigstore after the checks pass. Native OS certificates are separate: ordinary CI executables are not notarized or Authenticode-signed. The protected manual signing workflow performs those steps when owner credentials are configured.

[Acceptance, architecture and limits](docs/ACCEPTANCE.md) · [Running and platform prerequisites](docs/RUNNING.md) · [Verify provenance / configure native signing](docs/SIGNING.md) · [Historical alpha ledger](docs/IMPLEMENTATION.md)

Security defaults: production reconnects read-only; broadcast requires explicit non-production targets; SFTP starts read-only; history persistence defaults off; MFA/passphrases remain in memory; raw input is excluded from saved recordings; recovery encryption never uses a plaintext key fallback. Recordings may still contain secrets echoed in output. Missing Linux Secret Service is reported rather than silently writing credentials to files.

RoyalTerminal's pinned SSH.NET backend rejects X11 and command proxies explicitly. Physical-device and assistive-technology acceptance still requires documented execution on representative systems; automated screenshots are not a hardware certification.

