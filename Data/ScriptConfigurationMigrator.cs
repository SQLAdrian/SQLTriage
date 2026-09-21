/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data
{
    /// <summary>
    /// Adds shipped script-configuration entries that an INSTALLED config does not have, without
    /// touching anything already in it.
    ///
    /// <para>WHY THIS EXISTS. installer/SQLTriage.iss ships Config/script-configurations.json with
    /// <c>Flags: onlyifdoesntexist</c>, so an Inno UPGRADE keeps the operator's file and the new
    /// shipped entries never arrive. That is correct for the operator's edits and wrong for new
    /// scripts: the sp_Blitz 8.34 refresh added an install-only sp_ineachdb entry that 8.34 hard
    /// depends on, and an upgraded install would have run sp_Blitz 8.34 without it. Adrian's
    /// ruling, DECISIONS 2026-08-23 05:00 #4: at startup, add any shipped script entries missing
    /// from the installed config; never overwrite operator edits.</para>
    ///
    /// <para>THE KEY IS <c>ScriptPath</c>, the script's file name, matched case-insensitively,
    /// with <c>Id</c> as a second guard so a re-added entry can never collide on either identity.
    /// Both sets grow as entries are spliced, so the guard covers the shipped file colliding with
    /// ITSELF as well as with the installed one.
    /// ScriptPath is the natural key: it is what actually names the script on disk, and two
    /// entries pointing at one file would install and run it twice. Ids are stable across releases
    /// (verified against the 8ff4cac config: all four surviving entries kept their GUIDs), so the
    /// Id guard costs nothing and catches a hand-edited rename.</para>
    ///
    /// <para>NOTHING EXISTING IS EVER REWRITTEN, at the byte level. The merge is a TEXT splice: the
    /// new entries are inserted immediately before the array's closing bracket, so every byte up to
    /// and including the last pre-existing entry survives exactly as the operator left it - its
    /// property names, its property order, its formatting, its byte-order mark if it had one, and
    /// the file's own line endings. Only the whitespace between that entry and the closing bracket
    /// changes, because a comma has to go there. Serialising the merged LIST back would have rewritten every byte of the file instead,
    /// and would have silently dropped any property ScriptConfiguration does not model.</para>
    ///
    /// <para>AN UNREADABLE STORE IS NOT REPLACED WITH DEFAULTS (the config-store write-guard
    /// convention). If the file cannot be read, cannot be parsed, or does not hold a JSON array,
    /// this announces the problem at Warning and returns having written nothing. It never throws:
    /// the caller is a startup path.</para>
    ///
    /// <para>AN OPERATOR CANNOT DELETE AN ENTRY THROUGH THE PRODUCT. Pages/EditAuditScripts.razor
    /// edits and enables/disables entries and has no add or remove control, so a shipped entry
    /// that is absent came from an older shipped file or a hand edit, never from a decision the
    /// operator made in the app. The operator's real lever for "do not run this" is
    /// <c>Enabled = false</c>, and that survives untouched because existing entries are never
    /// read back or rewritten. That is also why an EMPTY array is migrated rather than left alone:
    /// it cannot be a curated choice.</para>
    /// </summary>
    internal static class ScriptConfigurationMigrator
    {
        /// <summary>The shipped defaults are embedded so this does not depend on the installer
        /// having delivered a second copy of the file. SQLTriage.csproj embeds
        /// Config\script-configurations.json; the name is resolved by suffix because MSBuild's
        /// logical-name mangling is an implementation detail.</summary>
        private const string ShippedResourceSuffix = "Config.script-configurations.json";

        private static readonly JsonNodeOptions NodeOptions = new()
        {
            // The shipped file is PascalCase and so is anything the script editor writes, because
            // ScriptConfiguration carries explicit [JsonPropertyName] attributes that override
            // SaveScriptConfigurations' CamelCase policy. A hand-edited file need not be, and the
            // reader that consumes this file is case-insensitive too, so the key lookup matches it:
            // a config the app can read must not be a config the merge fails to recognise, or the
            // merge would add a duplicate of every entry.
            PropertyNameCaseInsensitive = true
        };

        private static readonly JsonDocumentOptions DocumentOptions = new()
        {
            CommentHandling = JsonCommentHandling.Disallow
        };

        /// <summary>
        /// Reads the shipped defaults out of the assembly manifest. Returns null when the resource
        /// is absent, which is a build problem and not a reason to fail startup.
        /// </summary>
        internal static byte[]? ReadShippedDefaultBytes()
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(ShippedResourceSuffix, StringComparison.OrdinalIgnoreCase));
            if (name is null) return null;

            using var stream = asm.GetManifestResourceStream(name);
            if (stream is null) return null;

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }

        /// <summary>The embedded defaults decoded as text, BOM stripped if one is ever added.</summary>
        internal static string? ReadShippedDefaults()
        {
            var bytes = ReadShippedDefaultBytes();
            if (bytes is null) return null;

            var text = new UTF8Encoding(false).GetString(bytes);
            return text.Length > 0 && text[0] == '\uFEFF' ? text.Substring(1) : text;
        }

        /// <summary>
        /// The pure half: splices the shipped entries that <paramref name="installedJson"/> does
        /// not have into it, and reports which keys were added. Returns false, with
        /// <paramref name="mergedJson"/> null, when nothing needs adding OR when either input is
        /// not a JSON array - the caller writes only on true.
        /// </summary>
        internal static bool TryMerge(
            string installedJson,
            string shippedJson,
            out string? mergedJson,
            out IReadOnlyList<string> added)
        {
            mergedJson = null;
            added = Array.Empty<string>();

            var installed = ParseArray(installedJson);
            var shipped = ParseArray(shippedJson);
            if (installed is null || shipped is null) return false;

            var haveScriptPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var haveIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in installed)
            {
                var path = ReadString(entry, "ScriptPath");
                if (path is not null) haveScriptPaths.Add(path);
                var id = ReadString(entry, "Id");
                if (id is not null) haveIds.Add(id);
            }

            var missing = new List<JsonNode>();
            var keys = new List<string>();
            foreach (var entry in shipped)
            {
                var path = ReadString(entry, "ScriptPath");
                if (path is null) continue;                 // a shipped entry with no script is not addable
                if (haveScriptPaths.Contains(path)) continue;

                var id = ReadString(entry, "Id");
                if (id is not null && haveIds.Contains(id)) continue;

                if (entry is null) continue;
                missing.Add(entry);
                keys.Add(path);

                // The have-sets grow as entries are spliced, so two shipped entries naming one
                // script (a repo-authoring mistake) add the script ONCE instead of twice. Before
                // this the sets were built from the installed file alone, and the doc comment's
                // "can never collide on either identity" was true of the installed file only.
                haveScriptPaths.Add(path);
                if (id is not null) haveIds.Add(id);
            }

            if (missing.Count == 0) return false;

            var newline = installedJson.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var close = installedJson.LastIndexOf(']');
            if (close < 0) return false;                    // parsed as an array, so this cannot happen

            var builder = new StringBuilder();
            builder.Append(installedJson, 0, close);

            // Trim back to just after the last element so the splice does not inherit the closing
            // bracket's own indentation as a blank line.
            while (builder.Length > 0 && char.IsWhiteSpace(builder[builder.Length - 1]))
                builder.Length--;

            // A comma is needed only when something already precedes the splice point: a
            // pre-existing entry, or one this loop has already spliced. The first draft asked
            // whether the buffer held more than one character, which is the same question ONLY for
            // a file whose '[' is byte 0. An empty array behind any leading whitespace - " []", or
            // a leading blank line - made it write "[," instead: invalid JSON, which the loader
            // cannot parse, which leaves the install running ZERO diagnostic scripts, and which
            // this migrator then correctly refuses to touch again because an unparseable store is
            // never rewritten. So the file could not self-heal. Found by the verifier, 2026-08-23.
            var somethingPrecedes = installed.Count > 0;

            foreach (var entry in missing)
            {
                if (somethingPrecedes) builder.Append(',');
                builder.Append(newline);
                builder.Append(Indent(entry.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), newline));
                somethingPrecedes = true;
            }

            builder.Append(newline);
            builder.Append(installedJson, close, installedJson.Length - close);

            mergedJson = builder.ToString();
            added = keys;
            return true;
        }

        // ── Repairing a shipped value we now know was wrong ─────────────────────────────────

        /// <summary>
        /// The sp_Blitz output queries THIS PRODUCT has shipped and has since replaced, by SHA-256 of
        /// the value with its line endings normalised to LF. Derived from this file's own git history,
        /// not typed from memory.
        ///
        /// <para>A hash rather than the string: the point is an EXACT match against something we
        /// shipped, and two long SQL literals in a source file are two more places for a stray
        /// character to make the comparison quietly stop matching.</para>
        /// </summary>
        private static readonly Dictionary<string, string> SupersededOutputQueries =
            new(StringComparer.OrdinalIgnoreCase)
            {
                // Shipped c3e91cd (2026-03-12, the initial commit) through a0c9566 (2026-03-26).
                // xp_regread domain, unaliased CONVERT; the output table was ALREADY master.dbo-
                // qualified at c3e91cd, in both the FROM and the WHERE subquery.
                ["8bfd4277a5798f0386875d906d66af0fd2a1236674ce07123c98174dd224b444"] = "sp_Blitz.sql",
                // Shipped 6f15efe (2026-03-27) through a4c3a1c, so this is the value on every install
                // upgraded from any release before 2026-08-24. Identical to the entry above except one
                // added statement, "SET @ThisDomain = ISNULL(@ThisDomain, DEFAULT_DOMAIN());" (a
                // workgroup fallback for the xp_regread domain lookup) - not a table-qualification
                // change; bebc91d (2026-07-08), which DID add master.dbo qualification to two OTHER
                // scripts' output queries, never touched sp_Blitz's.
                ["16a1a5a50221ddeee871b3a186fd8abd091184a11a4ea31b3427f41eb2d7c5bf"] = "sp_Blitz.sql",
            };

        private static readonly Regex OutputQueryProperty = new(
            "(\"SqlQueryForOutput\"\\s*:\\s*)\"((?:\\\\.|[^\"\\\\])*)\"",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Replaces an output query an installed config still holds VERBATIM AS THIS PRODUCT SHIPPED
        /// IT with the value shipped now. Returns false, with <paramref name="repairedJson"/> null,
        /// when there is nothing to repair.
        ///
        /// <para>WHY THIS EXISTS AND WHY IT DOES NOT CONTRADICT "nothing existing is ever rewritten".
        /// installer/SQLTriage.iss ships Config/script-configurations.json <c>onlyifdoesntexist</c>,
        /// so an upgraded install keeps the file it has. For a NEW entry that is handled by
        /// <see cref="TryMerge"/>. For a CHANGED value there was nothing: the sp_Blitz output query
        /// shipped from the repo's first commit produced a CSV the Export Pack could not read, which
        /// is why every pack built between 2026-07-23 and 2026-08-24 shipped without sp_Blitz, and
        /// fixing the shipped file alone would have fixed it for new installs only - the population
        /// that did not have the problem. The invariant that matters is "an operator's edit is never
        /// overwritten", and that is what is enforced here: the value is replaced ONLY when it is
        /// byte-identical to a value in <see cref="SupersededOutputQueries"/>, which holds only
        /// strings this product itself wrote. An edited query does not match any of them and is left
        /// exactly as the operator left it - and its CSV keeps the shape their edit produces.</para>
        ///
        /// <para>SCOPED TO THE ENTRY THE HASH NAMES, NOT TO ANY TEXT THAT MATCHES. A hash proves the
        /// VALUE is one this product shipped for a particular ScriptPath (<c>SupersededOutputQueries</c>
        /// maps hash to that ScriptPath); it says nothing about which JSON entry a given occurrence of
        /// that text sits inside. An operator who cloned sp_Blitz's entry to author their own script -
        /// same query, a different <c>ScriptPath</c> like <c>my_custom_blitz.sql</c> - has that value
        /// byte-identical to a superseded one too, and a match keyed on the STRING alone would rewrite
        /// the clone's query as readily as sp_Blitz's own, silently changing what the operator's script
        /// runs. So the replacement additionally requires the ENCLOSING entry's own <c>ScriptPath</c>
        /// to equal the ScriptPath <see cref="SupersededOutputQueries"/> names for that hash - found via
        /// <see cref="TopLevelObjectSpans"/>, which locates each array element's own text span, honouring
        /// quoted strings and escapes so a brace inside a SQL literal cannot miscount. A clone entry's
        /// ScriptPath never matches, so its query is left exactly as the operator wrote it.</para>
        ///
        /// <para>The rest of the file is untouched at the BYTE level, for the same reason
        /// <see cref="TryMerge"/> splices text rather than re-serialising: only the matched string
        /// literal, inside the one authorised entry's own span, is rewritten in place, and every other
        /// byte - property order, formatting, byte order mark, line endings, and every OTHER entry's
        /// <c>SqlQueryForOutput</c> even when it holds the identical superseded text under a different
        /// ScriptPath - survives. The replacement value is read out of the EMBEDDED shipped defaults,
        /// so there is no second copy of the query in this file to drift.</para>
        ///
        /// <para>Line endings are normalised to LF before hashing, so a config saved with CRLF still
        /// matches the value it holds. Nothing is written when the file does not parse as a JSON
        /// array (the config-store write-guard convention), and nothing is written when the array's
        /// element count disagrees with the span count <see cref="TopLevelObjectSpans"/> found - a
        /// shape this scanner does not recognise is refused rather than guessed at.</para>
        /// </summary>
        internal static bool TryRepairSupersededOutputQueries(
            string installedJson,
            string shippedJson,
            out string? repairedJson,
            out IReadOnlyList<string> repaired)
        {
            repairedJson = null;
            repaired = Array.Empty<string>();

            var installed = ParseArray(installedJson);
            var shipped = ParseArray(shippedJson);
            if (installed is null || shipped is null) return false;

            var shippedQueries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in shipped)
            {
                var path = ReadString(entry, "ScriptPath");
                if (path is null) continue;
                var query = ReadString(entry, "SqlQueryForOutput");
                if (query is not null) shippedQueries[path] = query;
            }

            var spans = TopLevelObjectSpans(installedJson);
            if (spans.Count != installed.Count) return false;   // a shape this scanner does not recognise

            var keys = new List<string>();
            var edits = new List<(int Start, int End, string Replacement)>();

            for (var i = 0; i < installed.Count; i++)
            {
                var entry = installed[i];
                var ownScriptPath = ReadString(entry, "ScriptPath");
                if (ownScriptPath is null) continue;
                if (!shippedQueries.ContainsKey(ownScriptPath)) continue;   // no shipped value for THIS path

                var (start, end) = spans[i];
                var entryText = installedJson.Substring(start, end - start);
                var changedHere = false;

                var newEntryText = OutputQueryProperty.Replace(entryText, match =>
                {
                    var current = Unescape(match.Groups[2].Value);
                    if (current is null) return match.Value;

                    var hash = Sha256OfNormalised(current);
                    if (!SupersededOutputQueries.TryGetValue(hash, out var expectedScriptPath)) return match.Value;

                    // The hash says this value is a superseded one FOR expectedScriptPath. Only replace
                    // it here if this entry IS that ScriptPath - never on a clone that merely copied
                    // the same text under a different name.
                    if (!string.Equals(expectedScriptPath, ownScriptPath, StringComparison.OrdinalIgnoreCase))
                        return match.Value;

                    var replacement = shippedQueries[ownScriptPath];
                    if (string.Equals(replacement, current, StringComparison.Ordinal)) return match.Value;

                    changedHere = true;
                    return match.Groups[1].Value + JsonSerializer.Serialize(replacement);
                });

                if (!changedHere) continue;

                edits.Add((start, end, newEntryText));
                keys.Add(ownScriptPath);
            }

            if (edits.Count == 0) return false;

            // Apply back-to-front so an earlier edit's length change never shifts a later span's
            // offsets - spans were collected in ascending document order, so reversing here is enough.
            var builder = new StringBuilder(installedJson);
            for (var e = edits.Count - 1; e >= 0; e--)
            {
                var (start, end, replacement) = edits[e];
                builder.Remove(start, end - start);
                builder.Insert(start, replacement);
            }

            repairedJson = builder.ToString();
            repaired = keys;
            return true;
        }

        /// <summary>
        /// The character span (start index, exclusive end index) of each top-level object directly
        /// inside a JSON array, in document order - used to scope a text-level edit to the ONE array
        /// element it belongs to instead of to any text in the file that happens to match. String
        /// literals and backslash escapes are honoured, so a brace or bracket inside a quoted SQL
        /// literal (or any other property value) never miscounts depth. Not a general JSON tokeniser:
        /// it assumes every element of the top-level array is an object, which is what this file's
        /// entries always are - <see cref="TryRepairSupersededOutputQueries"/> refuses to act rather
        /// than guess when the count this returns disagrees with the parsed array's own element count.
        /// </summary>
        internal static List<(int Start, int End)> TopLevelObjectSpans(string json)
        {
            var spans = new List<(int, int)>();
            var depth = 0;
            var inString = false;
            var escaped = false;
            var objectStart = -1;

            for (var i = 0; i < json.Length; i++)
            {
                var c = json[i];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                    continue;
                }

                switch (c)
                {
                    case '"':
                        inString = true;
                        break;
                    case '{':
                        if (depth == 1 && objectStart < 0) objectStart = i;
                        depth++;
                        break;
                    case '[':
                        depth++;
                        break;
                    case '}':
                        depth--;
                        if (depth == 1 && objectStart >= 0)
                        {
                            spans.Add((objectStart, i + 1));
                            objectStart = -1;
                        }
                        break;
                    case ']':
                        depth--;
                        break;
                }
            }

            return spans;
        }

        internal static string Sha256OfNormalised(string value)
        {
            var normalised = value.Replace("\r\n", "\n", StringComparison.Ordinal);
            var bytes = SHA256.HashData(new UTF8Encoding(false).GetBytes(normalised));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        private static string? Unescape(string jsonStringBody)
        {
            try
            {
                return JsonSerializer.Deserialize<string>("\"" + jsonStringBody + "\"");
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// The IO half of the repair, called from the loader immediately after
        /// <see cref="EnsureShippedEntries"/>. Announces and returns rather than throwing; writes
        /// only when something was actually replaced, so a second run is a no-op.
        /// </summary>
        internal static IReadOnlyList<string> RepairSupersededOutputQueries(string configPath, ILogger logger)
        {
            try
            {
                if (!File.Exists(configPath)) return Array.Empty<string>();

                var shipped = ReadShippedDefaults();
                if (shipped is null) return Array.Empty<string>();

                string installed;
                Encoding encoding;
                try
                {
                    using var reader = new StreamReader(
                        configPath, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
                    installed = reader.ReadToEnd();
                    encoding = reader.CurrentEncoding;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Could not read {Path} to check its diagnostic output queries. It has NOT been replaced or rewritten",
                        configPath);
                    return Array.Empty<string>();
                }

                if (!TryRepairSupersededOutputQueries(installed, shipped, out var merged, out var keys)
                    || merged is null)
                    return Array.Empty<string>();

                File.WriteAllText(configPath, merged, encoding);

                foreach (var key in keys)
                {
                    logger.LogInformation(
                        "Replaced the output query for {ScriptPath} in {Path}. The value there was the one an earlier release of this product shipped, and it produced a CSV the Export Pack could not read; an edited query would have been left alone",
                        key, configPath);
                }

                return keys;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Could not check {Path} for superseded diagnostic output queries. The installed file is unchanged",
                    configPath);
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// The IO half, called from the loader. Announces and returns rather than throwing or
        /// overwriting; writes only when at least one entry was added, so a second run is a no-op.
        /// </summary>
        internal static IReadOnlyList<string> EnsureShippedEntries(string configPath, ILogger logger)
        {
            try
            {
                if (!File.Exists(configPath)) return Array.Empty<string>();

                var shipped = ReadShippedDefaults();
                if (shipped is null)
                {
                    logger.LogWarning(
                        "Shipped script configurations are not embedded in this build, so new shipped entries cannot be added to {Path}. The installed file is left exactly as it is",
                        configPath);
                    return Array.Empty<string>();
                }

                string installed;
                Encoding encoding;
                try
                {
                    // Read through a DETECTING reader rather than File.ReadAllText so the file's
                    // own byte-order mark survives the write. ReadAllText strips a BOM and
                    // UTF8Encoding(false) does not put one back, so a config the operator had
                    // saved with a BOM quietly lost it - harmless to every reader, but this
                    // promises that nothing already in the file changes, and that promise has to
                    // be true at the byte level. CurrentEncoding after the read is the DETECTED
                    // encoding: a UTF-8 BOM comes back as a UTF-8 BOM, a UTF-16 file is written
                    // back as UTF-16, and a file with no mark keeps the BOM-less UTF-8 it was
                    // opened with. Found by the verifier, 2026-08-23.
                    using var reader = new StreamReader(
                        configPath, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
                    installed = reader.ReadToEnd();
                    encoding = reader.CurrentEncoding;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Could not read {Path} to check it for new shipped script entries. It has NOT been replaced or rewritten - whatever is in it is still what runs",
                        configPath);
                    return Array.Empty<string>();
                }

                if (ParseArray(installed) is null)
                {
                    logger.LogWarning(
                        "{Path} is not a readable JSON array, so new shipped script entries cannot be merged into it. It has NOT been replaced with the shipped defaults - fix or remove the file and the install will use the shipped copy",
                        configPath);
                    return Array.Empty<string>();
                }

                if (!TryMerge(installed, shipped, out var merged, out var added) || merged is null)
                    return Array.Empty<string>();

                File.WriteAllText(configPath, merged, encoding);

                foreach (var key in added)
                {
                    logger.LogInformation(
                        "Added shipped script configuration {ScriptPath} to {Path}. This install was upgraded over a config that predates it; existing entries were left untouched",
                        key, configPath);
                }

                return added;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Could not check {Path} for new shipped script entries. The installed file is unchanged",
                    configPath);
                return Array.Empty<string>();
            }
        }

        private static JsonArray? ParseArray(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                return JsonNode.Parse(json, NodeOptions, DocumentOptions) as JsonArray;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string? ReadString(JsonNode? entry, string property)
        {
            if (entry is not JsonObject obj) return null;
            if (!obj.TryGetPropertyValue(property, out var value) || value is null) return null;
            try
            {
                var text = value.GetValue<string>();
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
            catch (Exception)
            {
                return null;                                 // a non-string ScriptPath is not a key
            }
        }

        /// <summary>Pushes a serialised entry in by one array level so it lines up with its
        /// siblings, and converts the serialiser's LF to the file's own newline.</summary>
        private static string Indent(string entryJson, string newline)
        {
            var lines = entryJson.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            return string.Join(newline, lines.Select(l => "  " + l));
        }
    }
}
