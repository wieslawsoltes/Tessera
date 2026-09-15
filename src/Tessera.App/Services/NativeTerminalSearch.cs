using RoyalTerminal.Avalonia.Controls;
using RoyalTerminal.Terminal;
using Tessera.Core;

namespace Tessera.Services;

/// <summary>Immutable native-buffer snapshot with bounded worker matching and native literal-hit navigation.</summary>
public static class NativeTerminalSearch
{
    public static string Snapshot(TerminalControl terminal)
    {
        var options = new TerminalSnapshotExportOptions(Unwrap:false,TrimTrailingWhitespace:false);
        if (!terminal.TryExportSnapshot(TerminalSnapshotExportFormat.PlainText,options,out var text))
            throw new InvalidOperationException("The current terminal backend cannot export its search buffer.");
        if (text.Length > 8 * 1024 * 1024) throw new InvalidOperationException("Search is limited to an 8 MiB terminal snapshot. Reduce the scrollback limit.");
        return text.Replace("\r\n","\n",StringComparison.Ordinal);
    }
    public static bool Navigate(TerminalControl terminal,string snapshot,TerminalSearchHit hit)
    {
        // Never navigate an old offset after output, reflow or scrollback eviction changed the source buffer.
        if (!string.Equals(Snapshot(terminal),snapshot,StringComparison.Ordinal)) return false;
        string needle = hit.Text.Split('\n').FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "";
        if (needle.Length == 0) return false;
        int occurrence = 0, offset = 0;
        int needleOffset = hit.Offset + hit.Text.IndexOf(needle,StringComparison.Ordinal);
        while (offset < needleOffset)
        {
            int found = snapshot.IndexOf(needle,offset,StringComparison.Ordinal);
            if (found < 0 || found >= needleOffset) break;
            occurrence++; offset = found + needle.Length;
        }
        terminal.StartSearch(needle);
        if (occurrence >= terminal.SearchTotal) return false;
        terminal.SetSearchSelected(occurrence); return true;
    }
}
