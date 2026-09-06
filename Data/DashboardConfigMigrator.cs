/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data
{
    /// <summary>
    /// Re-bases a panel in an INSTALLED dashboard-config.json to the shipped body, but ONLY when the
    /// installed panel is byte-identical — every property except its <c>enabled</c> toggle — to a
    /// panel body this product shipped and has since corrected. It carries a corrected panel to an
    /// existing install without touching a panel an operator has edited.
    ///
    /// <para>WHY THIS EXISTS. An upgraded install keeps its own Config/dashboard-config.json by four
    /// independent mechanisms — installer/SQLTriage.iss ships it <c>onlyifdoesntexist</c>;
    /// AutoUpdateService lists it in ProtectedConfigFiles and restores the operator's copy over the
    /// package's; tools/SQLTriageUpdater lists it in ProtectedConfig; and the service deploy script
    /// never copies the publish config folder. That is correct for the operator's enable/disable and
    /// layout edits, and wrong for a query the SOFTWARE shipped broken. The only prior merge path,
    /// DashboardConfigService.PatchMissingDashboards, adds a whole DASHBOARD absent by id from
    /// DefaultConfigGenerator's three generated dashboards — all three always already present — so it
    /// is a proved no-op on every real install and never reaches a single panel, description or query.
    /// A content-only fix to dashboard-config.json therefore reaches fresh installs alone. This closes
    /// that gap for the panels the honesty-hunt dashboards lane corrected. Same shape, same discipline
    /// as AlertDefinitionMigrator.RepairSupersededDefinitions and
    /// ScriptConfigurationMigrator.TryRepairSupersededOutputQueries.</para>
    ///
    /// <para>THE MATCH IS EXACT AND THE OPERATOR ALWAYS WINS. <see cref="SupersededResourceSuffix"/>
    /// embeds the exact pre-fix bodies of only the panels this lane changed (from the base the lane
    /// forked and from the real installed config on record, where they differ). A re-base fires for an
    /// installed panel only when its canonical form — every property but <c>enabled</c>, keys sorted,
    /// numbers normalised — equals one of those superseded bodies. An operator who edited ANY of a
    /// panel's fields (its SQL, a threshold, a title, its layout) produces a body that matches nothing
    /// here and is left exactly as they left it. The one field a match ignores, and a re-base
    /// preserves, is <c>enabled</c>: a panel the operator disabled is corrected in place and stays
    /// disabled.</para>
    ///
    /// <para>PANELS ARE NEVER ADDED OR REMOVED, AND NO OTHER PANEL IS TOUCHED. A panel absent from the
    /// installed file stays absent; a panel the lane did not change matches nothing in the superseded
    /// set and is never rewritten. Bringing a stock install wholesale up to the current catalogue would
    /// be a ruling, not a migration — this delivers a reviewed, enumerated set of corrections and
    /// nothing else.</para>
    ///
    /// <para>THE WHOLE FILE IS RE-SERIALISED when at least one panel is re-based — the same weaker
    /// promise AlertDefinitionMigrator makes, and for the same reason: DashboardConfigService.Save
    /// already rewrites this file end to end through the typed model on every operator edit, so a
    /// JsonNode round-trip that preserves every property (including any the model does not carry) is
    /// strictly safer than the app's own writer. It does not promise byte-identical whitespace, and it
    /// matches the file's existing line endings.</para>
    ///
    /// <para>AN UNREADABLE OR UNPARSEABLE STORE IS NEVER REPLACED WITH DEFAULTS (the config-store
    /// write-guard convention). Missing, unreadable, not a JSON object, or carrying no dashboards: it
    /// announces at Warning and returns having written nothing, and never throws — the caller is a
    /// constructor. A corrupt installed file is left for DashboardConfigService.Load to back up and
    /// report.</para>
    /// </summary>
    internal static class DashboardConfigMigrator
    {
        /// <summary>The corrected shipped config, embedded so the re-base has a source even though the
        /// installer keeps the operator's older copy on disk. SQLTriage.csproj embeds
        /// Config\dashboard-config.json; the name is resolved by suffix because MSBuild's logical-name
        /// mangling is an implementation detail.</summary>
        private const string ShippedResourceSuffix = "Config.dashboard-config.json";

        /// <summary>The pre-fix panel bodies this lane superseded, embedded from
        /// Config\dashboard-config.superseded.json.</summary>
        private const string SupersededResourceSuffix = "Config.dashboard-config.superseded.json";

        /// <summary>The one panel property a match ignores and a re-base preserves: the operator's
        /// enable/disable toggle. Everything else must match exactly for a re-base to fire, and
        /// everything else is replaced when it does.</summary>
        private const string EnabledProperty = "enabled";

        private static readonly JsonNodeOptions NodeOptions = new()
        {
            // The reader that consumes this file (DashboardConfigService.SerializerOptions) is
            // case-insensitive, so the lookups here match it.
            PropertyNameCaseInsensitive = true
        };

        private static readonly JsonDocumentOptions DocumentOptions = new()
        {
            // The safe direction: a commented or trailing-comma file is announced and left as it is
            // rather than silently re-serialised without its comments.
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false
        };

        private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

        // ── Embedded-resource readers ───────────────────────────────────────────────────────

        internal static byte[]? ReadResourceBytes(string suffix)
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            if (name is null) return null;

            using var stream = asm.GetManifestResourceStream(name);
            if (stream is null) return null;

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }

        internal static string? ReadResourceText(string suffix)
        {
            var bytes = ReadResourceBytes(suffix);
            if (bytes is null) return null;
            var text = new UTF8Encoding(false).GetString(bytes);
            return text.Length > 0 && text[0] == '\uFEFF' ? text.Substring(1) : text;
        }

        internal static string? ReadShippedDefaults() => ReadResourceText(ShippedResourceSuffix);

        internal static string? ReadSupersededBodies() => ReadResourceText(SupersededResourceSuffix);

        // ── The pure half ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// Re-bases every installed panel whose canonical body (ignoring <c>enabled</c>) matches a
        /// superseded body for its dashboard+panel id, replacing all of its properties except
        /// <c>enabled</c> with the shipped body's. Reports what it changed as "dashboardId/panelId".
        /// Returns false, with <paramref name="mergedJson"/> null, when nothing matched OR any input
        /// is not a usable config document — the caller writes only on true.
        /// </summary>
        internal static bool TryRepairSupersededPanels(
            string installedJson,
            string shippedJson,
            string supersededJson,
            out string? mergedJson,
            out IReadOnlyList<string> repaired)
        {
            mergedJson = null;
            repaired = Array.Empty<string>();

            var installedRoot = ParseObject(installedJson);
            var shippedRoot = ParseObject(shippedJson);
            if (installedRoot is null || shippedRoot is null) return false;

            var superseded = BuildSupersededIndex(supersededJson);
            if (superseded.Count == 0) return false;

            var shippedPanels = IndexShippedPanels(shippedRoot);
            if (shippedPanels.Count == 0) return false;

            var repairedKeys = new List<string>();

            foreach (var (dashboardId, panel) in EnumerateInstalledPanels(installedRoot))
            {
                var panelId = ReadString(panel, "id");
                if (panelId is null) continue;

                var key = PanelKey(dashboardId, panelId);
                if (!superseded.TryGetValue(key, out var oldBodies)) continue;
                if (!oldBodies.Contains(CanonicalPanel(panel))) continue;   // edited, or already current
                if (!shippedPanels.TryGetValue(key, out var shippedPanel)) continue; // shipped no longer defines it

                RebasePanel(panel, shippedPanel);
                repairedKeys.Add(key);
            }

            if (repairedKeys.Count == 0) return false;

            mergedJson = SerializeMatchingLineEndings(installedRoot, installedJson);
            repaired = repairedKeys;
            return true;
        }

        /// <summary>
        /// Renders <paramref name="root"/> indented, with the line endings <paramref name="installedJson"/>
        /// already uses.
        ///
        /// <para>System.Text.Json emits Environment.NewLine between tokens (CRLF on Windows), so this first
        /// collapses whatever the serializer produced to a bare LF, then matches the installed file's own
        /// line endings. Collapsing first is what keeps a CRLF install from being handed doubled CRs
        /// (<c>\r\r\n</c>) when the serializer already emitted CRLF — the defect fixed at 0848ec8. Raw CR/LF
        /// never appear inside a JSON string value (the serializer escapes them), so this only ever touches
        /// indentation breaks. Shared with <see cref="DashboardFactoryReset"/> so that fix cannot be
        /// re-broken by a second hand-written copy of the rule.</para>
        /// </summary>
        internal static string SerializeMatchingLineEndings(JsonObject root, string installedJson)
        {
            var text = root.ToJsonString(WriteOptions).Replace("\r\n", "\n", StringComparison.Ordinal);
            return installedJson.Contains("\r\n", StringComparison.Ordinal)
                ? text.Replace("\n", "\r\n", StringComparison.Ordinal)
                : text;
        }

        /// <summary>Replaces every property of <paramref name="installed"/> except <c>enabled</c> with
        /// the shipped panel's, preserving the operator's exact enable state (present-or-absent and
        /// value).</summary>
        private static void RebasePanel(JsonObject installed, JsonObject shipped)
        {
            var hadEnabled = installed.TryGetPropertyValue(EnabledProperty, out var enabledValue);
            var enabledClone = hadEnabled ? enabledValue?.DeepClone() : null;

            foreach (var propertyName in installed.Select(kv => kv.Key).ToList())
                installed.Remove(propertyName);

            foreach (var kv in shipped)
            {
                if (string.Equals(kv.Key, EnabledProperty, StringComparison.OrdinalIgnoreCase)) continue;
                installed[kv.Key] = kv.Value?.DeepClone();
            }

            if (hadEnabled)
                installed[EnabledProperty] = enabledClone;
        }

        // ── The IO half ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Called from the loader before it reads the file. Announces and returns rather than throwing
        /// or overwriting; writes only when at least one panel was re-based, so a second run is a no-op.
        /// Returns the "dashboardId/panelId" list of panels corrected.
        /// </summary>
        internal static IReadOnlyList<string> RepairSupersededPanels(string configPath, ILogger logger)
        {
            try
            {
                if (!File.Exists(configPath)) return Array.Empty<string>();

                var shipped = ReadShippedDefaults();
                var superseded = ReadSupersededBodies();
                if (shipped is null || superseded is null)
                {
                    logger.LogWarning(
                        "The shipped dashboard config or its superseded-panel catalogue is not embedded in this build, so a panel this product shipped broken and has since corrected cannot be delivered to {Path}. The installed file is left exactly as it is",
                        configPath);
                    return Array.Empty<string>();
                }

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
                        "Could not read {Path} to check it for superseded dashboard panels. It has NOT been replaced or rewritten - whatever is in it is still what runs",
                        configPath);
                    return Array.Empty<string>();
                }

                if (ParseObject(installed) is null)
                {
                    logger.LogWarning(
                        "{Path} is not a plain JSON object this migration can read, so a superseded panel cannot be re-based in it. It has NOT been replaced with the shipped defaults; DashboardConfigService.Load will back up and report a corrupt file",
                        configPath);
                    return Array.Empty<string>();
                }

                if (!TryRepairSupersededPanels(installed, shipped, superseded, out var merged, out var repaired) || merged is null)
                    return Array.Empty<string>();

                File.WriteAllText(configPath, merged, encoding);

                logger.LogInformation(
                    "Re-based {Count} superseded dashboard panel(s) in {Path} to the shipped catalogue: {Panels}. Each was byte-identical to a panel this product shipped and has since corrected; a panel an operator has edited matches no shipped body and was left exactly as it is, and every enable/disable is preserved",
                    repaired.Count, configPath, string.Join(", ", repaired));

                return repaired;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Could not check {Path} for superseded dashboard panels. The installed file is unchanged",
                    configPath);
                return Array.Empty<string>();
            }
        }

        // ── Indexing ────────────────────────────────────────────────────────────────────────

        /// <summary>The shipped (corrected) panel bodies keyed by dashboardId/panelId, walking both the
        /// flat panels list and every tab's panels.</summary>
        private static Dictionary<string, JsonObject> IndexShippedPanels(JsonObject shippedRoot)
        {
            var index = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
            foreach (var (dashboardId, panel) in EnumerateInstalledPanels(shippedRoot))
            {
                var panelId = ReadString(panel, "id");
                if (panelId is null) continue;
                var key = PanelKey(dashboardId, panelId);
                if (!index.ContainsKey(key)) index[key] = panel;   // first wins; duplicate ids are an authoring mistake
            }
            return index;
        }

        /// <summary>The superseded (pre-fix) bodies as a set of canonical strings per dashboardId/panelId.</summary>
        private static Dictionary<string, HashSet<string>> BuildSupersededIndex(string supersededJson)
        {
            var index = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var root = ParseObject(supersededJson);
            if (root is null) return index;
            if (!root.TryGetPropertyValue("supersededPanels", out var listNode) || listNode is not JsonArray list)
                return index;

            foreach (var node in list)
            {
                if (node is not JsonObject entry) continue;
                var dashboardId = ReadString(entry, "dashboardId");
                var panelId = ReadString(entry, "panelId");
                if (dashboardId is null || panelId is null) continue;
                if (!entry.TryGetPropertyValue("panel", out var panelNode) || panelNode is not JsonObject panel) continue;

                var key = PanelKey(dashboardId, panelId);
                if (!index.TryGetValue(key, out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    index[key] = set;
                }
                set.Add(CanonicalPanel(panel));
            }
            return index;
        }

        /// <summary>Every panel in the config, paired with the id of the dashboard that owns it, across
        /// the flat panels list and every tab's panels.</summary>
        private static IEnumerable<(string dashboardId, JsonObject panel)> EnumerateInstalledPanels(JsonObject root)
        {
            if (!root.TryGetPropertyValue("dashboards", out var dashboardsNode) || dashboardsNode is not JsonArray dashboards)
                yield break;

            foreach (var dashboardNode in dashboards)
            {
                if (dashboardNode is not JsonObject dashboard) continue;
                var dashboardId = ReadString(dashboard, "id") ?? "";

                foreach (var panel in EnumerateDashboardPanels(dashboard))
                    yield return (dashboardId, panel);
            }
        }

        /// <summary>Every panel one dashboard owns, across its flat <c>panels</c> list and every
        /// <c>tabs[].panels</c> entry. Shared with <see cref="DashboardFactoryReset"/> so "how many panels
        /// does this dashboard have" is answered by one walk, not two that can disagree.</summary>
        internal static IEnumerable<JsonObject> EnumerateDashboardPanels(JsonObject dashboard)
        {
            if (dashboard.TryGetPropertyValue("panels", out var panelsNode) && panelsNode is JsonArray panels)
                foreach (var p in panels)
                    if (p is JsonObject panel) yield return panel;

            if (dashboard.TryGetPropertyValue("tabs", out var tabsNode) && tabsNode is JsonArray tabs)
                foreach (var tabNode in tabs)
                    if (tabNode is JsonObject tab
                        && tab.TryGetPropertyValue("panels", out var tabPanelsNode)
                        && tabPanelsNode is JsonArray tabPanels)
                        foreach (var p in tabPanels)
                            if (p is JsonObject panel) yield return panel;
        }

        private static string PanelKey(string dashboardId, string panelId)
            => dashboardId + "␟" + panelId;   // unit separator: dashboard and panel ids can each carry dots

        // ── Canonical form ──────────────────────────────────────────────────────────────────

        /// <summary>A deterministic, whitespace-free rendering of a panel with its <c>enabled</c> toggle
        /// removed, object keys sorted ordinally at every level and JSON numbers normalised (so a config
        /// saved with <c>0.10</c> matches one shipped with <c>0.1</c>). Two panels share a canonical form
        /// exactly when they are the same in every field an operator can edit.</summary>
        internal static string CanonicalPanel(JsonObject panel)
        {
            var sb = new StringBuilder();
            WriteCanonical(panel, sb, EnabledProperty);
            return sb.ToString();
        }

        /// <summary>The same deterministic rendering as <see cref="CanonicalPanel"/> but with NOTHING
        /// excluded, so <c>enabled</c> counts. <see cref="DashboardFactoryReset"/> compares whole
        /// dashboards with it: a factory reset restores the shipped enable state, so a dashboard that
        /// differs only in a panel's <c>enabled</c> toggle is genuinely not at the shipped state and must
        /// not be reported as already restored.</summary>
        internal static string CanonicalNode(JsonNode? node)
        {
            var sb = new StringBuilder();
            WriteCanonical(node, sb);
            return sb.ToString();
        }

        private static void WriteCanonical(JsonNode? node, StringBuilder sb, string? excludeTopKey = null)
        {
            switch (node)
            {
                case JsonObject obj:
                    sb.Append('{');
                    var first = true;
                    foreach (var kv in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                    {
                        if (excludeTopKey is not null && string.Equals(kv.Key, excludeTopKey, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!first) sb.Append(',');
                        first = false;
                        AppendJsonString(kv.Key, sb);
                        sb.Append(':');
                        WriteCanonical(kv.Value, sb);
                    }
                    sb.Append('}');
                    break;

                case JsonArray arr:
                    sb.Append('[');
                    for (var i = 0; i < arr.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        WriteCanonical(arr[i], sb);
                    }
                    sb.Append(']');
                    break;

                case JsonValue value:
                    AppendCanonicalValue(value, sb);
                    break;

                default: // null node
                    sb.Append("null");
                    break;
            }
        }

        private static void AppendCanonicalValue(JsonValue value, StringBuilder sb)
        {
            if (value.TryGetValue<JsonElement>(out var element))
            {
                switch (element.ValueKind)
                {
                    case JsonValueKind.String:
                        AppendJsonString(element.GetString() ?? "", sb);
                        return;
                    case JsonValueKind.Number:
                        sb.Append(NormalizeNumber(element));
                        return;
                    case JsonValueKind.True:
                        sb.Append("true");
                        return;
                    case JsonValueKind.False:
                        sb.Append("false");
                        return;
                    case JsonValueKind.Null:
                        sb.Append("null");
                        return;
                }
            }
            // Fallback for values not backed by a JsonElement (e.g. constructed in code).
            sb.Append(value.ToJsonString());
        }

        private static string NormalizeNumber(JsonElement element)
        {
            var raw = element.GetRawText();
            if (raw.IndexOf('.') < 0 && raw.IndexOf('e') < 0 && raw.IndexOf('E') < 0)
                return raw;   // integer literal: JSON forbids leading zeros, so the raw text is canonical
            return element.GetDouble().ToString("R", CultureInfo.InvariantCulture);
        }

        private static void AppendJsonString(string s, StringBuilder sb)
        {
            // JsonSerializer.Serialize gives a spec-correct, deterministic escaping of the string.
            sb.Append(JsonSerializer.Serialize(s));
        }

        // ── Shared helpers ──────────────────────────────────────────────────────────────────

        /// <summary>Parses a config document, returning null for anything that is not a plain JSON object
        /// (missing, empty, an array, malformed, or carrying comments/trailing commas). Internal so
        /// <see cref="DashboardFactoryReset"/> refuses on exactly the same inputs this does — the
        /// config-store write guard is only a guard if every writer shares one definition of "unreadable".
        /// </summary>
        internal static JsonObject? ParseObject(string json)
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

        internal static string? ReadString(JsonObject obj, string property)
        {
            if (!obj.TryGetPropertyValue(property, out var value) || value is null) return null;
            try
            {
                var text = value.GetValue<string>();
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
