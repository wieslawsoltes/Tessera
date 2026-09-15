using RoyalTerminal.Terminal;
namespace Tessera.Services;
/// <summary>Preserves RoyalTerminal PTY behavior with isolated Windows standard handles.</summary>
public sealed class TesseraPtyFactory : IPtyFactory
{
    public IPty Create() => OperatingSystem.IsWindows()
        ? new Compatibility.WindowsPty()
        : new DefaultPtyFactory().Create();
}
