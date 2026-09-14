using System.Text.RegularExpressions;

namespace Tessera.Core;

public static partial class Safety
{
    public const int MaximumPasteLength = 1024 * 1024;
    public static bool RequiresPasteConfirmation(string text) => text.Length > 4096 || text.Any(c => c is '\r' or '\n' or '\u001b' or '\0' || (c < 32 && c != '\t'));
    public static bool IsSafeCommandInsertion(string text) => text.Length <= MaximumPasteLength && !text.Any(c => c is '\r' or '\n' or '\u001b' or '\0');
    public static bool CanBroadcast(bool production, bool locked, bool running, bool replay, bool optedIn) => !production && !locked && running && !replay && optedIn;
    public static string CleanTitle(string? text) => string.IsNullOrWhiteSpace(text) ? "Terminal" : new string(text.Where(c => !char.IsControl(c)).Take(120).ToArray());
    public static string StripAnsi(string value) => Ansi().Replace(value, "");
    [GeneratedRegex("\\x1B(?:\\[[0-?]*[ -/]*[@-~]|\\][^\\x07]*(?:\\x07|\\x1B\\\\))", RegexOptions.CultureInvariant, 100)]
    private static partial Regex Ansi();
}
