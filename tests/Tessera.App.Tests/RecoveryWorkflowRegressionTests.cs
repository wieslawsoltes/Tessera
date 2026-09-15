using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using RoyalTerminal.Terminal;
using Tessera.Services;
using Xunit;
using F = Tessera.NativeTests.UiRegressionFixture;

namespace Tessera.NativeTests;

public sealed class RecoveryWorkflowRegressionTests
{
    [AvaloniaFact]
    public async Task RecoveryUnlockReadOnlyReplayExportAndConfirmedDiscardUseRealEncryptedStorage()
    {
        await using var f = await F.OpenAsync(); var w = f.Window;
        const string passphrase = "fixture-only-passphrase-123";
        var id = Guid.NewGuid();
        // Provision an actual passphrase-wrapped checkpoint. Only vault availability
        // is synthetic; encryption, storage, password UI, replay/export are real.
        using (var keys = new RecoveryKeyStore(f.DirectoryPath, new MemoryVault { Unavailable = true }))
        {
            keys.PassphrasePrompt = (_, _) => Task.FromResult<string?>(passphrase);
            var store = new CaptureRecoveryStore(f.DirectoryPath, keys);
            await store.SaveAsync(id, new RecoveredCapture("Recovered UI fixture", "local", FileWorkflowRegressionTests.Recording()), F.Token);
        }
        await w.Commands["recover-capture"].ExecuteAsync(); await F.PumpAsync();
        F.Click(F.Button(f.Overlay, "Recover read-only"));
        await F.UntilAsync(() => w.OwnedWindows.Any(), "Recovery must request the passphrase."); await F.PumpAsync();
        var unlock = w.OwnedWindows.Single()!; F.Named<TextBox>(unlock, "Recovery passphrase").Text = passphrase;
        F.Click(F.Button(unlock, "Unlock recordings"));
        await F.UntilAsync(() => w.Shell.ActiveSession?.IsReplay == true && !w.IsOverlayOpen, "Checkpoint must become a read-only replay."); await F.PumpAsync();
        Assert.True(w.Shell.ActiveSession!.Locked); Assert.False(w.Shell.ActiveSession.IsRunning);
        Assert.Throws<InvalidOperationException>(() => w.Shell.ActiveSession.Send("never-run\r"));
        Assert.Contains(w.Shell.Recovery.Store.List(), item => item.Id == id);
        await w.Commands["recover-capture"].ExecuteAsync(); await F.PumpAsync();
        string export = Path.Combine(f.DirectoryPath, "recovered.rtcap.json"); f.Files.Saves.Enqueue(export);
        F.Click(F.Button(f.Overlay, "Export")); await F.UntilAsync(() => File.Exists(export), "Recovery export writes a real file."); await F.PumpAsync();
        var recording = await TerminalCaptureSessionSerializer.LoadFromFileAsync(export, F.Token);
        Assert.DoesNotContain(recording.Events, e => e.Kind == TerminalCaptureEventKind.Input);
        Assert.Contains(w.Shell.Recovery.Store.List(), item => item.Id == id);
        F.Click(F.Button(f.Overlay, "Permanently discard recovered recording")); await F.PumpAsync();
        F.Click(F.Button(f.Overlay, "Cancel")); await F.PumpAsync(); Assert.Contains(w.Shell.Recovery.Store.List(), item => item.Id == id);
        await w.Commands["recover-capture"].ExecuteAsync(); await F.PumpAsync();
        F.Click(F.Button(f.Overlay, "Permanently discard recovered recording")); await F.PumpAsync();
        F.Click(F.Button(f.Overlay, "Discard recording")); await F.UntilAsync(() => w.Shell.Recovery.Store.List().Length == 0, "Confirmed discard removes the encrypted checkpoint.");
    }
}
