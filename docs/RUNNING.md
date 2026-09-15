# Running the native alpha

## From source

```sh
git clone https://github.com/wieslawsoltes/Tessera.git
cd Tessera
dotnet run --project src/Tessera.App
```

The .NET 10 SDK is required to build. Runtime packages produced by CI are self-contained and do not require a separately installed .NET runtime. Keep the native libraries together with the application; do not copy only the executable.

## macOS arm64

Extract `Tessera-0.2.0-preview.1-osx-arm64.tar.gz`. It contains `Tessera.app`; open it from Finder or use `open Tessera.app`. This is an unsigned, non-notarized alpha. macOS may require explicit per-application approval in Privacy & Security. The package does not change Gatekeeper policy.

The direct diagnostic launch is:

```sh
./Tessera.app/Contents/MacOS/Tessera
./Tessera.app/Contents/MacOS/Tessera --design-mode
```

## Linux x64

```sh
tar -xzf Tessera-0.2.0-preview.1-linux-x64.tar.gz
cd Tessera-0.2.0-preview.1-linux-x64
./Tessera
```

The archive preserves executable permission. A functioning desktop graphics stack and the operating-system dependencies of Avalonia/.NET are still required. Automated validation runs on the GitHub Ubuntu runner; it is not a certification of every Linux distribution and compositor.

## Windows x64

Extract `Tessera-0.2.0-preview.1-win-x64.zip`, then run `Tessera.exe` inside the extracted folder. The build is unsigned; any Windows trust prompt is not a claim of production signing. Administrator privileges are not required by Tessera's manifest.

## Design inspection versus real sessions

`--design-mode` recreates the three-pane designer composition with explicitly labeled deterministic output. Normal startup uses a real local terminal. Remote connections are only established after an explicit connect action; the initial design's `.example` hosts are never used as real defaults.

Try the command palette (`Cmd+K` on macOS, `Ctrl+K` elsewhere), type `split`, and choose Split right. Open the layout chooser to rearrange sessions without restarting them. Use Preferences for Obsidian, Porcelain and Blueprint, and Connections & profiles for RoyalTerminal's native transport settings.

## Verification and provenance

Every packaged archive has a neighboring `.sha256` checksum. Its application directory includes `build-info.json` with the exact Git commit, runtime identifier and unsigned status. `ThirdPartyNotices/` contains the restored package inventory and upstream notice files supplied by those packages. Native CI only reaches the package step after that platform's test step succeeds.

The source archive is a Git archive of the tested revision, not a separately regenerated application. See `IMPLEMENTATION.md` for scope boundaries and the difference between automated tests, rendered screenshots and representative-hardware acceptance.


## New workflows

Use **Tools → SFTP file workspace** for remote files. Select an SSH profile, connect, verify a new host independently, and complete any key-passphrase or keyboard-interactive challenge. Enable SFTP writes explicitly before upload, save, rename or delete. Use **Advanced profile JSON** for arguments, environments, complete forwarding/key arrays and fields not represented by the compact upstream editor. Reconnect for transport/logging changes.

Preferences contain cursor shape, blink, line spacing and opt-in ranked history persistence. Profile fonts are retained unless **Override profile font** is enabled. Use Alt+arrow for spatial pane movement, Alt+PageUp/PageDown for sequential group movement, or edit the command bindings. Drag a tab onto another window's header for ordered insertion, its center to join, or an edge to split.

The shader editor saves the applied source for the terminal document. Recording start first prepares an OS-vault-protected recovery key or asks for an explicit recovery passphrase when the vault is unavailable. **Recover unsaved recordings** restores a read-only replay; recovered originals remain until explicitly discarded. Exports omit input events and are not encrypted.

Linux credential persistence requires `libsecret-tools` (`secret-tool`) plus a running, unlocked Freedesktop Secret Service provider such as GNOME Keyring or a compatible desktop vault. Missing/locked services do not trigger plaintext fallback. Headless servers can still connect using runtime credentials; encrypted capture recovery can use the explicit passphrase workflow.

Consult [ACCEPTANCE.md](ACCEPTANCE.md) for bounds, SFTP concurrency semantics and physical-device audit requirements, and [SIGNING.md](SIGNING.md) before treating any build as OS-native signed.
