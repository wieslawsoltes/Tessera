using System.Text;

namespace Tessera.Core;

/// <summary>Streaming UTF-8/VT sanitizer for output-only text logs. Parser state survives arbitrary byte boundaries.</summary>
public sealed class TerminalTextDecoder
{
    private enum State { Text, Escape, Csi, String, StringEscape }
    private State _state;
    private readonly Decoder _decoder = new UTF8Encoding(false, false).GetDecoder();
    public string Decode(ReadOnlySpan<byte> bytes, bool flush = false)
    {
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        var count = _decoder.GetChars(bytes, chars, flush);
        var result = new StringBuilder(count);
        foreach (var ch in chars.AsSpan(0, count))
        {
            switch (_state)
            {
                case State.Text:
                    if (ch == '\x1b') _state = State.Escape;
                    else if (ch == '\u009b') _state = State.Csi;
                    else if (ch is '\u0090' or '\u009d' or '\u009e' or '\u009f') _state = State.String;
                    else if (!char.IsControl(ch) || ch is '\r' or '\n' or '\t') result.Append(ch);
                    break;
                case State.Escape:
                    _state = ch switch { '[' => State.Csi, ']' or 'P' or '^' or '_' => State.String, >= ' ' and <= '/' => State.Escape, _ => State.Text };
                    break;
                case State.Csi:
                    if (ch is >= '@' and <= '~') _state = State.Text;
                    else if (ch == '\x1b') _state = State.Escape;
                    break;
                case State.String:
                    if (ch is '\a' or '\u009c') _state = State.Text;
                    else if (ch == '\x1b') _state = State.StringEscape;
                    break;
                case State.StringEscape:
                    _state = ch == '\\' || ch == '\a' ? State.Text : ch == '\x1b' ? State.StringEscape : State.String;
                    break;
            }
        }
        return result.ToString();
    }
}
