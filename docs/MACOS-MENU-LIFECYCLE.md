# macOS startup: preserve native menu ownership

## Reported failure

`dotnet run --project ./Tessera.App` could terminate from a queued `MainWindow.RenderShell()` with:

```
System.ArgumentException: The menu being updated does not match. (Parameter 'menu')
Avalonia.Native.Interop.Impl.__MicroComIAvnMenuProxy.Update(...)
Avalonia.Native.AvaloniaNativeMenuExporter.SetNativeMenu(...)
Tessera.Views.MainWindow.BuildMenu()
```

In Avalonia **12.1.1**, the native exporter initializes its `IAvnMenu` proxy with a particular `NativeMenu` instance. `Update` rejects a different managed root. Tessera previously created and assigned another `NativeMenu` on every shell render: opening the initial terminals and delivering queued layout updates was already sufficient to violate that identity contract.

Primary source: <https://github.com/AvaloniaUI/Avalonia/blob/12.1.1/src/Avalonia.Native/IAvnMenu.cs> and <https://github.com/AvaloniaUI/Avalonia/blob/12.1.1/src/Avalonia.Native/AvaloniaNativeMenuExporter.cs>.

## Implementation

`WindowMenuBinding` constructs and attaches **one complete menu tree per top-level terminal window**, after registering the shared commands. Main and floating windows never share roots or submenu items. The managed in-window menu is also built once. Its items and the native items reference the same `AppCommand` instances.

`BuildMenu()` now refreshes existing bindings rather than allocating or attaching a new menu. Single-stroke gestures update existing items; two-stroke chords remain in the application keyboard dispatcher, not a misleading Cocoa key equivalent. `NativeMenu.NeedsUpdate` refreshes `CanExecute` state before the exporter updates its items. Closing a binding detaches its event subscriptions but **does not clear or replace `NativeMenu.Menu`**: clearing it would also manufacture another root in the pinned exporter.

Binding edits notify the shell so both menus and keyboard dispatch update, including programmatic changes. Reapplying an unchanged binding dictionary during a redraw does not discard an in-progress keyboard chord. Pending overlay decisions complete as cancelled when their owner closes, and late focus callbacks do not focus detached overlay content. Password controls are cleared on every security-dialog close path, including Escape and owner cancellation. Clearing a control is lifecycle hygiene, not a guarantee of managed-string memory zeroization.

The main window also uses `OwnerWindowOnly` closing coordination: otherwise a floating
window's standalone "return tabs" handler can veto application Quit before the main
confirmation is reached. The main handler already checks running sessions and SFTP
edits and drains captures/sessions; a repeated Quit while confirmation is pending
cannot replace the prompt. Cancelling keeps the floating PTY alive.

The fix retains native menus. It does not swallow the exporter exception, disable the menu on macOS, or replace the native app with a web surface.

## Regression layers

`MenuLifecycleRegressionTests` keeps the complete native root/item inventory and the managed `ItemsSource` across initialization, repeated theme/layout/tool changes, binding edits, native-exporter callbacks and floating-window close/reopen. It checks that independently created main windows never share menu objects. The root-identity test was also run against the pre-fix application and failed at `Assert.Same`; the retained negative-control result is not counted as a passing new test.

Avalonia.Headless does **not** implement Cocoa's exporter, even when the tests execute on a macOS runner. Therefore `Tessera.Desktop.Smoke` is a separate executable using the production `AppBuilder`, platform services, resources and event loop. It runs in a fresh process with temporary application storage, first using explicitly labeled design fixtures and then a real local PTY. It exercises redraws, dialogs, bindings and floating windows, requires a real platform handle, and on macOS requires native menus to be exported. Unhandled dispatcher exceptions, incomplete scenarios and wall-clock timeouts fail its JSON report and process exit code.

The CI native-build job runs that harness before packaging. On macOS it also checks out the exact reported baseline (`a4cfad3658a0c2ac1a7396fbaf46b345dda846e2`) into an isolated directory, copies only the smoke harness, and requires the old application to fail with the exact reported exporter message. An unrelated failure or a baseline that does not reproduce the exception does not satisfy the negative control.

Run on macOS/Windows:

```sh
python tools/run_native_validation.py
python tools/run_desktop_validation.py
```

Run on Linux with Xvfb installed:

```sh
python tools/run_native_validation.py
xvfb-run -a -s '-screen 0 1600x1000x24' python tools/run_desktop_validation.py
```

The desktop harness drives command handlers and Avalonia routed key events on a real backend. It is not a physical-keyboard, trackpad or screen-reader audit. The separate existing X11 smoke still tests OS-delivered synthetic pointer drag/drop in CI. A local Linux result is not relabeled as a verified Cocoa or Win32 result.
