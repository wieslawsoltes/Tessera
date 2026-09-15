using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tessera.Services;
using Tessera.Views;
using Xunit;

namespace Tessera.NativeTests;

internal sealed class ScriptedFileDialogs : IWorkspaceFileDialogs
{
    public Queue<IReadOnlyList<string>> Opens { get; } = new();
    public Queue<string?> Saves { get; } = new();
    public Queue<string?> Folders { get; } = new();
    public List<string> Requests { get; } = [];
    public Task<IReadOnlyList<string>> OpenFilesAsync(Window owner, FilePickerOpenOptions options)
    { Requests.Add(options.Title ?? "Open"); return Task.FromResult(Opens.Dequeue()); }
    public Task<string?> SaveFileAsync(Window owner, FilePickerSaveOptions options)
    { Requests.Add(options.Title ?? "Save"); return Task.FromResult(Saves.Dequeue()); }
    public Task<string?> OpenFolderAsync(Window owner, FolderPickerOpenOptions options)
    { Requests.Add(options.Title ?? "Folder"); return Task.FromResult(Folders.Dequeue()); }
}

internal sealed class UiRegressionFixture : IAsyncDisposable
{
    private readonly AcceptanceDirectory _directory = new();
    public string DirectoryPath => _directory.Path;
    public MainWindow Window { get; private set; } = null!;
    public ScriptedFileDialogs Files { get; } = new();
    public static CancellationToken Token => TestContext.Current.CancellationToken;
    public Control Overlay => Window.FindControl<ContentControl>("OverlayContent")!;

    public static async Task<UiRegressionFixture> OpenAsync()
    {
        var fixture = new UiRegressionFixture();
        try
        {
            fixture.Window = new MainWindow(true, fixture.DirectoryPath, fixture.Files);
            fixture.Window.Show();
            await fixture.Window.InitializeAsync();
            await PumpAsync();
            return fixture;
        }
        catch { await fixture.DisposeAsync(); throw; }
    }

    public static async Task PumpAsync(int delay = 35)
    {
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(delay, Token);
        Dispatcher.UIThread.RunJobs();
    }

    public static async Task UntilAsync(Func<bool> condition, string reason, int timeout = 5000)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && watch.ElapsedMilliseconds < timeout) await PumpAsync();
        Assert.True(condition(), reason);
    }

    public static T Named<T>(Control root, string label) where T : Control =>
        Assert.Single(root.GetVisualDescendants().OfType<T>(), c => AutomationProperties.GetName(c) == label);
    public static Button Button(Control root, string label) =>
        Assert.Single(root.GetVisualDescendants().OfType<Button>(), b =>
            AutomationProperties.GetName(b) == label || Equals(b.Content, label) ||
            b.Content is Control content && content.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == label));
    public static void Click(Button button)
    {
        Assert.True(button.IsEffectivelyEnabled);
        button.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
    }
    public static void Press(Control target, Key key, KeyModifiers modifiers = KeyModifiers.None) =>
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });
    public async Task DismissAsync()
    {
        Press(Window, Key.Escape);
        await PumpAsync();
        Assert.False(Window.IsOverlayOpen);
    }
    public static void Screenshot(Window window, string name)
    {
        string root = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Directory.GetCurrentDirectory();
        string path = Path.Combine(root, "artifacts", "screenshots", "ui-regressions");
        Directory.CreateDirectory(path);
        using var bitmap = window.CaptureRenderedFrame(); Assert.NotNull(bitmap);
#pragma warning disable CS0618
        bitmap.Save(Path.Combine(path, name + ".png"));
#pragma warning restore CS0618
    }
    public async ValueTask DisposeAsync()
    {
        if (Window is not null)
        {
            try { await Window.Shell.PrepareShutdownAsync(); }
            finally { Window.CloseForTests(); }
            Dispatcher.UIThread.RunJobs();
        }
        _directory.Dispose();
    }
}
