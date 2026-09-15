using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using RoyalTerminal.Terminal;
using Tessera.Services;
using Tessera.Views;
using Xunit;

namespace Tessera.NativeTests;

/// <summary>
/// Executes a real PTY without inheriting the runner's interactive shell customization.
/// The readiness marker comes from the child, and command assertions cannot pass on input echo.
/// </summary>
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
            // /Q and @echo off can suppress the interactive prompt. Readiness
            // must be an explicit write by the child, not a prompt assumption.
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
        // Both sides of the marker are separated in the input, including on Windows.
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
        // Raw ConPTY output can contain XML-invalid control characters. Never put
        // them directly in assertion messages consumed by TRX or runner protocols.
        string evidence = JsonSerializer.Serialize(new
        {
            Marker = marker, session.State, session.Error,
            RawOutput = session.OutputSnapshot(), RenderedOutput = rendered,
            session.Terminal.Columns, session.Terminal.Rows, session.Profile.Transport.Pty
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
