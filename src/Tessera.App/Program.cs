using Avalonia;

namespace Tessera;
public static class Program
{
    public static bool DesignMode { get; private set; }
    [STAThread]
    public static void Main(string[] args)
    {
        DesignMode = args.Contains("--design-mode", StringComparer.Ordinal);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
