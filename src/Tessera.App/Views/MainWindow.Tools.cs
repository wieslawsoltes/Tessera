using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RoyalTerminal.Terminal;
using Tessera.Core;

namespace Tessera.Views;

public sealed partial class MainWindow
{
    private string? _fileDirectory;
    private Slider? _timeline;
    private TextBlock? _timelineTime;
    private bool _updatingTimeline;
    private DispatcherTimer? _timelineTimer;
    private void BuildTools()
    {
        var tabs=Q<StackPanel>("ToolTabs");tabs.Children.Clear();
        foreach(var (name,icon) in new[]{("Commands","code"),("History","history"),("Files","folder"),("Timeline","record"),("Notes","note")})
        {
            var button=Ui.Button(name,icon,()=>Shell.SetTool(name));button.FontSize=10;if(Shell.Data.Preferences.Tool==name)button.Classes.Add("selected");tabs.Children.Add(button);
        }
        var actions=Q<StackPanel>("ToolActions");actions.Children.Clear();actions.Children.Add(Ui.IconButton("add","Add command",()=>EditSnippet()));
        actions.Children.Add(Ui.IconButton("layout","Dock tools at bottom / right",()=>Shell.SetPreferences(Shell.Data.Preferences with{ToolsOnRight=!Shell.Data.Preferences.ToolsOnRight,ToolSize=Shell.Data.Preferences.ToolsOnRight?184:340})));
        actions.Children.Add(Ui.IconButton("close","Hide tool panel",Shell.ToggleTools));
        _timeline=null;_timelineTime=null;_timelineTimer?.Stop();
        Q<ContentControl>("ToolContent").Content=Shell.Data.Preferences.Tool switch
        {
            "History"=>BuildHistory(),"Files"=>BuildFiles(),"Timeline"=>BuildTimeline(),"Notes"=>BuildNotes(),_=>BuildCommands()
        };
    }
    private Control BuildCommands()
    {
        var grid=new UniformGrid{Columns=Shell.Data.Preferences.ToolsOnRight?1:3};
        foreach(var snippet in Shell.Data.Snippets)
        {
            var category=Ui.Text(snippet.Category,8,"Faint");category.LetterSpacing=1.2;
            var command=Ui.Text(snippet.Command,10,"Muted");command.FontFamily=new FontFamily("monospace");command.TextTrimming=TextTrimming.CharacterEllipsis;
            var insert=Ui.IconButton("right","Insert command without executing",()=>Run(()=>{var session=Shell.ActiveSession??throw new InvalidOperationException("Open a terminal first.");session.Send(snippet.Command);session.Terminal.Focus();Shell.Report("Command inserted. Press Enter in the terminal to execute.");return Task.CompletedTask;}));
            var card=Ui.Card(Ui.Row("*,Auto",Ui.Stack(category,Ui.Text(snippet.Name,12),command),insert),new Thickness(14));card.Margin=new Thickness(0,0,12,10);
            card.ContextMenu=new ContextMenu{ItemsSource=new[]{new MenuItem{Header="Edit command",Command=new Services.AppCommand("edit","Edit","","","",()=>{EditSnippet(snippet);return Task.CompletedTask;},ex=>ShowError(ex.Message))},new MenuItem{Header="Remove command",Command=new Services.AppCommand("remove","Remove","","","",()=>{Shell.RemoveSnippet(snippet.Id);return Task.CompletedTask;},ex=>ShowError(ex.Message))}}};grid.Children.Add(card);
        }
        if(Shell.Data.Snippets.Length==0)grid.Children.Add(Ui.Button("Save a reusable command","add",()=>EditSnippet()));
        return grid;
    }
    private void EditSnippet(Snippet? existing=null)
    {
        var name=Ui.Input(existing?.Name,"Command name");var command=Ui.Input(existing?.Command,"git status --short");var category=Ui.Input(existing?.Category??"CUSTOM");
        var save=Ui.Button("Save command","save",()=>Run(()=>
        {
            if(string.IsNullOrWhiteSpace(name.Text)||string.IsNullOrWhiteSpace(command.Text))throw new InvalidOperationException("Name and command are required.");
            if(!Safety.IsSafeCommandInsertion(command.Text))throw new InvalidOperationException("Use one command line without control characters.");
            if(existing is not null)Shell.RemoveSnippet(existing.Id);Shell.AddSnippet(new(existing?.Id??Guid.NewGuid(),name.Text.Trim(),command.Text,category.Text??"CUSTOM"));DismissOverlay();return Task.CompletedTask;
        }),"primary");
        ShowOverlay(existing is null?"A command worth keeping.":"Edit reusable command","Commands are inserted into the active terminal. They never execute automatically.",Ui.Stack(Ui.Field("Name",name),Ui.Field("Command",command),Ui.Field("Category",category),save),660);
    }
    private Control BuildHistory()
    {
        var list=new StackPanel{Spacing=6};
        if(Shell.CommandHistory.Count==0)
        {
            list.Children.Add(Ui.Text("Your completed commands will appear here.",14));
            var text=Ui.Text("History uses RoyalTerminal shell-integration events, not a keylogger. Enable shell integration in your shell; this list stays empty until completed command events are received.",11,"Muted");text.TextWrapping=TextWrapping.Wrap;list.Children.Add(text);
        }
        foreach(var entry in Shell.CommandHistory.Take(100))
        {
            var button=Ui.Button("",null,()=>Run(()=>{if(!Safety.IsSafeCommandInsertion(entry.CommandLine))throw new InvalidOperationException("This history entry requires manual review.");Shell.ActiveSession?.Send(entry.CommandLine);return Task.CompletedTask;}));
            button.Content=Ui.Row("85,*,80",Ui.Text(entry.StartedAtUtc.ToLocalTime().ToString("HH:mm:ss"),10,"Faint"),Ui.Text(entry.CommandLine,11),Ui.Text(entry.ExitCode is {} code?"exit "+code:"",10,entry.ExitCode==0?"Accent":"Warning"));button.HorizontalContentAlignment=HorizontalAlignment.Stretch;list.Children.Add(button);
        }
        return list;
    }
    private Control BuildNotes()
    {
        var notes=new TextBox{Text=Shell.Active.Notes,PlaceholderText="Keep context next to your commands. Notes are saved with this workspace.",AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MinHeight=100,Background=Brushes.Transparent,BorderThickness=new Thickness(0),FontSize=12};
        notes.TextChanged+=(_,_)=>Shell.SetNotes(notes.Text??"");return notes;
    }
    private Control BuildFiles()
    {
        var list=new StackPanel{Spacing=5};
        list.Children.Add(Ui.Button(_fileDirectory is null?"Choose a local folder":_fileDirectory,"folder",()=>Run(async()=>{var folders=await StorageProvider.OpenFolderPickerAsync(new(){Title="Browse local files",AllowMultiple=false});if(folders.FirstOrDefault()?.TryGetLocalPath() is {} path){_fileDirectory=path;BuildTools();}})));
        if(_fileDirectory is null){list.Children.Add(Ui.Text("Local files only. Remote SFTP is not connected to this panel.",11,"Muted"));return list;}
        if(System.IO.Directory.GetParent(_fileDirectory) is {} parent)list.Children.Add(Ui.Button("..","folder",()=>{_fileDirectory=parent.FullName;BuildTools();}));
        try
        {
            foreach(var entry in new DirectoryInfo(_fileDirectory).EnumerateFileSystemInfos().OrderByDescending(f=>f is DirectoryInfo).ThenBy(f=>f.Name,StringComparer.OrdinalIgnoreCase).Take(200))
            {
                var isDirectory=entry is DirectoryInfo;
                var button=Ui.Button(entry.Name,isDirectory?"folder":"file",()=>{if(isDirectory){_fileDirectory=entry.FullName;BuildTools();}else Run(()=>OpenTextFileAsync(entry.FullName));});button.HorizontalContentAlignment=HorizontalAlignment.Left;button.Padding=new Thickness(3,2);list.Children.Add(button);
            }
        }
        catch(Exception ex){list.Children.Add(Ui.Text(ex.Message,11,"Danger"));}
        return list;
    }
    private async Task OpenTextFileAsync(string path)
    {
        var info=new FileInfo(path);if(info.Length>2*1024*1024)throw new InvalidOperationException("The inline editor accepts text files up to 2 MiB.");
        var originalTime=info.LastWriteTimeUtc;var bytes=await File.ReadAllBytesAsync(path);if(bytes.Contains((byte)0))throw new InvalidOperationException("This file appears to contain binary data.");
        var editor=new TextBox{Text=System.Text.Encoding.UTF8.GetString(bytes),AcceptsReturn=true,AcceptsTab=true,TextWrapping=TextWrapping.NoWrap,Height=390,FontFamily=new FontFamily("monospace")};
        var save=Ui.Button("Save file","save",()=>Run(async()=>
        {
            if(File.GetLastWriteTimeUtc(path)!=originalTime)throw new IOException("The file changed outside Tessera. Reopen it before saving.");
            var content=editor.Text??"";if(!await ConfirmAsync("Write changes to disk?",path,"Save file"))return;
            var temp=path+".tessera-"+Guid.NewGuid().ToString("N")+".tmp";
            try{await File.WriteAllTextAsync(temp,content);if(!OperatingSystem.IsWindows())File.SetUnixFileMode(temp,File.GetUnixFileMode(path));File.Move(temp,path,true);Shell.Report("Saved "+path);}finally{if(File.Exists(temp))File.Delete(temp);}
        }),"primary");
        ShowOverlay(Path.GetFileName(path),path,Ui.Stack(editor,save),860);
    }
    private Control BuildTimeline()
    {
        var session=Shell.ActiveSession;var content=new StackPanel{Spacing=10};if(session is null){content.Children.Add(Ui.Text("Open a terminal or a recording first."));return content;}
        var actions=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8};
        actions.Children.Add(CommandButton("capture"));actions.Children.Add(CommandButton("save-capture"));actions.Children.Add(CommandButton("load-replay"));content.Children.Add(actions);
        if(!session.IsReplay){content.Children.Add(Ui.Text(session.Capture.IsCaptureActive?"RECORDING · terminal input, output, and resize events are being captured.":"Capture a session, or open a RoyalTerminal / asciicast recording.",11,session.Capture.IsCaptureActive?"Warning":"Muted"));return content;}
        _timeline=new Slider{Minimum=0,Maximum=Math.Max(1,session.Capture.ReplayDurationSeconds),Value=session.Capture.ReplayPositionSeconds};
        _timelineTime=Ui.Text("",10,"Muted");
        _timeline.PropertyChanged+=(_,e)=>{if(e.Property==Slider.ValueProperty&&!_updatingTimeline)session.Capture.SeekReplay(_timeline!.Value);};
        var playback=new StackPanel{Orientation=Orientation.Horizontal,Spacing=10};playback.Children.Add(Ui.Button("Play","play",session.Capture.PlayReplay));playback.Children.Add(Ui.Button("Pause","pause",session.Capture.PauseReplay));playback.Children.Add(Ui.Button("Stop","stop",session.Capture.StopReplay));playback.Children.Add(_timelineTime);
        content.Children.Add(_timeline);content.Children.Add(playback);
        _timelineTimer??=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(250)};
        _timelineTimer.Tick-=TimelineTick;_timelineTimer.Tick+=TimelineTick;_timelineTimer.Start();return content;
    }
    private void TimelineTick(object? sender,EventArgs e)
    {
        if(_closed){_timelineTimer?.Stop();return;}var capture=Shell.ActiveSession?.Capture;if(capture is null||!capture.IsReplayEnabled||_timeline is null)return;
        _updatingTimeline=true;_timeline.Value=capture.ReplayPositionSeconds;_updatingTimeline=false;
        if(_timelineTime is not null)_timelineTime.Text=$"{TimeSpan.FromSeconds(capture.ReplayPositionSeconds):mm\\:ss} / {TimeSpan.FromSeconds(capture.ReplayDurationSeconds):mm\\:ss} · replay is read-only";
    }
    private async Task CaptureAsync()
    {
        var session=Shell.ActiveSession;if(session is null)return;if(session.IsReplay)throw new InvalidOperationException("A replay is not a live capture source.");
        if(session.Capture.IsCaptureActive){session.Capture.StopCapture();Shell.Report("Capture stopped · save it before closing the terminal");Shell.SetTool("Timeline");return;}
        if(await ConfirmAsync("Record this terminal session?","Recordings contain terminal output AND input. Passwords, tokens, or other sensitive data can be captured. Recording is opt-in and stays in memory until you save it.","Start recording")){session.Capture.StartCapture();Shell.Report("Recording active");Shell.SetTool("Timeline");}
    }
    private async Task SaveCaptureAsync()
    {
        var session=Shell.ActiveSession;if(session is null||!session.Capture.HasCapture)throw new InvalidOperationException("There is no capture to save.");
        var file=await StorageProvider.SaveFilePickerAsync(new(){Title="Save recording",SuggestedFileName="session.rtcap.json",FileTypeChoices=[new("RoyalTerminal capture"){Patterns=["*.rtcap.json"]},new("Asciicast v3"){Patterns=["*.cast"]}]});
        if(file?.TryGetLocalPath() is not {} path)return;
        var capture=session.Capture.GetCaptureSnapshot() ?? throw new InvalidOperationException("The capture is empty.");
        if(path.EndsWith(".cast",StringComparison.OrdinalIgnoreCase))await TerminalCaptureSessionSerializer.SaveToFileAsync(capture,path,TerminalCaptureSessionFormats.AsciicastV3);
        else await TerminalCaptureSessionSerializer.SaveToFileAsync(capture,path);
        if(!OperatingSystem.IsWindows())File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite);Shell.Report("Recording saved");
    }
    private async Task LoadReplayAsync()
    {
        var files=await StorageProvider.OpenFilePickerAsync(new(){Title="Open terminal recording",AllowMultiple=false,FileTypeFilter=[new("Terminal recordings"){Patterns=["*.rtcap.json","*.cast","*.json"]}]});
        if(files.FirstOrDefault()?.TryGetLocalPath() is not {} path)return;
        if(new FileInfo(path).Length>64*1024*1024)throw new InvalidOperationException("Recordings over 64 MiB require an indexed streaming player.");
        var capture=await TerminalCaptureSessionSerializer.LoadFromFileAsync(path);
        var document=await Shell.NewTerminalAsync(Shell.Profiles.Document.Profiles[0].Id,start:false);Shell.RenameDocument(document.Id,"Replay · "+Path.GetFileName(path));
        Shell.MarkReplay(document.Id);var session=Shell.GetSession(Shell.Active.Documents[document.Id]);session.Capture.LoadReplay(capture,Path.GetFileName(path));session.Locked=true;Shell.SetTool("Timeline");
    }
    private async Task ExportOutputAsync()
    {
        var session=Shell.ActiveSession;if(session is null)return;
        var file=await StorageProvider.SaveFilePickerAsync(new(){Title="Export terminal output",SuggestedFileName="terminal-output.txt"});if(file is null)return;
        await using var stream=await file.OpenWriteAsync();using var writer=new StreamWriter(stream);await writer.WriteAsync(Safety.StripAnsi(session.OutputSnapshot()));Shell.Report("Terminal text exported (bounded recent output)");
    }
}
