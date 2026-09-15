using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tessera.Core;
using Tessera.Services;
using Tessera.Views;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(Tessera.NativeTests.TestBootstrap))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Tessera.NativeTests;

public static class TestBootstrap
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<global::Tessera.App>()
        .WithInterFont().UseSkia().UseHarfBuzz()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class NativeUiTests
{
    private static async Task<MainWindow> OpenAsync(bool design = true)
    {
        var window = new MainWindow(design, Path.Combine(Path.GetTempPath(), "tessera-native-" + Guid.NewGuid()));
        window.Show(); await window.InitializeAsync(); await SettleAsync(); return window;
    }
    private static async Task SettleAsync()
    {
        Dispatcher.UIThread.RunJobs(); await Task.Delay(100); Dispatcher.UIThread.RunJobs();
    }
    private static void Screenshot(MainWindow window, string name)
    {
        var directory = Path.Combine(Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Directory.GetCurrentDirectory(), "artifacts", "screenshots");
        Directory.CreateDirectory(directory);
        using var bitmap = window.CaptureRenderedFrame();
        Assert.NotNull(bitmap);
#pragma warning disable CS0618 // Avalonia preserves this PNG overload across supported versions.
        bitmap!.Save(Path.Combine(directory, name + ".png"));
#pragma warning restore CS0618
    }
    [AvaloniaFact]
    public async Task DesignerCompositionHasStableSessionsAndThreeGroups()
    {
        var window = await OpenAsync();
        try
        {
            Assert.Equal(4, window.Shell.Active.Documents.Count);
            Assert.Equal(3, Layout.Groups(window.Shell.Active.Root).Count());
            var id = window.Shell.ActiveDocumentId!.Value;
            var terminal = window.Shell.ActiveSession!.Terminal;
            var target = Layout.Groups(window.Shell.Active.Root).Last().Id;
            window.Shell.Move(id, target, DockEdge.Center); await SettleAsync();
            Assert.Same(terminal, window.Shell.Sessions.Find(id)!.Terminal);
            Assert.Equal(4, window.Shell.Sessions.All.Count);
            Layout.Validate(window.Shell.Active);
        }
        finally { window.CloseForTests(); }
    }
    [AvaloniaTheory]
    [InlineData("Obsidian")][InlineData("Porcelain")][InlineData("Blueprint")]
    public async Task CaptureNativeDesignerTheme(string theme)
    {
        var window = await OpenAsync();
        try
        {
            window.Shell.SetTheme(theme); await SettleAsync();
            Assert.Equal(55, window.FindControl<Grid>("Root")!.RowDefinitions[0].ActualHeight, 0);
            Assert.Equal(theme, ThemeManager.Current);
            Screenshot(window, theme.ToLowerInvariant());
        }
        finally { window.CloseForTests(); }
    }
    [AvaloniaFact]
    public async Task ToolbarExecutesEachCommandExactlyOnce()
    {
        var window = await OpenAsync();
        try
        {
            var before = window.Shell.Active.Documents.Count;
            var button = window.FindControl<StackPanel>("WorkspaceActions")!.Children.OfType<Button>().Last();
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await SettleAsync();
            Assert.Equal(before + 1, window.Shell.Active.Documents.Count);
        }
        finally { window.CloseForTests(); }
    }
    [AvaloniaFact]
    public async Task PaletteAndSettingsAreNativeInteractiveControls()
    {
        var window = await OpenAsync();
        try
        {
            window.ShowPalette("split"); await SettleAsync(); Assert.True(window.IsOverlayOpen);
            Assert.NotEmpty(window.GetVisualDescendants().OfType<ListBox>()); Screenshot(window, "command-palette");
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Assert.False(window.IsOverlayOpen);
            window.ShowSettings(); await SettleAsync(); Assert.NotEmpty(window.GetVisualDescendants().OfType<CheckBox>()); Screenshot(window, "preferences");
            window.ShowProfiles(); await SettleAsync();
            Assert.Contains(window.GetVisualDescendants(), control => control is RoyalTerminal.Avalonia.Settings.TerminalSettingsPanel);
            Screenshot(window, "connection-profiles");
            window.ShowLayouts(); await SettleAsync(); Screenshot(window, "layout-chooser");
        }
        finally { window.CloseForTests(); }
    }
    [AvaloniaFact]
    public async Task NotesAndThemeSurviveStateSerialization()
    {
        var window = await OpenAsync();
        try
        {
            window.Shell.SetNotes("Deployment checks\n• verify rollback"); window.Shell.SetTheme("Blueprint");
            var json = System.Text.Json.JsonSerializer.Serialize(window.Shell.Data, WorkspaceStore.Json);
            var data = System.Text.Json.JsonSerializer.Deserialize<AppDocument>(json, WorkspaceStore.Json)!;
            WorkspaceStore.Validate(data); Assert.Contains("Deployment checks", data.Workspaces.Single(w => w.Id == data.ActiveWorkspace).Notes);
            Assert.Equal("Blueprint", data.Preferences.Theme);
        }
        finally { window.CloseForTests(); }
    }
    [AvaloniaFact]
    public async Task ProductionCannotAcceptProgrammaticInputOrBroadcast()
    {
        var window = await OpenAsync();
        try
        {
            var document = await window.Shell.NewTerminalAsync("production", start: false);
            var session = window.Shell.GetSession(document);
            Assert.True(session.Locked); Assert.True(session.IsProduction);
            Assert.Throws<InvalidOperationException>(() => session.Send("echo forbidden\r"));
            session.BroadcastTarget = true;
            Assert.Throws<InvalidOperationException>(() => window.Shell.SetBroadcast(true));
            await SettleAsync(); Screenshot(window, "production-read-only");
        }
        finally { window.CloseForTests(); }
    }
    [AvaloniaFact]
    public async Task ReplayIdentityCannotBecomeAnAutostartedPty()
    {
        var window = await OpenAsync();
        try
        {
            var document = await window.Shell.NewTerminalAsync(start: false);
            window.Shell.MarkReplay(document.Id);
            var session = window.Shell.GetSession(window.Shell.Active.Documents[document.Id]);
            await session.StartAsync();
            Assert.True(session.IsReplay); Assert.True(session.Locked); Assert.False(session.IsRunning);
            var json = System.Text.Json.JsonSerializer.Serialize(window.Shell.Data, WorkspaceStore.Json);
            var loaded = System.Text.Json.JsonSerializer.Deserialize<AppDocument>(json, WorkspaceStore.Json)!;
            Assert.True(loaded.Workspaces.Single(w => w.Id == loaded.ActiveWorkspace).Documents[document.Id].IsReplay);
        }
        finally { window.CloseForTests(); }
    }
    [AvaloniaFact]
    public async Task FocusModeAndToolDockingNeverDisposeTerminals()
    {
        var window = await OpenAsync();
        try
        {
            var sessions = window.Shell.Sessions.All.Select(s => s.Terminal).ToArray();
            window.Shell.ToggleFocus(); await SettleAsync(); Screenshot(window, "focus-mode");
            window.Shell.ToggleFocus(); window.Shell.SetPreferences(window.Shell.Data.Preferences with { ToolsOnRight = true, ToolSize = 340 }); await SettleAsync();
            Assert.Equal(sessions, window.Shell.Sessions.All.Select(s => s.Terminal)); Screenshot(window, "right-docked-tools");
            window.Width = 840; await SettleAsync(); Assert.False(window.FindControl<Border>("Sidebar")!.IsVisible); Screenshot(window, "compact-workspace");
        }
        finally { window.CloseForTests(); }
    }
    [AvaloniaFact]
    public async Task LocalTerminalExecutesAnActualPtyCommand()
    {
        using var directory = new AcceptanceDirectory();
        var token = TestContext.Current.CancellationToken;
        var window = await PtyTestFixture.OpenAsync(directory.Path, token);
        try
        {
            await PtyTestFixture.AssertCommandAsync(window.Shell.ActiveSession!, "TESSERA_PTY_", token);
        }
        finally
        {
            try { await window.Shell.PrepareShutdownAsync(); }
            finally { window.CloseForTests(); }
        }
    }
}
