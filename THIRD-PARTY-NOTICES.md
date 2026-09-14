# Third-party components

Tessera uses the following primary projects. Their copyrights and licenses remain with their respective authors; the Tessera application does not replace those terms.

| Project | Use | Upstream |
|---|---|---|
| Avalonia | Native cross-platform application and UI rendering integration | https://github.com/AvaloniaUI/Avalonia |
| RoyalTerminal | Native terminal control, VT, transport, profiles, capture and shader services | https://github.com/royalapplications/RoyalTerminal |
| Ghostty | Native VT implementation via RoyalTerminal's provider | https://github.com/ghostty-org/ghostty |
| .NET | Managed runtime and base libraries in self-contained packages | https://github.com/dotnet/runtime |
| SkiaSharp / HarfBuzzSharp | Rendering and text shaping dependencies | https://github.com/mono/SkiaSharp |
| SSH.NET | SSH transport dependency | https://github.com/sshnet/SSH.NET |

The package step copies license and notice files present in the restored NuGet packages into `ThirdPartyNotices/`, along with a machine-readable package/license inventory. Consult that inventory for the exact resolved versions and transitive components. A package whose metadata uses a license expression rather than a bundled file is recorded explicitly; a distribution license review is still required before a signed production release.

The xterm.js browser design prototype is not included in the native runtime.
