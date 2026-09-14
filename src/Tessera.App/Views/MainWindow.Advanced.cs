using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using RoyalTerminal.Avalonia.Rendering;
using RoyalTerminal.Shaders;
using RoyalTerminal.Terminal;
using Tessera.Services;

namespace Tessera.Views;

public sealed partial class MainWindow
{
    private const string TintShader = "uniform shader shaderTexture;\nhalf4 main(float2 p) {\n    half4 c = shaderTexture.eval(p);\n    return half4(c.r * 0.86, c.g, c.b * 0.90, c.a);\n}";
    private const string ScanlineShader = "uniform shader shaderTexture;\nhalf4 main(float2 p) {\n    half4 c = shaderTexture.eval(p);\n    float stripe = 0.97 + 0.03 * sin(p.y * 3.14159265);\n    return half4(c.rgb * stripe, c.a);\n}";

    private void RegisterAdvancedCommands()
    {
        void Add(string id, string title, string category, string icon, Action action) =>
            _commands.Add(new AppCommand(id, title, category, icon, "", () => { action(); return Task.CompletedTask; }, ex => ShowError(ex.Message)));
        Add("shaders", "Terminal shaders", "View", "sun", ShowShaders);
        Add("shell-integration", "Enable shell integration", "Tools", "terminal", ShowShellIntegration);
        Add("keybindings", "Edit keyboard bindings", "View", "code", ShowKeybindings);
    }

    private void ShowShaders()
    {
        var session = Shell.ActiveSession;
        if(session is null) { ShowError("Open a terminal before configuring its shader pipeline."); return; }
        var presets = new ComboBox { ItemsSource = new[] { "None", "Sage tint", "Subtle scanlines", "Custom" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var language = new ComboBox { ItemsSource = Enum.GetValues<TerminalShaderLanguage>(), SelectedItem = TerminalShaderLanguage.SkiaRuntimeEffect, HorizontalAlignment = HorizontalAlignment.Stretch };
        var editor = new TextBox { Text = TintShader, AcceptsReturn = true, AcceptsTab = true, MaxLength = 262144, FontFamily = new FontFamily("monospace"), Height = 245, TextWrapping = TextWrapping.NoWrap };
        var animate = new CheckBox { Content = "Shader needs continuous animation", IsChecked = false };
        var diagnostics = Ui.Text("No shader will be applied until validation succeeds.", 11, "Muted");
        diagnostics.TextWrapping = TextWrapping.Wrap;
        presets.SelectionChanged += (_, _) =>
        {
            if(presets.SelectedIndex == 1) editor.Text = TintShader;
            if(presets.SelectedIndex == 2) editor.Text = ScanlineShader;
            if(presets.SelectedIndex is 1 or 2) language.SelectedItem = TerminalShaderLanguage.SkiaRuntimeEffect;
        };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        actions.Children.Add(Ui.Button("Load source", "file", () => Run(async () =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new() { Title = "Open shader source", AllowMultiple = false });
            if(files.FirstOrDefault()?.TryGetLocalPath() is not {} path) return;
            if(new FileInfo(path).Length > 262144) throw new InvalidOperationException("Shader source is limited to 256 KiB.");
            editor.Text = await File.ReadAllTextAsync(path); presets.SelectedIndex = 3;
        })));
        actions.Children.Add(Ui.Button("Validate & apply", "check", () => Run(() =>
        {
            if(presets.SelectedIndex == 0) { session.Terminal.ShaderSources = []; diagnostics.Text = "Shader pipeline disabled."; return Task.CompletedTask; }
            if(string.IsNullOrWhiteSpace(editor.Text)) throw new InvalidOperationException("Enter shader source first.");
            TerminalShaderSource[] sources = [new("Tessera custom shader", editor.Text, (TerminalShaderLanguage)language.SelectedItem!, requiresContinuousAnimation: animate.IsChecked == true)];
            using(var processor = TerminalShaderPostProcessor.Create(sources))
            {
                if(!string.IsNullOrWhiteSpace(processor.CompileLog)) { diagnostics.Text = processor.CompileLog; diagnostics.Foreground = ThemeManager.Brush("Danger"); return Task.CompletedTask; }
            }
            session.Terminal.ShaderSources = sources;
            session.Terminal.ShaderAnimationEnabled = !Shell.Data.Preferences.ReducedMotion;
            session.Terminal.InvalidateTerminal();
            diagnostics.Text = "Compiled and applied to the active terminal. Reduced motion disables continuous animation.";
            diagnostics.Foreground = ThemeManager.Brush("Accent");
            return Task.CompletedTask;
        }), "primary"));
        ShowOverlay("A little atmosphere. Still a terminal.", "A real RoyalTerminal framebuffer shader pipeline. Effects are opt-in, per-session, and do not modify terminal bytes.",
            Ui.Stack(Ui.Row("*,16,*", Ui.Field("Preset", presets), new Border(), Ui.Field("Source language", language)), editor, animate, diagnostics, actions), 820);
    }

    private void ShowShellIntegration()
    {
        var session = Shell.ActiveSession;
        if(session is null || !session.IsRunning || session.Locked || session.IsReplay)
        {
            ShowError("Connect an unlocked terminal before enabling shell integration."); return;
        }
        var shell = new ComboBox { ItemsSource = Enum.GetValues<TerminalShellIntegrationBootstrapShell>(), SelectedItem = OperatingSystem.IsWindows() ? TerminalShellIntegrationBootstrapShell.PowerShell : TerminalShellIntegrationBootstrapShell.Bash, HorizontalAlignment = HorizontalAlignment.Stretch };
        var explanation = Ui.Text("Choose the shell currently running in this pane. Tessera will send RoyalTerminal's bootstrap script into that shell. It reports command completion, exit status, and working directory through OSC 7/133. It does not edit your dotfiles or replace native completion.", 12, "Muted");
        explanation.TextWrapping = TextWrapping.Wrap;
        var enable = Ui.Button("Enable in this session", "check", () => Run(async () =>
        {
            var kind = (TerminalShellIntegrationBootstrapShell)shell.SelectedItem!;
            var script = TerminalShellIntegrationBootstrapBuilder.Build(new TerminalShellIntegrationBootstrapOptions(kind));
            if(string.IsNullOrWhiteSpace(script)) throw new InvalidOperationException("No bootstrap is available for this shell.");
            if(!await ConfirmAsync("Run shell integration bootstrap?", "This executes the generated " + kind + " hooks in " + session.Profile.DisplayName + ". Verify the selected shell and host first.", "Enable integration")) return;
            session.Send(script + "\r");
            Shell.SetTool("History"); Shell.Report("Shell integration bootstrap sent. Completed commands will appear in History.");
        }), "primary");
        ShowOverlay("Context, without taking over your shell.", "Explicit, session-local shell integration.", Ui.Stack(explanation, Ui.Field("Running shell", shell), enable), 710);
    }
}
