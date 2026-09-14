using System.Collections.Concurrent;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tessera.Views;
using Xunit;

namespace Tessera.NativeTests;

public sealed class NativeInteractionRegressionTests
{
    private static async Task<MainWindow> OpenAsync(bool design = true)
    {
        var window = new MainWindow(design, Path.Combine(Path.GetTempPath(), "tessera-interaction-" + Guid.NewGuid()));
        window.Show(); await window.InitializeAsync(); await SettleAsync(); return window;
    }
    private static async Task SettleAsync() { Dispatcher.UIThread.RunJobs(); await Task.Delay(80); Dispatcher.UIThread.RunJobs(); }

    [AvaloniaFact]
    public async Task PaletteCanFilterToEmptyAndRecycleItemsRepeatedly()
    {
        var window = await OpenAsync();
        try
        {
            window.ShowPalette(); await SettleAsync();
            var overlay = window.FindControl<ContentControl>("OverlayContent")!;
            var input = overlay.GetVisualDescendants().OfType<TextBox>().Single();
            foreach(var query in new[] { "s", "sh", "shader", "no-such-command-94398", "", "split", "layout", "" })
            {
                input.Text = query; await SettleAsync(); Assert.True(window.IsOverlayOpen);
            }
            Assert.True(overlay.GetVisualDescendants().OfType<ListBox>().Single().ItemCount > 20);
        }
        finally { window.CloseForTests(); }
    }

    [AvaloniaFact]
    public async Task FloatingAndReturningPreservesTheTerminalInstance()
    {
        var window = await OpenAsync();
        try
        {
            var id = window.Shell.ActiveDocumentId!.Value;
            var terminal = window.Shell.ActiveSession!.Terminal;
            await window.Commands["float"].ExecuteAsync(); await SettleAsync();
            Assert.True(window.IsFloating(id)); Assert.Same(terminal, window.Shell.Sessions.Find(id)!.Terminal);
            window.ReturnFloating(id); await SettleAsync();
            Assert.False(window.IsFloating(id)); Assert.Same(terminal, window.Shell.Sessions.Find(id)!.Terminal);
        }
        finally { window.CloseForTests(); }
    }

    [AvaloniaFact]
    public async Task LockedTerminalRejectsNativeKeyAndTextEvents()
    {
        var window = await OpenAsync(design: false);
        try
        {
            var session = window.Shell.ActiveSession!;
            Assert.True(session.IsRunning, session.Error);
            session.Locked = true; session.Terminal.Focus(); await SettleAsync();
            var sent = new ConcurrentQueue<byte[]>();
            session.Terminal.TerminalSessionService.InputSent += (_, e) => sent.Enqueue(e.Data.ToArray());
            window.KeyPress(Key.A, RawInputModifiers.None, PhysicalKey.A, "a");
            session.Terminal.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "LOCKED_INPUT_MUST_NOT_REACH_PTY" });
            window.KeyRelease(Key.A, RawInputModifiers.None, PhysicalKey.A, "a");
            window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
            window.KeyRelease(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
            await SettleAsync(); Assert.Empty(sent);
        }
        finally { window.CloseForTests(); }
    }

    [AvaloniaFact]
    public async Task NativeShaderEditorCompilesAndAppliesItsPreset()
    {
        var window = await OpenAsync();
        try
        {
            await window.Commands["shaders"].ExecuteAsync(); await SettleAsync();
            var overlay = window.FindControl<ContentControl>("OverlayContent")!;
            var preset = overlay.GetVisualDescendants().OfType<ComboBox>().First();
            preset.SelectedIndex = 1; await SettleAsync();
            var apply = overlay.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Validate & apply");
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await SettleAsync();
            var sources = window.Shell.ActiveSession!.Terminal.ShaderSources;
            Assert.NotNull(sources); Assert.NotEmpty(sources!);
        }
        finally { window.CloseForTests(); }
    }
}
