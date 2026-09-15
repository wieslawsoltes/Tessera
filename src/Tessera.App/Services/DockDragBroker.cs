using System.Security.Cryptography;
using Avalonia.Input;
using Tessera.Core;

namespace Tessera.Services;

/// <summary>Opaque, one-use, process-local capability. External drags never supply trusted document identities.</summary>
public static class DockDragBroker
{
    public static DataFormat<string> Format { get; } = DataFormat.CreateStringApplicationFormat("tessera-terminal-dock-v1");
    private sealed record Offer(ShellController Shell, Guid Workspace, Guid Document);
    private static readonly Dictionary<string, Offer> Offers = [];
    private static readonly object Sync = new();
    public static string Begin(ShellController shell, Guid document)
    {
        if (!shell.Active.Documents.ContainsKey(document)) throw new InvalidOperationException("The dragged tab is no longer open.");
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        lock (Sync) Offers.Add(token, new(shell, shell.Active.Id, document));
        return token;
    }
    public static bool TryResolve(string? token, ShellController shell, out Guid document)
    {
        document = default;
        lock (Sync)
        {
            if (token is null || !Offers.TryGetValue(token, out var offer) || !ReferenceEquals(shell, offer.Shell) ||
                shell.Active.Id != offer.Workspace || !shell.Active.Documents.ContainsKey(offer.Document)) return false;
            document = offer.Document; return true;
        }
    }
    public static bool Drop(string? token, ShellController shell, Guid group, DockEdge edge, int index = int.MaxValue)
    {
        if (!TryResolve(token, shell, out var document) || !Layout.Groups(shell.Active).Any(g => g.Id == group)) return false;
        lock (Sync) { if (!Offers.Remove(token!)) return false; }
        shell.Move(document, group, edge, index); return true;
    }
    public static void End(string token) { lock (Sync) Offers.Remove(token); }
}
