/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data
{
    /// <summary>
    /// Adds shipped CATALOGUE FACTS that an INSTALLED alert-definitions.json never received,
    /// without changing any value already in it.
    ///
    /// <para>WHY THIS EXISTS. An upgraded install keeps its own copy of
    /// Config/alert-definitions.json, by four independent mechanisms: installer/SQLTriage.iss
    /// ships it <c>onlyifdoesntexist</c>; AutoUpdateService lists it in ProtectedConfigFiles and
    /// copies the operator's copy back OVER the package's; tools/SQLTriageUpdater lists it in
    /// ProtectedConfig as never overwritten; and the service deploy script deliberately never
    /// copies the publish config folder. That is correct for the operator's enable/disable and
    /// threshold edits, and wrong for a property the SOFTWARE needs in order to read the alert's
    /// number correctly.</para>
    ///
    /// <para>The property that forced this is <c>valueKind</c>. Six shipped alerts query a
    /// cumulative counter, and <c>valueKind: "cumulative_counter"</c> is the only switch that
    /// makes the evaluator difference two samples instead of comparing a since-startup total to a
    /// per-second threshold. Ship the marker only in the packaged file and the fix reaches fresh
    /// installs alone - while the installs that have been firing permanent false Criticals, which
    /// are the entire reason the fix exists, go on firing them. Proved by execution against a byte
    /// copy of a real install's file on 2026-08-24: all six read back IsCumulativeCounter=false.
    /// Same shape, same remedy as ScriptConfigurationMigrator under ruling 2026-08-23 #4.</para>
    ///
    /// <para>ONLY ABSENT PROPERTIES, AND ONLY FROM AN ALLOW-LIST. See
    /// <see cref="MergeableProperties"/>: a blanket "add anything the installed file lacks" merge
    /// would be wrong, because the writer omits nulls
    /// (<c>JsonIgnoreCondition.WhenWritingNull</c>), so an operator who CLEARED a per-alert
    /// setting leaves that property absent - and re-adding the shipped value would silently undo
    /// the operator's edit. The allow-list holds only properties that describe what the alert's
    /// query MEASURES, which no operator surface can set.</para>
    ///
    /// <para>ALERTS THEMSELVES ARE NEVER ADDED OR REMOVED. An alert present in the shipped file
    /// and absent from the installed one stays absent, and vice versa. A new alert appearing on an
    /// upgrade would start notifying a client about a condition nobody chose to watch; that is a
    /// ruling, not a migration. This closes a property gap and nothing else.</para>
    ///
    /// <para>ONE RULED EXCEPTION TO "never changes a value": A SUPERSEDED DEFINITION IS RE-BASED,
    /// AND ONLY WHEN IT IS BYTE-FOR-BYTE THE ONE THIS PRODUCT SHIPPED. <see cref="TryMerge"/> still
    /// only ADDS absent allow-listed properties. <see cref="TryRepairSupersededDefinitions"/> is the
    /// separate, hash-gated path added under ruling 2026-08-25 06:53 #7, which folded
    /// <c>wait_time_anomaly</c>'s re-base (its measurement changed from a raw wait-millisecond total
    /// to the signal-wait ratio, so its query, unit, thresholds, name and description all changed at
    /// once) into this delivery mechanism, because no allow-list widening can reach a re-base: those
    /// properties are all PRESENT in an installed file, and "present" is what the merge refuses to
    /// touch. The repair replaces an alert's measurement-defining catalogue facts with the shipped
    /// values ONLY when the installed ones hash to a signature this product itself shipped and has
    /// since retired (<see cref="SupersededDefinitionSignatures"/>). An operator who tuned any of
    /// those facts — a threshold, the query — has an alert that matches no known signature, and it is
    /// left exactly as they left it, measurement and all. Operator SETTINGS (enable/disable, cooldown,
    /// channels, escalation, send-email) are never in the replaced set and survive untouched. Same
    /// shape, same discipline as <c>ScriptConfigurationMigrator.TryRepairSupersededOutputQueries</c>.
    /// Adrian confirmed the 30/50 bands and cleared the DRAFT wording on 2026-08-26 (DECISIONS 04:20,
    /// ruling 1); the mechanism carries whatever the shipped catalogue holds and does not pick it.</para>
    ///
    /// <para>THE WHOLE FILE IS RE-SERIALISED, deliberately, and that is a weaker promise than
    /// ScriptConfigurationMigrator's byte splice - because it can afford to be. This file is
    /// already rewritten end to end by the product itself: AlertDefinitionService.Save serialises
    /// the TYPED model on every enable/disable, threshold edit and global-defaults save, which
    /// drops any property the model does not carry. The merge here goes through JsonNode, which
    /// carries every property including ones the model never models, so it preserves strictly MORE
    /// than the app's own writer does. What it does not promise is byte-identical whitespace.</para>
    ///
    /// <para>AN UNREADABLE STORE IS NOT REPLACED WITH DEFAULTS (the config-store write-guard
    /// convention). Unreadable, unparseable, not a JSON object, or no <c>alerts</c> array: it
    /// announces at Warning and returns having written nothing, and never throws - the caller is a
    /// constructor.</para>
    /// </summary>
    internal static class AlertDefinitionMigrator
    {
        /// <summary>The shipped defaults are embedded so this does not depend on the installer
        /// having delivered a second copy. SQLTriage.csproj embeds Config\alert-definitions.json;
        /// the name is resolved by suffix because MSBuild's logical-name mangling is an
        /// implementation detail.</summary>
        private const string ShippedResourceSuffix = "Config.alert-definitions.json";

        /// <summary>
        /// The JSON property names this merge is allowed to add, and the rule for adding to the
        /// list: a property belongs here only if it states what the alert's query MEASURES, and
        /// no operator surface can set or clear it. <c>valueKind</c> qualifies - it says the query
        /// returns a running total rather than the quantity the thresholds describe, it is a fact
        /// about the SQL and not a preference, and Pages/Alerts.razor has no control for it.
        ///
        /// <para>Counter-example, so the boundary is on record: <c>cooldownMinutes</c> must NEVER
        /// go in here. It is nullable, the operator can clear it to inherit the global default,
        /// and a cleared value is ABSENT from the saved file - indistinguishable from never
        /// received. Adding the shipped value back would revert the operator's edit on the next
        /// start. The same argument rules out primaryChannel, escalationChannel, baselineQueryId
        /// and every other nullable setting.</para>
        /// </summary>
        internal static readonly string[] MergeableProperties = { "valueKind" };

        private static readonly JsonNodeOptions NodeOptions = new()
        {
            // The reader that consumes this file is case-insensitive
            // (AlertDefinitionService.JsonOptions), so the key lookup here matches it: a file the
            // app can read must not be a file the merge misreads as missing its properties.
            PropertyNameCaseInsensitive = true
        };

        private static readonly JsonDocumentOptions DocumentOptions = new()
        {
            // The app's own reader allows comments and trailing commas; this refuses them, which
            // is the safe direction. A commented file is announced and left exactly as it is
            // rather than re-serialised with its comments silently deleted.
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false
        };

        private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

        /// <summary>Reads the shipped defaults out of the assembly manifest. Null when the
        /// resource is absent, which is a build problem and not a reason to fail startup.</summary>
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
        /// The pure half. Adds every allow-listed property that the shipped catalogue defines and
        /// the installed one does not carry at all, matched on the alert's <c>id</c>. Reports what
        /// it added as "alertId.property". Returns false, with <paramref name="mergedJson"/> null,
        /// when nothing needs adding OR when either input is not a usable definitions document -
        /// the caller writes only on true.
        /// </summary>
        internal static bool TryMerge(
            string installedJson,
            string shippedJson,
            out string? mergedJson,
            out IReadOnlyList<string> added)
        {
            mergedJson = null;
            added = Array.Empty<string>();

            var installedRoot = ParseObject(installedJson);
            var shippedRoot = ParseObject(shippedJson);
            if (installedRoot is null || shippedRoot is null) return false;

            var installedAlerts = ReadAlerts(installedRoot);
            var shippedAlerts = ReadAlerts(shippedRoot);
            if (installedAlerts is null || shippedAlerts is null) return false;

            // Shipped alerts by id. A duplicate id in the shipped file is an authoring mistake;
            // the first wins, so the merge is deterministic either way.
            var shippedById = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in shippedAlerts)
            {
                if (node is not JsonObject obj) continue;
                var id = ReadString(obj, "id");
                if (id is null || shippedById.ContainsKey(id)) continue;
                shippedById[id] = obj;
            }

            var keys = new List<string>();
            foreach (var node in installedAlerts)
            {
                if (node is not JsonObject installedAlert) continue;
                var id = ReadString(installedAlert, "id");
                if (id is null || !shippedById.TryGetValue(id, out var shippedAlert)) continue;

                foreach (var property in MergeableProperties)
                {
                    // Present, even as an explicit null, means the installed file has an answer
                    // for this property and the merge is not entitled to a second opinion.
                    if (HasProperty(installedAlert, property)) continue;

                    var value = ReadProperty(shippedAlert, property);
                    if (value is null) continue;             // shipped says nothing either

                    installedAlert[property] = value.DeepClone();
                    keys.Add(id + "." + property);
                }
            }

            if (keys.Count == 0) return false;

            var text = installedRoot.ToJsonString(WriteOptions);

            // Match the file's own line endings. A raw newline cannot appear inside a JSON string
            // token in the output (the serialiser escapes it as \n), so this only touches the
            // newlines the serialiser itself wrote.
            if (installedJson.Contains("\r\n", StringComparison.Ordinal))
                text = text.Replace("\n", "\r\n", StringComparison.Ordinal);

            mergedJson = text;
            added = keys;
            return true;
        }

        /// <summary>
        /// The IO half, called from the loader before it reads the file. Announces and returns
        /// rather than throwing or overwriting; writes only when at least one property was added,
        /// so a second run is a no-op.
        /// </summary>
        internal static IReadOnlyList<string> EnsureShippedProperties(string configPath, ILogger logger)
        {
            try
            {
                if (!File.Exists(configPath)) return Array.Empty<string>();

                var shipped = ReadShippedDefaults();
                if (shipped is null)
                {
                    logger.LogWarning(
                        "The shipped alert definitions are not embedded in this build, so catalogue properties this install never received cannot be added to {Path}. The installed file is left exactly as it is, and any alert missing valueKind goes on comparing a running total to a rate threshold",
                        configPath);
                    return Array.Empty<string>();
                }

                string installed;
                Encoding encoding;
                try
                {
                    // A DETECTING reader, so a file the operator saved with a byte-order mark is
                    // written back with one. Same reason as ScriptConfigurationMigrator.
                    using var reader = new StreamReader(
                        configPath, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
                    installed = reader.ReadToEnd();
                    encoding = reader.CurrentEncoding;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Could not read {Path} to check it for catalogue properties this install never received. It has NOT been replaced or rewritten - whatever is in it is still what runs",
                        configPath);
                    return Array.Empty<string>();
                }

                if (ParseObject(installed) is null)
                {
                    logger.LogWarning(
                        "{Path} is not a plain JSON object this migration can read (a comment, a trailing comma or a syntax error will do it), so catalogue properties cannot be merged into it. It has NOT been replaced with the shipped defaults",
                        configPath);
                    return Array.Empty<string>();
                }

                if (!TryMerge(installed, shipped, out var merged, out var added) || merged is null)
                    return Array.Empty<string>();

                File.WriteAllText(configPath, merged, encoding);

                logger.LogInformation(
                    "Added {Count} shipped alert property value(s) to {Path} that this install never received: {Added}. This install was upgraded over a config that predates them; every value already in the file, including every threshold and enable/disable, is unchanged",
                    added.Count, configPath, string.Join(", ", added));

                return added;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Could not check {Path} for catalogue properties this install never received. The installed file is unchanged",
                    configPath);
                return Array.Empty<string>();
            }
        }

        // ── Re-basing a superseded definition (ruling 2026-08-25 06:53 #7) ──────────────────

        /// <summary>
        /// The catalogue facts a re-base replaces on a matched alert, taken verbatim from the shipped
        /// definition of the same id: what it measures (<c>query</c>, <c>unit</c>, <c>operator</c>,
        /// <c>valueKind</c>), the bands it fires on (<c>thresholds</c>), and the words the client sees
        /// (<c>name</c>, <c>description</c>). A property absent from the shipped alert is REMOVED from
        /// the installed one, which is how <c>wait_time_anomaly</c> loses the <c>valueKind</c> it
        /// carried as a raw counter — a ratio of two cumulative columns is scale-free and must not be
        /// differenced. Operator SETTINGS (enabled, cooldownMinutes, channels, escalation, sendEmail,
        /// includeInDailySummary, nextAlertDelayMinutes, baseline*) are deliberately NOT here.
        /// </summary>
        internal static readonly string[] CatalogueFactProperties =
            { "name", "description", "query", "unit", "operator", "thresholds", "valueKind" };

        /// <summary>
        /// Per alert id, the SHA-256 signatures of definitions this product shipped for it and has
        /// since re-based. A signature covers the MEASUREMENT and FIRING facts only — query (line
        /// endings normalised), unit, operator, and the two thresholds — see
        /// <see cref="DefinitionSignature"/>. A match means the operator has touched NONE of those, so
        /// replacing the measurement is honest; a mismatch means an edited (or already-current) alert,
        /// left alone.
        ///
        /// <para>A hash rather than the strings: the point is an EXACT match against something we
        /// shipped, and a multi-line SQL literal in source is one more place a stray character quietly
        /// stops the comparison matching. Same reason ScriptConfigurationMigrator hashes its superseded
        /// output queries. Derived from the alert as it shipped, extracted from git at a4c3a1c (the
        /// fixture Tests/.../prelane-wait-time-anomaly-alert.json) and corroborated against the live
        /// service's own installed copy (2026-08-26), not typed from memory; a test recomputes it from
        /// that fixture so a drift fails loudly.</para>
        /// </summary>
        internal static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> SupersededDefinitionSignatures =
            new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                // wait_time_anomaly as it shipped from a0c9566 (2026-08 v0.80) through a4c3a1c: a raw
                // SUM(wait_time_ms) rate against warning 5000 / critical 15000, unit "milliseconds".
                // It measured 27,167 ms/s on an IDLE instance over a critical of 15,000 - a ceiling
                // that is really "how many cores may be waiting", easier to breach the more CPU a
                // client buys. Ruling D2(b) re-based it on the signal-wait ratio; ruling 06:53 #7
                // folded delivery to existing installs into this migrator. ONE signature covers every
                // shape in the wild: the pre-raw-counter shape (no valueKind) and the raw-counter shape
                // (valueKind added by TryMerge) - valueKind is not hashed - AND the two shipped
                // descriptions this alert has carried, because the description is not hashed either.
                // The live service install (dated 2026-07-31) holds the earlier description over the
                // identical measurement; this signature re-bases it too.
                ["wait_time_anomaly"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "05f81393438efe8859d2961901c75d42acc67f6488287aa9cdf704785d72bb49",
                },
            };

        /// <summary>
        /// The SHA-256 of an alert's MEASUREMENT and FIRING facts, as lowercase hex: query (line
        /// endings normalised to LF, so a config saved with CRLF still matches), unit, operator, and
        /// the two thresholds as their raw JSON tokens. The projection is fixed and labelled so it
        /// cannot collide across fields. Property lookup is case-insensitive, matching the app's own
        /// reader.
        ///
        /// <para>NAME AND DESCRIPTION ARE DELIBERATELY NOT HASHED, though the re-base does replace
        /// them. They are client-facing PROSE that this product rewords across releases, and hashing
        /// them would fragment one broken population into one signature per wording. PROVED by copy-
        /// reading the live service's own installed alert-definitions.json (2026-08-26): its
        /// wait_time_anomaly holds the identical old query/unit/thresholds but an EARLIER description
        /// than the a4c3a1c fixture, so a description-inclusive signature would have re-based the
        /// fixture population and missed the real install. The measurement facts are what prove the
        /// operator has not tuned what the alert measures or when it fires; the prose is corrected
        /// along with the measurement it describes.</para>
        /// </summary>
        internal static string DefinitionSignature(JsonObject alert)
        {
            var sb = new StringBuilder();
            sb.Append("query=").Append(SignatureField(alert, "query").Replace("\r\n", "\n", StringComparison.Ordinal)).Append('\n');
            sb.Append("unit=").Append(SignatureField(alert, "unit")).Append('\n');
            sb.Append("operator=").Append(SignatureField(alert, "operator")).Append('\n');
            sb.Append("warning=").Append(ThresholdToken(alert, "warning")).Append('\n');
            sb.Append("critical=").Append(ThresholdToken(alert, "critical")).Append('\n');

            var bytes = SHA256.HashData(new UTF8Encoding(false).GetBytes(sb.ToString()));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        private static string SignatureField(JsonObject alert, string property)
        {
            if (!alert.TryGetPropertyValue(property, out var value) || value is null) return string.Empty;
            try { return value.GetValue<string>(); }
            catch (Exception) { return string.Empty; }
        }

        private static string ThresholdToken(JsonObject alert, string which)
        {
            if (!alert.TryGetPropertyValue("thresholds", out var t) || t is not JsonObject thresholds) return string.Empty;
            if (!thresholds.TryGetPropertyValue(which, out var value) || value is null) return string.Empty;
            return value.ToJsonString();
        }

        /// <summary>
        /// The pure half of the re-base. For every installed alert whose id has a known superseded
        /// signature AND whose current signature matches it, replaces that alert's
        /// <see cref="CatalogueFactProperties"/> with the shipped definition's values (removing any
        /// the shipped alert does not carry), and leaves every operator setting untouched. Returns
        /// false, with <paramref name="mergedJson"/> null, when nothing matched OR either input is not
        /// a usable definitions document — the caller writes only on true. Idempotent: once repaired,
        /// an alert's signature is the current one, which is not in the superseded set.
        /// </summary>
        internal static bool TryRepairSupersededDefinitions(
            string installedJson,
            string shippedJson,
            out string? mergedJson,
            out IReadOnlyList<string> repaired)
        {
            mergedJson = null;
            repaired = Array.Empty<string>();

            var installedRoot = ParseObject(installedJson);
            var shippedRoot = ParseObject(shippedJson);
            if (installedRoot is null || shippedRoot is null) return false;

            var installedAlerts = ReadAlerts(installedRoot);
            var shippedAlerts = ReadAlerts(shippedRoot);
            if (installedAlerts is null || shippedAlerts is null) return false;

            var shippedById = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in shippedAlerts)
            {
                if (node is not JsonObject obj) continue;
                var id = ReadString(obj, "id");
                if (id is null || shippedById.ContainsKey(id)) continue;
                shippedById[id] = obj;
            }

            var repairedIds = new List<string>();
            foreach (var node in installedAlerts)
            {
                if (node is not JsonObject installedAlert) continue;
                var id = ReadString(installedAlert, "id");
                if (id is null) continue;
                if (!SupersededDefinitionSignatures.TryGetValue(id, out var knownOld)) continue;
                if (!shippedById.TryGetValue(id, out var shippedAlert)) continue;    // shipped no longer defines it

                if (!knownOld.Contains(DefinitionSignature(installedAlert))) continue; // edited, or already current

                foreach (var property in CatalogueFactProperties)
                {
                    if (shippedAlert.TryGetPropertyValue(property, out var shippedValue) && shippedValue is not null)
                        installedAlert[property] = shippedValue.DeepClone();
                    else
                        installedAlert.Remove(property);
                }

                repairedIds.Add(id);
            }

            if (repairedIds.Count == 0) return false;

            var text = installedRoot.ToJsonString(WriteOptions);
            if (installedJson.Contains("\r\n", StringComparison.Ordinal))
                text = text.Replace("\n", "\r\n", StringComparison.Ordinal);

            mergedJson = text;
            repaired = repairedIds;
            return true;
        }

        /// <summary>
        /// The IO half of the re-base, called from the loader immediately after
        /// <see cref="EnsureShippedProperties"/>. Announces and returns rather than throwing or
        /// overwriting; writes only when at least one alert was re-based, so a second run is a no-op.
        /// </summary>
        internal static IReadOnlyList<string> RepairSupersededDefinitions(string configPath, ILogger logger)
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
                        "Could not read {Path} to check it for superseded alert definitions. It has NOT been replaced or rewritten - whatever is in it is still what runs",
                        configPath);
                    return Array.Empty<string>();
                }

                if (ParseObject(installed) is null)
                {
                    logger.LogWarning(
                        "{Path} is not a plain JSON object this migration can read, so a superseded alert definition cannot be re-based in it. It has NOT been replaced with the shipped defaults",
                        configPath);
                    return Array.Empty<string>();
                }

                if (!TryRepairSupersededDefinitions(installed, shipped, out var merged, out var repaired) || merged is null)
                    return Array.Empty<string>();

                File.WriteAllText(configPath, merged, encoding);

                logger.LogInformation(
                    "Re-based {Count} superseded alert definition(s) in {Path} to the shipped catalogue: {Ids}. Each held, byte for byte, a definition this product shipped and has since corrected; an operator-tuned alert matches no shipped signature and was left exactly as it is, and every enable/disable, cooldown and channel setting is unchanged",
                    repaired.Count, configPath, string.Join(", ", repaired));

                return repaired;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Could not check {Path} for superseded alert definitions. The installed file is unchanged",
                    configPath);
                return Array.Empty<string>();
            }
        }

        private static JsonObject? ParseObject(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                return JsonNode.Parse(json, NodeOptions, DocumentOptions) as JsonObject;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static JsonArray? ReadAlerts(JsonObject root)
            => root.TryGetPropertyValue("alerts", out var value) ? value as JsonArray : null;

        private static bool HasProperty(JsonObject obj, string property)
            => obj.TryGetPropertyValue(property, out _);

        private static JsonNode? ReadProperty(JsonObject obj, string property)
            => obj.TryGetPropertyValue(property, out var value) ? value : null;

        private static string? ReadString(JsonObject obj, string property)
        {
            if (!obj.TryGetPropertyValue(property, out var value) || value is null) return null;
            try
            {
                var text = value.GetValue<string>();
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
            catch (Exception)
            {
                return null;                                 // a non-string id is not a key
            }
        }
    }
}
