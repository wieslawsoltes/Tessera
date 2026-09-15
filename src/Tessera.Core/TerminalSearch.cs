using System.Text.RegularExpressions;

namespace Tessera.Core;

public sealed record TerminalSearchQuery(string Text, bool RegularExpression = false, bool CaseSensitive = false, bool WholeWord = false);
public sealed record TerminalSearchHit(int Offset, int Length, int Line, string Text, string Context);
public sealed record TerminalSearchResults(TerminalSearchHit[] Matches, string? Error = null, bool Truncated = false);

/// <summary>Bounded Unicode-aware literal/regex matching over an immutable terminal snapshot.</summary>
public static class TerminalSearch
{
    public static TerminalSearchResults Find(string snapshot, TerminalSearchQuery query, int maximumMatches = 5000)
    {
        if (string.IsNullOrEmpty(query.Text)) return new([]);
        if (query.Text.Length > 2048) return new([], "Search patterns are limited to 2,048 characters.");
        if (snapshot.Length > 8 * 1024 * 1024) return new([], "The search snapshot exceeds 8 MiB.");
        try
        {
            var pattern = query.RegularExpression ? query.Text : Regex.Escape(query.Text);
            if (query.WholeWord) pattern = @"(?<![\p{L}\p{N}_])(?:" + pattern + @")(?![\p{L}\p{N}_])";
            var options = RegexOptions.CultureInvariant | RegexOptions.Multiline | (query.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
            var regex = new Regex(pattern, options, TimeSpan.FromMilliseconds(100));
            var matches = new List<TerminalSearchHit>();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            int line = 1, scanned = 0;
            foreach (Match match in regex.Matches(snapshot))
            {
                if (stopwatch.ElapsedMilliseconds > 350) return new(matches.ToArray(), "Search stopped at its time budget.", true);
                if (match.Length == 0) continue;
                while (scanned < match.Index) if (snapshot[scanned++] == '\n') line++;
                int start = snapshot.LastIndexOf('\n', Math.Max(0, match.Index - 1));
                start = start < 0 ? 0 : start + 1;
                int end = snapshot.IndexOf('\n', match.Index);
                end = end < 0 ? snapshot.Length : end;
                matches.Add(new(match.Index, match.Length, line, match.Value,
                    snapshot.Substring(start, Math.Min(end - start, 512)).TrimEnd('\r')));
                if (matches.Count >= Math.Clamp(maximumMatches, 1, 5000)) return new(matches.ToArray(), Truncated: true);
            }
            return new(matches.ToArray());
        }
        catch (ArgumentException ex) { return new([], ex.Message); }
        catch (RegexMatchTimeoutException) { return new([], "Pattern exceeded the 100 ms execution limit. Simplify the expression."); }
    }
}
