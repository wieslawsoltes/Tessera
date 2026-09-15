using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RoyalTerminal.Terminal;
using Tessera.Core;
using Tessera.Services;
using Xunit;
using F = Tessera.NativeTests.UiRegressionFixture;

namespace Tessera.NativeTests;

public sealed class FullUiRegressionTests
{
    [AvaloniaTheory]
    [InlineData("palette", "A place for every command.")]
    [InlineData("profiles", "Connections & terminal profiles")]
    [InlineData("settings", "Make room for your way of working.")]
    [InlineData("layouts", "A layout for the task at hand.")]
    [InlineData("keybindings", "Keyboard bindings")]
    [InlineData("shaders", "A little atmosphere. Still a terminal.")]
    [InlineData("find", "Find in api-service")]
    [InlineData("broadcast", "One input. Explicit destinations.")]
    [InlineData("diagnostics", "Runtime, in plain sight.")]
    [InlineData("about", "tessera")]
    [InlineData("recover-capture", "Nothing important left behind.")]
    public async Task EveryOverlayOpensThroughItsCommandAndEscapeRestoresFocus(string command, string title)
    {
        await using var f = await F.OpenAsync(); var w = f.Window;
        w.Shell.ActiveSession!.Terminal.Focus();
        var focus = w.FocusManager!.GetFocusedElement();
        var root = NativeMenu.GetMenu(w);
        await w.Commands[command].ExecuteAsync(); await F.PumpAsync();
        Assert.True(w.IsOverlayOpen);
        Assert.Contains(f.Overlay.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == title);
        F.Screenshot(w, "overlay-" + command);
        await f.DismissAsync();
        Assert.Same(focus, w.FocusManager.GetFocusedElement());
        Assert.Same(root, NativeMenu.GetMenu(w));
    }

    [AvaloniaTheory]
    [InlineData("Focus", 1)]
    [InlineData("Two columns", 2)]
    [InlineData("Two rows", 2)]
    [InlineData("Build & monitor", 3)]
    [InlineData("Three columns", 3)]
    [InlineData("Four-way inspection", 4)]
    public async Task LayoutCardsReallyRearrangeAndUndoRedoWithoutRestartingSessions(string preset, int groups)
    {
        await using var f = await F.OpenAsync(); var w = f.Window;
        var before = w.Shell.Active.Root;
        var instances = w.Shell.Sessions.All.ToDictionary(s => s.Id, s => s.Terminal);
        await w.Commands["layouts"].ExecuteAsync(); await F.PumpAsync();
        F.Click(F.Button(f.Overlay, preset)); await F.PumpAsync();
        Assert.False(w.IsOverlayOpen); Assert.Equal(groups, Layout.Groups(w.Shell.Active.Root).Count());
        var arranged = w.Shell.Active.Root;
        await w.Commands["undo"].ExecuteAsync(); await F.PumpAsync(); Assert.Equal(before, w.Shell.Active.Root);
        await w.Commands["redo"].ExecuteAsync(); await F.PumpAsync(); Assert.Equal(arranged, w.Shell.Active.Root);
        Layout.Validate(w.Shell.Active);
        foreach (var (id, terminal) in instances) Assert.Same(terminal, w.Shell.Sessions.Find(id)!.Terminal);
        F.Screenshot(w, "layout-" + preset.Replace(' ', '-'));
    }

    [AvaloniaTheory]
    [InlineData("Obsidian")]
    [InlineData("Porcelain")]
    [InlineData("Blueprint")]
    public async Task ThemeCardsApplyAndResponsiveChromePreservesSessionAndMenuIdentity(string theme)
    {
        await using var f = await F.OpenAsync(); var w = f.Window;
        var root = NativeMenu.GetMenu(w); var terminal = w.Shell.ActiveSession!.Terminal;
        await w.Commands["settings"].ExecuteAsync(); await F.PumpAsync();
        F.Click(F.Button(f.Overlay, theme)); await F.PumpAsync(); Assert.Equal(theme, w.Shell.Data.Preferences.Theme);
        await f.DismissAsync();
        foreach (int width in new[] { 1450, 1050, 840, 1450 })
        {
            w.Width = width; await F.PumpAsync();
            Assert.Equal(width >= 1120, w.FindControl<Border>("Sidebar")!.IsVisible);
            Assert.Same(root, NativeMenu.GetMenu(w)); Assert.Same(terminal, w.Shell.ActiveSession!.Terminal);
        }
        F.Screenshot(w, "theme-" + theme);
    }

    [AvaloniaFact]
    public async Task PreferencesApplyRealFormValuesAndPersistToWorkspaceDocument()
    {
        await using var f = await F.OpenAsync(); var w = f.Window;
        await w.Commands["settings"].ExecuteAsync(); await F.PumpAsync();
        F.Named<TextBox>(f.Overlay, "Terminal font family").Text = "monospace";
        F.Named<NumericUpDown>(f.Overlay, "Size (DIP)").Value = 16;
        F.Named<NumericUpDown>(f.Overlay, "Line height multiplier").Value = 1.3m;
        F.Named<NumericUpDown>(f.Overlay, "History retention (days)").Value = 30;
        F.Named<ComboBox>(f.Overlay, "Cursor shape").SelectedItem = "Bar";
        foreach (string label in new[] { "Compact chrome", "Reduce motion", "Override connection profile fonts", "Persist completed command history locally (opt-in)" })
            Check(f.Overlay, label).IsChecked = true;
        Check(f.Overlay, "Blink cursor").IsChecked = false;
        F.Click(F.Button(f.Overlay, "Apply appearance")); await F.UntilAsync(() => !w.IsOverlayOpen, "Preferences should finish applying."); await F.PumpAsync();
        var prefs = w.Shell.Data.Preferences;
        Assert.Equal(16, prefs.FontSize); Assert.Equal(1.3, prefs.LineHeight); Assert.Equal("Bar", prefs.CursorStyle);
        Assert.True(prefs.Compact); Assert.True(prefs.ReducedMotion); Assert.True(prefs.OverrideProfileFont); Assert.True(prefs.PersistHistory);
        Assert.False(prefs.CursorBlink); Assert.Equal(30, prefs.HistoryRetentionDays);
        Assert.Equal(1.3, w.Shell.ActiveSession!.Terminal.LineHeight);
        Assert.Equal(45, w.FindControl<Grid>("Root")!.RowDefinitions[0].Height.Value);
        string json = System.Text.Json.JsonSerializer.Serialize(w.Shell.Data, WorkspaceStore.Json);
        var restored = System.Text.Json.JsonSerializer.Deserialize<AppDocument>(json, WorkspaceStore.Json)!;
        Assert.Equal(prefs, restored.Preferences);
    }

    [AvaloniaFact]
    public async Task TabCreateRenameDuplicatePinCancelCloseAndCloseExecuteTheRealHandlers()
    {
        await using var f = await F.OpenAsync(); var w = f.Window; int count = w.Shell.Active.Documents.Count;
        await w.Commands["new"].ExecuteAsync(); await F.PumpAsync(); Assert.Equal(count + 1, w.Shell.Active.Documents.Count);
        var id = w.Shell.ActiveDocumentId!.Value; var terminal = w.Shell.ActiveSession!.Terminal;
        var rename = w.Commands["rename"].ExecuteAsync(); await F.PumpAsync();
        Assert.False(rename.IsCompleted);
        f.Overlay.GetVisualDescendants().OfType<TextBox>().Single().Text = "integration console";
        F.Click(F.Button(f.Overlay, "Save")); await rename; await F.PumpAsync();
        Assert.Equal("integration console", w.Shell.Active.Documents[id].Title);
        await w.Commands["pin"].ExecuteAsync(); Assert.True(w.Shell.Active.Documents[id].Pinned);
        var cancelled = w.Commands["close"].ExecuteAsync(); await F.PumpAsync(); await f.DismissAsync(); await cancelled;
        Assert.True(w.Shell.Active.Documents.ContainsKey(id)); Assert.Same(terminal, w.Shell.Sessions.Find(id)!.Terminal);
        await w.Commands["duplicate"].ExecuteAsync(); await F.PumpAsync(); Assert.Equal(count + 2, w.Shell.Active.Documents.Count);
        Assert.NotSame(terminal, w.Shell.ActiveSession!.Terminal);
        await w.Commands["close"].ExecuteAsync(); await F.PumpAsync(); Assert.Equal(count + 1, w.Shell.Active.Documents.Count);
        w.Shell.Select(id); await F.PumpAsync();
        var close = w.Commands["close"].ExecuteAsync(); await F.PumpAsync(); F.Click(F.Button(f.Overlay, "Close terminal"));
        await close; await F.PumpAsync(); Assert.False(w.Shell.Active.Documents.ContainsKey(id)); Assert.Equal(count, w.Shell.Active.Documents.Count);
    }

    [AvaloniaFact]
    public async Task WorkspaceCreateRenameSaveLayoutAndRemoveRespectConfirmation()
    {
        await using var f = await F.OpenAsync(); var w = f.Window; int initial = w.Shell.Data.Workspaces.Length;
        await PromptAsync(f, "new-workspace", "UI integration"); var id = w.Shell.Active.Id;
        Assert.Equal(initial + 1, w.Shell.Data.Workspaces.Length); Assert.Equal("UI integration", w.Shell.Active.Name);
        await PromptAsync(f, "rename-workspace", "Renamed integration"); Assert.Equal(id, w.Shell.Active.Id);
        await w.Commands["new"].ExecuteAsync(); await F.PumpAsync();
        await PromptAsync(f, "save-layout", "Saved integration layout");
        Assert.Contains(w.Shell.Data.Layouts, l => l.Name == "Saved integration layout");
        var remove = w.Commands["remove-workspace"].ExecuteAsync(); await F.PumpAsync(); await f.DismissAsync(); await remove;
        Assert.Equal(id, w.Shell.Active.Id);
        remove = w.Commands["remove-workspace"].ExecuteAsync(); await F.PumpAsync(); F.Click(F.Button(f.Overlay, "Remove workspace")); await remove;
        Assert.Equal(initial, w.Shell.Data.Workspaces.Length); Assert.DoesNotContain(w.Shell.Data.Workspaces, item => item.Id == id);
    }

    [AvaloniaFact]
    public async Task NotesToolRetainsTextAcrossDockingSwitchesAndFocusMode()
    {
        await using var f = await F.OpenAsync(); var w = f.Window;
        await w.Commands["notes"].ExecuteAsync(); await F.PumpAsync();
        var notes = Tool(w).GetVisualDescendants().OfType<TextBox>().Single();
        notes.Text = "Zażółć 🍃\nDeploy only after verification"; await F.PumpAsync();
        Assert.Contains("Deploy", w.Shell.Active.Notes);
        F.Click(F.Button(w, "Dock tools at bottom / right")); await F.PumpAsync(); Assert.True(w.Shell.Data.Preferences.ToolsOnRight);
        await w.Commands["focus"].ExecuteAsync(); await F.PumpAsync(); Assert.False(w.FindControl<Border>("ToolsBorder")!.IsVisible);
        await w.Commands["focus"].ExecuteAsync(); await F.PumpAsync();
        Assert.Equal(w.Shell.Active.Notes, Tool(w).GetVisualDescendants().OfType<TextBox>().Single().Text);
        F.Click(F.Button(w, "Hide tool panel")); await F.PumpAsync(); Assert.False(w.Shell.ToolsVisible);
        await w.Commands["tools"].ExecuteAsync(); await F.PumpAsync(); Assert.True(w.Shell.ToolsVisible);
    }

    [AvaloniaFact]
    public async Task CommandLibraryCreatesEditsAndRemovesReusableCommands()
    {
        await using var f = await F.OpenAsync(); var w = f.Window;
        F.Click(F.Button(w, "Add command")); await F.PumpAsync();
        F.Named<TextBox>(f.Overlay, "Name").Text = "Inspect worktree";
        F.Named<TextBox>(f.Overlay, "Command").Text = "git status --short";
        F.Named<TextBox>(f.Overlay, "Category").Text = "QA";
        F.Click(F.Button(f.Overlay, "Save command")); await F.PumpAsync();
        var snippet = Assert.Single(w.Shell.Data.Snippets, s => s.Name == "Inspect worktree");
        var card = Tool(w).GetVisualDescendants().OfType<Border>().Single(b => b.ContextMenu is not null && b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == snippet.Name));
        var edit = card.ContextMenu!.Items.Cast<MenuItem>().Single(i => Equals(i.Header, "Edit command"));
        edit.Command!.Execute(null); await F.PumpAsync();
        F.Named<TextBox>(f.Overlay, "Name").Text = "Inspect edited";
        F.Click(F.Button(f.Overlay, "Save command")); await F.PumpAsync();
        Assert.Equal("Inspect edited", w.Shell.Data.Snippets.Single(s => s.Id == snippet.Id).Name);
        card = Tool(w).GetVisualDescendants().OfType<Border>().Single(b => b.ContextMenu is not null && b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Inspect edited"));
        card.ContextMenu!.Items.Cast<MenuItem>().Single(i => Equals(i.Header, "Remove command")).Command!.Execute(null);
        await F.PumpAsync(); Assert.DoesNotContain(w.Shell.Data.Snippets, s => s.Id == snippet.Id);
    }

    [AvaloniaFact]
    public async Task HistorySearchAndExplicitClearOperateOnCompletedEntries()
    {
        await using var f = await F.OpenAsync(); var w = f.Window; var now = DateTimeOffset.UtcNow;
        w.Shell.HistoryStore.Add(new(Guid.NewGuid().ToString(), "git status --short", now, now, 0, "local", null, f.DirectoryPath));
        w.Shell.HistoryStore.Add(new(Guid.NewGuid().ToString(), "dotnet test", now, now, 0, "local", null, f.DirectoryPath));
        await w.Commands["history"].ExecuteAsync(); await F.PumpAsync();
        var search = Tool(w).GetVisualDescendants().OfType<TextBox>().Single(); search.Text = "dotnet"; await F.PumpAsync();
        Assert.Contains(Tool(w).GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "dotnet test");
        Assert.DoesNotContain(Tool(w).GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "git status --short");
        var pending = w.Commands["clear-history"].ExecuteAsync(); await F.PumpAsync(); await f.DismissAsync(); await pending;
        Assert.Equal(2, w.Shell.HistoryStore.Search("").Length);
        pending = w.Commands["clear-history"].ExecuteAsync(); await F.PumpAsync(); F.Click(F.Button(f.Overlay, "Clear history")); await pending;
        Assert.Empty(w.Shell.HistoryStore.Search(""));
    }

    [AvaloniaFact]
    public async Task SearchFormTogglesRegexCaseAndWholeWordAndCancelsPendingWorkOnClose()
    {
        await using var f = await F.OpenAsync(); var w = f.Window;
        w.Shell.ActiveSession!.Terminal.WriteOutput(Encoding.UTF8.GetBytes("\r\nRareWord rareword rarewords RAREWORD\r\ncode=731 code=732\r\n"));
        await F.PumpAsync(); await w.Commands["find"].ExecuteAsync(); await F.PumpAsync();
        var input = F.Named<TextBox>(f.Overlay, "Search pattern");
        var list = f.Overlay.GetVisualDescendants().OfType<ListBox>().Single();
        input.Text = "rareword"; Check(f.Overlay, "Match case").IsChecked = true; Check(f.Overlay, "Whole word").IsChecked = true;
        await F.UntilAsync(() => list.ItemCount == 1, "Case and whole-word filters should leave one match.");
        Check(f.Overlay, "Regular expression").IsChecked = true; Check(f.Overlay, "Whole word").IsChecked = false; input.Text = @"code=73\d";
        await F.UntilAsync(() => list.ItemCount == 2, "Regex should match both codes.");
        int first = list.SelectedIndex; F.Click(F.Button(f.Overlay, "Next")); await F.PumpAsync(); Assert.NotEqual(first, list.SelectedIndex);
        F.Click(F.Button(f.Overlay, "Previous")); Assert.Equal(first, list.SelectedIndex);
        input.Text = "["; await F.PumpAsync(300);
        Assert.Empty(list.Items);
        input.Text = "Rare"; await f.DismissAsync(); await F.PumpAsync(300); Assert.False(w.IsOverlayOpen);
    }

    [AvaloniaFact]
    public async Task ClosingWindowCompletesOutstandingConfirmationWithoutRunningItsAction()
    {
        using var dir = new AcceptanceDirectory(); var w = new Tessera.Views.MainWindow(true, dir.Path);
        w.Show(); await w.InitializeAsync(); await F.PumpAsync();
        var id = w.Shell.ActiveDocumentId!.Value; w.Shell.TogglePin(id); await F.PumpAsync();
        var pending = w.Commands["close"].ExecuteAsync(); await F.PumpAsync(); Assert.False(pending.IsCompleted);
        w.CloseForTests(); await pending.WaitAsync(TimeSpan.FromSeconds(2), F.Token);
        Assert.True(w.Shell.Active.Documents.ContainsKey(id)); Assert.False(w.IsVisible);
    }

    [AvaloniaFact]
    public async Task PaletteKeyboardSelectionExecutesExactlyOneCommand()
    {
        await using var f = await F.OpenAsync(); var w = f.Window; int count = w.Shell.Active.Documents.Count;
        w.ShowPalette("New terminal"); await F.PumpAsync();
        var input = f.Overlay.GetVisualDescendants().OfType<TextBox>().Single();
        F.Press(input, Key.Enter); await F.UntilAsync(() => w.Shell.Active.Documents.Count == count + 1, "Palette should create one terminal.");
        Assert.False(w.IsOverlayOpen); await F.PumpAsync(); Assert.Equal(count + 1, w.Shell.Active.Documents.Count);
    }

    private static Control Tool(Tessera.Views.MainWindow w) => w.FindControl<ContentControl>("ToolContent")!;
    private static CheckBox Check(Control root, string label) => Assert.Single(root.GetVisualDescendants().OfType<CheckBox>(), c => Equals(c.Content, label));
    private static async Task PromptAsync(F f, string command, string value)
    {
        var pending = f.Window.Commands[command].ExecuteAsync(); await F.PumpAsync();
        f.Overlay.GetVisualDescendants().OfType<TextBox>().Single().Text = value;
        F.Click(F.Button(f.Overlay, "Save")); await pending; await F.PumpAsync();
    }
}
