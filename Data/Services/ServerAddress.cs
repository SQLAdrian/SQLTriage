/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Linq;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// Single source of truth for reading a SQL Server ADDRESS and for splitting a multi-server
    /// LIST into addresses. Promoted 2026-07-21 after the persona board found the same defect at
    /// six call sites, each patched independently.
    ///
    /// The defect: <c>host,port</c> is standard SQL Server addressing syntax (the comma is the
    /// port separator — <c>SQL01\INST,56510</c>), but every site that wanted "the list of servers"
    /// or "the first server" reached for <c>.Split(',')</c>. Two distinct failures followed:
    ///
    ///   • <c>.Split(',')</c> turned ONE server into SEVERAL. A stored connection
    ///     <c>.\OLD2017localhost,56510</c> rendered as a server card <c>.\OLD2017localhost</c> AND
    ///     a server card <c>56510</c> — a PORT presented to the client as a server, scoring
    ///     0/100, and dragging the equal-weight estate mean down with it (the /cio caption read
    ///     "across 4 assessed servers" for an estate with 1 server tested).
    ///
    ///   • <c>.Split(',')[0]</c> / <c>.First()</c> silently DROPPED the port, so a server
    ///     addressed by port was contacted on the default port instead. Wrong server, or no
    ///     connection at all — not cosmetic.
    ///
    /// Both are removed by construction here: an address is never split on its port separator,
    /// and a list separator is never confused for one.
    /// </summary>
    public static class ServerAddress
    {
        /// <summary>
        /// Separator used to join an instance SET into a single opaque key (cache keys, filter
        /// keys). Deliberately NOT a comma: a comma is part of an address. '|' cannot appear in a
        /// validated server name (see ServerConnection.ValidateServerName's whitelist), so a key
        /// built from it is unambiguously splittable back into addresses.
        /// </summary>
        public const char KeySeparator = '|';

        /// <summary>
        /// Splits a multi-server LIST into individual addresses, leaving <c>host\instance,port</c>
        /// intact.
        ///
        /// Newline and semicolon are ALWAYS separators — the server-add UI joins with '\n'
        /// (Pages/Servers.razor), so that is the stored format, and ';' has never been legal
        /// inside a server name.
        ///
        /// A comma is treated as a separator ONLY when the segment following it is not a port.
        /// A port is one-to-five digits, nothing else. This keeps faith with legacy free-text
        /// entries like "SQL01,SQL02" (two servers) while never severing "SQL01,56510" (one
        /// server on a non-default port) — the case that produced the phantom cards.
        ///
        /// Ambiguity note, stated rather than hidden: an address list of the form "SQL01,1433"
        /// is genuinely ambiguous in isolation, and this method resolves it as ONE ported server.
        /// That is the reading the product needs — a bare numeric hostname is not a thing anyone
        /// audits, and the add-path has joined with newlines since it was written.
        /// </summary>
        public static List<string> SplitList(string? raw)
        {
            // The strict reading first. Where it succeeds it produces exactly what the lenient
            // merge below produces — a port re-attaches, anything else starts a new address — so
            // this is not a second splitting rule, it is the same rule with a judgement attached.
            // Where it REFUSES, this method keeps its historical answer: it feeds the desktop's
            // stored free text, which is not a machine-readable argument and must not start
            // throwing at whatever a user typed into the server box years ago.
            // TrySplitListAgreesWithSplitListOnEveryShapeItAccepts pins the first half of that.
            if (TrySplitList(raw, out var judged, out _)) return judged;

            if (string.IsNullOrWhiteSpace(raw)) return new List<string>();

            var addresses = new List<string>();
            foreach (var line in raw.Split(new[] { '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var address in SplitOneLineOnNonPortCommas(line))
                {
                    var trimmed = address.Trim();
                    if (trimmed.Length > 0) addresses.Add(trimmed);
                }
            }
            return addresses;
        }

        /// <summary>
        /// Splits a list AND judges every comma in it, so a mistyped port is a PARSE error instead
        /// of a second server.
        ///
        /// <para><b>Why this exists.</b> <see cref="TryValidate"/> judged only the character set and
        /// the length, and it was called AFTER <see cref="SplitList"/> had already decided what a
        /// comma meant. So <c>SQL01\INST,5651Z</c> arrived as two addresses, both of them
        /// well-formed on their own, and the next thing to count them was the corpus-demo
        /// allocation: a typo in a port came back as a price quote. The comma is where the mistake
        /// is, and by the time a per-address check runs the comma is gone.</para>
        ///
        /// <para><b>The rule, in the order it is applied to a segment that FOLLOWS a comma.</b></para>
        /// <list type="number">
        /// <item>All digits ⇒ it is a port, and it must be a real one (1 to 65535). <c>566510</c> is
        ///   not, and is refused rather than becoming a server called "566510" — the exact shape
        ///   that drew a PORT as a server card on /cio.</item>
        /// <item>Begins with a digit, with no '.', '\' or ':' in it ⇒ it is a port somebody
        ///   mistyped (<c>5651Z</c>, <c>0x1F</c>), not a host. A real second server addressed
        ///   numerically carries dots (<c>10.1.2.4</c>) and is judged by rule 3.</item>
        /// <item>Recognisable as a server address — it has a letter, and it carries '.', '\', ':'
        ///   or '-' or a digit ⇒ a second server, the legacy "SQL01,SQL02" free-text list.</item>
        /// <item>Anything else is AMBIGUOUS and is refused. <c>SQL01\INST,port</c> is the case:
        ///   "port" is a legal hostname label and it is also plainly a mistyped port, and this
        ///   method's whole job is to stop guessing which. The message names both readings and the
        ///   unambiguous way to write a list (a newline or ';', neither of which has ever been
        ///   legal inside a server name). A bare single-label second entry — "SQL01,localhost" —
        ///   is refused by this rule too, and that is the cost of not guessing.</item>
        /// </list>
        ///
        /// <para>Returns true with the addresses, or false with a <paramref name="reason"/> naming
        /// the offending segment. Never a guess at what the caller meant.</para>
        /// </summary>
        public static bool TrySplitList(string? raw, out List<string> addresses, out string? reason)
        {
            addresses = new List<string>();
            reason = null;

            if (string.IsNullOrWhiteSpace(raw)) return true;

            foreach (var line in raw.Split(new[] { '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var segments = line.Split(',');
                for (var i = 0; i < segments.Length; i++)
                {
                    var segment = segments[i].Trim();

                    if (i == 0)
                    {
                        if (segment.Length > 0) { addresses.Add(segment); continue; }
                        if (segments.Length == 1) continue;   // a blank line
                        reason = "an address begins with a comma, so there is nothing for the "
                                 + "comma to belong to";
                        return false;
                    }

                    if (segment.Length == 0)
                    {
                        reason = $"'{line.Trim()}' has a comma with nothing after it";
                        return false;
                    }

                    if (addresses.Count == 0)
                    {
                        reason = $"'{segment}' follows a comma that has no address in front of it";
                        return false;
                    }

                    if (IsAllAsciiDigits(segment))
                    {
                        if (IsPortToken(segment) && int.TryParse(segment, out var port)
                                                 && port >= 1 && port <= 65535)
                        {
                            addresses[^1] = addresses[^1] + "," + segment;
                            continue;
                        }

                        reason = $"'{segment}' follows a comma, so it is read as a TCP port, and it "
                                 + "is not one: a port is 1 to 65535";
                        return false;
                    }

                    if (LooksLikeAMistypedPort(segment))
                    {
                        reason = $"'{segment}' follows a comma and begins with a digit, so it is "
                                 + "read as a TCP port, and a port is digits only, 1 to 65535";
                        return false;
                    }

                    if (LooksLikeAServerAddress(segment))
                    {
                        addresses.Add(segment);
                        continue;
                    }

                    reason = $"'{segment}' follows a comma and is neither a TCP port nor a "
                             + "recognisable server address, so it cannot be read as either the "
                             + "port of the address before it or a second server. Separate servers "
                             + "with a newline or ';'";
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// The first address in a multi-server list, port and instance intact, or null when the
        /// list is empty. This is the replacement for every <c>.Split(',')[0]</c> /
        /// <c>.Split(',').First()</c> that was silently discarding the port.
        /// </summary>
        public static string? First(string? raw) => SplitList(raw).FirstOrDefault();

        /// <summary>Longest address this product accepts, mirroring ServerConnection's own cap.</summary>
        private const int MaxAddressLength = 100;

        /// <summary>
        /// Characters that legitimately appear in a SQL Server host or named-instance specifier.
        /// The same set ServerConnection.ValidateServerName whitelists — the two are stated to
        /// match here rather than shared, because they answer different questions and must keep
        /// answering them differently: that method SANITISES (drops what it does not recognise and
        /// hands back a shorter name), this one JUDGES (says the address is malformed and names
        /// what it found). A UI paste wants the first; a machine-readable argument wants the
        /// second, because a silently-shortened name is a different server.
        /// </summary>
        private static bool IsAddressChar(char c) =>
            char.IsLetterOrDigit(c) || c == '.' || c == '\\' || c == '-' || c == '_' || c == ':' || c == ',';

        /// <summary>
        /// Judges ONE address's syntax. Returns false with a <paramref name="reason"/> naming
        /// exactly what was found — never a guess at what the caller meant.
        ///
        /// This exists so a parse failure can be reported AS a parse failure. Without it, a
        /// malformed argument survives parsing as an extra list entry and is refused further down
        /// by whatever counts entries; when the thing counting entries is a licence allocation,
        /// the user is told the price of their plan in answer to a typo.
        /// </summary>
        public static bool TryValidate(string? address, out string? reason)
        {
            reason = null;

            if (string.IsNullOrWhiteSpace(address))
            {
                reason = "the address is empty";
                return false;
            }

            var trimmed = address.Trim();

            if (trimmed.Length > MaxAddressLength)
            {
                reason = $"the address is {trimmed.Length} characters; the limit is {MaxAddressLength}";
                return false;
            }

            var bad = trimmed.Where(c => !IsAddressChar(c)).Distinct().ToList();
            if (bad.Count > 0)
            {
                var rendered = string.Join(" ", bad.Select(Describe));
                reason = bad.Count == 1
                    ? $"the character {rendered} cannot appear in a server address"
                    : $"these characters cannot appear in a server address: {rendered}";
                return false;
            }

            // SHAPE, added 2026-08-06. Character set and length said nothing about what the COMMAS
            // in an address mean, and the comma is where the mistake was: SQL01\INST,5651Z passed
            // this method as two well-formed addresses and was priced as two servers.
            //
            // Judged as ONE address here, which is stricter than TrySplitList's list reading on
            // purpose — a caller that hands this method a whole list has already skipped the
            // splitter. So: at most one comma, and what follows it is a port.
            var comma = trimmed.IndexOf(',');
            if (comma >= 0)
            {
                var tail = trimmed[(comma + 1)..];
                if (tail.IndexOf(',') >= 0)
                {
                    reason = "one address has more than one comma; a comma is the port separator, "
                             + "so there can be only one";
                    return false;
                }

                if (comma == 0)
                {
                    reason = "the address begins with a comma, so there is nothing for the comma "
                             + "to belong to";
                    return false;
                }

                if (!IsPortToken(tail) || !int.TryParse(tail, out var port) || port < 1 || port > 65535)
                {
                    reason = $"'{tail}' follows the comma, so it is read as a TCP port, and it is "
                             + "not one: a port is 1 to 65535";
                    return false;
                }
            }

            return true;
        }

        /// <summary>Renders one rejected character so a control character is visible in the message.</summary>
        private static string Describe(char c) =>
            char.IsControl(c) || char.IsWhiteSpace(c)
                ? $"U+{(int)c:X4}"
                : $"'{c}'";

        /// <summary>
        /// The Windows HOST an address resolves to — instance and port removed. This is a
        /// legitimately different question from <see cref="First"/>: an OS-level API
        /// (dbatools <c>-ComputerName</c>, a WMI/perf counter probe) wants the machine, and a
        /// machine has no instance and no port. Callers that want to CONNECT must never use this.
        /// </summary>
        public static string HostOnly(string? address)
        {
            if (string.IsNullOrWhiteSpace(address)) return "";
            var s = address.Trim();
            var slash = s.IndexOf('\\');
            if (slash >= 0) s = s.Substring(0, slash);
            var comma = s.IndexOf(',');
            if (comma >= 0) s = s.Substring(0, comma);
            var colon = s.IndexOf(':');
            if (colon >= 0) s = s.Substring(0, colon);
            return s.Trim();
        }

        /// <summary>
        /// Joins an instance SET into one opaque key using <see cref="KeySeparator"/>, so a ported
        /// address inside the set can never be re-read as two instances.
        /// </summary>
        public static string JoinKey(IEnumerable<string> instances) =>
            string.Join(KeySeparator, instances);

        /// <summary>Splits a key produced by <see cref="JoinKey"/> back into addresses.</summary>
        public static string[] SplitKey(string? key) =>
            string.IsNullOrEmpty(key)
                ? Array.Empty<string>()
                : key.Split(KeySeparator, StringSplitOptions.RemoveEmptyEntries);

        /// <summary>True when <paramref name="token"/> is a TCP port, i.e. the tail of "host,port".</summary>
        private static bool IsPortToken(string token)
        {
            var t = token.Trim();
            if (t.Length == 0 || t.Length > 5) return false;
            foreach (var c in t) if (!char.IsAsciiDigit(c)) return false;
            return true;
        }

        private static bool IsAllAsciiDigits(string s)
        {
            if (s.Length == 0) return false;
            foreach (var c in s) if (!char.IsAsciiDigit(c)) return false;
            return true;
        }

        /// <summary>
        /// A segment that reads as a port somebody got wrong: it starts with a digit and carries
        /// none of the punctuation a numerically-addressed host would ("10.1.2.4" has dots,
        /// "10.1.2.4\INST" a backslash). <c>5651Z</c> and <c>0x1F</c> are the shapes this catches.
        /// </summary>
        private static bool LooksLikeAMistypedPort(string s) =>
            s.Length > 0 && char.IsAsciiDigit(s[0])
            && s.IndexOf('.') < 0 && s.IndexOf('\\') < 0 && s.IndexOf(':') < 0;

        /// <summary>
        /// Recognisable as a server address rather than a bare word: it carries one of the
        /// characters that make an address an address — '.' (a domain, an IP literal, or
        /// ".\instance"), '\' (instance), ':' ("tcp:") or '-' (a hyphenated host) — or it mixes
        /// letters and digits the way a machine name does (SQL02).
        ///
        /// <para>A letter is NOT required: <c>10.1.2.4</c> is a second server and has none. An
        /// all-digit segment never reaches here — it was judged as a port two rules earlier.</para>
        ///
        /// <para>Deliberately NOT "is this a legal hostname label": "port" is a legal label, and
        /// after a comma it is far likelier to be a mistyped port than a machine. This method says
        /// what it can defend, and <see cref="TrySplitList"/> refuses everything it cannot.</para>
        /// </summary>
        private static bool LooksLikeAServerAddress(string s)
        {
            if (s.IndexOfAny(new[] { '.', '\\', ':', '-' }) >= 0) return true;

            var hasLetter = false;
            var hasDigit = false;
            foreach (var c in s)
            {
                if (char.IsAsciiLetter(c)) hasLetter = true;
                else if (char.IsAsciiDigit(c)) hasDigit = true;
            }
            return hasLetter && hasDigit;
        }

        /// <summary>
        /// Splits one line on commas, then re-attaches any segment that is a bare port to the
        /// segment before it — the operation that keeps "SQL01\INST,56510" whole.
        /// </summary>
        private static IEnumerable<string> SplitOneLineOnNonPortCommas(string line)
        {
            var parts = line.Split(',');
            if (parts.Length == 1) return parts;

            var merged = new List<string>();
            foreach (var part in parts)
            {
                if (merged.Count > 0 && IsPortToken(part))
                    merged[merged.Count - 1] = merged[merged.Count - 1].Trim() + "," + part.Trim();
                else
                    merged.Add(part);
            }
            return merged;
        }
    }
}
