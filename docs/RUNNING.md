# Running the native alpha

## From source

```sh
git clone https://github.com/wieslawsoltes/Tessera.git
cd Tessera
dotnet run --project src/Tessera.App
```

The .NET 10 SDK is required to build. Runtime packages produced by CI are self-contained and do not require a separately installed .NET runtime. Keep the native libraries together with the application; do not copy only the executable.

## macOS arm64

Extract `Tessera-0.1.0-alpha.1-osx-arm64.tar.gz`. It contains `Tessera.app`; open it from Finder or use `open Tessera.app`. This is an unsigned, non-notarized alpha. macOS may require explicit per-application approval in Privacy & Security. The package does not change Gatekeeper policy.

The direct diagnostic launch is:

```sh
./Tessera.app/Contents/MacOS/Tessera
./Tessera.app/Contents/MacOS/Tessera --design-mode
```

## Linux x64

```sh
tar -xzf Tessera-0.1.0-alpha.1-linux-x64.tar.gz
cd Tessera-0.1.0-alpha.1-linux-x64
./Tessera
```

The archive preserves executable permission. A functioning desktop graphics stack and the operating-system dependencies of Avalonia/.NET are still required. Automated validation runs on the GitHub Ubuntu runner; it is not a certification of every Linux distribution and compositor.

## Windows x64

Extract `Tessera-0.1.0-alpha.1-win-x64.zip`, then run `Tessera.exe` inside the extracted folder. The build is unsigned; any Windows trust prompt is not a claim of production signing. Administrator privileges are not required by Tessera's manifest.

## Design inspection versus real sessions

`--design-mode` recreates the three-pane designer composition with explicitly labeled deterministic output. Normal startup uses a real local terminal. Remote connections are only established after an explicit connect action; the initial design's `.example` hosts are never used as real defaults.

Try the command palette (`Cmd+K` on macOS, `Ctrl+K` elsewhere), type `split`, and choose Split right. Open the layout chooser to rearrange sessions without restarting them. Use Preferences for Obsidian, Porcelain and Blueprint, and Connections & profiles for RoyalTerminal's native transport settings.

## Verification and provenance

Every packaged archive has a neighboring `.sha256` checksum. Its application directory includes `build-info.json` with the exact Git commit, runtime identifier and unsigned status. `ThirdPartyNotices/` contains the restored package inventory and upstream notice files supplied by those packages. Native CI only reaches the package step after that platform's test step succeeds.

The source archive is a Git archive of the tested revision, not a separately regenerated application. See `IMPLEMENTATION.md` for scope boundaries and the difference between automated tests, rendered screenshots and representative-hardware acceptance.
