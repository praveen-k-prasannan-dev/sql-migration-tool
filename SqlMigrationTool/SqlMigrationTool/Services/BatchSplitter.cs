using System.Text;
using System.Text.RegularExpressions;

namespace SqlMigrationTool.Services;

/// <summary>
/// Splits a script into batches on GO separators the way sqlcmd/SSMS do: GO must
/// stand on its own line (any case, optionally "GO n" to repeat the batch, and an
/// optional trailing -- comment), and is ignored inside comments, string literals
/// and [bracketed] or "quoted" identifiers. GOTO and the like never split.
/// </summary>
public static class BatchSplitter
{
    private static readonly Regex GoLine =
        new(@"^\s*GO(?:\s+(?<count>\d+))?\s*(?:--.*)?$", RegexOptions.IgnoreCase);

    private enum State { Normal, BlockComment, String, Bracket, Quoted }

    public static List<string> Split(string script)
    {
        var batches = new List<string>();
        var current = new StringBuilder();
        var state   = State.Normal;
        int depth   = 0;   // block comment nesting

        foreach (var rawLine in script.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            if (state == State.Normal && GoLine.Match(line) is { Success: true } go)
            {
                var count = go.Groups["count"].Success ? int.Parse(go.Groups["count"].Value) : 1;
                var batch = current.ToString();
                for (int i = 0; i < count; i++) batches.Add(batch);
                current.Clear();
                continue;
            }

            Scan(line, ref state, ref depth);
            current.Append(rawLine).Append('\n');
        }

        if (current.Length > 0) batches.Add(current.ToString());
        return batches.Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => b.TrimEnd('\r', '\n')).ToList();
    }

    /// <summary>Advances the lexical state across one line.</summary>
    private static void Scan(string line, ref State state, ref int depth)
    {
        for (int i = 0; i < line.Length; i++)
        {
            char c    = line[i];
            char next = i + 1 < line.Length ? line[i + 1] : '\0';

            switch (state)
            {
                case State.Normal:
                    if (c == '-' && next == '-') return;                         // rest of line is a comment
                    if (c == '/' && next == '*') { state = State.BlockComment; depth = 1; i++; }
                    else if (c == '\'') state = State.String;
                    else if (c == '[')  state = State.Bracket;
                    else if (c == '"')  state = State.Quoted;
                    break;

                case State.BlockComment:
                    if (c == '/' && next == '*') { depth++; i++; }
                    else if (c == '*' && next == '/') { i++; if (--depth == 0) state = State.Normal; }
                    break;

                case State.String:   // '' is an escaped quote
                    if (c == '\'') { if (next == '\'') i++; else state = State.Normal; }
                    break;

                case State.Bracket:  // ]] is an escaped bracket
                    if (c == ']') { if (next == ']') i++; else state = State.Normal; }
                    break;

                case State.Quoted:   // "" is an escaped quote
                    if (c == '"') { if (next == '"') i++; else state = State.Normal; }
                    break;
            }
        }
    }
}
