using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Tessera.Views;
using Tessera.Services;

namespace Tessera;
public sealed partial class App : Application
{
    public override void Initialize() { AvaloniaXamlLoader.Load(this); ThemeManager.Apply("Obsidian"); }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.MainWindow = new MainWindow(Program.DesignMode);
        base.OnFrameworkInitializationCompleted();
    }
}
