using System.Text.Json;
using Tessera.Core;
using Xunit;

namespace Tessera.Tests;

public class LayoutTests
{
    private static Workspace Sample(int count = 4)
    {
        var w = Workspace.Empty("Development");
        for (var i = 0; i < count; i++) w = Layout.Add(w, w.Root.Id, new(Guid.NewGuid(), "local", "shell " + i));
        return w;
    }
    [Theory]
    [InlineData("Focus")][InlineData("Two columns")][InlineData("Two rows")][InlineData("Build & monitor")][InlineData("Three columns")][InlineData("Four-way inspection")]
    public void PresetsPreserveEveryDocument(string preset)
    {
        var w = Sample(9); var arranged = Layout.Arrange(w, preset);
        Assert.Equal(w.Documents.Keys.Order(), arranged.Documents.Keys.Order()); Layout.Validate(arranged);
    }
    [Theory]
    [InlineData(DockEdge.Center)][InlineData(DockEdge.Left)][InlineData(DockEdge.Right)][InlineData(DockEdge.Top)][InlineData(DockEdge.Bottom)]
    public void MovingPreservesSessionIdentity(DockEdge edge)
    {
        var w = Layout.Arrange(Sample(), "Build & monitor"); var groups = Layout.Groups(w.Root).ToArray(); var id = groups[0].Tabs[0];
        var moved = Layout.Move(w, id, groups[1].Id, edge); Layout.Validate(moved);
        Assert.Same(w.Documents[id], moved.Documents[id]); Assert.Equal(1, Layout.Groups(moved.Root).Count(g => g.Tabs.Contains(id)));
    }
    [Fact] public void MovingSoleTabAgainstItselfIsNoOp() { var w = Sample(1); Assert.Same(w, Layout.Move(w, w.Documents.Keys.Single(), w.Root.Id, DockEdge.Right)); }
    [Fact] public void LastCloseProducesValidEmptyRoot() { var w = Sample(1); w = Layout.Close(w, w.Documents.Keys.Single()); Assert.Empty(w.Documents); Assert.IsType<TabGroup>(w.Root); Layout.Validate(w); }
    [Fact] public void ClosePrunesEmptySplits() { var w = Layout.Arrange(Sample(2), "Two columns"); w = Layout.Close(w, w.Documents.Keys.First()); Assert.IsType<TabGroup>(w.Root); }
    [Fact] public void DuplicateTabOwnershipIsRejected() { var w = Sample(1); var g = (TabGroup)w.Root; Assert.Throws<InvalidDataException>(() => Layout.Validate(w with { Root = g with { Tabs = [g.Tabs[0], g.Tabs[0]] } })); }
    [Fact] public void InvalidRatioIsRejected() { var w = Layout.Arrange(Sample(2), "Two columns"); Assert.Throws<InvalidDataException>(() => Layout.Validate(w with { Root = ((DockSplit)w.Root) with { Ratio = double.NaN } })); }
    [Fact] public void ResizeClampsWithoutMutatingOldState() { var w = Layout.Arrange(Sample(2), "Two columns"); var next = Layout.Resize(w, w.Root.Id, 3); Assert.Equal(.9, ((DockSplit)next.Root).Ratio); Assert.Equal(.5, ((DockSplit)w.Root).Ratio); }
    [Fact] public void SerializationRoundTripsPolymorphicTree() { var w = Layout.Arrange(Sample(), "Four-way inspection"); var json = JsonSerializer.Serialize(w, WorkspaceStore.Json); var loaded = JsonSerializer.Deserialize<Workspace>(json, WorkspaceStore.Json)!; Layout.Validate(loaded); Assert.Equal(4, loaded.Documents.Count); }
    [Fact] public void SavedLayoutsCreateFreshDocumentIdentities() { var w = Sample(); var restored = Layout.RestoreLayout(w, new(Guid.NewGuid(), "Saved", w.Root, w.Documents)); Assert.Empty(w.Documents.Keys.Intersect(restored.Documents.Keys)); Layout.Validate(restored); }
    [Fact] public void UndoRedoPreserveImmutableSnapshots() { var before = Sample(); var after = Layout.Arrange(before, "Two columns"); var history = new WorkspaceHistory(); history.Push(before); Assert.Same(before, history.Undo(after)); Assert.Same(after, history.Redo(before)); }
    [Theory][InlineData("echo hi", false)][InlineData("echo a\necho b", true)][InlineData("\u001b[31m", true)][InlineData("\0", true)]
    public void PasteSafetyDetectsControls(string text, bool confirm) => Assert.Equal(confirm, Safety.RequiresPasteConfirmation(text));
    [Theory][InlineData(true,false,true,false,true,false)][InlineData(false,true,true,false,true,false)][InlineData(false,false,true,true,true,false)][InlineData(false,false,true,false,false,false)][InlineData(false,false,true,false,true,true)]
    public void BroadcastIsOptInAndNeverProduction(bool prod, bool locked, bool running, bool replay, bool optIn, bool allowed) => Assert.Equal(allowed, Safety.CanBroadcast(prod, locked, running, replay, optIn));
    [Fact] public void FuzzDockingMaintainsAllInvariants()
    {
        var rng = new Random(42); var w = Sample(20);
        for (int i = 0; i < 500; i++)
        {
            var groups = Layout.Groups(w.Root).ToArray(); var ids = w.Documents.Keys.ToArray();
            if (groups.Length > 10) w = Layout.Arrange(w, "Four-way inspection");
            else w = Layout.Move(w, ids[rng.Next(ids.Length)], groups[rng.Next(groups.Length)].Id, (DockEdge)rng.Next(5));
            Layout.Validate(w); Assert.Equal(20, w.Documents.Count);
        }
    }
    [Fact] public async Task AtomicStorePersistsAndRetainsBackup()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tessera-test-" + Guid.NewGuid());
        try
        {
            var w = Sample(); var doc = new AppDocument(1, w.Id, [w], [], [], new(), []); var store = new WorkspaceStore(Path.Combine(directory, "workspace.json"));
            await store.SaveAsync(doc); await store.SaveAsync(doc); var loaded = await store.LoadAsync(); Assert.Equal(w.Id, loaded!.ActiveWorkspace); Assert.True(File.Exists(store.Path + ".bak"));
        }
        finally { if(Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
