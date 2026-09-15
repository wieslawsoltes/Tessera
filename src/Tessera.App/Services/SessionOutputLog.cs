using System.Text;
using System.Threading.Channels;
using RoyalTerminal.Terminal;
using Tessera.Core;

namespace Tessera.Services;

/// <summary>Bounded, asynchronous output-only session logger. Input, passwords and MFA responses are never subscribed.</summary>
public sealed class SessionOutputLog : IAsyncDisposable
{
    private readonly Channel<byte[]> _queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task _writer;
    private readonly Action<string> _report;
    private int _failed;
    private int _closed;
    public SessionOutputLog(TerminalSessionLoggingSettings settings, Action<string> report)
    {
        if (string.IsNullOrWhiteSpace(settings.FilePath)) throw new InvalidOperationException("A session log file path is required.");
        _report = report; _writer = Task.Run(() => WriteAsync(settings));
    }
    public void Receive(ReadOnlySpan<byte> bytes)
    {
        if (Volatile.Read(ref _failed) != 0 || Volatile.Read(ref _closed) != 0) return;
        while (!bytes.IsEmpty)
        {
            int length = Math.Min(4096, bytes.Length);
            if (!_queue.Writer.TryWrite(bytes[..length].ToArray()))
            {
                Fail("Session logging stopped: output exceeded the bounded disk-write queue. The terminal remains responsive.");
                return;
            }
            bytes = bytes[length..];
        }
    }
    private void Fail(string message)
    {
        if (Interlocked.Exchange(ref _failed, 1) == 0) _report(message);
        _queue.Writer.TryComplete();
    }
    private async Task WriteAsync(TerminalSessionLoggingSettings settings)
    {
        FileStream? stream = null;
        try
        {
            var path = Path.GetFullPath(settings.FilePath!);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            FileStream Open()
            {
                var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read,
                    Options = FileOptions.Asynchronous, BufferSize = 65536 };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                var result = new FileStream(path, options);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                return result;
            }
            stream = Open(); var decoder = new TerminalTextDecoder();
            await foreach (var chunk in _queue.Reader.ReadAllAsync())
            {
                byte[] output = settings.Format == TerminalSessionLogFormat.RawBytes ? chunk : Encoding.UTF8.GetBytes(decoder.Decode(chunk));
                if (stream.Length + output.Length > 64L * 1024 * 1024)
                {
                    await stream.FlushAsync(); stream.Flush(true); await stream.DisposeAsync(); stream = null;
                    File.Move(path, path + ".1", true); stream = Open();
                }
                await stream.WriteAsync(output);
                if (settings.FlushFrequently) { await stream.FlushAsync(); stream.Flush(true); }
            }
            if (settings.Format == TerminalSessionLogFormat.PlainText) await stream.WriteAsync(Encoding.UTF8.GetBytes(decoder.Decode([], true)));
            await stream.FlushAsync(); stream.Flush(true);
        }
        catch (Exception ex) { Fail("Session log failure: " + Safety.CleanTitle(ex.Message)); }
        finally { if (stream is not null) await stream.DisposeAsync(); }
    }
    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _closed, 1); _queue.Writer.TryComplete(); return new(_writer);
    }
}
