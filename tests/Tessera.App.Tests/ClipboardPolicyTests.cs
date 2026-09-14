using Avalonia.Headless.XUnit;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Tessera.Views;
using Xunit;

namespace Tessera.NativeTests;

public sealed class ClipboardPolicyTests
{
    [AvaloniaFact]
    public async Task DirectNativePasteCannotBypassReadOnlyPolicy()
    {
        var window = new MainWindow(false, Path.Combine(Path.GetTempPath(), "tessera-paste-" + Guid.NewGuid()));
        window.Show();
        try
        {
            await window.InitializeAsync();
            Dispatcher.UIThread.RunJobs();
            var session = window.Shell.ActiveSession!;
            Assert.True(session.IsRunning, session.Error);
            session.Locked = true;
            Assert.NotNull(window.Clipboard);
            await window.Clipboard!.SetTextAsync("LOCKED_CLIPBOARD_MUST_NOT_REACH_PTY");
            var count = 0;
            session.Terminal.TerminalSessionService.InputSent += (_, _) => count++;
            await session.Terminal.PasteAsync();
            await Task.Delay(80);
            Assert.Equal(0, count);
        }
        finally { window.CloseForTests(); }
    }
}
