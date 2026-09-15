using RoyalTerminal.Terminal;

namespace Tessera.Services;

/// <summary>Raw input is never persisted, even though the upstream in-memory recorder observes it.</summary>
public static class CapturePrivacy
{
    public static TerminalCaptureSession OutputOnly(TerminalCaptureSession source) => source with
    {
        Events = source.Events.Where(e => e.Kind != TerminalCaptureEventKind.Input).ToList(),
        DurationMilliseconds = source.DurationMilliseconds
    };
}
