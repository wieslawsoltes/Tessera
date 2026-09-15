using System.Text;
using System.Text.Json;
using RoyalTerminal.Terminal;
using Xunit;

namespace Tessera.NativeTests;

public sealed class PtyTransportTests
{
    [Fact]
    public async Task ChildStandardOutputReachesThePseudoTerminalWithoutUiOrParser()
    {
        using var directory = new AcceptanceDirectory();
        var received = new StringBuilder();
        var sync = new object();
        string marker = "TESSERA_RAW_PTY_" + Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string shell;
        string[] arguments;
        if (OperatingSystem.IsWindows())
        {
            string script = Path.Combine(directory.Path, "raw-pty.cmd");
            await File.WriteAllTextAsync(script, $"@echo off\r\necho started>child-started.txt\r\necho {marker}\r\n", new UTF8Encoding(false));
            shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            arguments = ["/D", "/C", script];
        }
        else
        {
            shell = "/bin/sh";
            arguments = ["-c", "printf '%s\\n' '" + marker + "'"];
        }
        using var pty = new DefaultPtyFactory().Create();
        pty.DataReceived += (bytes, length) =>
        {
            lock (sync)
            {
                received.Append(Encoding.UTF8.GetString(bytes, 0, length));
                if (received.ToString().Contains(marker, StringComparison.Ordinal)) completion.TrySetResult();
            }
        };
        pty.Start(shell: shell, columns: 120, rows: 30, workingDirectory: directory.Path, arguments: arguments);
        try
        {
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        }
        catch (TimeoutException)
        {
            string raw;
            lock (sync) raw = received.ToString();
            string evidence = JsonSerializer.Serialize(new { Marker = marker, RawOutput = raw, pty.IsRunning,
                ChildStarted = File.Exists(Path.Combine(directory.Path, "child-started.txt")) });
            if (Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") is { Length: > 0 } root)
            {
                string folder = Path.Combine(root, "artifacts", "pty-diagnostics");
                Directory.CreateDirectory(folder);
                await File.WriteAllTextAsync(Path.Combine(folder, "raw-transport.json"), evidence);
            }
            Assert.Fail("Real child stdout was not received by the PTY: " + evidence);
        }
    }
}
