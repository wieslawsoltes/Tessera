using System.Text;
using System.Text.Json;
using Tessera.Core;
using Xunit;

namespace Tessera.Tests;

public sealed class AcceptanceCoreTests
{
    private static Workspace Sample(int count=8)
    {
        var workspace=Workspace.Empty("Acceptance");
        for(int i=0;i<count;i++)workspace=Layout.Add(workspace,workspace.Root.Id,new(Guid.NewGuid(),"local","Terminal "+i));
        return workspace;
    }
    [Theory]
    [InlineData(DockEdge.Center)][InlineData(DockEdge.Left)][InlineData(DockEdge.Right)][InlineData(DockEdge.Top)][InlineData(DockEdge.Bottom)]
    public void CrossWindowDockingHasExactlyOneOwner(DockEdge edge)
    {
        var original=Sample();var ids=original.Documents.Keys.ToArray();
        var workspace=Layout.Float(Layout.Float(original,ids[0]),ids[1]);
        var destination=Layout.Groups(workspace.Floating[1].Root).Single().Id;
        workspace=Layout.Move(workspace,ids[0],destination,edge);
        Layout.Validate(workspace);Assert.Single(workspace.Floating);
        Assert.Equal(original.Documents.Keys.Order(),Layout.Groups(workspace).SelectMany(g=>g.Tabs).Order());
        foreach(var id in ids)Assert.Same(original.Documents[id],workspace.Documents[id]);
        Assert.Equal(edge==DockEdge.Center?1:2,Layout.Groups(workspace.Floating[0].Root).Count());
    }
    [Fact]
    public void FloatingLastTabLeavesAnEmptyMainDockAndReturnsWithoutRestartIdentity()
    {
        var workspace=Sample(1);var id=workspace.Documents.Keys.Single();var doc=workspace.Documents[id];
        workspace=Layout.Float(workspace,id);Assert.Empty(((TabGroup)workspace.Root).Tabs);Assert.Single(workspace.Floating);
        workspace=Layout.ReturnWindow(workspace,workspace.Floating[0].Id);
        Assert.Empty(workspace.Floating);Assert.Same(doc,workspace.Documents[id]);Assert.Equal(id,((TabGroup)workspace.Root).Active);
    }
    [Fact]
    public void ClosingLastFloatingTabPrunesOnlyItsWindow()
    {
        var workspace=Sample(2);var id=workspace.Documents.Keys.First();workspace=Layout.Float(workspace,id);
        workspace=Layout.Close(workspace,id);Assert.Empty(workspace.Floating);Assert.Single(workspace.Documents);Layout.Validate(workspace);
    }
    [Fact]
    public void SavedFloatingLayoutClonesAllIdentities()
    {
        var workspace=Sample();workspace=Layout.Float(workspace,workspace.Documents.Keys.First());
        var saved=new SavedLayout(Guid.NewGuid(),"Floating",workspace.Root,workspace.Documents){Floating=workspace.Floating};
        var next=Layout.RestoreLayout(workspace,saved);Layout.Validate(next);
        Assert.Single(next.Floating);Assert.NotEqual(workspace.Floating[0].Id,next.Floating[0].Id);
        Assert.Empty(workspace.Documents.Keys.Intersect(next.Documents.Keys));
        var roundtrip=JsonSerializer.Deserialize<Workspace>(JsonSerializer.Serialize(next,WorkspaceStore.Json),WorkspaceStore.Json)!;
        Layout.Validate(roundtrip);Assert.Single(roundtrip.Floating);
    }
    [Fact]
    public void FloatingDuplicateOwnershipIsRejected()
    {
        var workspace=Sample(1);var id=workspace.Documents.Keys.Single();
        Assert.Throws<InvalidDataException>(()=>Layout.Validate(workspace with{Floating=[new(Guid.NewGuid(),new TabGroup(Guid.NewGuid(),[id],id))]}));
    }
    [Theory][InlineData(double.NaN,600)][InlineData(900,double.PositiveInfinity)][InlineData(100,500)]
    public void FloatingWindowGeometryIsValidated(double width,double height)
    {
        var workspace=Sample(1);workspace=Layout.Float(workspace,workspace.Documents.Keys.Single());
        Assert.Throws<InvalidDataException>(()=>Layout.Validate(workspace with{Floating=[workspace.Floating[0] with{Width=width,Height=height}]}));
    }
    [Fact]
    public void MixedWindowFuzzPreservesOwnershipAcrossTwoThousandTransitions()
    {
        var random=new Random(24153);var workspace=Sample(24);var documents=workspace.Documents.Keys.Order().ToArray();
        for(int i=0;i<2000;i++)
        {
            var ids=workspace.Documents.Keys.ToArray();var groups=Layout.Groups(workspace).ToArray();
            int operation=random.Next(5);
            if(operation==0&&workspace.Floating.Length<10)workspace=Layout.Float(workspace,ids[random.Next(ids.Length)]);
            else if(operation==1&&workspace.Floating.Length>0)workspace=Layout.ReturnWindow(workspace,workspace.Floating[random.Next(workspace.Floating.Length)].Id);
            else if(operation==2)workspace=Layout.Arrange(workspace,"Four-way inspection");
            else workspace=Layout.Move(workspace,ids[random.Next(ids.Length)],groups[random.Next(groups.Length)].Id,(DockEdge)random.Next(5));
            Layout.Validate(workspace);Assert.Equal(documents,Layout.Groups(workspace).SelectMany(g=>g.Tabs).Order());
        }
    }
    [Theory]
    [InlineData("one ONE one", "one", false,false,false,3)]
    [InlineData("one ONE one", "one", false,true,false,2)]
    [InlineData("error 10\nerror 20\nwarn 30", "^error \\d+$", true,true,false,2)]
    [InlineData("cat cats concat CAT", "cat", false,false,true,2)]
    [InlineData("dot.net dotXnet", "dot.net", false,true,false,1)]
    [InlineData("αβ αβγ αβ", "αβ", false,true,true,2)]
    [InlineData("😀 done 😀", "😀", false,true,false,2)]
    [InlineData("abc", "^", true,true,false,0)]
    public void SearchSupportsLiteralRegexCaseUnicodeAndWholeWords(string text,string pattern,bool regex,bool matchCase,bool word,int count)
    {
        var result=TerminalSearch.Find(text,new(pattern,regex,matchCase,word));Assert.Null(result.Error);Assert.Equal(count,result.Matches.Length);
        foreach(var hit in result.Matches)Assert.Equal(hit.Text,text.Substring(hit.Offset,hit.Length));
    }
    [Fact]public void InvalidRegexReturnsAnErrorWithoutThrowing()=>Assert.NotNull(TerminalSearch.Find("test",new("[",true)).Error);
    [Fact]public void SearchMatchLimitIsExplicit()=>Assert.True(TerminalSearch.Find("a a a",new("a"),2).Truncated);
    [Fact]public void OversizedPatternIsRejected()=>Assert.NotNull(TerminalSearch.Find("a",new(new string('a',2049))).Error);
    [Fact]public void PathologicalRegexHasABoundedFailure()=>Assert.NotNull(TerminalSearch.Find(new string('a',20000)+"!",new("(a+)+$",true)).Error);

    [Theory]
    [InlineData("\u001b[31mhello\u001b[0m 世界\n","hello 世界\n")]
    [InlineData("before\u001b]52;c;c2VjcmV0\aafter","beforeafter")]
    [InlineData("a\u001bPpayload\u001b\\b","ab")]
    [InlineData("α😀\tend\r\n","α😀\tend\r\n")]
    public void OutputTextDecoderSurvivesEveryByteBoundary(string input,string expected)
    {
        byte[] bytes=Encoding.UTF8.GetBytes(input);
        for(int split=0;split<=bytes.Length;split++)
        {
            var decoder=new TerminalTextDecoder();var actual=decoder.Decode(bytes.AsSpan(0,split))+decoder.Decode(bytes.AsSpan(split),true);
            Assert.Equal(expected,actual);
        }
        var one=new TerminalTextDecoder();var text=string.Concat(bytes.Select(b=>one.Decode([b])))+one.Decode([],true);Assert.Equal(expected,text);
    }

    [Fact]
    public async Task HistoryPersistenceIsExplicitAndRankingSurvivesRestart()
    {
        var directory=Path.Combine(Path.GetTempPath(),"tessera-history-"+Guid.NewGuid());var path=Path.Combine(directory,"history.json");
        try
        {
            var now=DateTimeOffset.UtcNow;var store=new CommandHistoryStore(path);
            store.Add(new("1","dotnet test",now,now,0,"local","localhost","/project"));
            store.Add(new("2","git status",now,now,0,"other","host","/other"));
            store.Add(new("3","dotnet test",now,now,0,"local","localhost","/project"));
            await store.SaveAsync();Assert.False(File.Exists(path));store.Persist=true;await store.SaveAsync();
            var restored=new CommandHistoryStore(path){Persist=true};await restored.LoadAsync();
            var results=restored.Search("","local","/project");Assert.Equal("dotnet test",results[0].Entry.Command);Assert.Equal(2,results[0].Uses);
            await restored.ClearAsync();Assert.False(File.Exists(path));Assert.Empty(restored.Entries);
        }
        finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
    [Theory][InlineData("TOKEN=secret")][InlineData("curl --password mypassword")][InlineData(" command without history")][InlineData("echo a\necho b")]
    public void LikelySecretsAndUnsafeHistoryAreOmitted(string command)
    {
        var store=new CommandHistoryStore("unused");var now=DateTimeOffset.UtcNow;
        Assert.False(store.Add(new("id",command,now,now,0,null,null,null)));Assert.Empty(store.Entries);
    }
    [Fact]
    public void HistoryRetentionAndDuplicateIdsAreBounded()
    {
        var store=new CommandHistoryStore("unused"){RetentionDays=7};var now=DateTimeOffset.UtcNow;
        store.Add(new("old","old command",now.AddDays(-10),now.AddDays(-10),0,null,null,null));
        var entry=new CompletedCommand("recent","recent command",now,now,0,null,null,null);
        Assert.True(store.Add(entry));Assert.False(store.Add(entry));Assert.Single(store.Entries);
    }
    [Fact]
    public async Task ShadersRoundTripSourceAndRejectInvalidDefinitions()
    {
        var directory=Path.Combine(Path.GetTempPath(),"tessera-shaders-"+Guid.NewGuid());var path=Path.Combine(directory,"shaders.json");
        try
        {
            var id=Guid.NewGuid();var store=new ShaderStore(path);store.Set(id,[new("Effect","half4 main(float2 p) {return half4(1);}","SkiaRuntimeEffect",false)]);await store.SaveAsync();
            var loaded=new ShaderStore(path);await loaded.LoadAsync();Assert.Equal(store.Get(id),loaded.Get(id));
            Assert.Throws<InvalidDataException>(()=>store.Set(id,[new("Effect","code","JavaScript",false)]));
            loaded.Retain(new HashSet<Guid>());await loaded.SaveAsync();Assert.Empty(loaded.Get(id));
        }
        finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
    [Fact]
    public async Task AtomicFileHasPrivatePermissionsAndCancellationNeverReplacesGoodData()
    {
        var directory=Path.Combine(Path.GetTempPath(),"tessera-atomic-"+Guid.NewGuid());var path=Path.Combine(directory,"data");
        try
        {
            await AtomicFile.WriteAsync(path,"original"u8.ToArray());
            using var cts=new CancellationTokenSource();cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>AtomicFile.WriteAsync(path,"replacement"u8.ToArray(),cts.Token));
            Assert.Equal("original",await File.ReadAllTextAsync(path));Assert.Single(Directory.GetFiles(directory));
            if(!OperatingSystem.IsWindows())Assert.Equal(UnixFileMode.UserRead|UnixFileMode.UserWrite,File.GetUnixFileMode(path));
        }
        finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
}
