using System.Text.Json.Serialization;

namespace Tessera.Core;

public enum SplitAxis { Columns, Rows }
public enum DockEdge { Center, Left, Right, Top, Bottom }

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(TabGroup), "group")]
[JsonDerivedType(typeof(DockSplit), "split")]
public abstract record DockNode(Guid Id);
public sealed record TabGroup(Guid Id, Guid[] Tabs, Guid? Active) : DockNode(Id);
public sealed record DockSplit(Guid Id, SplitAxis Axis, double Ratio, DockNode First, DockNode Second) : DockNode(Id);
public sealed record TerminalDocument(Guid Id, string ProfileId, string Title, bool Pinned = false);
public sealed record Workspace(Guid Id, string Name, string Description, DockNode Root, Dictionary<Guid, TerminalDocument> Documents, string Notes = "")
{
    public static Workspace Empty(string name) => new(Guid.NewGuid(), name, "Your sessions. Your way of working.", new TabGroup(Guid.NewGuid(), [], null), []);
}
public sealed record SavedLayout(Guid Id, string Name, DockNode Root, Dictionary<Guid, TerminalDocument> Documents);
public sealed record Snippet(Guid Id, string Name, string Command, string Category);
public sealed record HistoryEntry(DateTimeOffset Time, string Command, string ProfileId, string Directory);
public sealed record AppPreferences(string Theme = "Obsidian", double FontSize = 13, string FontFamily = "", bool Compact = false, bool ConfirmClose = true, bool RestoreLocalSessions = true, bool ReducedMotion = false, double ToolSize = 184, bool ToolsOnRight = false, string Tool = "Commands");
public sealed record AppDocument(int Version, Guid ActiveWorkspace, Workspace[] Workspaces, SavedLayout[] Layouts, Snippet[] Snippets, AppPreferences Preferences, Dictionary<string, string> Bindings);

/// <summary>Pure layout algebra. Visuals and live sessions never enter this model.</summary>
public static class Layout
{
    public static IEnumerable<TabGroup> Groups(DockNode node) => node switch
    {
        TabGroup group => [group],
        DockSplit split => Groups(split.First).Concat(Groups(split.Second)),
        _ => throw new InvalidDataException("Unknown dock node.")
    };

    public static DockNode Replace(DockNode node, Guid id, Func<DockNode, DockNode> map) => node.Id == id ? map(node) : node switch
    {
        DockSplit split => split with { First = Replace(split.First, id, map), Second = Replace(split.Second, id, map) },
        _ => node
    };

    public static Workspace Add(Workspace workspace, Guid groupId, TerminalDocument document)
    {
        if (workspace.Documents.ContainsKey(document.Id)) throw new InvalidOperationException("Document already exists.");
        var group = Groups(workspace.Root).Single(g => g.Id == groupId);
        var docs = new Dictionary<Guid, TerminalDocument>(workspace.Documents) { [document.Id] = document };
        return Checked(workspace with { Documents = docs, Root = Replace(workspace.Root, groupId, _ => group with { Tabs = [.. group.Tabs, document.Id], Active = document.Id }) });
    }

    public static Workspace Activate(Workspace workspace, Guid id)
    {
        var group = Groups(workspace.Root).Single(g => g.Tabs.Contains(id));
        return Checked(workspace with { Root = Replace(workspace.Root, group.Id, _ => group with { Active = id }) });
    }

    public static Workspace Update(Workspace workspace, TerminalDocument document)
    {
        if (!workspace.Documents.ContainsKey(document.Id)) throw new KeyNotFoundException();
        var docs = new Dictionary<Guid, TerminalDocument>(workspace.Documents) { [document.Id] = document };
        return Checked(workspace with { Documents = docs });
    }

    public static Workspace Close(Workspace workspace, Guid documentId)
    {
        if (!workspace.Documents.ContainsKey(documentId)) return workspace;
        var docs = new Dictionary<Guid, TerminalDocument>(workspace.Documents);
        docs.Remove(documentId);
        return Checked(workspace with { Documents = docs, Root = Prune(RemoveTab(workspace.Root, documentId)) ?? new TabGroup(Guid.NewGuid(), [], null) });
    }

    public static Workspace Move(Workspace workspace, Guid documentId, Guid targetGroup, DockEdge edge, int index = int.MaxValue)
    {
        if (!workspace.Documents.ContainsKey(documentId)) throw new KeyNotFoundException("Unknown document.");
        var target = Groups(workspace.Root).Single(g => g.Id == targetGroup);
        // A sole tab cannot be split against itself. This is a no-op, not a lost session.
        if (target.Tabs.Length == 1 && target.Tabs[0] == documentId && edge != DockEdge.Center) return workspace;
        var root = RemoveTab(workspace.Root, documentId);
        root = Replace(root, targetGroup, n =>
        {
            var group = (TabGroup)n;
            if (edge == DockEdge.Center)
            {
                var tabs = group.Tabs.ToList();
                tabs.Insert(Math.Clamp(index, 0, tabs.Count), documentId);
                return group with { Tabs = tabs.ToArray(), Active = documentId };
            }
            var added = new TabGroup(Guid.NewGuid(), [documentId], documentId);
            var first = edge is DockEdge.Left or DockEdge.Top;
            return new DockSplit(Guid.NewGuid(), edge is DockEdge.Left or DockEdge.Right ? SplitAxis.Columns : SplitAxis.Rows, .5, first ? added : group, first ? group : added);
        });
        return Checked(workspace with { Root = Prune(root)! });
    }

    public static Workspace Resize(Workspace workspace, Guid splitId, double ratio)
    {
        if (!double.IsFinite(ratio)) throw new ArgumentOutOfRangeException(nameof(ratio));
        return Checked(workspace with { Root = Replace(workspace.Root, splitId, n => ((DockSplit)n) with { Ratio = Math.Clamp(ratio, .1, .9) }) });
    }

    public static Workspace Arrange(Workspace workspace, string preset)
    {
        var ids = Groups(workspace.Root).SelectMany(g => g.Tabs).ToArray();
        if (ids.Length == 0) return workspace;
        var count = Math.Min(ids.Length, preset switch { "Focus" => 1, "Two columns" or "Two rows" => 2, "Four-way inspection" => 4, _ => 3 });
        var groups = Enumerable.Range(0, count).Select(i =>
        {
            var tabs = ids.Where((_, n) => n % count == i).ToArray();
            return new TabGroup(Guid.NewGuid(), tabs, tabs[0]);
        }).ToArray();
        DockNode Pair(DockNode a, DockNode b, SplitAxis axis, double ratio = .5) => new DockSplit(Guid.NewGuid(), axis, ratio, a, b);
        DockNode root = count switch
        {
            1 => groups[0],
            2 => Pair(groups[0], groups[1], preset == "Two rows" ? SplitAxis.Rows : SplitAxis.Columns),
            3 when preset == "Three columns" => Pair(groups[0], Pair(groups[1], groups[2], SplitAxis.Columns), SplitAxis.Columns, 1d / 3),
            3 => Pair(groups[0], Pair(groups[1], groups[2], SplitAxis.Rows), SplitAxis.Columns, .62),
            _ => Pair(Pair(groups[0], groups[1], SplitAxis.Rows), Pair(groups[2], groups[3], SplitAxis.Rows), SplitAxis.Columns)
        };
        return Checked(workspace with { Root = root });
    }

    public static Workspace RestoreLayout(Workspace workspace, SavedLayout saved)
    {
        // A saved arrangement describes new slots, not revived processes. Fresh IDs avoid aliasing a live session.
        var map = saved.Documents.Keys.ToDictionary(id => id, _ => Guid.NewGuid());
        DockNode Clone(DockNode n) => n switch
        {
            TabGroup g => new TabGroup(Guid.NewGuid(), g.Tabs.Select(id => map[id]).ToArray(), g.Active is {} a ? map[a] : null),
            DockSplit s => new DockSplit(Guid.NewGuid(), s.Axis, s.Ratio, Clone(s.First), Clone(s.Second)),
            _ => throw new InvalidDataException()
        };
        return Checked(workspace with { Root = Clone(saved.Root), Documents = saved.Documents.Values.ToDictionary(d => map[d.Id], d => d with { Id = map[d.Id] }) });
    }

    private static DockNode RemoveTab(DockNode node, Guid id) => node switch
    {
        TabGroup group => group with { Tabs = group.Tabs.Where(x => x != id).ToArray(), Active = group.Active == id ? group.Tabs.Where(x => x != id).Select(x => (Guid?)x).FirstOrDefault() : group.Active },
        DockSplit split => split with { First = RemoveTab(split.First, id), Second = RemoveTab(split.Second, id) },
        _ => node
    };
    private static DockNode? Prune(DockNode node) => node switch
    {
        TabGroup group => group.Tabs.Length == 0 ? null : group,
        DockSplit split => (Prune(split.First), Prune(split.Second)) switch
        {
            (null, var b) => b,
            (var a, null) => a,
            ({} a, {} b) => split with { First = a, Second = b }
        },
        _ => throw new InvalidDataException()
    };
    public static Workspace Checked(Workspace workspace) { Validate(workspace); return workspace; }
    public static void Validate(Workspace workspace)
    {
        if (workspace.Id == Guid.Empty || string.IsNullOrWhiteSpace(workspace.Name) || workspace.Name.Length > 120 || workspace.Documents is null || workspace.Documents.Count > 512) throw new InvalidDataException("Invalid workspace.");
        var nodes = new HashSet<Guid>();
        var documents = new HashSet<Guid>();
        Visit(workspace.Root, 0);
        if (!documents.SetEquals(workspace.Documents.Keys)) throw new InvalidDataException("Unattached document.");
        foreach (var (id, d) in workspace.Documents)
            if (id == Guid.Empty || id != d.Id || string.IsNullOrWhiteSpace(d.ProfileId) || string.IsNullOrWhiteSpace(d.Title) || d.Title.Length > 256) throw new InvalidDataException("Invalid terminal document.");
        void Visit(DockNode node, int depth)
        {
            if (node is null || depth > 24 || nodes.Count >= 1024 || node.Id == Guid.Empty || !nodes.Add(node.Id)) throw new InvalidDataException("Invalid dock tree identity or depth.");
            switch (node)
            {
                case TabGroup group:
                    if (group.Tabs is null || (group.Tabs.Length > 0 && (group.Active is null || !group.Tabs.Contains(group.Active.Value))) || (group.Tabs.Length == 0 && (group.Active is not null || depth != 0))) throw new InvalidDataException("Invalid tab group.");
                    foreach (var id in group.Tabs) if (!documents.Add(id) || !workspace.Documents.ContainsKey(id)) throw new InvalidDataException("A document must have exactly one owner.");
                    break;
                case DockSplit split:
                    if (!Enum.IsDefined(split.Axis) || !double.IsFinite(split.Ratio) || split.Ratio is < .1 or > .9) throw new InvalidDataException("Invalid split ratio.");
                    Visit(split.First, depth + 1); Visit(split.Second, depth + 1); break;
                default: throw new InvalidDataException("Unsupported dock node.");
            }
        }
    }
}
