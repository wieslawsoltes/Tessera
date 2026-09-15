using System.Security.Cryptography;
using Renci.SshNet;
using Renci.SshNet.Sftp;
using RoyalTerminal.Terminal;
using Tessera.Core;

namespace Tessera.Services;

public sealed record RemoteFile(string Name, string Path, bool Directory, bool SymbolicLink, long Length, DateTime ModifiedUtc);
public sealed record RemoteVersion(long Length, DateTime ModifiedUtc, string? Sha256 = null);
public sealed record FileTransferProgress(long Completed, long Total, string Operation);

/// <summary>Single-connection SFTP workspace. Transfers use staging files and never delete a destination to simulate atomic overwrite.</summary>
public sealed class SftpWorkspace : IAsyncDisposable
{
    private readonly SftpClient _client;
    private readonly SshConnectionContext _security;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;
    public bool ReadOnly { get; set; } = true;
    private void EnsureWritable() { if(ReadOnly) throw new InvalidOperationException("This SFTP workspace is read-only. Enable writes explicitly."); }
    public string ProfileId { get; }
    public string Host { get; }
    public bool Connected => !_disposed && _client.IsConnected;
    public string HomeDirectory { get; }
    private SftpWorkspace(SftpClient client, SshConnectionContext security, TerminalSessionProfile profile)
    {
        _client = client; _security = security; ProfileId = profile.Id; Host = profile.Transport.Ssh.Host;
        HomeDirectory = client.WorkingDirectory;
    }

    public static async Task<SftpWorkspace> ConnectAsync(ProfileRepository profiles, TerminalSessionProfile profile, CancellationToken token)
    {
        if (profile.Transport.TransportId != TerminalTransportIds.Ssh) throw new InvalidOperationException("Choose an SSH profile for SFTP.");
        var options = (SshTransportOptions)await profiles.RuntimeOptionsAsync(profile, token).ConfigureAwait(false);
        var context = new SshConnectionContext(profiles);
        context.Begin(options, token);
        SftpClient? client = null;
        try
        {
            var credentials = await context.ResolveAsync(new(options.Endpoint, options.Authentication), token).ConfigureAwait(false);
            var methods = new List<AuthenticationMethod>();
            if (options.Authentication.UsePassword) methods.Add(new PasswordAuthenticationMethod(options.Endpoint.Username, credentials.Password ?? throw new OperationCanceledException()));
            methods.AddRange(await Task.Run(() => context.CreateAuthenticationMethods(options, credentials), token).ConfigureAwait(false));
            client = new SftpClient(SshConnectionFactory.Create(options, methods))
            {
                KeepAliveInterval = TimeSpan.FromSeconds(Math.Clamp(options.Policy.KeepAliveIntervalSeconds, 0, 3600)),
                OperationTimeout = TimeSpan.FromSeconds(60)
            };
            client.HostKeyReceived += (_, e) => e.CanTrust = context.IsTrusted(options.Endpoint,
                new(e.HostKeyName, e.FingerPrintSHA256, e.FingerPrintMD5, e.KeyLength, Convert.ToBase64String(e.HostKey)));
            await client.ConnectAsync(token).ConfigureAwait(false);
            return new(client, context, profile);
        }
        catch { client?.Dispose(); context.Dispose(); throw; }
    }

    public static string ChildPath(string directory, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(['/', '\\']) >= 0 || name.Any(char.IsControl))
            throw new ArgumentException("Enter a single remote file name, without path separators or control characters.", nameof(name));
        return directory.TrimEnd('/') + "/" + name;
    }
    private static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || path.Any(char.IsControl)) throw new ArgumentException("Invalid remote path.");
    }
    private async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_client.IsConnected) throw new IOException("SFTP disconnected. Reconnect explicitly before retrying the operation.");
            return await operation(linked.Token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public Task<RemoteFile[]> ListAsync(string path, CancellationToken token = default) => ExecuteAsync(async ct =>
    {
        ValidatePath(path);
        var result = new List<RemoteFile>();
        await foreach (var file in _client.ListDirectoryAsync(path, ct).ConfigureAwait(false))
        {
            if (file.Name is "." or "..") continue;
            result.Add(new(file.Name, file.FullName, file.IsDirectory, file.IsSymbolicLink, file.Length, file.LastWriteTimeUtc));
            if (result.Count > 5000) throw new IOException("Directory exceeds 5,000 entries. Narrow the remote path.");
        }
        return result.OrderByDescending(f => f.Directory).ThenBy(f => f.Name, StringComparer.Ordinal).ToArray();
    }, token);

    public Task<RemoteVersion> VersionAsync(string path, CancellationToken token = default) => ExecuteAsync(async ct =>
    {
        ValidatePath(path); var a = await _client.GetAttributesAsync(path, ct).ConfigureAwait(false);
        return Version(a);
    }, token);
    private async Task<ISftpFile?> EntryAsync(string path, CancellationToken token)
    {
        string trimmed = path.TrimEnd('/'); int slash = trimmed.LastIndexOf('/');
        if (slash < 0 || trimmed.Length == 0) throw new ArgumentException("Use an absolute remote file path.");
        string parent = slash == 0 ? "/" : trimmed[..slash], name = trimmed[(slash + 1)..];
        int count = 0;
        await foreach (var file in _client.ListDirectoryAsync(parent, token).ConfigureAwait(false))
        {
            if (file.Name == name) return file; // Directory entries retain lstat semantics; GetAsync resolves links first.
            if (++count > 5002) throw new IOException("The parent directory exceeds the 5,000-entry safety limit.");
        }
        return null;
    }
    private async Task CheckContentVersionAsync(string remote, SftpFileAttributes? attributes, RemoteVersion expected, CancellationToken token)
    {
        if (attributes is null || attributes.Size != expected.Length || attributes.LastWriteTimeUtc != expected.ModifiedUtc)
            throw new IOException("The remote file changed since it was opened. Refresh before saving.");
        if (expected.Sha256 is not null)
        {
            await using var input = await _client.OpenAsync(remote, FileMode.Open, FileAccess.Read, token).ConfigureAwait(false);
            using var bytes = new MemoryStream();
            await CopyAsync(input, bytes, expected.Length, "Verify remote content", null, token, 2 * 1024 * 1024).ConfigureAwait(false);
            if (Convert.ToHexString(SHA256.HashData(bytes.GetBuffer().AsSpan(0, (int)bytes.Length))) != expected.Sha256)
                throw new IOException("The remote content changed, even though its size and timestamp match. Reopen before saving.");
        }
    }
    private static RemoteVersion Version(SftpFileAttributes attributes) => new(attributes.Size, attributes.LastWriteTimeUtc);

    public Task DownloadAsync(string remote, string local, bool overwrite, IProgress<FileTransferProgress>? progress = null, CancellationToken token = default) => ExecuteAsync(async ct =>
    {
        ValidatePath(remote); local = Path.GetFullPath(local);
        if (!overwrite && File.Exists(local)) throw new IOException("The local destination exists.");
        var initial = Version(await _client.GetAttributesAsync(remote, ct).ConfigureAwait(false));
        var localVersion = File.Exists(local) ? (new FileInfo(local).Length, File.GetLastWriteTimeUtc(local)) : ((long, DateTime)?)null;
        string temporary = local + ".tessera-" + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            var opts = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows()) opts.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var destination = new FileStream(temporary, opts))
            await using (var source = await _client.OpenAsync(remote, FileMode.Open, FileAccess.Read, ct).ConfigureAwait(false))
            {
                await CopyAsync(source, destination, initial.Length, "Download", progress, ct).ConfigureAwait(false);
                await destination.FlushAsync(ct).ConfigureAwait(false); destination.Flush(true);
            }
            if (Version(await _client.GetAttributesAsync(remote, ct).ConfigureAwait(false)) != initial) throw new IOException("The remote file changed during download. The local destination was not replaced.");
            var now = File.Exists(local) ? (new FileInfo(local).Length, File.GetLastWriteTimeUtc(local)) : ((long, DateTime)?)null;
            if (now != localVersion) throw new IOException("The local destination changed during transfer.");
            ct.ThrowIfCancellationRequested(); File.Move(temporary, local, overwrite);
            return true;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }, token);

    public Task UploadAsync(string local, string remote, bool overwrite, IProgress<FileTransferProgress>? progress = null, CancellationToken token = default, RemoteVersion? expected = null) => ExecuteAsync(async ct =>
    {
        EnsureWritable(); ValidatePath(remote);
        await using var source = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        await UploadCoreAsync(source, remote, overwrite, expected, progress, ct).ConfigureAwait(false);
        return true;
    }, token);

    public Task<(byte[] Content, RemoteVersion Version)> ReadTextAsync(string remote, CancellationToken token = default) => ExecuteAsync(async ct =>
    {
        ValidatePath(remote);
        var initial = Version(await _client.GetAttributesAsync(remote, ct).ConfigureAwait(false));
        if (initial.Length > 2 * 1024 * 1024) throw new IOException("The inline editor is limited to 2 MiB.");
        await using var source = await _client.OpenAsync(remote, FileMode.Open, FileAccess.Read, ct).ConfigureAwait(false);
        using var memory = new MemoryStream();
        await CopyAsync(source, memory, initial.Length, "Read", null, ct, 2 * 1024 * 1024).ConfigureAwait(false);
        var content = memory.ToArray();
        if (content.Contains((byte)0)) throw new IOException("This file appears to be binary.");
        if (Version(await _client.GetAttributesAsync(remote, ct).ConfigureAwait(false)) != initial) throw new IOException("Remote file changed while opening.");
        return (content, initial with { Sha256 = Convert.ToHexString(SHA256.HashData(content)) });
    }, token);

    public Task<RemoteVersion> SaveTextAsync(string remote, byte[] content, RemoteVersion expected, CancellationToken token = default) => ExecuteAsync(async ct =>
    {
        EnsureWritable(); ValidatePath(remote);
        if (content.Length > 2 * 1024 * 1024) throw new IOException("The inline editor is limited to 2 MiB.");
        using var source = new MemoryStream(content, writable: false);
        await UploadCoreAsync(source, remote, true, expected, null, ct).ConfigureAwait(false);
        var saved = Version(await _client.GetAttributesAsync(remote, ct).ConfigureAwait(false));
        return saved with { Sha256 = Convert.ToHexString(SHA256.HashData(content)) };
    }, token);

    private async Task UploadCoreAsync(Stream source, string remote, bool overwrite, RemoteVersion? expected, IProgress<FileTransferProgress>? progress, CancellationToken ct)
    {
        EnsureWritable();
        var existingFile = await EntryAsync(remote, ct).ConfigureAwait(false);
        if (existingFile is not null && !overwrite) throw new IOException("The remote destination exists. Confirm overwrite explicitly.");
        if (existingFile?.IsSymbolicLink == true || existingFile?.IsDirectory == true) throw new IOException("Uploads cannot replace a directory or symbolic link.");
        var initial = existingFile?.Attributes;
        if (expected is not null) await CheckContentVersionAsync(remote, initial, expected, ct).ConfigureAwait(false);
        var temporary = remote + ".tessera-" + Guid.NewGuid().ToString("N") + ".part";
        bool created = false;
        try
        {
            await using (var destination = await _client.OpenAsync(temporary, FileMode.CreateNew, FileAccess.Write, ct).ConfigureAwait(false))
            {
                created = true;
                var privateAttributes = await _client.GetAttributesAsync(temporary,ct).ConfigureAwait(false);
                privateAttributes.SetPermissions(600); // SSH.NET accepts octal digits as decimal, not the bit mask 0x180.
                await Task.Run(()=>_client.SetAttributes(temporary,privateAttributes),ct).ConfigureAwait(false);
                await CopyAsync(source, destination, source.Length, "Upload", progress, ct).ConfigureAwait(false);
                await destination.FlushAsync(ct).ConfigureAwait(false);
            }
            EnsureWritable(); ct.ThrowIfCancellationRequested();
            if (initial is not null)
            {
                var entry = await EntryAsync(remote, ct).ConfigureAwait(false);
                if (entry is null || entry.IsSymbolicLink || entry.IsDirectory) throw new IOException("Remote destination identity changed during upload.");
                var current = entry.Attributes;
                if (Version(current) != Version(initial)) throw new IOException("The remote destination changed during upload.");
                if (expected is not null) await CheckContentVersionAsync(remote, current, expected, ct).ConfigureAwait(false);
                var staging = await _client.GetAttributesAsync(temporary, ct).ConfigureAwait(false);
                staging.OwnerCanRead = initial.OwnerCanRead; staging.OwnerCanWrite = initial.OwnerCanWrite; staging.OwnerCanExecute = initial.OwnerCanExecute;
                staging.GroupCanRead = initial.GroupCanRead; staging.GroupCanWrite = initial.GroupCanWrite; staging.GroupCanExecute = initial.GroupCanExecute;
                staging.OthersCanRead = initial.OthersCanRead; staging.OthersCanWrite = initial.OthersCanWrite; staging.OthersCanExecute = initial.OthersCanExecute;
                await Task.Run(() => _client.SetAttributes(temporary, staging), ct).ConfigureAwait(false);
                // OpenSSH extension is an atomic rename-and-replace. Do not fall back to delete+rename.
                await Task.Run(() => _client.RenameFile(temporary, remote, isPosix: true), ct).ConfigureAwait(false);
            }
            else await _client.RenameFileAsync(temporary, remote, ct).ConfigureAwait(false);
            created = false;
        }
        finally
        {
            if (created && _client.IsConnected)
            {
                try { using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await _client.DeleteFileAsync(temporary, cleanup.Token).ConfigureAwait(false); }
                catch (Exception) { /* A disconnected server may retain a clearly named .part; never delete the destination. */ }
            }
        }
    }
    private static async Task CopyAsync(Stream input, Stream output, long length, string operation, IProgress<FileTransferProgress>? progress, CancellationToken ct, long maximum = long.MaxValue)
    {
        byte[] buffer = new byte[65536]; long completed = 0; var throttle = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var count = await input.ReadAsync(buffer, ct).ConfigureAwait(false); if (count == 0) break;
            if (completed + count > maximum) throw new IOException("Transfer exceeded its size limit.");
            await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
            completed += count; if(throttle.ElapsedMilliseconds >= 100) {progress?.Report(new(completed,length,operation));throttle.Restart();}
        }
        progress?.Report(new(completed,length,operation));
        if (completed != length) throw new IOException("File size changed during transfer.");
    }
    public Task CreateDirectoryAsync(string path, CancellationToken token = default) => ExecuteAsync(async ct => { EnsureWritable(); ValidatePath(path); await _client.CreateDirectoryAsync(path, ct).ConfigureAwait(false); return true; }, token);
    public Task RenameAsync(string from, string to, CancellationToken token = default) => ExecuteAsync(async ct =>
    {
        EnsureWritable(); ValidatePath(from); ValidatePath(to);
        if (await EntryAsync(to, ct).ConfigureAwait(false) is not null) throw new IOException("Rename destination exists.");
        var entry = await EntryAsync(from, ct).ConfigureAwait(false) ?? throw new IOException("The remote entry no longer exists.");
        await Task.Run(() => entry.MoveTo(to), ct).ConfigureAwait(false); return true;
    }, token);
    public Task DeleteAsync(RemoteFile file, CancellationToken token = default) => ExecuteAsync(async ct =>
    {
        EnsureWritable(); ValidatePath(file.Path);
        var current = await EntryAsync(file.Path, ct).ConfigureAwait(false) ?? throw new IOException("The remote entry no longer exists.");
        if (current.IsDirectory != file.Directory || current.IsSymbolicLink != file.SymbolicLink || current.Length != file.Length || current.LastWriteTimeUtc != file.ModifiedUtc)
            throw new IOException("The remote entry changed since it was selected. Refresh before deleting.");
        await current.DeleteAsync(ct).ConfigureAwait(false); // Do not canonicalize a symlink into its target.
        return true;
    }, token);
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _lifetime.Cancel(); _security.Cancel();
        await _gate.WaitAsync().ConfigureAwait(false);
        try { if (_disposed) return; _disposed = true; _client.Dispose(); _security.Dispose(); }
        finally { _gate.Release(); }
    }
}
