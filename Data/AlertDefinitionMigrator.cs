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
    /// channels, escalation, send-email) are never in the replaced set and survive untouched, with one
    /// declared exception: <see cref="SupersededDefinitionCarries"/> names, per alert, an operator-owned
    /// field a re-base also moves, and only where the installed field still holds the value this
    /// product shipped (canBaseline and holdSeconds, lane Q14 fix round 2, 2026-09-18). Same
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
        /// <para><c>queryMode</c> joined the list on 2026-08-28 (strings-r2-04b) and it is the same
        /// kind of fact as the rest: it decides WHICH SQL runs at all, routing the evaluator to a
        /// built-in handler and past the definition's own <c>query</c> field entirely. No operator
        /// surface sets it - Pages/Alerts.razor has no control for it and CheckValidator only displays
        /// it - so it cannot carry an operator's edit. It has to be here for a re-base to land:
        /// <c>io_error</c>'s repair moves its measurement OUT of a hardcoded handler and INTO the
        /// config query, which only takes effect once the installed alert stops carrying
        /// <c>queryMode: "io_error_check"</c>. Without this entry the re-base would install honest SQL
        /// into a field that the stale routing still ignores.</para>
        internal static readonly string[] CatalogueFactProperties =
            { "name", "description", "query", "unit", "operator", "thresholds", "valueKind", "queryMode" };

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

                // ── Strings lane cluster 2, ledger lane 8 (2026-08-28) ───────────────────────────
                // Twelve alerts whose measurement did not match the words they shipped with. Every
                // signature below is the body as it shipped at 84beb1c, captured before the fix into
                // Tests/.../Fixtures/prelane-cluster2-alerts-84beb1c.json, and every one is proved by
                // AlertDefinitionMigratorCluster2Tests to re-base against the current catalogue.
                //
                // io_error (strings-r2-04b): counted files whose LIFETIME cumulative I/O stall
                // exceeded 5 s, under a Critical named "SQL Server has encountered an I/O error on a
                // database file" with warning 1 - so it fired continuously on healthy servers (PROVED
                // .\new2022: 14 of 33 files after 96 minutes of uptime, suspect_pages empty). Now
                // counts unrepaired suspect pages, and drops queryMode so the config query is what
                // actually runs.
                ["io_error"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "c2bb446df899fe1d5562f36be2c7c0620f5d630648527cd55507a45f2278d998",
                },

                // strings-r1-06, the master-scoping class. All five ran against the hardcoded master
                // connection in ExecuteAlertQueryAsync and so measured master and no user database
                // (PROVED .\new2022: the shipped queries saw 1 file where the instance has 29 across
                // 11 databases). Two of them are Critical and were therefore structurally incapable of
                // ever seeing a user database fill up: log_space_full read master's log at 5.2 percent
                // while a real file on the instance stood at 96.6 percent of its current size, and
                // database_space_full read 19.3 percent while the true worst was 92.5 percent.
                ["disk_space_low"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "ba524514b53dbcd3b8e8727f3eda1fb17e8fab429076b662691e5089797b7209",
                },
                ["database_space_full"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "2f57b8cddf4a0fba288f43a90cde3ee0756d2e79e0e8d0f27de514dbb29b2985",
                },
                ["log_space_full"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "b95b7eb2b3451f599e278e6da158d317736379465d6a2d3300c2f35cda3323dc",
                },
                ["filegroup_space"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "f502a6d4869427726217c86c6c9e36ac3d44b80e1fc9da37d47fcab50ef69514",
                },
                ["vlf_count"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "3ebe212917d830caf528bb07ee106f106941397d95cb810444dad08795c545f9",
                },

                // strings-r2-05: three sys.dm_io_virtual_file_stats alerts read a SINCE-STARTUP total
                // and asserted a current reading, described a MAX (the worst file) as an "Average",
                // and divided two integers so the result was truncated to whole milliseconds (PROVED
                // .\new2022: the shipped disk_latency_read returned 1 where the true value was 1.625).
                // Because a lifetime average barely moves, they could not recover once breached:
                // io_stall_time stood at 141 ms against a critical of 100, and disk_latency_write at
                // 537 ms against a critical of 50, on an idle healthy instance. Now a windowed delta.
                ["io_stall_time"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "6074d7f80e65454588a689baae8d301bfa948fb006985766d7fbf9d49b0d8f9c",
                },
                ["disk_latency_read"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "b6485cd9b4e32bc9bf7a1a2c36fab819ad21b18818cc1f1ea99524cc96c1af83",
                },
                ["disk_latency_write"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "98c5c4a7450e18e03e559d48dbf61cae7317fedcbf98fa9767b616b4c88cfa3f",
                },

                // strings-r2-11: tempdb_contention summed instance-wide PAGELATCH waits from
                // sys.dm_os_wait_stats, which carries neither a database nor a page-type column, so a
                // hot page in any user database was counted as tempdb allocation contention (PROVED
                // .\new2022: 67,059 PAGELATCH_EX waiting tasks summed in). Now reads
                // sys.dm_os_waiting_tasks filtered to database 2 and to allocation pages.
                ["tempdb_contention"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "211d9c4967e33d0450c4beedcba791b385cfa8c4f8ed4a540faa1ab8324eeac7",
                },

                // strings-r1-03: read ProcessUtilization, the SQL Server PROCESS's share of CPU, under
                // a description that said "Total processor utilization" (PROVED .\new2022, both values
                // off the same ring-buffer record: ProcessUtilization 0 while total was 6). The
                // measurement is unchanged and correct for the question the alert asks; only the words
                // and the name were wrong, so only they were re-based. Its signature is registered all
                // the same, because a re-base is the only way an existing install stops displaying the
                // false claim.
                ["processor_under_utilization"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "2d8fb6745eef5ea3ea61400b97d4978078672d5b2c3f3a333b4e7c19eb887d07",
                },

                // strings-r2-07: declared unit "percent_of_average" over a query returning raw elapsed
                // SECONDS with no baseline computed anywhere. The unit was also unrepresentable in the
                // alert editor's Unit control, so opening the alert and saving silently rewrote it to
                // "percent". Re-based to unit "seconds", which is what the query has always returned.
                ["agent_job_long_running"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "3f22bd4a12ae4def600d039f493ae40f76c59a23fdeebd6b9b52a881878db54a",
                },

                // -- Strings lane cluster 1, ledger lane 8 (2026-08-28): alerts no value could reach --
                //
                // AlertEvaluationService.IsThresholdBreached is STRICT: value > threshold, never >=.
                // Thirty-one shipped alerts combined that with warning = 1 over a query whose reachable
                // range made 1 the answer, so no operator could make them speak except by typing a
                // threshold below 1. All thirty-one move to warning = 0, which is the same remedy the
                // Alerts lane applied to io_error. Nine also had their query replaced, for the reason
                // recorded beside them. Every signature below is the body as it shipped at 84beb1c,
                // captured before the fix into Tests/.../Fixtures/prelane-cluster1-alerts-84beb1c.json,
                // and each is proved to re-base by AlertDefinitionMigratorCluster1Tests.

                // (a) SIXTEEN yes/no queries - CASE WHEN ... THEN 1 ELSE 0 END - under warning 1, so
                // the capped value 1 was never greater than the threshold and they were silent for
                // ever. PROVED live on NEW2022 that the condition can be PRESENT and the alert still
                // mute: high_risk_linked_servers returned 1 and implicit_column_conversions returned 1,
                // neither able to fire. Seven of them declared unit "count" over a yes/no, so their
                // query now returns the real cardinality and the unit stops lying about what the number
                // means. Three of those seven needed more than a COUNT:
                //   high_risk_linked_servers counted the instance's OWN row in sys.servers, which
                //     carries is_rpc_out_enabled and is_remote_login_enabled on every SQL Server
                //     (PROVED on both NEW2022 and OLD2017), so simply making it fire would have
                //     reported a high-risk linked server on every server in the world. It now requires
                //     is_linked = 1.
                //   public_role_dangerous_permissions read a column that does not exist -
                //     sys.database_permissions has major_id, not object_id - so it did not merely stay
                //     silent, it THREW on every evaluation (PROVED on both instances: "Invalid column
                //     name 'object_id'"). It now counts across every accessible database and excludes
                //     Microsoft's own shipped objects, which a stock instance grants to public in every
                //     database (PROVED: 44 rows before the exclusion, 0 after; the alertable leg was
                //     exercised in a rolled-back transaction - 0 clean, 1 with one user table granted
                //     to public, 0 after rollback).
                //   sql_agent_jobs_without_notifications tested sysjobs steps' on_fail_action, which is
                //     a flow-control action and not a notification. It now reads the notify_level_* and
                //     operator columns, and does not accept a Windows event-log entry as telling a
                //     person anything.
                // strings-r1-04's implicit_column_conversions matched EXPLICIT '%CAST(%' and
                // '%CONVERT(%' in query text, the opposite of an implicit conversion. It now counts
                // plans SQL Server itself flagged with PlanAffectingConvert / ConvertIssue "Seek Plan",
                // capped at the 50 most CPU-expensive plans because the unbounded scan cost 2,625 ms of
                // CPU on a 718-plan cache (MEASURED) against 329 ms bounded.
                // strings-r1-05's cluster_failover compared NodeName against a subquery restricted to
                // the same row by the outer WHERE, so it was false by construction, and it held no
                // prior-owner state so it could not have detected a failover in principle. It is
                // re-based on what the instance can actually see - a cluster node that is not up - and
                // its description now says plainly that a completed failover is invisible from inside
                // SQL Server.
                ["agent_stopped"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "aacd63169839655db3687372b9b8c09f5e36094f21928abd66da4cc04eedefb4",
                },
                ["fulltext_stopped"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "c9da04545ed76be3a64b511d51ff5e1d38c4c77cb8197f896d14f15fd49e3322",
                },
                ["dtc_stopped"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "019d64ccfca024f3511920b262741cf98863f85b4dd374004f9c15c8c7c36532",
                },
                ["browser_stopped"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "cf48acc0e92116aa0bd42634599b17b9c116307ab2982d026ebb25dd6ffd33a0",
                },
                ["ssis_stopped"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "8cb58b1b7a87c26381c181e310c9157dc0a2118e5b6f801ea20b9a47259a480e",
                },
                ["sql_analysis_service_stopped"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "ac94b6463040b3fcd1c5a406381ecdaf2c9feed1196d825b7680a4704fb50b56",
                },
                ["sql_reporting_service_stopped"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "d2b3cd5b0368d23c1274142ac5934e4647beb5669dcb4ad8a261dfc500abcd7d",
                },
                ["database_mail_not_configured"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "d85f376dfb58886c19cda7bc27af1c863603b5e00628f30cab328504105713bb",
                },
                ["unencrypted_tcp_connections"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "2e611d869fbf57608390ae16a3186fc324f078381e2a687b6502ac0f4e8b7d2d",
                },
                ["high_risk_linked_servers"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "24777fda69e0c2ec71337bffc8ef11e70f4c3595d5f83d1b4790ddb5198afa6d",
                },
                ["public_role_dangerous_permissions"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "49f8d9ee8f0603448dc49f26f52b7cec3bdd25b558a9521ca08018a3bd34b79c",
                },
                ["orphaned_sql_agent_jobs"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "06c799bc917c5dbc412cd6a7d8951c317a93a0ddb005f90a17db769e0dd9dd41",
                },
                ["sql_agent_jobs_without_notifications"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "603116ed11a357929854d94910e07a22e907c43fed46c563cffe27ef12b31d38",
                },
                ["auto_update_stats_async"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "926bb4bb2e48a3682a66935c590a27530395411d733544368b35f136778030f9",
                },
                ["implicit_column_conversions"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "3042a5a6648eb37b49f6de324b4d3a669beffa861bf7f0e140144e6decc38db7",
                },
                ["cluster_failover"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "72b71d310d93acf51aa1cb3662b6afc601e19fa8ff58c06671f7a6969bc5e9b5",
                },

                // (b) strings-r1-02: low_compression_success_rates shipped the literal
                // "SELECT 0 -- Requires manual review using compression analysis scripts". A constant
                // breaches nothing, whatever the operator types. It now measures the real thing -
                // page_compression_success_count over page_compression_attempt_count across every
                // accessible database - as a percentage under less_than, with no reading published
                // below 1000 attempts instance-wide because a handful of attempts says nothing. PROVED
                // live: 82.1 percent on NEW2022 (2,328 attempts), and a stated-reason NULL on OLD2017,
                // which has not reached the floor.
                ["low_compression_success_rates"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "0cccc1726ad5243fc64fe0e08b1e4ac0143094b10b443b7583a5cc806d7ef435",
                },

                // (c) FIFTEEN occurrence counts under the same warning = 1. These were not silent -
                // they needed TWO occurrences - while every one of their own descriptions named a
                // single one ("A user database is not in ONLINE state", Critical; "A fatal error
                // (severity >= 20) has been logged", Critical; "An Availability Group listener is
                // offline", Critical). Same arithmetic as (a), milder shape, same remedy. Not filed by
                // the hunt: its census looked for the yes/no shape and stopped there. Found by
                // extending that census to every alert whose threshold a single occurrence could not
                // reach, which is what closing strings-r2-06 honestly required.
                ["data_file_autogrow"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "61a25ae693f6c887e552568f5392df5c7ab6e11bab92846d4d9ce8563a471333",
                },
                ["log_file_autogrow"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "c538dc25d8a5fa37db418eabc4df11593722c708628864edd66ce02c4284416a",
                },
                ["tempdb_autogrow"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "0b0b58ca2e893927a74195614877e7c0b8d248092edad79d9cefbb41065e888c",
                },
                ["database_unavailable"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "e2fab527aa787082f8cea56d31601f858c3306631f46ec8b19b69c0a24644c41",
                },
                ["agent_job_failure"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "df9889cdbb1411fd31d6876c4e5a2706c3989aa7d210bd751ce71494f3c16da5",
                },
                ["agent_job_completion"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "6ec7e524a66034a5d53c420d6a25ebc61af8089b262dba53be8be32944c4d73f",
                },
                ["ag_failover"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "1f8d822de97dfbc8fdc674df9cd0715240a6a7311d77015183c8b665c77e3219",
                },
                ["ag_replica_unhealthy"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "d616ddd90e5e2e1bf9565102b452136ca3cfac27f6ed0cf5ee5ee9c34408a599",
                },
                ["ag_listener_offline"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "48ddd9d64c6f7b8baf9c68b95d4ac7584cf56c84d83c8a15c48e20739767c50e",
                },
                ["mirror_status_change"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "b2a63de6cfdd80292fcb9d5e143dc4841313ee6be7c654f00cad2e514dc7af2c",
                },
                ["error_log_severity"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "1f46c0a641ea432667c463f9ff7417de4990a18a595fa8288375f6fc80758d8f",
                },
                ["error_log_fatal"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "b6b6f878b41471bd7412b1282f362334dd41a1c9b7aaf46293b265154e5ddbf6",
                },
                ["config_change"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "8f48da8f89b005a75f52cd8637dcd66f6e8a63794215ad02f77c8bbb278159c6",
                },
                ["page_verify_disabled"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "f625c9f5c510546dce9dfd6394edc2a36d76539115c4764b682eacd5a2ed3c21",
                },
                ["ag_not_ready_automatic_failover"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "64cbb99ccaa01a3352b05e76e8b306ad612988a1a8f5e2c96facceba3ee467f1",
                },

                // ── Strings lane FIX ROUND, ledger lane 8 (2026-08-28) ───────────────────────────
                // The same never-fires arithmetic, in the four places cluster 1's own lint could not
                // see it. Its shape reader answered Unknown for every queryMode alert (the query
                // field is dead text) and matched only the literal "THEN 1 ELSE 0 END", so an
                // inverted yes/no and three built-in handlers were exempt BY CONSTRUCTION - the lint
                // reported green with members of its own defect class still in the file. Every
                // signature below is the body as it shipped at 84beb1c, captured into
                // Tests/.../Fixtures/prelane-gate-alerts-84beb1c.json by git show, and each is proved
                // to re-base by AlertDefinitionMigratorGateTests.
                //
                // instance_unreachable and machine_unreachable (both Critical, both enabled) shipped
                // warning 1, and CheckConnectivityAsync returns exactly 1 when the server is
                // unreachable, so a down instance could not fire. They share one signature because
                // their measurement facts were byte-identical: same dead query, same unit, same
                // operator, same threshold, no critical. PROVED by execution against a dead endpoint
                // (127.0.0.1,1) through EvaluateSpecialAlertAsync: no alert at warning 1, an alert at
                // warning 0. machine_unreachable's description is re-based too - it promised a HOST
                // check and runs the same SQL connection test as its sibling.
                ["instance_unreachable"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "299ed7bef9a86f13c50393611c0bc95a3e98467f752906513867e13b745618c7",
                },
                ["machine_unreachable"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "299ed7bef9a86f13c50393611c0bc95a3e98467f752906513867e13b745618c7",
                },

                // deadlock: warning 1 over CountDeadlocksAsync, a COUNT of xml_deadlock_report events,
                // under a description reading "One or more deadlocks detected ... in the last 5
                // minutes". One deadlock returned 1 and said nothing; it took two. PROVED on
                // .\new2022: a real deadlock was generated (victim reported Msg 1205) and the
                // handler's own SQL returned 1.
                ["deadlock"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "782ed0daac7b4d83dbdecac518cd9049b5ebf80fb75de8c798f79021f1b26c31",
                },

                // failed_login_xevent_session: the inverted yes/no. THEN 0 ELSE 1 END, so 1 means the
                // XEvent session is ABSENT - the alertable state - against warning 1 under strict >.
                // Disabled as shipped, so the blast radius was an operator who enabled it and heard
                // nothing, but it is the member that proves the reader's blind spot rather than an
                // argument about it.
                ["failed_login_xevent_session"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "fe40c49862738b193bec86b5a494ff25a02d06f4baed1b9f3ebd443ce5a29882",
                },

                // ── Lane Q14 alerts-that-cannot-fire (2026-09-18) ───────────────────────────────────
                // INVARIANT: every ENABLED shipped alert must be able to produce a value that can cross
                // its own threshold. Two enabled alerts could not, and a shipped-file edit alone reaches
                // fresh installs only, so both are registered here. Each carries TWO signatures, both
                // derived by a script from git (it hashed this alert at every one of the 18 commits that
                // touched Config/alert-definitions.json up to 11e6f88; the project's private evidence archive holds
                // evidence/alerts-that-cannot-fire-2026-09-18/builder/sig-history.py and its .out.txt): the
                // body as it shipped from 6f15efe (2026-03-27) through 11e6f88 (build 4078), captured
                // verbatim into Tests/.../Fixtures/prelane-q14-alerts-11e6f88.json; and the same body as
                // v0.80.0 (a0c9566) shipped it, with no "operator" property, which the model reads as
                // greater_than. Proved to re-base by AlertDefinitionMigratorQ14Tests. What ships in
                // their place is held to the invariant by AlertQueryResultSetCensusTests and
                // AlertQueryPropertyNameCensusTests.
                //
                // sql_response_time: ExecuteScalar reads the FIRST result set, and the query returned
                // a constant SELECT 1 before its DATEDIFF, so the value was always 1 against 1000/2000
                // ms. PROVED live on .\NEW2022 and .\OLD2017 (scout wf_ee2a97c4-5b3). Re-based onto
                // queryMode response_time_probe, timed in the app (ruling 1, DECISIONS 2026-09-18
                // 01:40); queryMode is a CatalogueFactProperty, so the routing travels with the query.
                // Its 2-minute hold (ruling 2, DECISIONS 2026-09-18 04:21) travels by
                // SupersededDefinitionCarries below.
                ["sql_response_time"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "0f728df191c74564bd86620de3be12a386980d568cf0142d87e03f228bd4cf40",
                    "7c8f504e94535b11b641ea98c2ef6b05f9bafe929e2c29bce524111837e98813",

                    // Lane alert-correctness 2 (A2, ruling DECISIONS 2026-09-18, "turn it off"): the
                    // body shipped from 4082 (f1c821e) through 4088 (5435f4e), with canBaseline true. The
                    // alert is handler-routed, so a learned baseline on it is inert today and a latent
                    // re-enable the day it is un-routed. Registered so SupersededDefinitionCarries can move
                    // canBaseline to false on an existing install. A2 changes no hashed field, so this is
                    // ALSO the signature of the body that ships now (the processor_under_utilization
                    // precedent above); WouldChange keeps a carried install from being repaired again.
                    // The accepted cost: an operator who hand-edits this alert's name, description or
                    // canBaseline in the JSON after the upgrade is reset to the shipped value on the next
                    // start. Guarded by AlertDefinitionMigratorLane2Tests, which computes this value from
                    // the 4082 fixture (prelane-alert-correctness-l2-alerts-5435f4e.json).
                    "96d19bed5c88506cf1c46af17710fcf0e743c8bcb4e27ebc1e829e1b995bb2b9",
                },

                // connection_count: divided by SERVERPROPERTY('MaxConnections'), which is not a
                // property, so the value was NULL on every server. PROVED live on both instances
                // (the project's private evidence archive, evidence/alerts-that-cannot-fire-2026-09-18/builder/live/probe-r2-and-props.out.txt).
                // Re-based onto the percentage of the configured 'user connections' limit, or 32767 when
                // that is 0 (ruling 2, DECISIONS 2026-09-18 01:40). Its learned-baseline and trend
                // firing is switched off (ruling 1, DECISIONS 2026-09-18 04:21) by
                // SupersededDefinitionCarries below.
                ["connection_count"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "52d1ee63db087cada1374328d16fe367536b823108182a4269ab9b3f2030f1f9",
                    "8897c0379045cdbb39e770809fbee18ea6b5193b074bbdd625dd97a5f8abddec",
                },

                // integrity_check_overdue (ruling 3, DECISIONS 2026-09-18 04:21): it read
                // DATABASEPROPERTYEX(name, 'LastGoodCheckDbTime') and nothing else, filtered the NULLs
                // out and answered ISNULL(MAX(...), 0). On .\OLD2017 (14.0.2130.4, RTM, no CU) that
                // property is NULL for all 8 databases and behaves exactly like an invented property
                // name, while 'Status' on the same databases is not NULL, so the alert read 0 hours for
                // ever and could not fire. DBCC DBINFO's dbi_dbccLastKnownGood on the same instance read
                // real dates (2026-07-29) for 7 of them, and on .\NEW2022 it agreed with the property on
                // all 11 databases. PROVED 2026-09-18 04:29 NZST (the project's private evidence archive,
                // evidence/alerts-that-cannot-fire-2026-09-18/fix2/w3/m1-integrity-sources.out.txt and
                // m2-dbinfo-by-name.out.txt). Re-based onto the property with a DBCC DBINFO fallback that
                // answers no reading, never 0, when neither can be read. THREE signatures, one per body
                // this alert has shipped with, hashed at every commit that touched the file up to 11e6f88
                // (fix2/w3/sig-history-integrity.out.txt): 15 commits e15cb4e..11e6f88 (ISNULL and
                // CONVERT), 2 commits 6f15efe..ea00b7d (no ISNULL, no CONVERT, operator present) and
                // v0.80.0 a0c9566 (no operator). All three shipped the same thresholds and unit.
                ["integrity_check_overdue"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "cf2d11e2914cde94086f2d53e5bde018c521ff3e043b1e58eff786b49d6f74fa",
                    "bebec2779b7c641b3e927b044c46e8eb1700479837bb8b936368b7c85a0c92a1",
                    "8c500d9b701d9d1bc8697cb9fdbc9e415edbc48d4d989b54ebb3c486e5201ef2",

                    // Lane alert-correctness 2 (C1, ruling DECISIONS 2026-09-18, "show it as Unknown now"):
                    // the body shipped from 4082 (f1c821e) through 4088 (5435f4e). With no online user
                    // database readable it answered NULL, which the engine treats as a completed query with
                    // no value, so an alert that could not read its evidence looked like one that read it and
                    // found nothing wrong. PROVED on .\OLD2017 as a login without sysadmin: neither the
                    // property nor DBCC DBINFO yields a date for that login. The shipped query now THROWs a user error
                    // before its final SELECT, which the engine records as an evaluation failure (Unknown)
                    // and ServerAnswerClassifier reads as an answer. Guarded by AlertDefinitionMigratorLane2Tests
                    // and IntegrityUnreadableIsUnknownTests.
                    "39b7e60ef3c5afbc73a1ebfea152b537113588a824bb82ff0ae7d9f971138f1d",
                },
            };

        /// <summary>
        /// One operator-owned field OUTSIDE the definition signature that a re-base also moves:
        /// <paramref name="Property"/> goes from <paramref name="OldValueJson"/> (a raw JSON token, or
        /// null for "absent") to <paramref name="NewValueJson"/>.
        /// </summary>
        internal sealed record OperatorFieldCarry(string Property, string? OldValueJson, string NewValueJson);

        /// <summary>
        /// Per alert id, the operator-owned fields a re-base carries to the new shipped value.
        ///
        /// <para><b>THE CARRY RULE (orchestrator ruling, DECISIONS 2026-09-18 04:21).</b> A field outside
        /// the signature is an operator SETTING, so a re-base normally leaves it alone (see
        /// <see cref="CatalogueFactProperties"/>). It moves ONLY when BOTH hold: the installed body
        /// matches a registered <see cref="SupersededDefinitionSignatures"/> entry, AND the installed
        /// field still equals the value this product shipped (<c>OldValueJson</c>, compared as a raw
        /// JSON token, absent included). An operator who changed the field keeps their value, and an
        /// operator who edited the body keeps everything, because nothing on a mismatched alert moves.
        /// It moves to the SHIPPED value, and only when that equals <c>NewValueJson</c>, so a declaration
        /// that has gone stale moves nothing rather than something nobody declared.
        /// AlertDefinitionMigratorQ14Tests.Every_declared_carry_is_registered_and_names_the_shipped_value
        /// enumerates this table and goes red when an entry has no signature, or its new value is not
        /// what ships.</para>
        ///
        /// <para>One mechanism for every entry: <see cref="TryRepairSupersededDefinitions"/> applies it,
        /// and WouldChange counts it, so a carry pending on an otherwise-current body is still a repair
        /// and a carried body is not repaired twice.</para>
        /// </summary>
        internal static readonly IReadOnlyDictionary<string, IReadOnlyList<OperatorFieldCarry>> SupersededDefinitionCarries =
            new Dictionary<string, IReadOnlyList<OperatorFieldCarry>>(StringComparer.OrdinalIgnoreCase)
            {
                // Ruling 1, 04:21: no learned or trend firing. PROVED defect (gate 2, X4): with baselines
                // on by default, +25 connections (0.1556 percent) fired a learned Critical and a rising
                // series fired a trend Critical at 0.0824 percent, on both instances. Every body this
                // alert shipped with before the lane carried canBaseline true, or no canBaseline at all,
                // which the model reads as false already.
                ["connection_count"] = new[] { new OperatorFieldCarry("canBaseline", "true", "false") },

                // Ruling 2, 04:21: stay high for 2 minutes before firing. No shipped body ever carried
                // holdSeconds (the property is new in this round), so the old value is absent.
                //
                // Lane alert-correctness 2 (A2, DECISIONS 2026-09-18, "turn it off"): no learned
                // baseline on a handler-routed alert. Every body it shipped with carried canBaseline
                // true, or no canBaseline at all, which the model reads as false already (21 commits of
                // the shipped file up to 5435f4e: 15 true, 6 absent). ONE entry per id: a second
                // ["sql_response_time"] = line would silently replace
                // this array and drop the holdSeconds carry.
                ["sql_response_time"] = new[]
                {
                    new OperatorFieldCarry("holdSeconds", null, "120"),
                    new OperatorFieldCarry("canBaseline", "true", "false"),
                },
            };

        private static IReadOnlyList<OperatorFieldCarry> CarriesFor(string id) =>
            SupersededDefinitionCarries.TryGetValue(id, out var carries) ? carries : Array.Empty<OperatorFieldCarry>();

        /// <summary>The raw JSON token of an installed property, or null when the property is absent.</summary>
        private static string? RawToken(JsonObject alert, string property) =>
            alert.TryGetPropertyValue(property, out var value) ? (value?.ToJsonString() ?? "null") : null;

        /// <summary>True when this carry would move the installed field: it still holds the value this
        /// product shipped, and the shipped alert now holds exactly the declared new value.</summary>
        private static bool CarryPending(JsonObject installedAlert, JsonObject shippedAlert, OperatorFieldCarry carry) =>
            RawToken(installedAlert, carry.Property) == carry.OldValueJson
            && RawToken(shippedAlert, carry.Property) == carry.NewValueJson
            && carry.OldValueJson != carry.NewValueJson;

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

                // Nothing to do is not a repair (2026-08-28, strings lane cluster 2). The signature
                // covers MEASUREMENT facts only - deliberately, so one broken population does not
                // fragment into one signature per wording - which means a correction to the PROSE
                // alone leaves the signature unchanged. processor_under_utilization is exactly that
                // case: it read the SQL Server process's CPU share under a description that said
                // "Total processor utilization", and only the words were wrong. Without this guard
                // its registration would match its own repaired body for ever, so every start would
                // "repair" it again and rewrite the file. Comparing what would actually change keeps
                // the repair idempotent by OBSERVATION rather than by assuming the signature moved,
                // and it is what lets a prose-only fix reach an existing install at all.
                if (!WouldChange(installedAlert, shippedAlert)) continue;

                // Decided BEFORE anything is written: a carry reads the installed field as the operator
                // left it.
                var pendingCarries = CarriesFor(id).Where(c => CarryPending(installedAlert, shippedAlert, c)).ToList();

                foreach (var property in CatalogueFactProperties)
                {
                    if (shippedAlert.TryGetPropertyValue(property, out var shippedValue) && shippedValue is not null)
                        installedAlert[property] = shippedValue.DeepClone();
                    else
                        installedAlert.Remove(property);
                }

                foreach (var carry in pendingCarries)
                    installedAlert[carry.Property] = shippedAlert[carry.Property]!.DeepClone();

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
        /// Whether re-basing this alert onto the shipped one would alter any
        /// <see cref="CatalogueFactProperties"/> value, presence included. Compared as serialised
        /// JSON so a value's TYPE is part of the comparison: a threshold that moved from <c>1</c> to
        /// <c>1.0</c>, or a property that changed from a string to null, are both real changes and
        /// neither should read as equal. A pending <see cref="SupersededDefinitionCarries"/> entry is a
        /// change too, so a carry still owed on a body whose facts already match is delivered once, and a
        /// carried body is not repaired again.
        /// </summary>
        private static bool WouldChange(JsonObject installedAlert, JsonObject shippedAlert)
        {
            var id = ReadString(installedAlert, "id");
            if (id != null && CarriesFor(id).Any(c => CarryPending(installedAlert, shippedAlert, c)))
                return true;

            foreach (var property in CatalogueFactProperties)
            {
                var shippedHas = shippedAlert.TryGetPropertyValue(property, out var shippedValue) && shippedValue is not null;
                var installedHas = installedAlert.TryGetPropertyValue(property, out var installedValue) && installedValue is not null;

                if (shippedHas != installedHas) return true;
                if (!shippedHas) continue;
                if (!string.Equals(shippedValue!.ToJsonString(), installedValue!.ToJsonString(), StringComparison.Ordinal))
                    return true;
            }
            return false;
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
                    "Re-based {Count} superseded alert definition(s) in {Path} to the shipped catalogue: {Ids}. Each held, byte for byte, a definition this product shipped and has since corrected; an operator-tuned alert matches no shipped signature and was left exactly as it is, and every enable/disable, cooldown and channel setting is unchanged. The only operator-owned fields that can move are the ones this product declares per alert (SupersededDefinitionCarries), and each moved only where it still held the value this product shipped",
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
