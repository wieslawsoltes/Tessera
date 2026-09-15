using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using RoyalTerminal.Terminal;
using Tessera.Core;
using Xunit;
using F = Tessera.NativeTests.UiRegressionFixture;

namespace Tessera.NativeTests;

public sealed class FileWorkflowRegressionTests
{
    [AvaloniaFact]
    public async Task ExportOutputWritesRealTextAndCancellationDoesNotMutateWorkspace()
    {
        await using var f = await F.OpenAsync(); var w = f.Window;
        string file = Path.Combine(f.DirectoryPath, "terminal.txt");
        w.Shell.ActiveSession!.Terminal.WriteOutput(Encoding.UTF8.GetBytes("\r\nUI_EXPORT_zażółć\r\n"));
        await F.PumpAsync();
        f.Files.Saves.Enqueue(file); await w.Commands["export"].ExecuteAsync();
        Assert.Contains("UI_EXPORT_zażółć", await File.ReadAllTextAsync(file, F.Token));
        string before = await File.ReadAllTextAsync(file, F.Token);
        var root = w.Shell.Active.Root; f.Files.Saves.Enqueue(null); await w.Commands["export"].ExecuteAsync();
        Assert.Equal(before, await File.ReadAllTextAsync(file, F.Token)); Assert.Same(root, w.Shell.Active.Root);
        Assert.Equal(2, f.Files.Requests.Count);
    }

    [AvaloniaFact]
    public async Task FileBrowserOpensEditsAndSavesWithConfirmation()
    {
        await using var f = await F.OpenAsync(); var w = f.Window;
        string path = Path.Combine(f.DirectoryPath, "notes.txt");
        await File.WriteAllTextAsync(path, "Original", F.Token);
        f.Files.Folders.Enqueue(f.DirectoryPath); await w.Commands["files"].ExecuteAsync(); await F.PumpAsync();
        F.Click(F.Button(Tool(w), "Choose a local folder")); await F.PumpAsync();
        F.Click(F.Button(Tool(w), "notes.txt")); await F.UntilAsync(() => w.IsOverlayOpen, "Local editor must open."); await F.PumpAsync();
        f.Overlay.GetVisualDescendants().OfType<TextBox>().Single().Text = "Updated zażółć 🍃";
        F.Click(F.Button(f.Overlay, "Save file")); await F.PumpAsync();
        Assert.Equal("Original", await File.ReadAllTextAsync(path, F.Token));
        Assert.Contains(f.Overlay.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Write changes to disk?");
        F.Click(F.Button(f.Overlay, "Save file"));
        await F.UntilAsync(() => w.Shell.Status.StartsWith("Saved ", StringComparison.Ordinal), "Confirmed save should finish.");
        Assert.Equal("Updated zażółć 🍃", await File.ReadAllTextAsync(path, F.Token));
        Assert.Empty(Directory.GetFiles(f.DirectoryPath, "*.tmp"));
    }

    [AvaloniaFact]
    public async Task FileEditorRejectsExternalChangesWithoutOverwritingThem()
    {
        await using var f = await F.OpenAsync(); var w = f.Window;
        string path = Path.Combine(f.DirectoryPath, "conflict.txt");
        await File.WriteAllTextAsync(path, "Original", F.Token);
        f.Files.Folders.Enqueue(f.DirectoryPath); await w.Commands["files"].ExecuteAsync(); await F.PumpAsync();
        F.Click(F.Button(Tool(w), "Choose a local folder")); await F.PumpAsync();
        F.Click(F.Button(Tool(w), "conflict.txt")); await F.UntilAsync(() => w.IsOverlayOpen, "Local editor must open."); await F.PumpAsync();
        f.Overlay.GetVisualDescendants().OfType<TextBox>().Single().Text = "Unsafe overwrite";
        await File.WriteAllTextAsync(path, "External update", F.Token); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        F.Click(F.Button(f.Overlay, "Save file")); await F.PumpAsync();
        Assert.Contains(f.Overlay.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("changed outside Tessera") == true);
        Assert.Equal("External update", await File.ReadAllTextAsync(path, F.Token));
    }

    [AvaloniaFact]
    public async Task ConfirmationCannotOverwriteAFileChangedWhileThePromptWasOpen()
    {
        await using var f = await F.OpenAsync(); var w = f.Window;
        string path = Path.Combine(f.DirectoryPath, "prompt-race.txt");
        await File.WriteAllTextAsync(path, "Original bytes", F.Token);
        f.Files.Folders.Enqueue(f.DirectoryPath); await w.Commands["files"].ExecuteAsync(); await F.PumpAsync();
        F.Click(F.Button(Tool(w), "Choose a local folder")); await F.PumpAsync();
        F.Click(F.Button(Tool(w), "prompt-race.txt")); await F.UntilAsync(() => w.IsOverlayOpen, "Editor opens."); await F.PumpAsync();
        f.Overlay.GetVisualDescendants().OfType<TextBox>().Single().Text = "Unsafe overwrite";
        F.Click(F.Button(f.Overlay, "Save file")); await F.PumpAsync();
        // Same-size mutation with restored timestamp defeats a metadata-only check.
        var timestamp = File.GetLastWriteTimeUtc(path);
        await File.WriteAllTextAsync(path, "External bytes", F.Token); File.SetLastWriteTimeUtc(path, timestamp);
        F.Click(F.Button(f.Overlay, "Save file"));
        await F.UntilAsync(() => w.IsOverlayOpen, "A stale confirmation must surface a conflict."); await F.PumpAsync();
        Assert.Contains(f.Overlay.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("changed outside Tessera") == true);
        Assert.Equal("External bytes", await File.ReadAllTextAsync(path, F.Token));
        Assert.Empty(Directory.GetFiles(f.DirectoryPath, "*.tmp"));
    }

    [AvaloniaTheory]
    [InlineData("binary.dat", false)]
    [InlineData("large.txt", true)]
    public async Task InlineEditorRejectsBinaryAndOversizeFiles(string name, bool large)
    {
        await using var f = await F.OpenAsync(); var w = f.Window;
        string path = Path.Combine(f.DirectoryPath, name);
        await File.WriteAllBytesAsync(path, large ? new byte[2 * 1024 * 1024 + 1] : new byte[] { 65, 0, 66 }, F.Token);
        f.Files.Folders.Enqueue(f.DirectoryPath); await w.Commands["files"].ExecuteAsync(); await F.PumpAsync();
        F.Click(F.Button(Tool(w), "Choose a local folder")); await F.PumpAsync();
        F.Click(F.Button(Tool(w), name)); await F.UntilAsync(() => w.IsOverlayOpen, "Invalid files must surface a visible error."); await F.PumpAsync();
        Assert.Contains(f.Overlay.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains(large ? "2 MiB" : "binary") == true);
        Assert.DoesNotContain(f.Overlay.GetVisualDescendants().OfType<TextBox>(), t => !t.IsReadOnly);
    }

    [AvaloniaFact]
    public async Task RecordingImportPlaySeekStopAndExportPreserveReadOnlyAndStripInput()
    {
        await using var f = await F.OpenAsync(); var w = f.Window;
        string source = Path.Combine(f.DirectoryPath, "fixture.rtcap.json");
        var capture = Recording();
        await using (var stream = File.Create(source)) await TerminalCaptureSessionSerializer.SaveAsync(capture, stream);
        f.Files.Opens.Enqueue(new[] { source }); await w.Commands["load-replay"].ExecuteAsync(); await F.PumpAsync();
        var session = w.Shell.ActiveSession!;
        Assert.True(session.IsReplay); Assert.True(session.Locked); Assert.False(session.IsRunning);
        Assert.Equal("Timeline", w.Shell.Data.Preferences.Tool);
        F.Click(F.Button(Tool(w), "Play")); await F.PumpAsync(100);
        F.Click(F.Button(Tool(w), "Pause")); await F.PumpAsync();
        var slider = Tool(w).GetVisualDescendants().OfType<Slider>().Single();
        slider.Value = .6; await F.PumpAsync(); Assert.InRange(session.Capture.ReplayPositionSeconds, .59, .61);
        F.Click(F.Button(Tool(w), "Stop")); await F.PumpAsync(); Assert.Equal(0, session.Capture.ReplayPositionSeconds);
        Assert.Throws<InvalidOperationException>(() => session.Send("echo unsafe\r"));
        foreach (string extension in new[] { "rtcap.json", "cast" })
        {
            string exported = Path.Combine(f.DirectoryPath, "export." + extension);
            f.Files.Saves.Enqueue(exported); await w.Commands["save-capture"].ExecuteAsync();
            await using var exportedStream = File.OpenRead(exported);
            var saved = await TerminalCaptureSessionFormats.DefaultRegistry.LoadAsync(exportedStream,exported,F.Token);
            Assert.DoesNotContain(saved.Events, e => e.Kind == TerminalCaptureEventKind.Input);
            Assert.DoesNotContain("NEVER_EXPORT_RAW_INPUT", await File.ReadAllTextAsync(exported, F.Token));
            // Exercise the real import handler, not just a standalone serializer.
            f.Files.Opens.Enqueue(new[] { exported }); await w.Commands["load-replay"].ExecuteAsync(); await F.PumpAsync();
            Assert.True(w.Shell.ActiveSession!.IsReplay); Assert.True(w.Shell.ActiveSession.Locked);
        }
        F.Screenshot(w, "replay-timeline");
    }

    [AvaloniaFact]
    public async Task CancelledAndCorruptRecordingImportsCannotCreateATerminal()
    {
        await using var f = await F.OpenAsync(); var w = f.Window;
        int count = w.Shell.Active.Documents.Count;
        f.Files.Opens.Enqueue(Array.Empty<string>()); await w.Commands["load-replay"].ExecuteAsync();
        Assert.Equal(count, w.Shell.Active.Documents.Count);
        string source = Path.Combine(f.DirectoryPath, "invalid.json");
        await File.WriteAllTextAsync(source, "not recording data", F.Token);
        f.Files.Opens.Enqueue(new[] { source });
        await Assert.ThrowsAnyAsync<Exception>(() => w.Commands["load-replay"].ExecuteAsync());
        Assert.Equal(count, w.Shell.Active.Documents.Count);
    }

    [AvaloniaFact]
    public async Task ShaderLoadCancelAndInvalidCompilationDoNotDestroyTheAppliedPipeline()
    {
        await using var f = await F.OpenAsync(); var w = f.Window;
        await w.Commands["shaders"].ExecuteAsync(); await F.PumpAsync();
        var preset = F.Named<ComboBox>(f.Overlay, "Preset"); preset.SelectedIndex = 1;
        F.Click(F.Button(f.Overlay, "Validate & apply")); await F.PumpAsync();
        var saved = w.Shell.Shaders.Get(w.Shell.ActiveDocumentId!.Value);
        Assert.NotEmpty(saved);
        f.Files.Opens.Enqueue(Array.Empty<string>()); F.Click(F.Button(f.Overlay, "Load source")); await F.PumpAsync();
        Assert.Equal(saved, w.Shell.Shaders.Get(w.Shell.ActiveDocumentId!.Value));
        string path = Path.Combine(f.DirectoryPath, "broken.sksl"); await File.WriteAllTextAsync(path, "this is not shader source", F.Token);
        f.Files.Opens.Enqueue(new[] { path }); F.Click(F.Button(f.Overlay, "Load source")); await F.PumpAsync();
        F.Click(F.Button(f.Overlay, "Validate & apply")); await F.PumpAsync();
        Assert.Equal(saved, w.Shell.Shaders.Get(w.Shell.ActiveDocumentId!.Value));
        Assert.NotEmpty(w.Shell.ActiveSession!.Terminal.ShaderSources!);
    }

    internal static TerminalCaptureSession Recording() => new()
    {
        InitialColumns = 100, InitialRows = 30, TransportId = "pty",
        Events = [new() { Kind = TerminalCaptureEventKind.Output, Data = "REPLAY_HEAD\r\n"u8.ToArray(), OffsetMilliseconds = 0 },
            new() { Kind = TerminalCaptureEventKind.Input, Data = "NEVER_EXPORT_RAW_INPUT\r"u8.ToArray(), OffsetMilliseconds = 100 },
            new() { Kind = TerminalCaptureEventKind.Output, Data = "REPLAY_TAIL\r\n"u8.ToArray(), OffsetMilliseconds = 500 },
            new() { Kind = TerminalCaptureEventKind.Exit, ExitCode = 0, OffsetMilliseconds = 1000 }]
    };
    private static Control Tool(Tessera.Views.MainWindow w) => w.FindControl<ContentControl>("ToolContent")!;
}
