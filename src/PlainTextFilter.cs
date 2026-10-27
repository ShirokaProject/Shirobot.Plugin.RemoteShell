using System.Text;

namespace RemoteShell;

/// <summary>Streaming terminal control filtering, applied separately to stdout and stderr before limiting output.</summary>
internal sealed class PlainTextFilter
{
    private enum State { Text, Escape, EscapeIntermediate, Csi, ControlString, StringEscape }
    private State _state;
    private bool _afterCarriageReturn;

    internal string Filter(ReadOnlySpan<char> input)
    {
        var result = new StringBuilder(input.Length);
        foreach (var c in input)
        {
            switch (_state)
            {
                case State.Escape:
                    _state = c switch
                    {
                        '[' => State.Csi,
                        ']' or 'P' or 'X' or '^' or '_' => State.ControlString,
                        >= '\x20' and <= '\x2f' => State.EscapeIntermediate,
                        '\x1b' => State.Escape,
                        _ => State.Text
                    };
                    continue;
                case State.EscapeIntermediate:
                    if (c is >= '\x30' and <= '\x7e') _state = State.Text;
                    else if (c == '\x1b') _state = State.Escape;
                    continue;
                case State.Csi:
                    if (c is >= '\x40' and <= '\x7e') _state = State.Text;
                    else if (c == '\x1b') _state = State.Escape;
                    continue;
                case State.ControlString:
                    if (c is '\a' or '\x9c') _state = State.Text;
                    else if (c == '\x1b') _state = State.StringEscape;
                    continue;
                case State.StringEscape:
                    _state = c is '\\' or '\a' or '\x9c' ? State.Text :
                        c == '\x1b' ? State.StringEscape : State.ControlString;
                    continue;
            }

            if (c == '\x1b') { _state = State.Escape; continue; }
            if (c == '\x9b') { _state = State.Csi; continue; }
            if (c is '\x90' or '\x98' or '\x9d' or '\x9e' or '\x9f')
            {
                _state = State.ControlString;
                continue;
            }
            if (c == '\r')
            {
                result.Append('\n');
                _afterCarriageReturn = true;
                continue;
            }
            if (c == '\n')
            {
                if (!_afterCarriageReturn) result.Append(c);
                _afterCarriageReturn = false;
                continue;
            }
            _afterCarriageReturn = false;
            if (c == '\t' || !char.IsControl(c)) result.Append(c);
        }
        return result.ToString();
    }
}
