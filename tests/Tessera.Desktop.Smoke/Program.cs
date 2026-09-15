using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Tessera.Core;
using Tessera.Services;
using Tessera.Views;

namespace Tessera.Desktop.Smoke;

/// <summary>
/// Process-isolated regression using the production platform initializer, not
/// Avalonia.Headless. On macOS this loads libAvaloniaNative and exports Cocoa menus.
/// Unhandled dispatcher exceptions escape MainLoop and make the report fail.
/// </summary>
internal static class Program
{
    private static bool _completed;
    private static readonly List<string> Checks = [];
    private static string? _failure;
    private static string? _handle;
    private static bool _exported;

    [STAThread]
    public static int Main(string[] args)
    {
        bool fixture = !args.Contains("--real-pty", StringComparer.Ordinal);
        int reportIndex = Array.IndexOf(args, "--report");
        string report = Path.GetFullPath(reportIndex >= 0 && reportIndex + 1 < args.Length
            ? args[reportIndex + 1] : "artifacts/desktop-backend/menu-lifecycle.json");
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        string directory = Path.Combine(Path.GetTempPath(), "tessera-native-menu-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var loop = new CancellationTokenSource();
        using var registration = timeout.Token.Register(() => loop.Cancel());
        MainWindow? window = null;
        try
        {
            // Uses the real App resources and real platform services. No lifetime
            // is supplied, so App does not create a second, non-isolated main window.
            global::Tessera.Program.BuildAvaloniaApp().SetupWithoutStarting();
            window = new MainWindow(fixture, directory);
            var activeWindow = window;
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    await ExerciseAsync(activeWindow, report, fixture, timeout.Token);
                    await activeWindow.Shell.PrepareShutdownAsync();
                    activeWindow.CloseForTests();
                    _completed = true;
                }
                catch (Exception exception) { _failure = exception.ToString(); }
                finally { loop.Cancel(); }
            }, DispatcherPriority.Background);
            window.Show();
            Dispatcher.UIThread.MainLoop(loop.Token);
        }
        catch (Exception exception) { _failure = exception.ToString(); }
        finally
        {
            try { window?.CloseForTests(); }
            catch (Exception exception) { _failure ??= exception.ToString(); }
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            if (!_completed && _failure is null) _failure = "Native backend scenario did not complete within its wall-clock budget.";
            bool passed = _completed && _failure is null;
            File.WriteAllText(report, JsonSerializer.Serialize(new
            {
                schemaVersion = 1, passed, mode = fixture ? "explicit design fixture" : "real local PTY",
                platform = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                headless = false, nativeHandle = _handle, nativeMenuExported = _exported,
                inputSource = "command handlers and Avalonia routed key events on native backend",
                commit = Environment.GetEnvironmentVariable("GITHUB_SHA"), checks = Checks,
                physicalHardwareAudited = false, error = _failure
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(File.ReadAllText(report));
        }
        return _completed && _failure is null ? 0 : 1;
    }

    private static async Task ExerciseAsync(MainWindow w, string report, bool fixture, CancellationToken token)
    {
        await w.InitializeAsync(); await TickAsync(token);
        _handle = w.TryGetPlatformHandle()?.HandleDescriptor;
        Require(!string.IsNullOrWhiteSpace(_handle), "real native window handle");
        _exported = NativeMenu.GetIsNativeMenuExported(w);
        if (OperatingSystem.IsMacOS()) Require(_exported, "Cocoa native menu actually exported");
        if (!fixture) Require(w.Shell.ActiveSession?.IsRunning == true, "real local PTY started");
        var root = NativeMenu.GetMenu(w) ?? throw new InvalidOperationException("No native menu was attached.");
        var items = Flatten(root).ToArray();
        var original = w.Shell.Sessions.All.ToDictionary(s => s.Id, s => s.Terminal);
        for (int cycle = 0; cycle < 18; cycle++)
        {
            w.Shell.SetTheme(new[] { "Obsidian", "Porcelain", "Blueprint" }[cycle % 3]);
            w.Shell.SetTool(cycle % 2 == 0 ? "Notes" : "Commands");
            w.Shell.ToggleSidebar();
            w.Shell.SetPreferences(w.Shell.Data.Preferences with { ToolsOnRight = cycle % 2 == 0 });
            w.Shell.Bind("theme", cycle % 2 == 0 ? "Ctrl+Alt+Q" : "Ctrl+Alt+K,Ctrl+Alt+T");
            w.Width = cycle % 2 == 0 ? 1380 : 1040;
            await TickAsync(token);
            Require(ReferenceEquals(root, NativeMenu.GetMenu(w)), "stable native root " + cycle);
            Require(items.SequenceEqual(Flatten(root)), "stable native item identities " + cycle);
        }
        foreach (string id in new[] { "settings", "layouts", "profiles", "keybindings", "find", "shaders", "about" })
        {
            await w.Commands[id].ExecuteAsync(); await TickAsync(token);
            Require(w.IsOverlayOpen, id + " opens on native backend");
            w.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            await TickAsync(token); Require(!w.IsOverlayOpen, id + " closes on Escape");
        }
        var document = w.Shell.ActiveDocumentId!.Value;
        await w.Commands["float"].ExecuteAsync(); await TickAsync(token);
        var floating = w.OwnedWindows.Single()!;
        var floatingMenu = NativeMenu.GetMenu(floating);
        Require(floatingMenu is not null && !ReferenceEquals(root, floatingMenu), "floating native menu is separately owned");
        w.Shell.SetTheme("Obsidian"); await TickAsync(token);
        Require(ReferenceEquals(floatingMenu, NativeMenu.GetMenu(floating)), "floating root survives redraw");
        if (OperatingSystem.IsMacOS()) Require(NativeMenu.GetIsNativeMenuExported(floating), "floating Cocoa menu actually exported");
        w.ReturnFloating(document); await TickAsync(token);
        Require(!w.OwnedWindows.Any(), "floating window returns and closes");
        foreach (var (id, terminal) in original) Require(ReferenceEquals(terminal, w.Shell.Sessions.Find(id)?.Terminal), "terminal identity " + id);
        Require(ReferenceEquals(root, NativeMenu.GetMenu(w)), "native root retained through full scenario");
        var bounds = w.Bounds.Size;
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)bounds.Width, (int)bounds.Height), new Vector(96, 96));
        bitmap.Render(w);
#pragma warning disable CS0618 // Pinned Avalonia 12 image encoding overload.
        bitmap.Save(Path.ChangeExtension(report, ".png"));
#pragma warning restore CS0618
    }

    private static void Require(bool condition, string check)
    {
        if (!condition) throw new InvalidOperationException("Native backend regression: " + check);
        Checks.Add(check);
    }
    private static async Task TickAsync(CancellationToken token)
    {
        // Let the actual platform loop deliver queued exporter/layout work.
        await Task.Delay(90, token);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background, token);
    }
    private static IEnumerable<NativeMenuItem> Flatten(NativeMenu root)
    {
        foreach (var item in root.Items.OfType<NativeMenuItem>())
        {
            yield return item;
            if (item.Menu is { } children) foreach (var child in Flatten(children)) yield return child;
        }
    }
}
