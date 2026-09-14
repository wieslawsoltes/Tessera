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

[assembly: AvaloniaTestApplication(typeof(Tessera.App.Tests.TestBootstrap))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Tessera.App.Tests;

public static class TestBootstrap
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<global::Tessera.App>()
        .WithInterFont().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
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
        Dispatcher.UIThread.RunJobs(); await Task.Delay(80); Dispatcher.UIThread.RunJobs();
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
            var root = window.FindControl<Grid>("Root")!;
            Assert.Equal(55, root.RowDefinitions[0].ActualHeight, 0);
            Assert.Equal(theme, ThemeManager.Current);
            var directory = Path.Combine(Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Directory.GetCurrentDirectory(), "artifacts", "screenshots");
            Directory.CreateDirectory(directory);
            using var bitmap = window.CaptureRenderedFrame();
            Assert.NotNull(bitmap); Assert.True(bitmap!.PixelSize.Width >= 1200);
            bitmap.Save(Path.Combine(directory, theme.ToLowerInvariant() + ".png"));
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
            var actions = window.FindControl<StackPanel>("WorkspaceActions")!;
            var button = actions.Children.OfType<Button>().Last();
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
            Assert.NotEmpty(window.GetVisualDescendants().OfType<ListBox>());
            window.KeyPress(Key.Escape, RawInputModifiers.None); window.KeyRelease(Key.Escape, RawInputModifiers.None);
            Assert.False(window.IsOverlayOpen);
            window.ShowSettings(); await SettleAsync(); Assert.NotEmpty(window.GetVisualDescendants().OfType<CheckBox>());
            window.ShowProfiles(); await SettleAsync();
            Assert.Contains(window.GetVisualDescendants(), control => control is RoyalTerminal.Avalonia.Settings.TerminalSettingsPanel);
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
            window.Shell.ToggleFocus(); await SettleAsync();
            window.Shell.ToggleFocus(); window.Shell.SetPreferences(window.Shell.Data.Preferences with { ToolsOnRight = true, ToolSize = 340 }); await SettleAsync();
            Assert.Equal(sessions, window.Shell.Sessions.All.Select(s => s.Terminal));
            window.Width = 840; await SettleAsync(); Assert.False(window.FindControl<Border>("Sidebar")!.IsVisible);
        }
        finally { window.CloseForTests(); }
    }
    [AvaloniaFact]
    public async Task LocalTerminalRunsAnActualPtyProcess()
    {
        var window = await OpenAsync(design: false);
        try
        {
            var session = window.Shell.ActiveSession!;
            Assert.True(session.IsRunning, session.Error ?? session.State);
            var marker = "TESSERA_PTY_" + Guid.NewGuid().ToString("N");
            session.Send("echo " + marker + "\r");
            for(var attempt = 0; attempt < 60 && !session.OutputSnapshot().Contains(marker, StringComparison.Ordinal); attempt++)
                await Task.Delay(100);
            Assert.Contains(marker, session.OutputSnapshot());
        }
        finally { window.CloseForTests(); }
    }
}
