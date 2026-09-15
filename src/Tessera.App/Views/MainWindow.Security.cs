using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Tessera.Services;
using Tessera.Core;

namespace Tessera.Views;

public sealed partial class MainWindow
{
    private readonly SemaphoreSlim _securityDialogs = new(1,1);
    private readonly CancellationTokenSource _windowLifetime = new();

    private void ConfigureSecurityWorkflows()
    {
        Shell.Profiles.CredentialPrompt = CredentialPromptAsync;
        Shell.Profiles.KnownHosts.Prompt = HostKeyPromptAsync;
        Shell.Profiles.ChallengePrompt = ChallengePromptAsync;
        Shell.Recovery.Keys.PassphrasePrompt = RecoveryPassphraseAsync;
    }

    /// <summary>Serial modal dialogs preserve an open profile/editor overlay and never block the UI dispatcher.</summary>
    private async Task<T> SecurityDialogAsync<T>(string title, Func<Action<T>, Control> content,
        T cancelled, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_windowLifetime.Token,cancellationToken);
        await _securityDialogs.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            return await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                linked.Token.ThrowIfCancellationRequested();
                var dialog = new Window { Title=title+" — Tessera",Width=660,MinWidth=460,MaxHeight=760,
                    SizeToContent=SizeToContent.Height,CanResize=true,WindowStartupLocation=WindowStartupLocation.CenterOwner,
                    Background=ThemeManager.Brush("Surface"),Foreground=ThemeManager.Brush("Text"),FontFamily=FontFamily };
                AutomationProperties.SetName(dialog,title);
                var body = content(value => dialog.Close(value));
                var root = Ui.Stack(Ui.Text(title,24),body);
                root.Margin = new Thickness(26); KeyboardNavigation.SetTabNavigation(root,KeyboardNavigationMode.Cycle);
                dialog.Content = new ScrollViewer { Content=root,MaxHeight=710,HorizontalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
                dialog.KeyDown += (_,e) => { if(e.Key==Key.Escape) { e.Handled=true;dialog.Close(cancelled); } };
                using var cancel = linked.Token.Register(() => Dispatcher.UIThread.Post(() => dialog.Close(cancelled)));
                return await dialog.ShowDialog<T>(this);
            });
        }
        finally { _securityDialogs.Release(); }
    }
    private Task<string?> CredentialPromptAsync(string title,CancellationToken token) =>
        SecurityDialogAsync<string?>(title,complete =>
        {
            var password = Ui.Input(); password.PasswordChar='●';password.MaxLength=4096;
            AutomationProperties.SetName(password,"Credential");
            var warning = Ui.Text("This response stays in memory. Use the explicit Remember control in connection profiles to save a password to the OS vault. Passphrases and MFA responses are never saved.",12,"Muted");warning.TextWrapping=TextWrapping.Wrap;
            void Accept() { var value=password.Text;password.Text="";complete(value); }
            password.KeyDown += (_,e) => { if(e.Key==Key.Enter) {e.Handled=true;Accept();} };
            password.AttachedToVisualTree += (_,_) => Dispatcher.UIThread.Post(() => password.Focus());
            return Ui.Stack(warning,password,Ui.Row("*,Auto,Auto",new Border(),Ui.Button("Cancel",null,()=>{password.Text="";complete(null);}),Ui.Button("Authenticate","lock",Accept,"primary")));
        },null,token);

    private Task<HostKeyDecision> HostKeyPromptAsync(HostKeyChallenge challenge,CancellationToken token) =>
        SecurityDialogAsync("Verify this SSH host",complete =>
        {
            var description=Ui.Text("This host is not yet trusted. Compare the fingerprint with an independent, trusted source before continuing.",13,"Warning");description.TextWrapping=TextWrapping.Wrap;
            var identity = new TextBox {Text=$"{challenge.User}@{challenge.Host}:{challenge.Port}\n{challenge.Algorithm}\n{challenge.Fingerprint}",IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap};
            AutomationProperties.SetName(identity,"Presented host key fingerprint");
            var remember=Ui.Text("Trust once applies only to this connection. Trust and save records the key in Tessera's known_hosts. Changed and revoked keys are rejected without an override button.",12,"Muted");remember.TextWrapping=TextWrapping.Wrap;
            return Ui.Stack(description,identity,remember,Ui.Row("Auto,*,Auto,Auto",Ui.Button("Reject",null,()=>complete(HostKeyDecision.Reject)),new Border(),
                Ui.Button("Trust once",null,()=>complete(HostKeyDecision.TrustOnce)),Ui.Button("Trust and save","check",()=>complete(HostKeyDecision.TrustAndSave),"primary")));
        },HostKeyDecision.Reject,token);

    private Task<string[]?> ChallengePromptAsync(SshChallenge challenge,CancellationToken token) =>
        SecurityDialogAsync<string[]?>("SSH authentication · challenge "+challenge.Round,complete =>
        {
            var fields=challenge.Prompts.Select(p =>
            {
                var input=Ui.Input();input.MaxLength=4096;if(!p.Echo)input.PasswordChar='●';AutomationProperties.SetName(input,p.Label);return input;
            }).ToArray();
            var body=Ui.Stack(Ui.Text(Safety.CleanTitle(challenge.User+"@"+challenge.Host),13,"Accent"));
            var instructions=Ui.Text(challenge.Instructions,12,"Muted");instructions.TextWrapping=TextWrapping.Wrap;body.Children.Add(instructions);
            for(int i=0;i<fields.Length;i++)body.Children.Add(Ui.Field(challenge.Prompts[i].Label,fields[i]));
            void Submit() {var answers=fields.Select(f=>f.Text??"").ToArray();foreach(var f in fields)f.Text="";complete(answers);}
            body.Children.Add(Ui.Row("*,Auto,Auto",new Border(),Ui.Button("Cancel",null,()=>{foreach(var f in fields)f.Text="";complete(null!);}),Ui.Button("Continue","lock",Submit,"primary")));
            if(fields.FirstOrDefault() is {} first)first.AttachedToVisualTree+=(_,_)=>Dispatcher.UIThread.Post(()=>first.Focus());
            return body;
        },null,token);

    private Task<string?> RecoveryPassphraseAsync(bool create,CancellationToken token) =>
        SecurityDialogAsync<string?>(create?"Protect capture recovery":"Unlock recovered recordings",complete =>
        {
            var password=Ui.Input();password.PasswordChar='●';password.MaxLength=4096;
            var confirm=Ui.Input();confirm.PasswordChar='●';confirm.MaxLength=4096;
            var message=Ui.Text(create?"The OS vault is unavailable. Choose a recovery passphrase of at least 12 characters. Recordings are encrypted with AES-256-GCM; only the wrapped key is stored. Losing this passphrase means losing access to recovered recordings.":"Enter the recovery passphrase. It is used locally to unlock the recording key and is never stored.",12,"Muted");message.TextWrapping=TextWrapping.Wrap;
            var error=Ui.Text("",11,"Danger");error.TextWrapping=TextWrapping.Wrap;
            var body=Ui.Stack(message,Ui.Field("Recovery passphrase",password));if(create)body.Children.Add(Ui.Field("Confirm passphrase",confirm));body.Children.Add(error);
            body.Children.Add(Ui.Button(create?"Protect recordings":"Unlock recordings","lock",()=>
            {
                if((password.Text?.Length??0)<12 || create&&password.Text!=confirm.Text) {error.Text="Use at least 12 characters and matching confirmation.";return;}
                var value=password.Text;password.Text="";confirm.Text="";complete(value);
            },"primary"));return body;
        },null,token);
}
