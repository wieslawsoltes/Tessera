using System.Text;
using System.Text.Json;

namespace Tessera.Core;

/// <summary>Owner-only temporary files, durable flush, same-directory atomic replacement.</summary>
public static class AtomicFile
{
    public static void EnsurePrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public static async Task WriteAsync(string path, ReadOnlyMemory<byte> content, CancellationToken token = default)
    {
        path = Path.GetFullPath(path);
        EnsurePrivateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                Share = FileShare.None, Options = FileOptions.Asynchronous | FileOptions.WriteThrough };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temp, options))
            {
                await stream.WriteAsync(content, token);
                await stream.FlushAsync(token);
                stream.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static Task WriteJsonAsync<T>(string path, T value, CancellationToken token = default) =>
        WriteAsync(path, JsonSerializer.SerializeToUtf8Bytes(value, WorkspaceStore.Json), token);

    public static async Task<byte[]> ReadBytesAsync(string path, int maximumBytes, CancellationToken token = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16384, FileOptions.Asynchronous);
        var length = stream.Length;
        if (length > maximumBytes) throw new InvalidDataException("The file exceeds its size limit.");
        var bytes = new byte[checked((int)length)];
        await stream.ReadExactlyAsync(bytes, token);
        if (await stream.ReadAsync(new byte[1], token) != 0) throw new InvalidDataException("The file grew while it was being read.");
        return bytes;
    }

    public static async Task<T?> ReadJsonAsync<T>(string path, long maximumBytes, CancellationToken token = default)
    {
        if (!File.Exists(path)) return default;
        var bytes = await ReadBytesAsync(path, checked((int)maximumBytes), token);
        return JsonSerializer.Deserialize<T>(bytes, WorkspaceStore.Json);
    }
}
