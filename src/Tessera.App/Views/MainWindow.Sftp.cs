using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Tessera.Core;
using Tessera.Services;

namespace Tessera.Views;

public sealed partial class MainWindow
{
    private Window? _sftpWindow;
    private SftpWorkspace? _sftp;
    private CancellationTokenSource? _sftpOperation;
    private Task _sftpWork = Task.CompletedTask;
    private bool _sftpCloseApproved;
    private bool _sftpBusy;
    private bool _sftpDirty;
    private string? _sftpEditPath;
    private RemoteVersion? _sftpEditVersion;
    private string _remoteDirectory = "/";
    private TextBox? _sftpEditor;
    private TextBlock? _sftpStatus;
    private TextBlock? _sftpEditLabel;
    private ListBox? _sftpFiles;
    private TextBox? _sftpPath;
    private ProgressBar? _sftpProgress;
    private CheckBox? _sftpLock;
    private Panel? _sftpActions;

    public void ShowSftp()
    {
        if(_sftpWindow is {} open) {open.Activate();return;}
        var profiles = Shell.Profiles.Document.Profiles.Where(p=>p.Transport.TransportId=="ssh").ToArray();
        var picker = new ComboBox {ItemsSource=profiles,SelectedItem=profiles.FirstOrDefault(p=>p.Id==Shell.ActiveDocument?.ProfileId)??profiles.FirstOrDefault(),MinWidth=230};
        picker.ItemTemplate=new FuncDataTemplate<RoyalTerminal.Terminal.TerminalSessionProfile>((p,_)=>Ui.Text(p?.DisplayName??"",12));
        AutomationProperties.SetName(picker,"SFTP connection profile");
        _sftpPath=Ui.Input("/","Remote absolute path");_sftpPath.MaxLength=4096;AutomationProperties.SetName(_sftpPath,"Remote directory");
        _sftpStatus=Ui.Text("Choose an SSH profile. This is a separate, authenticated SFTP connection.",11,"Muted");_sftpStatus.TextWrapping=TextWrapping.Wrap;
        _sftpFiles=new ListBox {Background=ThemeManager.Brush("TerminalBg"),BorderThickness=new Thickness(0),HorizontalAlignment=HorizontalAlignment.Stretch};
        AutomationProperties.SetName(_sftpFiles,"Remote files");
        _sftpFiles.ItemTemplate=new FuncDataTemplate<RemoteFile>((f,_)=>f is null?new Border():Ui.Row("24,*,85",Ui.Icon(f.Directory?"folder":"file",14),Ui.Text(f.Name+(f.SymbolicLink?" ↗":""),12),Ui.Text(f.Directory?"—":$"{f.Length:N0}",10,"Faint")));
        _sftpEditor=new TextBox {AcceptsReturn=true,AcceptsTab=true,MaxLength=2*1024*1024,TextWrapping=TextWrapping.NoWrap,FontFamily=new FontFamily("monospace"),IsReadOnly=true};
        AutomationProperties.SetName(_sftpEditor,"Remote UTF-8 file editor");
        _sftpEditor.TextChanged+=(_,_)=>{if(_sftpEditPath is not null){_sftpDirty=true;RefreshSftpEditLabel();}};
        _sftpEditLabel=Ui.Text("Select a text file and choose Edit",11,"Muted");
        _sftpProgress=new ProgressBar {Minimum=0,Maximum=100,Height=3};
        _sftpLock=new CheckBox {Content="Read-only",IsChecked=true};
        _sftpLock.IsCheckedChanged+=(_,_)=>
        {
            if(_sftp is not {} session)return;
            if(_sftpLock.IsChecked==true) {session.ReadOnly=true;if(_sftpEditor is {} ed)ed.IsReadOnly=true;return;}
            if(!session.ReadOnly)return;
            _sftpLock.IsChecked=true;
            Run(async()=>
            {
                bool accepted=await SecurityDialogAsync("Enable SFTP writes?",done=>
                {
                    var warning=Ui.Text($"Enable file modifications on {session.Host}? Uploads, saves, renames and deletes affect the remote filesystem. Production connections start read-only after every reconnect.",13,"Warning");warning.TextWrapping=TextWrapping.Wrap;
                    return Ui.Stack(warning,Ui.Button("Enable writes","lock",()=>done(true),"primary"));
                },false,_windowLifetime.Token);
                if(accepted&&ReferenceEquals(_sftp,session)){session.ReadOnly=false;_sftpLock.IsChecked=false;if(_sftpEditor is {} ed)ed.IsReadOnly=_sftpEditPath is null;}
            });
        };
        var connect=Ui.Button("Connect","server",()=>SftpRun(async ct=>
        {
            if(picker.SelectedItem is not RoyalTerminal.Terminal.TerminalSessionProfile profile)throw new InvalidOperationException("Create an SSH profile in Connections & terminal profiles first.");
            if(!await ConfirmDiscardSftpEditAsync())return;
            if(_sftp is {} previous){_sftp=null;await previous.DisposeAsync();}
            _sftp=await SftpWorkspace.ConnectAsync(Shell.Profiles,profile,ct);
            _sftp.ReadOnly=true;_sftpLock.IsChecked=true;ClearSftpEditor();
            _remoteDirectory=_sftp.HomeDirectory;await RefreshSftpAsync(ct);
        }),"primary");
        var disconnect=Ui.Button("Disconnect",null,()=>SftpRun(async ct=>
        {
            if(!await ConfirmDiscardSftpEditAsync())return;
            if(_sftp is {} connection){_sftp=null;await connection.DisposeAsync();}
            ClearSftpEditor();_sftpFiles.ItemsSource=null;_sftpStatus.Text="Disconnected";
        }));
        _sftpActions=new WrapPanel {Orientation=Orientation.Horizontal};
        foreach(var button in new[]{connect,disconnect,Ui.Button("Refresh","history",()=>SftpRun(RefreshSftpAsync)),
            Ui.Button("Up",null,()=>SftpRun(async ct=>{_remoteDirectory=RemoteParent(_remoteDirectory);await RefreshSftpAsync(ct);})),
            Ui.Button("Upload","right",()=>SftpRun(UploadSftpAsync)),Ui.Button("Download","save",()=>SftpRun(DownloadSftpAsync)),
            Ui.Button("New folder","folder",()=>SftpRun(CreateSftpFolderAsync)),Ui.Button("Rename","note",()=>SftpRun(RenameSftpAsync)),
            Ui.Button("Delete","close",()=>SftpRun(DeleteSftpAsync)),Ui.Button("Edit","code",()=>SftpRun(EditSftpAsync))})
        {button.Margin=new Thickness(0,0,6,6);_sftpActions.Children.Add(button);}
        _sftpFiles.DoubleTapped+=(_,_)=>SftpRun(async ct=>
        {
            if(_sftpFiles.SelectedItem is not RemoteFile file)return;
            if(file.Directory&&!file.SymbolicLink){_remoteDirectory=file.Path;await RefreshSftpAsync(ct);}else await EditSftpAsync(ct);
        });
        _sftpPath.KeyDown+=(_,e)=>{if(e.Key==Key.Enter){e.Handled=true;SftpRun(async ct=>{_remoteDirectory=_sftpPath.Text??"/";await RefreshSftpAsync(ct);});}};
        var editorPanel=new Grid {RowDefinitions=new RowDefinitions("Auto,*,Auto"),Margin=new Thickness(14,0,0,0)};
        editorPanel.Children.Add(_sftpEditLabel);Grid.SetRow(_sftpEditor,1);editorPanel.Children.Add(_sftpEditor);
        var save=Ui.Button("Save remote file","save",()=>SftpRun(SaveSftpAsync),"primary");Grid.SetRow(save,2);save.Margin=new Thickness(0,8,0,0);editorPanel.Children.Add(save);
        var content=Ui.Row("2*,3*",_sftpFiles,editorPanel);
        var header=Ui.Stack(Ui.Row("*,Auto",Ui.Text("Files, close to the work.",25),_sftpLock),Ui.Row("Auto,14,*",picker,new Border(),_sftpPath),_sftpActions);
        var footer=Ui.Stack(_sftpProgress,Ui.Row("*,Auto",_sftpStatus,Ui.Button("Cancel operation",null,()=>_sftpOperation?.Cancel())));
        var layout=new Grid {RowDefinitions=new RowDefinitions("Auto,*,Auto"),Margin=new Thickness(24)};layout.Children.Add(header);Grid.SetRow(content,1);layout.Children.Add(content);Grid.SetRow(footer,2);layout.Children.Add(footer);
        _sftpWindow=new Window {Title="SFTP workspace — Tessera",Width=1160,Height=760,MinWidth=820,MinHeight=480,Content=layout,Background=ThemeManager.Brush("Surface"),Foreground=ThemeManager.Brush("Text"),FontFamily=FontFamily};
        AutomationProperties.SetName(_sftpWindow,"SFTP workspace");_sftpCloseApproved=false;
        _sftpWindow.Closing+=(_,e)=>{if(_sftpCloseApproved||_closed)return;e.Cancel=true;Run(async()=>{await TryCloseSftpAsync();});};
        _sftpWindow.Closed+=(_,_)=>{_sftpWindow=null;};
        _sftpWindow.Show(this);
    }
    private static string RemoteParent(string path) {path=path.TrimEnd('/');int slash=path.LastIndexOf('/');return slash<=0?"/":path[..slash];}
    private void SftpRun(Func<CancellationToken,Task> operation)
    {
        if(_sftpBusy)return;
        _sftpBusy=true;_sftpOperation?.Dispose();_sftpOperation=CancellationTokenSource.CreateLinkedTokenSource(_windowLifetime.Token);
        var token=_sftpOperation.Token;
        async Task Execute()
        {
            if(_sftpActions is {} actions)actions.IsEnabled=false;
            if(_sftpEditor is {} edit)edit.IsReadOnly=true;
            if(_sftpProgress is {} progress){progress.Value=0;progress.IsIndeterminate=true;}
            try {await operation(token);}
            catch(OperationCanceledException){if(_sftpStatus is {} status)status.Text="Operation cancelled. Destination files were not deliberately removed.";}
            catch(Exception ex){if(_sftpStatus is {} status){status.Text=Safety.CleanTitle(ex.Message);status.Foreground=ThemeManager.Brush("Danger");}}
            finally { if(_sftpEditor is {} finalEditor) finalEditor.IsReadOnly=_sftp?.ReadOnly!=false||_sftpEditPath is null; _sftpBusy=false;if(_sftpActions is {} a)a.IsEnabled=true;if(_sftpProgress is {} p)p.IsIndeterminate=false; }
        }
        _sftpWork=Execute();
    }
    private SftpWorkspace RequireSftp() => _sftp is {Connected:true} connection?connection:throw new InvalidOperationException("Connect an SSH profile first.");
    private RemoteFile SelectedSftpFile() => _sftpFiles?.SelectedItem as RemoteFile??throw new InvalidOperationException("Select a remote file first.");
    private async Task RefreshSftpAsync(CancellationToken token)
    {
        var connection=RequireSftp();var files=await connection.ListAsync(_remoteDirectory,token);
        _sftpPath!.Text=_remoteDirectory;_sftpFiles!.ItemsSource=files;
        _sftpStatus!.Text=$"{connection.Host} · {files.Length} entries · {(connection.ReadOnly?"read-only":"writes enabled")} · transfers use staging files";
        _sftpStatus.Foreground=ThemeManager.Brush("Muted");
    }
    private IProgress<FileTransferProgress> SftpProgress() => new Progress<FileTransferProgress>(p=>
    {
        if(_sftpProgress is {} bar){bar.IsIndeterminate=p.Total<=0;if(p.Total>0)bar.Value=100d*p.Completed/p.Total;}
        if(_sftpStatus is {} text)text.Text=$"{p.Operation} · {p.Completed:N0} / {p.Total:N0} bytes";
    });
    private async Task UploadSftpAsync(CancellationToken token)
    {
        var connection=RequireSftp();if(connection.ReadOnly)throw new InvalidOperationException("Enable SFTP writes first.");
        var files=await _fileDialogs.OpenFilesAsync(_sftpWindow!, new(){Title="Upload local files",AllowMultiple=true});
        foreach(var path in files)
        {
            token.ThrowIfCancellationRequested();
            var destination=SftpWorkspace.ChildPath(_remoteDirectory,Path.GetFileName(path));
            var existing=(await connection.ListAsync(_remoteDirectory,token)).FirstOrDefault(f=>f.Path==destination);
            bool overwrite=existing is not null;
            if(overwrite&&!await ConfirmSftpAsync("Replace remote file?",destination+" already exists. Replacement requires the server's atomic rename extension.","Replace file",token))continue;
            await connection.UploadAsync(path,destination,overwrite,SftpProgress(),token,existing is null?null:new RemoteVersion(existing.Length,existing.ModifiedUtc));
        }
        await RefreshSftpAsync(token);
    }
    private async Task DownloadSftpAsync(CancellationToken token)
    {
        var connection=RequireSftp();var selected=SelectedSftpFile();if(selected.Directory||selected.SymbolicLink)throw new InvalidOperationException("Choose a regular file for download.");
        var path=await _fileDialogs.SaveFileAsync(_sftpWindow!, new(){Title="Download remote file",SuggestedFileName=selected.Name});
        if(path is null)return;
        if(File.Exists(path)&&!await ConfirmSftpAsync("Replace local file?",path,"Replace file",token))return;
        await connection.DownloadAsync(selected.Path,path,File.Exists(path),SftpProgress(),token);_sftpStatus!.Text="Download completed · "+path;
    }
    private async Task CreateSftpFolderAsync(CancellationToken token)
    {
        var name=await SftpNameAsync("New remote folder","Folder name",token);if(name is null)return;
        await RequireSftp().CreateDirectoryAsync(SftpWorkspace.ChildPath(_remoteDirectory,name),token);await RefreshSftpAsync(token);
    }
    private async Task RenameSftpAsync(CancellationToken token)
    {
        var selected=SelectedSftpFile();if(!await ConfirmDiscardSftpEditAsync())return;
        var name=await SftpNameAsync("Rename remote entry",selected.Name,token);if(name is null)return;
        await RequireSftp().RenameAsync(selected.Path,SftpWorkspace.ChildPath(RemoteParent(selected.Path),name),token);ClearSftpEditor();await RefreshSftpAsync(token);
    }
    private async Task DeleteSftpAsync(CancellationToken token)
    {
        var selected=SelectedSftpFile();if(!await ConfirmDiscardSftpEditAsync())return;
        if(!await ConfirmSftpAsync("Delete remote entry?",selected.Path+" will be permanently deleted. Directories must be empty; deletion is never recursive.","Delete entry",token))return;
        await RequireSftp().DeleteAsync(selected,token);ClearSftpEditor();await RefreshSftpAsync(token);
    }
    private async Task EditSftpAsync(CancellationToken token)
    {
        var selected=SelectedSftpFile();if(selected.Directory||selected.SymbolicLink)throw new InvalidOperationException("Choose a regular UTF-8 text file (up to 2 MiB).");
        if(!await ConfirmDiscardSftpEditAsync())return;
        var (content,version)=await RequireSftp().ReadTextAsync(selected.Path,token);
        var text=new UTF8Encoding(false,true).GetString(content);
        _sftpEditPath=null;_sftpEditor!.Text=text;_sftpEditPath=selected.Path;_sftpEditVersion=version;_sftpDirty=false;
        _sftpEditor.IsReadOnly=RequireSftp().ReadOnly;RefreshSftpEditLabel();
    }
    private async Task SaveSftpAsync(CancellationToken token)
    {
        if(_sftpEditPath is not {} path||_sftpEditVersion is not {} version)throw new InvalidOperationException("Open a remote text file first.");
        var text = _sftpEditor!.Text ?? "";
        _sftpEditVersion=await RequireSftp().SaveTextAsync(path,new UTF8Encoding(false,true).GetBytes(text),version,token);
        _sftpDirty=_sftpEditor.Text!=text;RefreshSftpEditLabel();await RefreshSftpAsync(token);
    }
    private void ClearSftpEditor() {_sftpEditPath=null;_sftpEditVersion=null;_sftpDirty=false;if(_sftpEditor is {} editor){editor.Text="";editor.IsReadOnly=true;}RefreshSftpEditLabel();}
    private void RefreshSftpEditLabel() {if(_sftpEditLabel is {} label)label.Text=(_sftpEditPath??"Select a text file and choose Edit")+(_sftpDirty?" · unsaved":"");}
    private Task<bool> ConfirmDiscardSftpEditAsync() => !_sftpDirty?Task.FromResult(true):ConfirmSftpAsync("Discard unsaved remote edits?","The remote file has not been changed by these edits.","Discard edits",_windowLifetime.Token);
    private Task<bool> ConfirmSftpAsync(string title,string description,string accept,CancellationToken token) => SecurityDialogAsync(title,done=>
    {
        var text=Ui.Text(description,13,"Warning");text.TextWrapping=TextWrapping.Wrap;
        return Ui.Stack(text,Ui.Button(accept,"check",()=>done(true),"primary"));
    },false,token);
    private Task<string?> SftpNameAsync(string title,string initial,CancellationToken token) => SecurityDialogAsync<string?>(title,done=>
    {
        var input=Ui.Input(initial);input.MaxLength=255;
        return Ui.Stack(Ui.Field("Remote entry name",input),Ui.Button("Apply","check",()=>done(input.Text),"primary"));
    },null,token);
    private async Task<bool> TryCloseSftpAsync()
    {
        if(_sftpWindow is null)return true;
        _sftpOperation?.Cancel();await _sftpWork;
        if(!await ConfirmDiscardSftpEditAsync())return false;
        if(_sftp is {} connection){_sftp=null;await connection.DisposeAsync();}
        _sftpCloseApproved=true;_sftpWindow?.Close();ClearSftpEditor();return true;
    }
}
