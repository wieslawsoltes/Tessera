using System.Text.RegularExpressions;

namespace Tessera.Core;

public sealed record CompletedCommand(string Id, string Command, DateTimeOffset Started, DateTimeOffset Completed,
    int? ExitCode, string? Profile, string? Host, string? Directory);
public sealed record RankedCommand(CompletedCommand Entry, int Uses, double Score);
public sealed record CommandHistoryDocument(int Version, CompletedCommand[] Entries);

/// <summary>Semantic completed commands only. Persistence is opt-in; never observes keystrokes.</summary>
public sealed partial class CommandHistoryStore(string path, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly List<CompletedCommand> _entries = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private bool _loaded;
    private Exception? _loadFailure;
    public bool Persist { get; set; }
    public int RetentionDays { get; set; } = 90;
    public const int MaximumEntries = 10000;
    public IReadOnlyList<CompletedCommand> Entries { get { lock (_sync) return _entries.ToArray(); } }

    // Deliberately conservative: omit likely credential commands rather than partially redact executable text.
    [GeneratedRegex(@"(?i)(?:password|passwd|token|secret|api[_-]?key|authorization)\s*(?:=|:|\s)\s*\S+|-----BEGIN .*PRIVATE KEY-----", RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveCommand();

    public bool Add(CompletedCommand entry)
    {
        if (entry is null || entry.Command is null || entry.Id?.Length is not (> 0 and <= 128) ||
            entry.Profile?.Length > 512 || entry.Host?.Length > 512 || entry.Directory?.Length > 4096 ||
            entry.Completed < entry.Started || entry.Completed > _clock.GetUtcNow().AddDays(1) ||
            !Safety.IsSafeCommandInsertion(entry.Command) || entry.Command.Length > 8192 ||
            entry.Command.StartsWith(' ') || SensitiveCommand().IsMatch(entry.Command) ||
            string.IsNullOrWhiteSpace(entry.Id)) return false;
        lock (_sync)
        {
            if (_entries.Any(e => e.Id == entry.Id)) return false;
            _entries.Insert(0, entry);
            Prune();
        }
        return true;
    }

    private void Prune()
    {
        var cutoff = _clock.GetUtcNow().AddDays(-Math.Clamp(RetentionDays, 1, 3650));
        _entries.RemoveAll(e => e.Completed < cutoff);
        _entries.Sort((a, b) => b.Completed.CompareTo(a.Completed));
        if (_entries.Count > MaximumEntries) _entries.RemoveRange(MaximumEntries, _entries.Count - MaximumEntries);
        long characters = 0;
        for (int i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            characters += e.Command.Length + e.Id.Length + (e.Profile?.Length ?? 0) + (e.Host?.Length ?? 0) + (e.Directory?.Length ?? 0) + 256;
            if (characters > 2 * 1024 * 1024) { _entries.RemoveRange(i, _entries.Count - i); break; }
        }
    }

    public RankedCommand[] Search(string query, string? profile = null, string? directory = null, int count = 100)
    {
        var now = _clock.GetUtcNow();
        lock (_sync)
        {
            Prune();
            return _entries.Where(e => e.Command.Contains(query, StringComparison.OrdinalIgnoreCase))
                .GroupBy(e => (e.Command, e.Profile, e.Host))
                .Select(g =>
                {
                    var newest = g.First();
                    double age = Math.Max(0, (now - newest.Completed).TotalDays);
                    double score = 5 * Math.Log2(1 + g.Count()) + 12 / (1 + age / 7) +
                        (profile is not null && newest.Profile == profile ? 5 : 0) +
                        (directory is not null && newest.Directory == directory ? 4 : 0) +
                        (newest.ExitCode == 0 ? 1 : 0) +
                        (query.Length > 0 && newest.Command.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 8 : 0);
                    return new RankedCommand(newest, g.Count(), score);
                }).OrderByDescending(e => e.Score).ThenBy(e => e.Entry.Command, StringComparer.Ordinal)
                .Take(Math.Clamp(count, 1, 1000)).ToArray();
        }
    }

    public async Task LoadAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (_loaded) return;
            var document = await AtomicFile.ReadJsonAsync<CommandHistoryDocument>(path, 16 * 1024 * 1024, token);
            if (document is not null)
            {
                if (document.Version != 1 || document.Entries is null || document.Entries.Length > MaximumEntries)
                    throw new InvalidDataException("Unsupported command history.");
                foreach (var entry in document.Entries.Reverse()) if (entry is not null) Add(entry);
            }
            _loaded = true; _loadFailure = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _loadFailure = ex; throw; }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (!Persist) return;
            if (_loadFailure is not null) throw new InvalidDataException("History was not overwritten because loading failed. Use Clear history to reset it explicitly.", _loadFailure);
            CompletedCommand[] entries;
            lock (_sync) { Prune(); entries = _entries.ToArray(); }
            await AtomicFile.WriteJsonAsync(path, new CommandHistoryDocument(1, entries), token);
        }
        finally { _gate.Release(); }
    }

    public async Task ClearAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try { lock (_sync) _entries.Clear(); if (File.Exists(path)) File.Delete(path); _loadFailure = null; _loaded = true; }
        finally { _gate.Release(); }
    }
}
