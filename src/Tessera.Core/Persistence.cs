using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tessera.Core;

public sealed class WorkspaceStore(string path)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public string Path { get; } = System.IO.Path.GetFullPath(path);
    public static JsonSerializerOptions Json { get; } = new() { WriteIndented = true, MaxDepth = 64, Converters = { new JsonStringEnumConverter() } };

    public async Task<AppDocument?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(Path)) return null;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (new FileInfo(Path).Length > 16 * 1024 * 1024) throw new InvalidDataException("Workspace file is too large.");
            await using var stream = File.OpenRead(Path);
            var document = await JsonSerializer.DeserializeAsync<AppDocument>(stream, Json, cancellationToken) ?? throw new InvalidDataException("Empty workspace file.");
            Validate(document);
            return document;
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(AppDocument document, CancellationToken cancellationToken = default)
    {
        Validate(document);
        await _gate.WaitAsync(cancellationToken);
        string temporary = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16384, FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, document, Json, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            if (File.Exists(Path)) File.Copy(Path, Path + ".bak", true);
            File.Move(temporary, Path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            _gate.Release();
        }
    }

    public static void Validate(AppDocument document)
    {
        if (document.Version != 1 || document.Workspaces is null || document.Workspaces.Length is < 1 or > 64 || document.Workspaces.Select(w => w.Id).Distinct().Count() != document.Workspaces.Length || !document.Workspaces.Any(w => w.Id == document.ActiveWorkspace)) throw new InvalidDataException("Unsupported or invalid application workspace file.");
        foreach (var workspace in document.Workspaces) Layout.Validate(workspace);
        if (document.Workspaces.SelectMany(w => w.Documents.Keys).Distinct().Count() != document.Workspaces.Sum(w => w.Documents.Count)) throw new InvalidDataException("Duplicate document across workspaces.");
        if (document.Preferences is null || document.Preferences.Theme is not ("Obsidian" or "Porcelain" or "Blueprint") || !double.IsFinite(document.Preferences.FontSize) || document.Preferences.FontSize is < 8 or > 36 || document.Bindings is null || document.Snippets is null || document.Layouts is null) throw new InvalidDataException("Invalid preferences.");
        if (document.Snippets.Length > 1000 || document.Layouts.Length > 100) throw new InvalidDataException("Too many saved items.");
        foreach (var saved in document.Layouts) Layout.Validate(new Workspace(saved.Id, saved.Name, "", saved.Root, saved.Documents));
    }
}

public sealed class WorkspaceHistory(int capacity = 64)
{
    private readonly List<Workspace> _undo = [];
    private readonly Stack<Workspace> _redo = [];
    public bool CanUndo => _undo.Count != 0;
    public bool CanRedo => _redo.Count != 0;
    public void Push(Workspace before)
    {
        Layout.Validate(before); _undo.Add(before); _redo.Clear();
        if (_undo.Count > capacity) _undo.RemoveAt(0);
    }
    public Workspace Undo(Workspace current)
    {
        if (!CanUndo) return current;
        _redo.Push(current); var result = _undo[^1]; _undo.RemoveAt(_undo.Count - 1); return result;
    }
    public Workspace Redo(Workspace current)
    {
        if (!CanRedo) return current;
        _undo.Add(current); return _redo.Pop();
    }
    public void Clear() { _undo.Clear(); _redo.Clear(); }
}
