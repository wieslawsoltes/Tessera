using Avalonia.VisualTree;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Tessera.Core;
using Tessera.Services;
using Tessera.Views;
using Xunit;

namespace Tessera.NativeTests;

public sealed class MenuLifecycleRegressionTests
{
    private static async Task SettleAsync()
    {
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(40, TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task NativeRootAndEveryMenuItemSurviveShellRedraws()
    {
        using var directory = new AcceptanceDirectory();
        var window = new MainWindow(true, directory.Path);
        try
        {
            window.Show(); await window.InitializeAsync(); await SettleAsync();
            var native = Assert.IsType<NativeMenu>(NativeMenu.GetMenu(window));
            var nativeItems = Flatten(native).ToArray();
            var managed = window.FindControl<Menu>("MainMenu")!.ItemsSource;
            for (int iteration = 0; iteration < 12; iteration++)
            {
                window.Shell.SetTheme(iteration % 2 == 0 ? "Porcelain" : "Obsidian");
                window.Shell.ToggleFocus();
                window.Shell.SetTool(iteration % 2 == 0 ? "Notes" : "Commands");
                await SettleAsync();
                Assert.Same(native, NativeMenu.GetMenu(window));
                Assert.Same(managed, window.FindControl<Menu>("MainMenu")!.ItemsSource);
                Assert.Equal(nativeItems.Length, Flatten(native).Count());
                foreach (var (before, after) in nativeItems.Zip(Flatten(native))) Assert.Same(before, after);
            }
        }
        finally { window.CloseForTests(); await SettleAsync(); }
    }

    [AvaloniaFact]
    public async Task FirstExportIsNeverReassignedDuringInitializationOrBindingEdits()
    {
        using var directory = new AcceptanceDirectory();
        var window = new MainWindow(true, directory.Path);
        int assignments = 0;
        var original = Assert.IsType<NativeMenu>(NativeMenu.GetMenu(window));
        window.PropertyChanged += (_, change) => { if (change.Property == NativeMenu.MenuProperty) assignments++; };
        try
        {
            window.Show(); await window.InitializeAsync(); await SettleAsync();
            foreach (string gesture in new[] { "Ctrl+Alt+Q", "Ctrl+Alt+K,Ctrl+Alt+S", "", "not-a-real-key" })
            {
                window.Shell.Bind("theme", gesture); await SettleAsync();
                Assert.Same(original, NativeMenu.GetMenu(window));
            }
            Assert.Equal(0, assignments);
        }
        finally { window.CloseForTests(); await SettleAsync(); }
        Assert.Equal(0, assignments);
    }

    [AvaloniaFact]
    public async Task EveryCommandIsExposedExactlyOnceInBothMenus()
    {
        await using var fixture = await UiRegressionFixture.OpenAsync(); var w = fixture.Window;
        var native = Flatten(NativeMenu.GetMenu(w)!).Where(i => i.Menu is null).ToArray();
        var managed = w.FindControl<Menu>("MainMenu")!.Items.Cast<MenuItem>()
            .SelectMany(i => i.Items.Cast<MenuItem>()).ToArray();
        Assert.Equal(new[] { "File", "Edit", "View", "Session", "Tools", "Help" }, NativeMenu.GetMenu(w)!.Items.Cast<NativeMenuItem>().Select(i => i.Header));
        Assert.Equal(w.Commands.All.Count, native.Length); Assert.Equal(w.Commands.All.Count, managed.Length);
        foreach (var command in w.Commands.All)
        {
            var n = Assert.Single(native, i => ReferenceEquals(i.Command, command));
            var m = Assert.Single(managed, i => ReferenceEquals(i.Command, command));
            Assert.Equal(command.Title, n.Header); Assert.Equal(command.Title, m.Header);
        }
    }

    [AvaloniaFact]
    public async Task BindingEditorChangesExistingNativeAndManagedItemsWithoutReplacingRoots()
    {
        await using var f = await UiRegressionFixture.OpenAsync(); var w = f.Window;
        var root = NativeMenu.GetMenu(w)!;
        var command = w.Commands["theme"];
        var native = Flatten(root).Single(i => ReferenceEquals(i.Command, command));
        var managed = w.FindControl<Menu>("MainMenu")!.Items.Cast<MenuItem>().SelectMany(i => i.Items.Cast<MenuItem>()).Single(i => ReferenceEquals(i.Command, command));
        await w.Commands["keybindings"].ExecuteAsync(); await SettleAsync();
        var filter = f.Overlay.GetVisualDescendants().OfType<TextBox>().Single(i => i.PlaceholderText == "Filter commands or shortcuts");
        filter.Text = command.Title; await SettleAsync();
        var input = f.Overlay.GetVisualDescendants().OfType<TextBox>().Single(i => i != filter);
        foreach (string gesture in new[] { "Ctrl+Alt+Q", "Ctrl+Alt+K,Ctrl+Alt+S", "" })
        {
            input.Text = gesture;
            UiRegressionFixture.Click(UiRegressionFixture.Button(f.Overlay, "Save shortcut")); await SettleAsync();
            Assert.Equal(gesture, w.Shell.Data.Bindings[command.Id]); Assert.Same(root, NativeMenu.GetMenu(w));
            Assert.Same(native, Flatten(root).Single(i => ReferenceEquals(i.Command, command)));
            var expected = gesture.Length == 0 || gesture.Contains(',') ? null : KeyGesture.Parse(gesture);
            Assert.Equal(expected, native.Gesture); Assert.Equal(expected, managed.InputGesture);
        }
    }

    [AvaloniaFact]
    public async Task NativeExporterCallbacksUseTheSharedCommandAndCurrentEnabledState()
    {
        await using var f = await UiRegressionFixture.OpenAsync(); var w = f.Window;
        var root = NativeMenu.GetMenu(w)!;
        var undo = Flatten(root).Single(i => ReferenceEquals(i.Command, w.Commands["undo"]));
        Assert.False(undo.IsEnabled);
        var before = w.Shell.Active.Root;
        w.Shell.Arrange("Two columns"); await SettleAsync();
        // The exporter bridge method is internal in Avalonia 12. Exercise the
        // real managed callback without claiming a Cocoa exporter ran headlessly.
        typeof(INativeMenuExporterEventsImplBridge).GetMethod("RaiseNeedsUpdate",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!.Invoke(root, null);
        Assert.True(undo.IsEnabled);
        ((INativeMenuItemExporterEventsImplBridge)undo).RaiseClicked(); await SettleAsync();
        Assert.Equal(before, w.Shell.Active.Root); Assert.False(undo.IsEnabled);
        Assert.Same(root, NativeMenu.GetMenu(w));
    }

    [AvaloniaFact]
    public async Task FloatingWindowsHaveIndependentStableRootsAndCanBeReopened()
    {
        await using var f = await UiRegressionFixture.OpenAsync(); var w = f.Window;
        var main = NativeMenu.GetMenu(w)!;
        var document = w.Shell.ActiveDocumentId!.Value;
        await w.Commands["float"].ExecuteAsync(); await SettleAsync();
        var floating = Assert.Single(w.OwnedWindows)!;
        var root = Assert.IsType<NativeMenu>(NativeMenu.GetMenu(floating));
        Assert.NotSame(main, root);
        foreach (var item in Flatten(root)) Assert.DoesNotContain(item, Flatten(main));
        for (int i = 0; i < 4; i++) { w.Shell.ToggleFocus(); await SettleAsync(); Assert.Same(root, NativeMenu.GetMenu(floating)); }
        w.Shell.Bind("theme", "Ctrl+Alt+Q"); await SettleAsync();
        Assert.Equal(KeyGesture.Parse("Ctrl+Alt+Q"), Flatten(root).Single(i => ReferenceEquals(i.Command, w.Commands["theme"])).Gesture);
        w.ReturnFloating(document); await SettleAsync(); Assert.Empty(w.OwnedWindows);
        await w.Commands["float"].ExecuteAsync(); await SettleAsync();
        Assert.NotSame(root, NativeMenu.GetMenu(Assert.Single(w.OwnedWindows)!));
        Assert.Same(main, NativeMenu.GetMenu(w));
    }

    [AvaloniaFact]
    public async Task SeparateMainWindowsNeverShareNativeMenuOwnership()
    {
        await using var a = await UiRegressionFixture.OpenAsync();
        await using var b = await UiRegressionFixture.OpenAsync();
        var ar = NativeMenu.GetMenu(a.Window)!; var br = NativeMenu.GetMenu(b.Window)!;
        Assert.NotSame(ar, br);
        a.Window.Shell.SetTheme("Blueprint"); b.Window.Shell.ToggleSidebar(); await SettleAsync();
        Assert.Same(ar, NativeMenu.GetMenu(a.Window)); Assert.Same(br, NativeMenu.GetMenu(b.Window));
    }

    [AvaloniaFact]
    public async Task ShellRedrawDoesNotCancelAnUnchangedKeyboardChord()
    {
        await using var f = await UiRegressionFixture.OpenAsync(); var w = f.Window;
        w.Shell.Bind("theme", "Ctrl+Alt+K,Ctrl+Alt+T"); await SettleAsync();
        string before = w.Shell.Data.Preferences.Theme;
        Assert.True(w.Commands.Handle(new KeyEventArgs { Key = Key.K, KeyModifiers = KeyModifiers.Control | KeyModifiers.Alt }));
        w.Shell.ToggleSidebar(); Dispatcher.UIThread.RunJobs();
        Assert.True(w.Commands.Handle(new KeyEventArgs { Key = Key.T, KeyModifiers = KeyModifiers.Control | KeyModifiers.Alt }));
        Assert.NotEqual(before, w.Shell.Data.Preferences.Theme);
    }

    internal static IEnumerable<NativeMenuItem> Flatten(NativeMenu menu)
    {
        foreach (var item in menu.Items.OfType<NativeMenuItem>())
        {
            yield return item;
            if (item.Menu is { } children)
                foreach (var child in Flatten(children)) yield return child;
        }
    }
}
