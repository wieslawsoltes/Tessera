namespace Tessera.Core;

public sealed record ShaderDefinition(string Name, string Source, string Language, bool Animated);
public sealed record ShaderDocument(int Version, Dictionary<Guid, ShaderDefinition[]> Sessions);

/// <summary>Persists source, not driver- or machine-dependent compiled shader binaries.</summary>
public sealed class ShaderStore(string path)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, ShaderDefinition[]> _sessions = [];
    private readonly object _sync = new();
    private Exception? _loadFailure;
    public ShaderDefinition[] Get(Guid id) { lock (_sync) return _sessions.GetValueOrDefault(id, []).ToArray(); }
    public void Set(Guid id, ShaderDefinition[] definitions)
    {
        if (id == Guid.Empty) throw new InvalidDataException("Invalid shader owner.");
        Validate(definitions);
        lock (_sync)
        {
            if (!_sessions.ContainsKey(id) && _sessions.Count >= 512) throw new InvalidOperationException("Too many saved shader pipelines.");
            if (_sessions.Where(p => p.Key != id).Sum(p => p.Value.Sum(d => (long)d.Source.Length + d.Name.Length + 128)) + definitions.Sum(d => (long)d.Source.Length + d.Name.Length + 128) > 1024 * 1024)
                throw new InvalidDataException("Saved shader source exceeds its aggregate 1 MiB character budget.");
            if (definitions.Length == 0) _sessions.Remove(id); else _sessions[id] = definitions.ToArray();
        }
    }
    public void Retain(ISet<Guid> documents) { lock (_sync) foreach (var key in _sessions.Keys.Where(k => !documents.Contains(k)).ToArray()) _sessions.Remove(key); }
    public static void Validate(ShaderDefinition[] definitions)
    {
        if (definitions is null || definitions.Length > 4) throw new InvalidDataException("At most four shader passes are supported.");
        foreach (var d in definitions)
            if (d is null || string.IsNullOrWhiteSpace(d.Name) || d.Name.Length > 128 || string.IsNullOrWhiteSpace(d.Source) || d.Source.Length > 262144 ||
                d.Language is not ("SkiaRuntimeEffect" or "GhosttyShadertoy" or "WindowsTerminalHlsl")) throw new InvalidDataException("Invalid shader definition.");
    }
    public async Task LoadAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            var data = await AtomicFile.ReadJsonAsync<ShaderDocument>(path, 8 * 1024 * 1024, token);
            if (data is null) return;
            if (data.Version != 1 || data.Sessions is null || data.Sessions.Count > 512) throw new InvalidDataException("Invalid shader document.");
            var validated = new ShaderStore(path);
            foreach (var (id, shaders) in data.Sessions) validated.Set(id, shaders);
            lock (_sync) { _sessions.Clear(); foreach (var item in validated._sessions) _sessions.Add(item.Key, item.Value); }
            _loadFailure = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _loadFailure = ex; throw; }
        finally { _gate.Release(); }
    }
    public async Task SaveAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (_loadFailure is not null) throw new InvalidDataException("Saved shaders were retained because loading failed. Repair shaders.json before saving.", _loadFailure);
            Dictionary<Guid, ShaderDefinition[]> copy;
            lock (_sync) copy = new(_sessions);
            await AtomicFile.WriteJsonAsync(path, new ShaderDocument(1, copy), token);
        }
        finally { _gate.Release(); }
    }
}
