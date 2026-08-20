/* In the name of God, the Merciful, the Compassionate */

using System.Text;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// Builds a Windows command line that <c>CommandLineToArgvW</c> parses back into exactly the
    /// arguments handed in.
    ///
    /// This exists because the previous inline form quoted an argument only when it
    /// <c>Contains(' ')</c>:
    ///
    ///     args.Select(a =&gt; a.Contains(' ') ? $"\"{a}\"" : a)
    ///
    /// which is wrong in two independent ways, both exercised in CommandLineEscaperTests:
    ///   1. An argument holding a double quote was wrapped in MORE quotes, terminating the quoted
    ///      run early. Everything after the embedded quote was re-parsed as command line syntax, so
    ///      a value could inject further arguments — e.g. a username of
    ///          a" --extra-arg x
    ///      arrives as TWO arguments plus a switch the caller never passed.
    ///   2. An argument holding a tab, newline or vertical tab was left bare, and argv splits on
    ///      those too, so the value silently arrived as several arguments.
    ///
    /// The algorithm is the one CommandLineToArgvW documents as its own inverse: backslashes are
    /// only special immediately before a double quote, where each must be doubled, and the quote
    /// itself escaped. The tests do not assert this by reading the code — they call the real
    /// CommandLineToArgvW and require the round trip to come back byte-identical.
    /// </summary>
    public static class CommandLineEscaper
    {
        // The exact set argv treats as a separator, plus the quote character itself.
        private static readonly char[] MustQuote = { ' ', '\t', '\n', '\v', '"' };

        /// <summary>
        /// Escapes one argument so it survives <c>CommandLineToArgvW</c> as a single argv token.
        /// </summary>
        public static string EscapeArgument(string argument)
        {
            ArgumentNullException.ThrowIfNull(argument);

            // An empty argument is NOT nothing — it must survive as an empty token, which only a
            // bare pair of quotes achieves. Returning it unchanged would drop it from argv entirely
            // and shift every later argument down one position.
            if (argument.Length > 0 && argument.IndexOfAny(MustQuote) < 0)
                return argument;

            var sb = new StringBuilder(argument.Length + 2);
            sb.Append('"');

            for (int i = 0; i < argument.Length; i++)
            {
                int backslashes = 0;
                while (i < argument.Length && argument[i] == '\\')
                {
                    backslashes++;
                    i++;
                }

                if (i == argument.Length)
                {
                    // Trailing backslashes sit immediately before the closing quote we are about to
                    // append, so argv would read them as escaping it. Double them.
                    sb.Append('\\', backslashes * 2);
                    break;
                }

                if (argument[i] == '"')
                {
                    // Double the run, then escape the quote itself.
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                }
                else
                {
                    // Backslashes not before a quote are literal — emit them unchanged.
                    sb.Append('\\', backslashes);
                    sb.Append(argument[i]);
                }
            }

            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>
        /// Joins arguments into one command line, each escaped so argv recovers it exactly.
        /// </summary>
        public static string JoinArguments(IEnumerable<string> arguments)
        {
            ArgumentNullException.ThrowIfNull(arguments);
            return string.Join(' ', arguments.Select(EscapeArgument));
        }
    }
}
