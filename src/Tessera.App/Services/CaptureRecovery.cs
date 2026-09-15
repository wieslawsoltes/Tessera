using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using RoyalTerminal.Terminal;
using Tessera.Core;

namespace Tessera.Services;

public sealed record RecoveryKeyDocument(int Version, string Mode, string Reference, byte[]? Salt = null,
    byte[]? Nonce = null, byte[]? Tag = null, byte[]? WrappedKey = null);
public sealed record RecoveredCapture(string Title, string ProfileId, TerminalCaptureSession Session);
public sealed record RecoveryItem(Guid Id, DateTime ModifiedUtc, long Bytes);

/// <summary>A random AES-256 key protected by the OS vault or by an explicit user recovery passphrase.</summary>
public sealed class RecoveryKeyStore(string directory, ICredentialVault vault) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private byte[]? _key;
    private readonly string _path = Path.Combine(directory, "recovery-key.json");
    private readonly string _reference = "recovery/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(directory))));
    public Func<bool, CancellationToken, Task<string?>>? PassphrasePrompt { get; set; }
    public string? ProtectionMode { get; private set; }
    public async Task<byte[]> UnlockAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (_key is not null) return _key;
            var metadata = await AtomicFile.ReadJsonAsync<RecoveryKeyDocument>(_path, 8192, token);
            if (metadata is not null)
            {
                if (metadata.Version != 1) throw new InvalidDataException("Unsupported recovery-key version.");
                if (metadata.Mode == "Vault")
                {
                    var encoded = await vault.ReadAsync(metadata.Reference, token) ?? throw new InvalidOperationException("The recovery key is missing from the OS vault. Existing recordings were retained.");
                    var key = Convert.FromBase64String(encoded);
                    if (key.Length != 32) { CryptographicOperations.ZeroMemory(key); throw new CryptographicException("Invalid recovery key."); }
                    _key = key; ProtectionMode = "OS vault"; return key;
                }
                if (metadata.Mode != "Passphrase" || metadata.Salt?.Length != 32 || metadata.Nonce?.Length != 12 || metadata.Tag?.Length != 16 || metadata.WrappedKey?.Length != 32)
                    throw new InvalidDataException("Invalid recovery-key envelope.");
                var passphrase = await PromptAsync(false, token);
                var derived = await DeriveAsync(passphrase, metadata.Salt, token);
                var plain = new byte[32];
                try
                {
                    using var aes = new AesGcm(derived, 16);
                    aes.Decrypt(metadata.Nonce, metadata.WrappedKey, metadata.Tag, plain, "Tessera/recovery-key/v1"u8);
                    _key = plain; ProtectionMode = "Recovery passphrase"; return plain;
                }
                catch { CryptographicOperations.ZeroMemory(plain); throw; }
                finally { CryptographicOperations.ZeroMemory(derived); }
            }
            byte[] candidate = RandomNumberGenerator.GetBytes(32);
            try
            {
                RecoveryKeyDocument envelope;
                try
                {
                    await vault.WriteAsync(_reference, Convert.ToBase64String(candidate), token);
                    envelope = new(1, "Vault", _reference); ProtectionMode = "OS vault";
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    string password = await PromptAsync(true, token);
                    var salt = RandomNumberGenerator.GetBytes(32); var derived = await DeriveAsync(password, salt, token);
                    var nonce = RandomNumberGenerator.GetBytes(12); var tag = new byte[16]; var ciphertext = new byte[32];
                    try { using var aes = new AesGcm(derived, 16); aes.Encrypt(nonce, candidate, ciphertext, tag, "Tessera/recovery-key/v1"u8); }
                    finally { CryptographicOperations.ZeroMemory(derived); }
                    envelope = new(1, "Passphrase", _reference, salt, nonce, tag, ciphertext); ProtectionMode = "Recovery passphrase";
                }
                await AtomicFile.WriteJsonAsync(_path, envelope, token);
                _key = candidate; return candidate;
            }
            catch { CryptographicOperations.ZeroMemory(candidate); throw; }
        }
        finally { _gate.Release(); }
    }
    private async Task<string> PromptAsync(bool create, CancellationToken token)
    {
        if (PassphrasePrompt is null) throw new InvalidOperationException("Crash recovery requires an unlocked OS vault or an explicit recovery passphrase.");
        var passphrase = await PassphrasePrompt(create, token);
        if (passphrase is null) throw new OperationCanceledException(token);
        if (passphrase.Length < 12 || passphrase.Length > 4096) throw new ArgumentException("Use a recovery passphrase between 12 and 4,096 characters.");
        return passphrase;
    }
    private static Task<byte[]> DeriveAsync(string passphrase, byte[] salt, CancellationToken token) =>
        Task.Run(() => Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, 600000, HashAlgorithmName.SHA256, 32), token);
    public void Dispose() { if (_key is not null) CryptographicOperations.ZeroMemory(_key); _key = null; }
}

public sealed class CaptureRecoveryStore(string directory, RecoveryKeyStore keys)
{
    private readonly string _directory = Path.Combine(directory, "recovery");
    private readonly SemaphoreSlim _gate = new(1, 1);
    public const int MaximumCaptureBytes = 64 * 1024 * 1024;
    private string PathFor(Guid id) => Path.Combine(_directory, id.ToString("N") + ".tsrec");
    public RecoveryItem[] List()
    {
        if (!Directory.Exists(_directory)) return [];
        return new DirectoryInfo(_directory).EnumerateFiles("*.tsrec").Take(513)
            .Where(f => Guid.TryParseExact(Path.GetFileNameWithoutExtension(f.Name), "N", out _))
            .Select(f => new RecoveryItem(Guid.ParseExact(Path.GetFileNameWithoutExtension(f.Name), "N"), f.LastWriteTimeUtc, f.Length))
            .OrderByDescending(f => f.ModifiedUtc).ToArray();
    }
    public async Task SaveAsync(Guid id, RecoveredCapture capture, CancellationToken token = default)
    {
        if (id == Guid.Empty) throw new ArgumentException("Empty capture identity.");
        var key = await keys.UnlockAsync(token);
        capture = capture with { Session = CapturePrivacy.OutputOnly(capture.Session) };
        byte[] plain = await Task.Run(() => JsonSerializer.SerializeToUtf8Bytes(capture, WorkspaceStore.Json), token).ConfigureAwait(false);
        try
        {
            if (plain.Length > MaximumCaptureBytes) throw new IOException("The recovery snapshot reached 64 MiB. Save the recording explicitly and start a new capture.");
            var data = new byte[32 + plain.Length]; "TSR1"u8.CopyTo(data);
            RandomNumberGenerator.Fill(data.AsSpan(4, 12));
            using (var aes = new AesGcm(key, 16)) aes.Encrypt(data.AsSpan(4, 12), plain, data.AsSpan(32), data.AsSpan(16, 16), id.ToByteArray());
            await _gate.WaitAsync(token);
            try
            {
                var existing = List();
                if (existing.Count(f => f.Id != id) >= 512 || existing.Where(f => f.Id != id).Sum(f => f.Bytes) + data.Length > 256L * 1024 * 1024)
                    throw new IOException("Recovery storage reached its 256 MiB quota. Export or discard old recordings; none were deleted automatically.");
                await AtomicFile.WriteAsync(PathFor(id), data, token);
            }
            finally { _gate.Release(); }
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public async Task<RecoveredCapture> LoadAsync(Guid id, CancellationToken token = default)
    {
        var key = await keys.UnlockAsync(token);
        var path = PathFor(id);
        if (new FileInfo(path).Length is < 32 or > MaximumCaptureBytes + 32) throw new InvalidDataException("Invalid recovery file size.");
        var data = await AtomicFile.ReadBytesAsync(path, MaximumCaptureBytes + 32, token);
        if (!data.AsSpan(0, 4).SequenceEqual("TSR1"u8)) throw new InvalidDataException("Unsupported recovery file.");
        var plain = new byte[data.Length - 32];
        try
        {
            using (var aes = new AesGcm(key, 16)) aes.Decrypt(data.AsSpan(4, 12), data.AsSpan(32), data.AsSpan(16, 16), plain, id.ToByteArray());
            var value = JsonSerializer.Deserialize<RecoveredCapture>(plain, WorkspaceStore.Json) ?? throw new InvalidDataException("Empty recovery recording.");
            if (value.Session is null || value.Session.Events.Count > 1000000) throw new InvalidDataException("Invalid recovered capture.");
            // Use the upstream validator/serializer before a recording is exposed to replay.
            using var roundTrip = new MemoryStream();
            await TerminalCaptureSessionSerializer.SaveAsync(value.Session, roundTrip, token); roundTrip.Position = 0;
            var validated = await TerminalCaptureSessionSerializer.LoadAsync(roundTrip, token);
            return value with { Session = validated };
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public async Task DeleteAsync(Guid id, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try { File.Delete(PathFor(id)); }
        finally { _gate.Release(); }
    }
}

/// <summary>Checkpoints opt-in captures every two seconds and on explicit stop/close. Keeps prior capture generations.</summary>
public sealed class CaptureRecoveryService : IDisposable
{
    private sealed class Track(SessionRuntime session)
    {
        public SessionRuntime Session { get; } = session;
        public Guid Id { get; } = Guid.NewGuid();
        public int LastCount { get; set; } = -1;
        public int ExportedCount { get; set; } = -1;
    }
    private readonly Dictionary<Guid, Track> _tracks = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DispatcherTimer _timer;
    private bool _disposed;
    public RecoveryKeyStore Keys { get; }
    public CaptureRecoveryStore Store { get; }
    public event Action<string>? Error;
    public CaptureRecoveryService(string directory, ICredentialVault vault)
    {
        Keys = new(directory, vault); Store = new(directory, Keys);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += async (_, _) =>
        {
            try { await CheckpointAllAsync(); }
            catch (Exception ex)
            {
                _timer.Stop();
                foreach (var track in _tracks.Values) if (track.Session.Capture.IsCaptureActive) track.Session.Capture.StopCapture();
                Error?.Invoke("Capture stopped because recovery could not checkpoint. Existing recordings were retained; export the in-memory capture now. " + ex.Message);
            }
        };
    }
    public async Task StartAsync(SessionRuntime session, CancellationToken token = default)
    {
        await Keys.UnlockAsync(token);
        if (_tracks.ContainsKey(session.Id)) await CheckpointAsync(session, token);
        session.Capture.StartCapture(); _tracks[session.Id] = new(session);
        await CheckpointAsync(session, token); _timer.Start();
    }
    public async Task CheckpointAllAsync(CancellationToken token = default)
    {
        foreach (var track in _tracks.Values.ToArray()) await CheckpointAsync(track.Session, token);
    }
    public async Task CheckpointAsync(SessionRuntime session, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (!_tracks.TryGetValue(session.Id, out var track)) return;
            var snapshot = session.Capture.GetCaptureSnapshot();
            if (snapshot is null || snapshot.Events.Count == track.LastCount || snapshot.Events.Count <= track.ExportedCount) return;
            if (snapshot.Events.Count > 100000 || snapshot.Events.Sum(e => (long)(e.Data?.Length ?? 0)) > 32L * 1024 * 1024)
            {
                if (session.Capture.IsCaptureActive) session.Capture.StopCapture();
                Error?.Invoke("Capture reached its 32 MiB / 100,000-event memory budget and was stopped. Export it before starting another.");
            }
            await Store.SaveAsync(track.Id, new(session.DocumentTitle, session.Profile.Id, snapshot), token);
            track.LastCount = snapshot.Events.Count;
        }
        finally { _gate.Release(); }
    }
    public async Task MarkExportedAsync(SessionRuntime session, TerminalCaptureSession snapshot)
    {
        await _gate.WaitAsync();
        try
        {
            if (!_tracks.TryGetValue(session.Id, out var track)) return;
            track.ExportedCount = snapshot.Events.Count;
            if (track.LastCount <= track.ExportedCount) await Store.DeleteAsync(track.Id);
        }
        finally { _gate.Release(); }
    }
    public async Task RetainOnCloseAsync(SessionRuntime session)
    {
        if (session.Capture.IsCaptureActive) session.Capture.StopCapture();
        await CheckpointAsync(session); _tracks.Remove(session.Id);
    }
    public async Task PrepareCloseAsync()
    {
        _timer.Stop();
        foreach (var track in _tracks.Values.ToArray()) await RetainOnCloseAsync(track.Session);
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _timer.Stop(); Keys.Dispose(); }
}
