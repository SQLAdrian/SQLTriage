/* In the name of God, the Merciful, the Compassionate */

using System;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// The one-server hand-off behind <c>/servers?add=NAME</c>.
    ///
    /// <para>A page that DISCOVERS a server (the Replication Map, today) never writes to the
    /// server-connections store itself. It hands one name to the existing add-connection
    /// dialog, which the operator reviews and saves. This parser is the whole contract, and it
    /// is deliberately strict: exactly one name, no newlines. The dialog's
    /// <c>ServerNames</c> field is newline-separated, so a value carrying newlines would turn a
    /// single reviewed add into a silent bulk write — the precise thing this route exists to
    /// avoid.</para>
    /// </summary>
    /// <summary>What the URL asked for, told apart from what the parser did with it.</summary>
    public enum AddPrefillOutcome
    {
        /// <summary>No <c>add</c> parameter was present. The page behaves as if the operator
        /// had simply navigated to /servers.</summary>
        NotRequested = 0,
        /// <summary>A single usable name was supplied.</summary>
        Accepted,
        /// <summary>A value was supplied and refused. This is NOT the same as absent, and the
        /// page must say so rather than carrying on as though nothing had been asked for.</summary>
        Refused
    }

    /// <summary>The parse result: the outcome, the accepted name, and the measured reason for
    /// a refusal.</summary>
    public sealed class AddPrefillRequest
    {
        public AddPrefillOutcome Outcome { get; init; }
        /// <summary>Set only when <see cref="Outcome"/> is <c>Accepted</c>.</summary>
        public string Name { get; init; } = "";
        /// <summary>Set only when <see cref="Outcome"/> is <c>Refused</c> — what was wrong with
        /// the value, in words that can be shown to the operator.</summary>
        public string Reason { get; init; } = "";

        internal static readonly AddPrefillRequest Absent = new() { Outcome = AddPrefillOutcome.NotRequested };
        internal static AddPrefillRequest Ok(string name) => new() { Outcome = AddPrefillOutcome.Accepted, Name = name };
        internal static AddPrefillRequest No(string reason) => new() { Outcome = AddPrefillOutcome.Refused, Reason = reason };
    }

    public static class ServerAddPrefill
    {
        /// <summary>Longest server name accepted. sysname is 128 characters; the extra room
        /// covers "host\instance,port" forms without admitting a payload.</summary>
        public const int MaxLength = 160;

        /// <summary>
        /// Reads the URI's <c>add</c> parameter and reports which of the three outcomes occurred.
        ///
        /// <para><b>Why an outcome and not a nullable string.</b> The nullable form collapsed
        /// "nothing was asked for" and "what was asked for was refused" into one null, and the
        /// page treated both as absent — so a refused value fell through into the ordinary
        /// first-add path and the dialog opened pre-filled with the local bulk-discovery list.
        /// The operator arriving from a hand-off saw a populated dialog and no refusal, which
        /// reads as "the name you asked for", and the pre-filled names were not it.</para>
        /// </summary>
        public static AddPrefillRequest Read(string? uri)
        {
            if (string.IsNullOrWhiteSpace(uri)) return AddPrefillRequest.Absent;

            var q = uri!.IndexOf('?');
            if (q < 0 || q == uri.Length - 1) return AddPrefillRequest.Absent;

            var query = uri.Substring(q + 1);
            var hash = query.IndexOf('#');
            if (hash >= 0) query = query.Substring(0, hash);

            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                var key = pair.Substring(0, eq);
                if (!string.Equals(key, "add", StringComparison.OrdinalIgnoreCase)) continue;

                var raw = pair.Substring(eq + 1);
                string value;
                try { value = Uri.UnescapeDataString(raw.Replace('+', ' ')); }
                catch (UriFormatException)
                {
                    return AddPrefillRequest.No("it is not valid percent-encoding, so no single name could be read from it");
                }

                // An empty add= carries no name to refuse and no name to accept. Treated as
                // absent, deliberately: there is nothing to disclose about it.
                if (string.IsNullOrWhiteSpace(value)) return AddPrefillRequest.Absent;

                var v = value.Trim();
                if (v.Length > MaxLength)
                    return AddPrefillRequest.No(
                        $"it is {v.Length} characters long and the limit is {MaxLength}; it was refused rather than truncated into a name you did not ask for");
                if (v.IndexOf('\n') >= 0 || v.IndexOf('\r') >= 0)
                    return AddPrefillRequest.No(
                        "it contains a line break. The Server Name(s) field is one server per line, so a name carrying a line break would turn one reviewed add into several");
                foreach (var ch in v)
                    if (char.IsControl(ch))
                        return AddPrefillRequest.No(
                            "it contains a control character, which cannot be part of a server name");

                return AddPrefillRequest.Ok(v);
            }
            return AddPrefillRequest.Absent;
        }

        /// <summary>Returns the single pre-fill name in the URI's <c>add</c> parameter, or null.
        /// Callers that must tell a refusal from an absence use <see cref="Read"/> instead.</summary>
        public static string? Parse(string? uri)
        {
            var r = Read(uri);
            return r.Outcome == AddPrefillOutcome.Accepted ? r.Name : null;
        }

        /// <summary>One name or nothing: anything with a line break, or longer than
        /// <see cref="MaxLength"/>, is refused outright rather than trimmed into something the
        /// operator did not ask for.</summary>
        public static string? Sanitize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var v = value.Trim();
            if (v.Length == 0 || v.Length > MaxLength) return null;
            if (v.IndexOf('\n') >= 0 || v.IndexOf('\r') >= 0) return null;
            foreach (var ch in v)
                if (char.IsControl(ch)) return null;
            return v;
        }
    }
}
