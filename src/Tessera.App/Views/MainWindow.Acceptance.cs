using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
using RoyalTerminal.Terminal;
using Tessera.Core;
using Tessera.Services;

namespace Tessera.Views;

public sealed partial class MainWindow
{
    private void RegisterAcceptanceCommands()
    {
        void Add(string id,string title,string icon,Func<Task> action) => _commands.Add(new(id,title,"Tools",icon,"",action,ex=>ShowError(ex.Message)));
        Add("sftp","SFTP file workspace","folder",()=>{ShowSftp();return Task.CompletedTask;});
        Add("recover-capture","Recover unsaved recordings","history",()=>{ShowCaptureRecovery();return Task.CompletedTask;});
        Add("clear-history","Clear command history","close",async()=>
        {
            if(await ConfirmAsync("Clear saved command history?","This removes Tessera's completed-command history from memory and disk. Your shell's own history is not modified.","Clear history"))
            {await Shell.HistoryStore.ClearAsync();Shell.Report("Command history cleared");QueueRender();}
        });
        Add("return-window","Return active floating terminal","layout",()=>{if(Shell.ActiveDocumentId is {} id)ReturnFloating(id);return Task.CompletedTask;});
        foreach(var (name,edge) in new[]{("left",DockEdge.Left),("right",DockEdge.Right),("above",DockEdge.Top),("below",DockEdge.Bottom)})
            _commands.Add(new("focus-"+name,"Focus pane "+name,"Session","layout","Alt+"+(name switch {"above"=>"Up","below"=>"Down",_=>name}),()=>
            {
                if(!_dock.FocusDirection(edge))foreach(var item in _floatingHosts.Values)if(item.FocusDirection(edge))break;
                return Task.CompletedTask;
            },ex=>ShowError(ex.Message)));
    }
    private void ShowAdvancedProfile(string id)
    {
        var profile=Shell.Profiles.Get(id);
        var editor=new TextBox {Text=System.Text.Json.JsonSerializer.Serialize(profile,WorkspaceStore.Json),AcceptsReturn=true,AcceptsTab=true,
            FontFamily=new FontFamily("monospace"),TextWrapping=TextWrapping.NoWrap,Height=390,MaxLength=1024*1024};
        var error=Ui.Text("",11,"Warning");error.TextWrapping=TextWrapping.Wrap;
        var save=Ui.Button("Validate and save document","save",()=>Run(async()=>
        {
            try
            {
                var candidate=System.Text.Json.JsonSerializer.Deserialize<TerminalSessionProfile>(editor.Text??"",WorkspaceStore.Json)??throw new InvalidDataException("Empty profile.");
                if(candidate.Id!=id)throw new InvalidDataException("The profile ID is immutable.");
                await Shell.Profiles.SaveAdvancedAsync(candidate,_windowLifetime.Token);
                error.Text="Saved. Reconnect to apply transport and logging changes.";
            }
            catch(Exception ex) {error.Text=ex.Message;}
        }),"primary");
        ShowOverlay("Every profile option, explicitly.","Edit arguments, environments, all forwarding rules, private-key paths, proxy settings, scrollback, behavior, and logging without losing fields. Do not put passwords in this document. Workspace geometry controls the final terminal size. X11 and command proxies are retained but rejected by the upstream SSH.NET backend rather than silently ignored.",Ui.Stack(Ui.Field("Native RoyalTerminal profile JSON",editor),error,save),1000);
    }
    private void ShowAdvancedSearch()
    {
        var session=Shell.ActiveSession;if(session is null)return;
        string snapshot=NativeTerminalSearch.Snapshot(session.Terminal);
        var text=Ui.Input(null,"Find in native terminal output");text.MaxLength=2048;
        var regex=new CheckBox {Content="Regular expression"};var matchCase=new CheckBox {Content="Match case"};var word=new CheckBox {Content="Whole word"};
        var status=Ui.Text("Search a stable snapshot; refresh after new output.",11,"Muted");status.TextWrapping=TextWrapping.Wrap;
        var list=new ListBox {Height=290,Background=Brushes.Transparent};
        list.ItemTemplate=new FuncDataTemplate<TerminalSearchHit>((hit,_)=>hit is null?new Border():Ui.Row("60,*",Ui.Text("L"+hit.Line,10,"Faint"),Ui.Text(hit.Context,11)));
        var flags=new StackPanel {Orientation=Orientation.Horizontal,Spacing=16,Children={regex,matchCase,word}};
        CancellationTokenSource? pending=null;bool closed=false;
        async Task SearchAsync()
        {
            pending?.Cancel();pending?.Dispose();pending=new();var token=pending.Token;
            var query=new TerminalSearchQuery(text.Text??"",regex.IsChecked==true,matchCase.IsChecked==true,word.IsChecked==true);var source=snapshot;
            try
            {
                await Task.Delay(180,token);
                var result=await Task.Run(()=>TerminalSearch.Find(source,query),token);
                if(closed||token.IsCancellationRequested)return;
                list.ItemsSource=result.Matches;
                status.Text=result.Error??$"{result.Matches.Length}{(result.Truncated?"+":"")} matches · arrows navigate · snapshot search";
                status.Foreground=ThemeManager.Brush(result.Error is null?"Muted":"Warning");
                if(result.Matches.Length>0)list.SelectedIndex=0;
            }
            catch(OperationCanceledException) { }
        }
        void Refresh() {snapshot=NativeTerminalSearch.Snapshot(session.Terminal);_ = SearchAsync();}
        text.TextChanged+=(_,_)=>_ = SearchAsync();
        foreach(var flag in new[]{regex,matchCase,word})flag.IsCheckedChanged+=(_,_)=>_ = SearchAsync();
        list.SelectionChanged+=(_,_)=>
        {
            if(list.SelectedItem is TerminalSearchHit hit && !NativeTerminalSearch.Navigate(session.Terminal,snapshot,hit))
                status.Text="The terminal buffer changed, or this match has no visible characters. Refresh before navigating.";
        };
        void Step(int delta) {if(list.ItemCount>0)list.SelectedIndex=(list.SelectedIndex+delta+list.ItemCount)%list.ItemCount;}
        text.KeyDown+=(_,e)=>{if(e.Key is Key.Enter or Key.Down) {Step(1);e.Handled=true;}else if(e.Key==Key.Up) {Step(-1);e.Handled=true;}};
        ShowOverlay("Find in "+Shell.ActiveDocument!.Title,"Literal, case-sensitive, whole-word and regex search. Zero-length matches are omitted. Multiline matches navigate to their first visible line.",
            Ui.Stack(Ui.Field("Search pattern",text),flags,Ui.Row("Auto,Auto,*,Auto",Ui.Button("Previous",null,()=>Step(-1)),Ui.Button("Next",null,()=>Step(1)),new Border(),Ui.Button("Refresh snapshot","history",Refresh)),status,list),860);
        _onDismiss=()=>{closed=true;pending?.Cancel();pending?.Dispose();session.Terminal.EndSearch();};Dispatcher.UIThread.Post(()=>text.Focus());
    }
    private void ShowCaptureRecovery()
    {
        var rows=new StackPanel {Spacing=10};
        foreach(var item in Shell.Recovery.Store.List())
        {
            var label=Ui.Text($"{item.ModifiedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {item.Bytes/1024:N0} KiB · {item.Id.ToString("N")[..8]}",12);
            var open=Ui.Button("Recover read-only","play",()=>Run(async()=>
            {
                var recovered=await Shell.Recovery.Store.LoadAsync(item.Id,_windowLifetime.Token);
                var doc=await Shell.NewTerminalAsync(start:false);Shell.MarkReplay(doc.Id);Shell.RenameDocument(doc.Id,"Recovered · "+Safety.CleanTitle(recovered.Title));
                Shell.GetSession(Shell.Active.Documents[doc.Id]).Capture.LoadReplay(recovered.Session,"Recovered recording");
                DismissOverlay();Shell.SetTool("Timeline");Shell.Report("Recovered read-only. The encrypted original remains until explicitly discarded.");
            }));
            var export=Ui.Button("Export","save",()=>Run(async()=>
            {
                var recovered=await Shell.Recovery.Store.LoadAsync(item.Id,_windowLifetime.Token);
                if(await ExportCaptureAsync(recovered.Session))Shell.Report("Recovery exported. The encrypted original was retained.");
            }));
            var discard=Ui.IconButton("close","Permanently discard recovered recording",()=>Run(async()=>
            {
                if(!await ConfirmAsync("Discard recovered recording?","The encrypted recovery file will be deleted. This cannot be undone.","Discard recording"))return;
                await Shell.Recovery.Store.DeleteAsync(item.Id);ShowCaptureRecovery();
            }));
            rows.Children.Add(Ui.Card(Ui.Stack(label,Ui.Row("Auto,Auto,*,Auto",open,export,new Border(),discard))));
        }
        if(rows.Children.Count==0)rows.Children.Add(Ui.Text("No unsaved recordings need recovery.",14,"Muted"));
        ShowOverlay("Nothing important left behind.","Opt-in captures are checkpointed with authenticated encryption. Recovery files never reconnect a transport or execute input. Unlocking may require your OS vault or recovery passphrase.",rows,840);
    }
    private async Task<bool> ExportCaptureAsync(TerminalCaptureSession capture)
    {
        capture = CapturePrivacy.OutputOnly(capture);
        var path=await _fileDialogs.SaveFileAsync(this, new(){Title="Export recording (may contain secrets)",SuggestedFileName="session.rtcap.json",FileTypeChoices=[new("RoyalTerminal capture"){Patterns=["*.rtcap.json"]},new("Asciicast v3"){Patterns=["*.cast"]}]});
        if(path is null)return false;
        var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            var options=new FileStreamOptions {Mode=FileMode.CreateNew,Access=FileAccess.Write,Share=FileShare.None,Options=FileOptions.Asynchronous};
            if(!OperatingSystem.IsWindows())options.UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite;
            await using(var stream=new FileStream(temporary,options))
            {
                if(path.EndsWith(".cast",StringComparison.OrdinalIgnoreCase))await TerminalCaptureSessionSerializer.SaveAsync(capture,stream,TerminalCaptureSessionFormats.AsciicastV3);
                else await TerminalCaptureSessionSerializer.SaveAsync(capture,stream);
                await stream.FlushAsync();stream.Flush(true);
            }
            File.Move(temporary,path,true);return true;
        }
        finally {if(File.Exists(temporary))File.Delete(temporary);}
    }
}
