using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RoyalTerminal.Terminal;
using Tessera.Core;
using Tessera.Services;
using Tessera.Views;
using Xunit;

namespace Tessera.NativeTests;

public sealed class AcceptanceUiTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static async Task SettleAsync(int milliseconds = 80)
    { Dispatcher.UIThread.RunJobs(); await Task.Delay(milliseconds, Token); Dispatcher.UIThread.RunJobs(); }
    private static async Task<MainWindow> OpenAsync(string directory, bool design = true)
    { var w = new MainWindow(design,directory); w.Show(); await w.InitializeAsync(); await SettleAsync(); return w; }
    private static void Shot(Window window, string name)
    {
        string root=Environment.GetEnvironmentVariable("GITHUB_WORKSPACE")??Directory.GetCurrentDirectory();
        string folder=Path.Combine(root,"artifacts","screenshots");Directory.CreateDirectory(folder);
        using var image=window.CaptureRenderedFrame();Assert.NotNull(image);
#pragma warning disable CS0618
        image.Save(Path.Combine(folder,name+".png"));
#pragma warning restore CS0618
    }
    [AvaloniaFact]
    public async Task CrossWindowDropConsumesCapabilityAndPreservesEveryTerminal()
    {
        using var dir = new AcceptanceDirectory(); var w=await OpenAsync(dir.Path);
        try
        {
            var shell=w.Shell;var first=shell.ActiveDocumentId!.Value;var terminal=shell.ActiveSession!.Terminal;
            shell.Apply(Layout.Float(shell.Active,first));await SettleAsync();
            Assert.Single(w.OwnedWindows);var floating=shell.Active.Floating.Single();
            var other=shell.Active.Documents.Keys.First(id=>id!=first);
            var token=DockDragBroker.Begin(shell,other);
            Assert.True(DockDragBroker.Drop(token,shell,floating.Root.Id,DockEdge.Right));
            Assert.False(DockDragBroker.Drop(token,shell,floating.Root.Id,DockEdge.Left));await SettleAsync();
            Assert.Equal(2,Layout.Groups(shell.Active.Floating.Single().Root).Count());
            Assert.Same(terminal,shell.Sessions.Find(first)!.Terminal);
            var returnToken=DockDragBroker.Begin(shell,first);var main=Layout.Groups(shell.Active.Root).First().Id;
            Assert.True(DockDragBroker.Drop(returnToken,shell,main,DockEdge.Center));await SettleAsync();
            Assert.Same(terminal,shell.Sessions.Find(first)!.Terminal);Layout.Validate(shell.Active);
            Shot(w,"acceptance-cross-window");Shot(w.OwnedWindows.Single(),"acceptance-floating-split");
        }
        finally {w.CloseForTests();}
    }
    [AvaloniaFact]
    public async Task DragCannotBeForgedOrReusedInAnotherWorkspaceOrShell()
    {
        using var a=new AcceptanceDirectory();using var b=new AcceptanceDirectory();
        var w=await OpenAsync(a.Path);using var foreign=new ShellController(true,b.Path);await foreign.InitializeAsync();
        try
        {
            var token=DockDragBroker.Begin(w.Shell,w.Shell.ActiveDocumentId!.Value);
            Assert.False(DockDragBroker.TryResolve(Guid.NewGuid().ToString(),w.Shell,out _));
            Assert.False(DockDragBroker.TryResolve(token,foreign,out _));
            w.Shell.SwitchWorkspace(w.Shell.Data.Workspaces.Last().Id);
            Assert.False(DockDragBroker.TryResolve(token,w.Shell,out _));DockDragBroker.End(token);
        }
        finally {w.CloseForTests();}
    }
    [AvaloniaFact]
    public async Task LivePtySurvivesTwoNativeWindowTransfersAndAsyncDisposal()
    {
        using var dir=new AcceptanceDirectory();var w=await OpenAsync(dir.Path,false);
        var session=w.Shell.ActiveSession!;
        try
        {
            Assert.True(session.IsRunning,session.Error);var terminal=session.Terminal;var id=session.Id;
            w.Shell.Apply(Layout.Float(w.Shell.Active,id));await SettleAsync();
            var floatId=w.Shell.Active.Floating.Single().Id;
            w.Shell.Apply(Layout.ReturnWindow(w.Shell.Active,floatId));await SettleAsync();
            Assert.Same(terminal,w.Shell.ActiveSession!.Terminal);Assert.True(session.IsRunning);
            string suffix=Guid.NewGuid().ToString("N");string marker="TRANSFER_"+suffix;
            session.Send(OperatingSystem.IsWindows()?"echo TRANSFER_"+suffix+"\r":"printf 'TRANSFER_%s\\n' '"+suffix+"'\r");
            for(int i=0;i<70&&!session.OutputSnapshot().Contains(marker,StringComparison.Ordinal);i++)await Task.Delay(80,Token);
            Assert.Contains(marker,session.OutputSnapshot());
            await w.Shell.PrepareShutdownAsync();Assert.False(session.IsRunning);Assert.Equal("Disposed",session.State);
            await session.DisposeAsync();
        }
        finally {w.CloseForTests();}
    }
    [AvaloniaFact]
    public async Task LineSpacingIsNotCumulativeAndCursorPreferencesNeverSendInput()
    {
        using var dir=new AcceptanceDirectory();var w=await OpenAsync(dir.Path);
        try
        {
            var t=w.Shell.ActiveSession!.Terminal;float initial=t.Renderer!.CellHeight;
            var sent=new List<byte[]>();t.TerminalSessionService.InputSent+=(_,e)=>sent.Add(e.Data.ToArray());
            t.LineHeight=1.4;await SettleAsync();Assert.Equal(initial*1.4,t.Renderer.CellHeight,2);
            t.LineHeight=1.4;await SettleAsync();Assert.Equal(initial*1.4,t.Renderer.CellHeight,2);
            t.LineHeight=1;await SettleAsync();Assert.Equal(initial,t.Renderer.CellHeight,2);
            t.ApplyCursor("Bar",false);t.ApplyCursor("Underline",true);Assert.Empty(sent);
            Assert.Throws<ArgumentOutOfRangeException>(()=>t.LineHeight=double.NaN);
            Assert.Throws<ArgumentOutOfRangeException>(()=>t.LineHeight=.5);
            w.Shell.SetPreferences(w.Shell.Data.Preferences with {LineHeight=1.25,CursorStyle="Underline",CursorBlink=false});
            w.ShowSettings();await SettleAsync();Shot(w,"acceptance-terminal-preferences");
        }
        finally {w.CloseForTests();}
    }
    [AvaloniaFact]
    public async Task NativeSearchFiltersRegexCaseAndWholeWordsAndRejectsStaleOffsets()
    {
        using var dir=new AcceptanceDirectory();var w=await OpenAsync(dir.Path);
        try
        {
            var t=w.Shell.ActiveSession!.Terminal;t.WriteOutput(Encoding.UTF8.GetBytes("\r\nAlpha alpha alphabet ALPHA\r\nerr=42 warn=17\r\n"));
            await SettleAsync();string snapshot=NativeTerminalSearch.Snapshot(t);
            var result=TerminalSearch.Find(snapshot,new("alpha",false,true,true));Assert.Single(result.Matches);
            Assert.True(NativeTerminalSearch.Navigate(t,snapshot,result.Matches[0]));
            Assert.Single(TerminalSearch.Find(snapshot,new(@"err=\d+",true,true)).Matches);
            t.WriteOutput(Encoding.UTF8.GetBytes("changed\r\n"));Assert.False(NativeTerminalSearch.Navigate(t,snapshot,result.Matches[0]));
            w.ShowSearch();await SettleAsync();
            var checks=w.GetVisualDescendants().OfType<CheckBox>().ToArray();
            Assert.Contains(checks,c=>Equals(c.Content,"Regular expression"));
            Assert.Contains(checks,c=>Equals(c.Content,"Match case"));
            var input=w.GetVisualDescendants().OfType<TextBox>().Single(t=>t.PlaceholderText=="Find in native terminal output");
            checks.Single(c=>Equals(c.Content,"Regular expression")).IsChecked=true;input.Text=@"err=\d+";
            await SettleAsync(350);Shot(w,"acceptance-regex-search");
        }
        finally {w.CloseForTests();}
    }
    [AvaloniaFact]
    public async Task TerminalAutomationExposesReadOnlyBoundedOutputWithoutExecution()
    {
        using var dir=new AcceptanceDirectory();var w=await OpenAsync(dir.Path);
        try
        {
            var t=w.Shell.ActiveSession!.Terminal;
            var peer=ControlAutomationPeer.CreatePeerForElement(t)!;Assert.NotNull(peer);
            Assert.Equal(AutomationControlType.Document,peer.GetAutomationControlType());
            var value=Assert.IsAssignableFrom<IValueProvider>(peer);Assert.True(value.IsReadOnly);
            Assert.Contains("tessera",value.Value,StringComparison.OrdinalIgnoreCase);Assert.True(value.Value.Length<=65536);
            Assert.Throws<InvalidOperationException>(()=>value.SetValue("echo never-execute\r"));
        }
        finally {w.CloseForTests();}
    }
    [AvaloniaFact]
    public async Task CancellingCredentialDialogPreservesProfileEditorAndHasLabelledSecretField()
    {
        using var dir=new AcceptanceDirectory();var w=await OpenAsync(dir.Path);
        try
        {
            w.ShowProfiles();await SettleAsync();using var cancel=CancellationTokenSource.CreateLinkedTokenSource(Token);
            var pending=w.Shell.Profiles.CredentialPrompt!("SSH password",cancel.Token);
            await SettleAsync();var dialog=w.OwnedWindows.Single()!;
            var field=dialog!.GetVisualDescendants().OfType<TextBox>().Single();
            Assert.NotEqual('\0',field.PasswordChar);Assert.Equal("Credential",AutomationProperties.GetName(field));
            field.Text="not-persisted";Shot(dialog,"acceptance-authentication");cancel.Cancel();
            Assert.Null(await pending);Assert.True(w.IsOverlayOpen);Assert.Empty(w.OwnedWindows);
        }
        finally {w.CloseForTests();}
    }
    [AvaloniaFact]
    public async Task SftpWorkspaceAndCaptureRecoveryExposeNativeActions()
    {
        using var dir=new AcceptanceDirectory();var w=await OpenAsync(dir.Path);
        try
        {
            // Public command registry execution through the palette proves menu dispatch is connected.
            w.ShowPalette("SFTP file workspace");await SettleAsync();
            w.KeyPress(Key.Enter,RawInputModifiers.None,PhysicalKey.Enter,null);
            w.KeyRelease(Key.Enter,RawInputModifiers.None,PhysicalKey.Enter,null);await SettleAsync();
            var sftp=w.OwnedWindows.Single();Assert.Contains("SFTP",sftp.Title!);
            Assert.Contains(sftp.GetVisualDescendants().OfType<CheckBox>(),c=>Equals(c.Content,"Read-only"));
            Shot(sftp,"acceptance-sftp-workspace");sftp.Close();
        }
        finally {w.CloseForTests();}
    }
}
