using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using RoyalTerminal.Terminal;
using Tessera.Services;
using Tessera.Views;
using Xunit;

namespace Tessera.NativeTests;

/// <summary>Real child-shell readiness and command assertions that cannot pass on input echo.</summary>
internal static class PtyTestFixture
{
    public static async Task<MainWindow> OpenAsync(string directory, CancellationToken token)
    {
        string ready = "TESSERA_READY_" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(directory);
        var profile = ProfileRepository.Local();
        TerminalSessionPtySettings pty;
        if (OperatingSystem.IsWindows())
        {
            string script = Path.Combine(directory, "pty-start.cmd");
            await File.WriteAllTextAsync(script, $"@echo off\r\necho {ready}\r\nprompt $G\r\n", new UTF8Encoding(false), token);
            pty = new()
            {
                ShellPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                Arguments = ["/D", "/Q", "/K", script], WorkingDirectory = directory
            };
        }
        else
        {
            pty = new()
            {
                ShellPath = "/bin/sh", Arguments = ["-i"], WorkingDirectory = directory,
                Environment = new(StringComparer.Ordinal)
                {
                    ["PS1"] = ready + "> ", ["ENV"] = Path.Combine(directory, "no-shell-rc"),
                    ["BASH_ENV"] = Path.Combine(directory, "no-shell-rc"), ["HISTFILE"] = "/dev/null"
                }
            };
        }
        var profiles = new ProfileRepository(directory, new MemoryVault());
        await profiles.SaveAdvancedAsync(profile with { Transport = profile.Transport with { Pty = pty } }, token);
        var window = new MainWindow(false, directory);
        try
        {
            window.Show();
            await window.InitializeAsync();
            var session = Assert.IsType<SessionRuntime>(window.Shell.ActiveSession);
            Assert.True(session.IsRunning, session.Error ?? session.State);
            await WaitForOutputAsync(session, ready, token);
            return window;
        }
        catch
        {
            try { await window.Shell.PrepareShutdownAsync(); }
            finally { window.CloseForTests(); }
            throw;
        }
    }

    public static async Task AssertCommandAsync(SessionRuntime session, string prefix, CancellationToken token)
    {
        string suffix = Guid.NewGuid().ToString("N");
        string marker = prefix + suffix;
        string input = OperatingSystem.IsWindows()
            ? $"set \"_tessera_test_suffix={suffix}\"\recho {prefix}%_tessera_test_suffix%\r"
            : $"printf '{prefix}%s\\n' '{suffix}'\r";
        Assert.DoesNotContain(marker, input);
        session.Send(input);
        await WaitForOutputAsync(session, marker, token);
    }

    private static async Task WaitForOutputAsync(SessionRuntime session, string marker, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(20))
        {
            token.ThrowIfCancellationRequested();
            Dispatcher.UIThread.RunJobs();
            session.Terminal.FlushPendingTransportOutput();
            if (session.OutputSnapshot().Contains(marker, StringComparison.Ordinal)) return;
            if (!session.IsRunning) break;
            await Task.Delay(50, token);
        }
        session.Terminal.TryExportSnapshot(TerminalSnapshotExportFormat.PlainText,
            new TerminalSnapshotExportOptions(true, true), out var rendered);
        var directory = session.Profile.Transport.Pty.WorkingDirectory!;
        string evidence = JsonSerializer.Serialize(new
        {
            Marker = marker, session.State, session.Error,
            RawOutput = session.OutputSnapshot(), RenderedOutput = rendered,
            session.Terminal.Columns, session.Terminal.Rows, session.Profile.Transport.Pty,
            ChildStarted = File.Exists(Path.Combine(directory, "pty-started.txt")),
            StartupScript = File.Exists(Path.Combine(directory, "pty-start.cmd")) ? File.ReadAllText(Path.Combine(directory, "pty-start.cmd")) : null
        });
        if (Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") is { Length: > 0 } root)
        {
            string folder = Path.Combine(root, "artifacts", "pty-diagnostics");
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, marker + ".json"), evidence, token);
        }
        Assert.Fail("PTY did not produce its expected marker. " + evidence);
    }
}
