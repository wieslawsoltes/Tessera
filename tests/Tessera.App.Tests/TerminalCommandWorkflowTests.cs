using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using Tessera.Core;
using Xunit;
using F = Tessera.NativeTests.UiRegressionFixture;

namespace Tessera.NativeTests;

public sealed class TerminalCommandWorkflowTests
{
    [AvaloniaTheory]
    [InlineData("split-right")]
    [InlineData("split-down")]
    public async Task SplitCommandsCreateIndependentSlotsAndKeepExistingTerminals(string command)
    {
        await using var f = await F.OpenAsync(); var w = f.Window;
        var before = w.Shell.Sessions.All.ToDictionary(s => s.Id, s => s.Terminal);
        int groups = Layout.Groups(w.Shell.Active.Root).Count();
        await w.Commands[command].ExecuteAsync(); await F.PumpAsync();
        Assert.Equal(groups + 1, Layout.Groups(w.Shell.Active.Root).Count());
        Assert.Equal(before.Count + 1, w.Shell.Active.Documents.Count);
        foreach (var (id, terminal) in before) Assert.Same(terminal, w.Shell.Sessions.Find(id)!.Terminal);
        Layout.Validate(w.Shell.Active);
    }

    [AvaloniaFact]
    public async Task TabAndPaneNavigationResizeAndZoomExecuteTheRegisteredCommands()
    {
        await using var f = await F.OpenAsync(); var w = f.Window;
        var original = w.Shell.ActiveDocumentId;
        await w.Commands["next-tab"].ExecuteAsync(); Assert.NotEqual(original, w.Shell.ActiveDocumentId);
        await w.Commands["previous-tab"].ExecuteAsync(); Assert.Equal(original, w.Shell.ActiveDocumentId);
        await w.Commands["next-pane"].ExecuteAsync(); Assert.NotEqual(original, w.Shell.ActiveDocumentId);
        await w.Commands["previous-pane"].ExecuteAsync(); Assert.Equal(original, w.Shell.ActiveDocumentId); await F.PumpAsync();
        await w.Commands["focus-right"].ExecuteAsync(); Assert.NotEqual(original, w.Shell.ActiveDocumentId); await F.PumpAsync();
        await w.Commands["focus-left"].ExecuteAsync(); Assert.Equal(original, w.Shell.ActiveDocumentId); await F.PumpAsync();
        var root = Assert.IsType<DockSplit>(w.Shell.Active.Root);
        await w.Commands["grow-pane"].ExecuteAsync(); Assert.True(Assert.IsType<DockSplit>(w.Shell.Active.Root).Ratio > root.Ratio);
        await w.Commands["shrink-pane"].ExecuteAsync(); Assert.Equal(root.Ratio, Assert.IsType<DockSplit>(w.Shell.Active.Root).Ratio, 6);
        double font = w.Shell.Data.Preferences.FontSize;
        await w.Commands["zoom-in"].ExecuteAsync(); Assert.Equal(font + 1, w.Shell.Data.Preferences.FontSize);
        await w.Commands["zoom-out"].ExecuteAsync(); Assert.Equal(font, w.Shell.Data.Preferences.FontSize);
        Assert.True(w.Shell.Data.Preferences.OverrideProfileFont);
    }

    [AvaloniaFact]
    public async Task MenuClipboardCommandsCopyActualOutputAndRejectLockedPaste()
    {
        await using var f = await F.OpenAsync(); var w = f.Window; var session = w.Shell.ActiveSession!;
        session.Terminal.WriteOutput(Encoding.UTF8.GetBytes("\r\nCOPY_REGRESSION_zażółć\r\n")); await F.PumpAsync();
        await w.Commands["select-all"].ExecuteAsync(); await w.Commands["copy"].ExecuteAsync();
        Assert.Contains("COPY_REGRESSION_zażółć", await w.Clipboard!.TryGetTextAsync());
        session.Locked = true; await w.Clipboard!.SetTextAsync("never-send-this");
        await Assert.ThrowsAsync<InvalidOperationException>(() => w.Commands["paste"].ExecuteAsync());
        var menu = NativeMenu.GetMenu(w);
        await w.Commands["clear"].ExecuteAsync(); await F.PumpAsync(); Assert.Same(menu, NativeMenu.GetMenu(w));
    }

    [AvaloniaFact]
    public async Task CaptureAndShellIntegrationCancelWithoutSendingInputOrCreatingRecoveryKeys()
    {
        await using var f = await F.OpenAsync(); var w = f.Window; var session = w.Shell.ActiveSession!;
        int sent = 0; session.Terminal.TerminalSessionService.InputSent += (_, _) => sent++;
        var capture = w.Commands["capture"].ExecuteAsync(); await F.PumpAsync();
        Assert.True(w.IsOverlayOpen); F.Click(F.Button(f.Overlay, "Cancel")); await capture;
        Assert.False(session.Capture.IsCaptureActive); Assert.False(File.Exists(Path.Combine(f.DirectoryPath, "recovery-key.json")));
        await w.Commands["shell-integration"].ExecuteAsync(); await F.PumpAsync();
        Assert.Contains(f.Overlay.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("Connect an unlocked terminal") == true);
        Assert.Equal(0, sent); await f.DismissAsync();
    }

    [AvaloniaFact]
    public async Task LiveQuitCancellationKeepsFloatingPtyAndRepeatedQuitDoesNotReplaceThePrompt()
    {
        using var directory = new AcceptanceDirectory();
        var w = await PtyTestFixture.OpenAsync(directory.Path, F.Token);
        try
        {
            var session = w.Shell.ActiveSession!;
            await w.Commands["float"].ExecuteAsync(); await F.PumpAsync();
            var floating = Assert.Single(w.OwnedWindows); var menu = NativeMenu.GetMenu(w);
            await w.Commands["quit"].ExecuteAsync(); await F.PumpAsync();
            var overlay = w.FindControl<ContentControl>("OverlayContent")!;
            var prompt = overlay.Content; Assert.True(w.IsOverlayOpen);
            await w.Commands["quit"].ExecuteAsync(); await F.PumpAsync(); Assert.Same(prompt, overlay.Content);
            F.Click(F.Button(overlay, "Cancel")); await F.PumpAsync();
            Assert.True(w.IsVisible); Assert.Same(floating, Assert.Single(w.OwnedWindows)); Assert.True(session.IsRunning);
            await w.Commands["quit"].ExecuteAsync(); await F.PumpAsync();
            F.Click(F.Button(overlay, "Close application"));
            await F.UntilAsync(() => !w.IsVisible, "Confirmed quit must drain the live PTY and close all windows.");
            Assert.False(session.IsRunning); Assert.Equal("Disposed", session.State);
            Assert.Empty(w.Shell.Sessions.All); Assert.Empty(w.OwnedWindows); Assert.Same(menu, NativeMenu.GetMenu(w));
        }
        finally { w.CloseForTests(); }
    }

    [AvaloniaFact]
    public async Task QuitCommandClosesOwnedWindowsAndDisposesSessionsWithoutChangingMenuIdentity()
    {
        await using var f = await F.OpenAsync(); var w = f.Window;
        await w.Commands["float"].ExecuteAsync(); await F.PumpAsync();
        var menu = NativeMenu.GetMenu(w); var sessions = w.Shell.Sessions.All.ToArray();
        await w.Commands["quit"].ExecuteAsync(); await F.UntilAsync(() => !w.IsVisible, "Quit completes shutdown.");
        Assert.Empty(w.OwnedWindows); Assert.Same(menu, NativeMenu.GetMenu(w));
        Assert.All(sessions, s => Assert.Equal("Disposed", s.State));
    }
}
