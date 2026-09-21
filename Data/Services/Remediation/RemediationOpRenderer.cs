/* In the name of God, the Merciful, the Compassionate */
/*
 * RemediationOpRenderer — the SINGLE renderer that turns a structured
 * RemediationOperation into the exact T-SQL the gate classifies and the executor
 * runs. One pure function, one source of truth: because the runner's gate and the
 * executor both render from the SAME op, the safety gate provably vets WHAT RUNS.
 * (The prior design classified a fixed, unrelated probe — "show advanced options"
 *  — while the executor ran a separate dbatools command the gate never inspected.)
 *
 * Injection-free by construction: the configuration NAME is shipped (validated to a
 * safe charset and single-quote escaped) and the only bound parameter is an integer
 * constrained to the op's [MinValue, MaxValue]. No untrusted text reaches the SQL.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SQLTriage.Data.Services.Remediation
{
    public static class RemediationOpRenderer
    {
        // sp_configure option names are letters/digits/spaces/parens only (e.g. "max degree of
        // parallelism", "max server memory (MB)"). Reject anything else — a defence against a
        // crafted overlay template (the overlay may ADD non-shipped templates, so its op text is
        // semi-trusted). Parens are harmless inside the single-quoted, escaped string literal.
        private static readonly Regex SafeConfigName =
            new(@"^[A-Za-z0-9 ()]{1,128}$", RegexOptions.Compiled);

        // db_set_option's option_sql is shipped (corpus-authored, never operator input) —
        // same trust tier as ConfigName, same defence-in-depth charset guard. Corpus shapes
        // observed: "SET DB_CHAINING OFF", "SET AUTO_CREATE_STATISTICS ON (INCREMENTAL = ON)",
        // "SET QUERY_STORE = ON (OPERATION_MODE = READ_WRITE)", "SET PAGE_VERIFY CHECKSUM WITH NO_WAIT".
        //
        // ⚠ UNCHANGED BY lane/safeoptionsql-comma (2026-09-02) AND THAT IS THE POINT: this regex is
        // still the ONE decision for every comma-free option_sql, so nothing that rendered before
        // that lane is re-judged by it, and nothing it refused is admitted by the widening. The
        // comma-separated multi-option form goes through the SEPARATE, NARROWER door below.
        private static readonly Regex SafeOptionSql =
            new(@"^[A-Za-z0-9 ()=_]{1,200}$", RegexOptions.Compiled);

        // ── The comma-separated multi-option form (lane/safeoptionsql-comma, ruled 2026-09-02) ──
        //
        // WHY IT EXISTS. T-SQL allows one ALTER DATABASE ... SET to carry several options:
        //   ALTER DATABASE [x] SET AUTO_UPDATE_STATISTICS ON, AUTO_UPDATE_STATISTICS_ASYNC ON;
        // BPCHK-00800 NEEDS that shape. Its check fails on two coupled options, and the async-only
        // remediation is a trap proved live on .\old2017: applied to a sync-OFF database it leaves
        // the database still failing the check but NO LONGER VISIBLE to an async-off offenders
        // query — a silent no-op that reads as a fix. The combined statement moves both arms in one
        // auto-committed ALTER. The old charset class carries no comma, so the corpus could not
        // ship an operation this renderer would render.
        //
        // WHY IT IS A LIST AND NOT A LOOSER REGEX. Adding ',' to SafeOptionSql's character class
        // would have widened the door for EVERY shape at once — "SET X OFF, anything at all" —
        // with the charset as the only wall. Instead the text is DECOMPOSED: a literal SET head,
        // then 2..MaxOptionListElements elements split on the comma, each of which must
        // independently match SafeOptionListElement. An element is one option keyword and ON or
        // OFF, nothing else — no parens, no '=', no suboptions, no WITH clause. So the list door
        // admits strictly LESS per element than the single door does, and the only thing it adds
        // over the pre-lane behaviour is the separator itself.
        //
        // Case is not a safety property here (T-SQL is case-insensitive and the whole clause is
        // corpus-authored), so the element match ignores case — the SHAPE is what is guarded.
        private const int MaxOptionListElements = 4;

        // \A ... \z (never ^ ... $): in .NET '$' also matches before a trailing newline, and '.'
        // does not match '\n' without Singleline — together those anchor the whole clause to a
        // single line, so no element can hide a second line of SQL behind the last one.
        private static readonly Regex OptionListHead =
            new(@"\ASET +(.+)\z", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex SafeOptionListElement =
            new(@"\A[A-Za-z][A-Za-z0-9_]{0,63} +(?:ON|OFF)\z",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// The db_set_option shape guard: two doors, and the comma door is the narrower one.
        /// <para>NO COMMA → judged by <see cref="SafeOptionSql"/> alone, exactly as before
        /// 2026-09-02. COMMA PRESENT → previously always a refusal (the charset class has no
        /// comma), so this branch is purely additive: it can only turn a refusal into an accept,
        /// never the reverse.</para>
        /// </summary>
        /// <param name="trimmed">The option_sql, already trimmed by the caller.</param>
        private static bool IsSafeOptionSql(string trimmed)
        {
            if (trimmed.IndexOf(',') < 0) return SafeOptionSql.IsMatch(trimmed);

            // The list form does not get a longer budget than the single form.
            if (trimmed.Length > 200) return false;

            var head = OptionListHead.Match(trimmed);
            if (!head.Success) return false;

            var elements = head.Groups[1].Value.Split(',');
            if (elements.Length < 2 || elements.Length > MaxOptionListElements) return false;

            foreach (var raw in elements)
            {
                var element = raw.Trim();
                if (element.Length == 0) return false;              // "A ON,", "A ON,,B ON", "SET ,"
                if (!SafeOptionListElement.IsMatch(element)) return false;
                // Structurally redundant TODAY (the element regex is a strict subset of the
                // charset class) and kept deliberately: it pins the invariant "the list door
                // never admits a character the single door refuses" at the place that would
                // break if SafeOptionListElement is ever widened.
                if (!SafeOptionSql.IsMatch(element)) return false;
            }
            return true;
        }

        // The ONLY db_set_option shape whose pre-apply state is deterministically knowable from
        // offenders_query membership alone: a simple boolean toggle "SET <NAME> ON|OFF". An
        // offender is, by definition, NOT at the option_sql target — so its true prior state is
        // the opposite value. Compound clauses (QUERY_STORE's OPERATION_MODE, PAGE_VERIFY's
        // 3-way enum, AUTO_CREATE_STATISTICS' INCREMENTAL suboption) have more than two states
        // and are NOT invertible from membership alone.
        private static readonly Regex SimpleBooleanOptionSql =
            new(@"\ASET\s+([A-Z_]+)\s+(ON|OFF)\z", RegexOptions.Compiled);

        // ── CreateIndex op: parameter keys + identifier guard ───────────────────
        // The index spec rides request parameters (a missing-index DMV candidate), not the
        // template — same model as the bounded integer value. Every identifier is charset-guarded
        // THEN bracket-quoted, so the rendered DDL is injection-free by construction.
        public const string IndexDatabaseParam = "Index.Database";
        public const string IndexSchemaParam   = "Index.Schema";
        public const string IndexTableParam    = "Index.Table";
        public const string IndexNameParam     = "Index.Name";
        public const string IndexKeyColumnsParam      = "Index.KeyColumns";      // comma-separated, >= 1
        public const string IndexIncludedColumnsParam = "Index.IncludedColumns"; // comma-separated, optional

        // A single SQL identifier we are willing to emit: letters/digits/underscore/space/$/#/@ only.
        // Deliberately conservative — it rejects '.', '[', ']', ''', ';', '-', etc., which is exactly
        // what stops a crafted column/object name from breaking out of the brackets. Missing-index
        // recommendations always have ordinary names; weird names are rejected, not escaped-and-hoped.
        // \A ... \z (not ^ ... $): in .NET, $ also matches BEFORE a trailing newline, so ^...$
        // would accept "Orders\n". \z anchors to the absolute end — no trailing-newline slip.
        private static readonly Regex SafeIdentifier =
            new(@"\A[A-Za-z0-9_@$# ]{1,128}\z", RegexOptions.Compiled);

        /// <summary>
        /// True if a value is a renderable SQL identifier under the guard charset. Public so the
        /// candidate-sourcing layer can pre-filter (drop) candidates whose identifiers would be
        /// rejected at render — the SAME charset, one source of truth — rather than offering a
        /// candidate that always errors, or one a lossy transport would mangle into a different index.
        /// </summary>
        public static bool IsSafeIdentifier(string? value) => SafeIdentifier.IsMatch((value ?? string.Empty).Trim());

        // Charset-guard then bracket-quote (belt + braces: the regex already rejects ']').
        private static bool TryQuoteIdentifier(string? raw, out string quoted, out string error)
        {
            quoted = string.Empty; error = string.Empty;
            var t = (raw ?? string.Empty).Trim();
            if (!SafeIdentifier.IsMatch(t)) { error = $"Unsafe or empty SQL identifier: '{raw}'."; return false; }
            quoted = "[" + t.Replace("]", "]]") + "]";
            return true;
        }

        // Charset-guard then emit an N'...' literal (for the sys.* exists-read predicate).
        private static bool TryQuoteLiteral(string? raw, out string literal, out string error)
        {
            literal = string.Empty; error = string.Empty;
            var t = (raw ?? string.Empty).Trim();
            if (!SafeIdentifier.IsMatch(t)) { error = $"Unsafe or empty SQL identifier: '{raw}'."; return false; }
            literal = "N'" + t.Replace("'", "''") + "'";
            return true;
        }

        private static List<string> SplitColumns(string? csv) =>
            (csv ?? string.Empty)
                .Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries)
                .ToList();

        /// <summary>The fully-guarded index spec resolved from request parameters.</summary>
        public sealed class IndexSpec
        {
            public string Database = "", Schema = "", Table = "", Name = "";
            public List<string> KeyColumns = new();
            public List<string> IncludedColumns = new();
        }

        /// <summary>
        /// Resolves + guards every identifier of a CreateIndex spec from request parameters.
        /// Fails closed if any identifier is unsafe, the database/schema/table/name is missing,
        /// or there is no key column. This is the single guard the renders below depend on.
        /// </summary>
        public static bool TryResolveIndexSpec(IReadOnlyDictionary<string, string> parameters, out IndexSpec spec, out string error)
        {
            spec = new IndexSpec(); error = string.Empty;
            if (parameters is null) { error = "No index parameters supplied."; return false; }

            string Get(string k) => parameters.TryGetValue(k, out var v) ? v : string.Empty;

            foreach (var (val, label, target) in new[]
            {
                (Get(IndexDatabaseParam), "database", 0),
                (Get(IndexSchemaParam),   "schema",   1),
                (Get(IndexTableParam),    "table",    2),
                (Get(IndexNameParam),     "index name", 3),
            })
            {
                if (string.IsNullOrWhiteSpace(val)) { error = $"Missing index {label}."; return false; }
                // Validate each is a safe identifier (quoting happens at render time).
                if (!SafeIdentifier.IsMatch(val.Trim())) { error = $"Unsafe index {label}: '{val}'."; return false; }
                switch (target) { case 0: spec.Database = val.Trim(); break; case 1: spec.Schema = val.Trim(); break; case 2: spec.Table = val.Trim(); break; default: spec.Name = val.Trim(); break; }
            }

            spec.KeyColumns = SplitColumns(Get(IndexKeyColumnsParam));
            spec.IncludedColumns = SplitColumns(Get(IndexIncludedColumnsParam));
            if (spec.KeyColumns.Count == 0) { error = "An index needs at least one key column."; return false; }
            foreach (var c in spec.KeyColumns.Concat(spec.IncludedColumns))
                if (!SafeIdentifier.IsMatch(c)) { error = $"Unsafe index column: '{c}'."; return false; }
            return true;
        }

        /// <summary>Renders the CREATE INDEX DDL from a guarded spec. Plain (non-unique, nonclustered).</summary>
        public static bool TryRenderCreateIndex(IReadOnlyDictionary<string, string> parameters, out string sql, out string error)
        {
            sql = string.Empty;
            if (!TryResolveIndexSpec(parameters, out var s, out error)) return false;

            if (!TryQuoteIdentifier(s.Schema, out var schema, out error)) return false;
            if (!TryQuoteIdentifier(s.Table, out var table, out error)) return false;
            if (!TryQuoteIdentifier(s.Name, out var name, out error)) return false;

            var keys = new List<string>();
            foreach (var c in s.KeyColumns) { if (!TryQuoteIdentifier(c, out var q, out error)) return false; keys.Add(q); }
            var incs = new List<string>();
            foreach (var c in s.IncludedColumns) { if (!TryQuoteIdentifier(c, out var q, out error)) return false; incs.Add(q); }

            var sb = new StringBuilder();
            sb.Append("CREATE INDEX ").Append(name).Append(" ON ").Append(schema).Append('.').Append(table)
              .Append(" (").Append(string.Join(", ", keys)).Append(')');
            if (incs.Count > 0) sb.Append(" INCLUDE (").Append(string.Join(", ", incs)).Append(')');
            sb.Append(';');
            sql = sb.ToString();
            return true;
        }

        /// <summary>Renders the DROP INDEX rollback from a guarded spec (the clean inverse of CREATE).</summary>
        public static bool TryRenderDropIndex(IReadOnlyDictionary<string, string> parameters, out string sql, out string error)
        {
            sql = string.Empty;
            if (!TryResolveIndexSpec(parameters, out var s, out error)) return false;
            if (!TryQuoteIdentifier(s.Schema, out var schema, out error)) return false;
            if (!TryQuoteIdentifier(s.Table, out var table, out error)) return false;
            if (!TryQuoteIdentifier(s.Name, out var name, out error)) return false;
            sql = $"DROP INDEX {name} ON {schema}.{table};";
            return true;
        }

        /// <summary>
        /// Renders the read that confirms whether the named index exists on the table — the
        /// snapshot (already-present?) and the post-apply verify. Identifiers go in as guarded
        /// N'...' literals against sys.* catalog views (a read; Validate-Safe).
        /// </summary>
        public static bool TryRenderIndexExistsRead(IReadOnlyDictionary<string, string> parameters, out string sql, out string error)
        {
            sql = string.Empty;
            if (!TryResolveIndexSpec(parameters, out var s, out error)) return false;
            if (!TryQuoteLiteral(s.Schema, out var schema, out error)) return false;
            if (!TryQuoteLiteral(s.Table, out var table, out error)) return false;
            if (!TryQuoteLiteral(s.Name, out var name, out error)) return false;
            sql =
                "SELECT CASE WHEN EXISTS (SELECT 1 FROM sys.indexes i " +
                "INNER JOIN sys.objects o ON i.object_id = o.object_id " +
                "INNER JOIN sys.schemas sc ON o.schema_id = sc.schema_id " +
                $"WHERE sc.name = {schema} AND o.name = {table} AND i.name = {name}) THEN 1 ELSE 0 END;";
            return true;
        }

        /// <summary>
        /// Request parameter a caller sets to say "yes, I mean to move this setting off its
        /// recommended value" — the Undo and Revert-to-history buttons, which exist to put a
        /// server back the way it was, and (ruling 7) the acknowledged off-recommended route.
        /// Namespaced so no corpus <c>value_param</c> can collide with it. Absent (the default)
        /// the executor refuses that write: see <see cref="IsRegressionFromRecommended"/>.
        /// </summary>
        public const string AcknowledgeRegressionParam = "Configuration.AcknowledgeRegression";

        /// <summary>
        /// Ruling 7 (DECISIONS 2026-08-25 17:33): the operator's stated REASON for moving a setting
        /// off its recommended value. Required alongside <see cref="AcknowledgeRegressionParam"/> —
        /// a tick on its own is a click, and what the ledger has to carry is why a compliant server
        /// was deliberately moved. Written into the RemediationApproved entry by the runner.
        /// </summary>
        public const string RegressionIntentParam = "Configuration.RegressionIntent";

        /// <summary>
        /// True when applying <paramref name="target"/> would move a server that is ALREADY at
        /// this op's declared <see cref="RemediationOperation.RecommendedValue"/> off it — i.e.
        /// the "fix" would undo the fix. Pure function of three inputs so it is testable without
        /// a server, and used by BOTH the preview and the apply path so the two cannot diverge.
        ///
        /// <para>Always false when the op declares no recommended value: with no declared
        /// direction there is no defensible definition of "worse", and inventing one would be a
        /// guess. Those templates instead ship no pre-filled target at all (see
        /// <see cref="RemediationOperation.RecommendedValue"/>).</para>
        /// </summary>
        public static bool IsRegressionFromRecommended(RemediationOperation? op, int? currentValue, int target) =>
            op?.RecommendedValue is int recommended
            && currentValue == recommended
            && target != recommended;

        /// <summary>
        /// Whether the caller explicitly acknowledged a deliberate move off the recommended value
        /// AND stated why. Anything other than an explicit "true" plus a non-blank intent reads as
        /// not acknowledged, so a malformed value, a missing tick or a blank reason all fail closed.
        ///
        /// <para>Ruling 7 added the intent half. A tick on its own is a click; the ledger has to
        /// carry the reason a compliant server was deliberately moved off its recommended value.
        /// The Undo and Revert-to-history buttons state their own intent, so they travel this same
        /// road rather than a private one.</para>
        /// </summary>
        public static bool IsRegressionAcknowledged(IReadOnlyDictionary<string, string>? parameters) =>
            parameters is not null
            && parameters.TryGetValue(AcknowledgeRegressionParam, out var raw)
            && bool.TryParse(raw, out var ack)
            && ack
            && !string.IsNullOrWhiteSpace(ReadRegressionIntent(parameters));

        /// <summary>
        /// The stated reason, trimmed, or empty when none was supplied. Never null, so a call site
        /// cannot forget the null case and write "null" into the ledger.
        /// </summary>
        public static string ReadRegressionIntent(IReadOnlyDictionary<string, string>? parameters) =>
            parameters is not null && parameters.TryGetValue(RegressionIntentParam, out var intent)
                ? (intent ?? string.Empty).Trim()
                : string.Empty;

        /// <summary>
        /// The one phrase that identifies a regression refusal, used to BUILD the sentence below and
        /// to RECOGNISE it on the page. One definition, so the acknowledged route can never appear
        /// beside a refusal it is not the answer to, and can never fail to appear beside one it is.
        /// </summary>
        public const string RegressionRefusalMarker = "is already at the recommended value";

        /// <summary>
        /// True when <paramref name="message"/> is a regression refusal — the exact case ruling 7's
        /// acknowledged route answers.
        /// </summary>
        public static bool LooksLikeRegressionRefusal(string? message) =>
            !string.IsNullOrEmpty(message)
            && message!.Contains(RegressionRefusalMarker, StringComparison.Ordinal);

        /// <summary>
        /// The refusal an operator reads when <see cref="IsRegressionFromRecommended"/> fires.
        /// Short sentences, the consequence stated: what the server is at, what was asked for,
        /// and what is available next.
        ///
        /// <para>It used to end "Use Undo or Revert to history if you mean to change it back",
        /// which named two controls that are not on screen in the exact case this refusal fires
        /// for. Undo renders only after a verified apply in the same session; Revert to history
        /// renders only when the audit ledger already holds a pre-change value for this template on
        /// this server. On a compliant server SQLTriage has never remediated, neither existed, so
        /// the refusal was directing the operator to something that was not there. Ruling 7 settled
        /// the product question that left open: there is now an acknowledged route, and this
        /// sentence names it.</para>
        /// </summary>
        public static string DescribeRegressionRefusal(RemediationOperation op, int target)
        {
            var name = string.IsNullOrWhiteSpace(op.ConfigName) ? "This setting" : $"'{op.ConfigName}'";
            return $"Refused: {name} {RegressionRefusalMarker} {op.RecommendedValue}. " +
                   $"Applying {target} would undo this fix and still cost a change credit. " +
                   "Nothing was changed and nothing was charged. " +
                   "If you mean to move it off the recommended value, state why and tick the acknowledgement below. " +
                   "The change and your reason are both written to the audit ledger.";
        }

        /// <summary>
        /// Resolves and bounds-checks the target value from the request parameters.
        /// Returns false with a human error when the op's value parameter is missing,
        /// not an integer, or outside [MinValue, MaxValue].
        /// </summary>
        public static bool TryResolveValue(
            RemediationOperation op, IReadOnlyDictionary<string, string> parameters,
            out int value, out string error)
            => TryResolveValue(op, parameters, null, out value, out error);

        /// <summary>
        /// Resolves and bounds-checks the target value, with a host ceiling the CALLER has already
        /// read where one applies (Phase-2 item 6a).
        ///
        /// <para>⚠ THIS OVERLOAD CANNOT GUARANTEE ORDER, and a comment here used to claim it could:
        /// it said "a refusal here happens before any read or write reaches the server". For
        /// 'max server memory (MB)' the caller had to read the host FIRST to fill
        /// <paramref name="physicalMemoryMb"/>, so the read had already happened — proved live at
        /// the fix-round gate, MaxServerMemoryMb="banana" refusing while sys.dm_os_sys_info's
        /// execution_count went 0 -> 1. Use <see cref="ResolveValueStagedAsync"/> on any path that
        /// has a server: it owns the order, and only its stage-1 refusals claim nothing ran.</para>
        /// </summary>
        public static bool TryResolveValue(
            RemediationOperation op, IReadOnlyDictionary<string, string> parameters,
            int? physicalMemoryMb,
            out int value, out string error)
        {
            if (!TryTakeRawValue(op, parameters, out var raw, out value, out error, out var done)) return done;
            return RemediationValueBounds.TryResolve(op, raw, physicalMemoryMb, out value, out error);
        }

        /// <summary>
        /// STAGE 1 ONLY: strict parse and the static range, with no host reading and no way to reach
        /// a server. Every path resolves through this before anything else happens.
        /// </summary>
        public static bool TryResolveValueStatic(
            RemediationOperation op, IReadOnlyDictionary<string, string> parameters,
            out int value, out string error)
        {
            if (!TryTakeRawValue(op, parameters, out var raw, out value, out error, out var done)) return done;
            return RemediationValueBounds.TryResolveStatic(op, raw, out value, out error);
        }

        /// <summary>
        /// THE ORDERED RESOLUTION every server-facing path uses (fix round, gate blocker 1). Stage 1
        /// on text alone; the host reader invoked only for a value that already parsed and already
        /// passed the static range, and only for the one host-relative option.
        /// </summary>
        public static async Task<StagedValueResolution> ResolveValueStagedAsync(
            RemediationOperation op, IReadOnlyDictionary<string, string> parameters,
            Func<CancellationToken, Task<int?>>? readHostMemoryMb, CancellationToken ct)
        {
            if (!TryTakeRawValue(op, parameters, out var raw, out var value, out var error, out var done))
                return new StagedValueResolution(done, value, error, HostCheckRan: false, HostMemoryMb: null);

            return await RemediationValueBounds
                .ResolveStagedAsync(op, raw, readHostMemoryMb, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// The shared prologue: the malformed-operation refusals and the shipped fixed-value
        /// shortcut. Returns false when the caller should stop and use <paramref name="done"/> as
        /// its answer; true when <paramref name="raw"/> is the operator text to bounds-check.
        /// </summary>
        private static bool TryTakeRawValue(
            RemediationOperation op, IReadOnlyDictionary<string, string> parameters,
            out string? raw, out int value, out string error, out bool done)
        {
            raw = null; value = 0; error = string.Empty; done = false;

            if (op is null) { error = "No operation on the template."; return false; }
            if (op.ValueFixed.HasValue)
            {
                // A fixed target (corpus `value_fixed`) — shipped, no operator input, nothing to
                // bounds-check against a request parameter.
                value = op.ValueFixed.Value;
                done = true;
                return false;
            }
            if (string.IsNullOrWhiteSpace(op.ValueParam)) { error = "Operation has no value parameter."; return false; }

            // The raw text, exactly as the operator typed it. Deliberately NOT pre-parsed here:
            // RemediationValueBounds owns strict parsing so "8 GB", "1,024" and "8; DROP DATABASE"
            // are refused as garbage with a message naming the allowed range, rather than being
            // rejected by a bare TryParse that says only "requires an integer".
            parameters?.TryGetValue(op.ValueParam, out raw);
            return true;
        }

        /// <summary>
        /// Renders the apply T-SQL for a (bounds-checked) value. Returns false with an
        /// error if the op is malformed (unknown kind / unsafe config name).
        /// </summary>
        public static bool TryRender(RemediationOperation op, int value, out string sql, out string error)
        {
            sql = string.Empty; error = string.Empty;
            if (op is null) { error = "No operation."; return false; }
            switch (op.OpKind)
            {
                case RemediationOpKind.SpConfigure:
                    if (string.IsNullOrWhiteSpace(op.ConfigName) || !SafeConfigName.IsMatch(op.ConfigName))
                    {
                        error = $"Unsafe or empty sp_configure option name: '{op.ConfigName}'.";
                        return false;
                    }
                    var name = op.ConfigName.Replace("'", "''");
                    var v = value.ToString(CultureInfo.InvariantCulture);
                    var sb = new StringBuilder();
                    if (op.AdvancedOption)
                        sb.Append("EXEC sp_configure 'show advanced options', 1; RECONFIGURE; ");
                    sb.Append("EXEC sp_configure '").Append(name).Append("', ").Append(v).Append("; RECONFIGURE;");
                    sql = sb.ToString();
                    return true;
                default:
                    error = $"Unsupported operation kind '{op.OpKind}'.";
                    return false;
            }
        }

        /// <summary>
        /// Renders the read of the CONFIGURED value (<c>sys.configurations.value</c>) — the column
        /// <c>sp_configure</c> actually writes, and therefore the column a rollback must capture,
        /// re-apply and confirm against.
        ///
        /// <para>⚠ THIS IS NOT THE VERIFY READ, and the split is the whole point of Phase-2 item 2.1.
        /// <see cref="TryRenderRead"/> reads <c>value_in_use</c>, the value the ENGINE is using, and
        /// that stays the post-change verify: a setting the engine coerced or ignored has not taken
        /// effect, and reporting it verified would be a lie. But <c>value_in_use</c> is the wrong
        /// thing to CAPTURE and the wrong thing to CONFIRM a rollback against. Spike S2 section 3.1
        /// proved both halves live on <c>min server memory (MB)</c>, which SQL Server pins at a 16 MB
        /// floor: the confirming read compared <c>value_in_use</c> (16) against a <c>value_in_use</c>
        /// snapshot (16), so "Confirmed" was guaranteed BEFORE the inverse ran, and the inverse
        /// re-applied 16 where the configured value the operator had was 0. The engine reported a
        /// confirmed rollback and left the server configured differently from how it found it.</para>
        ///
        /// <para>A confirming read has to read the column the inverse SETS. That is this one.</para>
        /// </summary>
        public static bool TryRenderConfiguredRead(RemediationOperation op, out string sql, out string error)
            => TryRenderConfiguredRead(op?.ConfigName, out sql, out error);

        /// <summary>
        /// The configured-value read for a BARE option name — used for the options a rendered batch
        /// touches beside its own target (the <c>show advanced options</c> prelude), which have no
        /// <see cref="RemediationOperation"/> of their own. Same charset guard, same escaping.
        /// </summary>
        public static bool TryRenderConfiguredRead(string? configName, out string sql, out string error)
        {
            sql = string.Empty; error = string.Empty;
            var name = (configName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name) || !SafeConfigName.IsMatch(name))
            {
                error = $"Unsafe or empty sp_configure option name: '{configName}'.";
                return false;
            }
            sql = "SELECT value FROM sys.configurations WHERE name = '" + name.Replace("'", "''") + "';";
            return true;
        }

        /// <summary>
        /// Renders the <c>sp_configure</c> write for a BARE option name and value — used to restore
        /// an option the rendered batch touched as a side effect (the <c>show advanced options</c>
        /// prelude), which has no <see cref="RemediationOperation"/> of its own. No prelude is
        /// emitted: <c>show advanced options</c> is not itself an advanced option, and the caller
        /// restores side effects only after the target is already back.
        /// </summary>
        public static bool TryRenderConfigureWrite(string? configName, int value, out string sql, out string error)
        {
            sql = string.Empty; error = string.Empty;
            var name = (configName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name) || !SafeConfigName.IsMatch(name))
            {
                error = $"Unsafe or empty sp_configure option name: '{configName}'.";
                return false;
            }
            sql = "EXEC sp_configure '" + name.Replace("'", "''") + "', "
                + value.ToString(CultureInfo.InvariantCulture) + "; RECONFIGURE;";
            return true;
        }

        /// <summary>
        /// Renders the read query that captures/verifies the op's current value — DERIVED from
        /// the op (single-statement, no free-form template text). For a Configuration template
        /// the executor uses this instead of any template-supplied snapshot/verify SQL, so an
        /// overlay cannot smuggle a write through the read path (the prior risk: Validate()
        /// exempts a whole batch if any `SELECT FROM sys.` is present). Injection-free: the
        /// config name is charset-guarded and single-quote escaped.
        ///
        /// <para>⚠ THIS IS THE EFFECTIVE VALUE (<c>value_in_use</c>) — what the ENGINE is using. It
        /// is the right read for the post-change VERIFY, and the wrong read for capture and for
        /// confirming a rollback: see <see cref="TryRenderConfiguredRead"/> and spike S2 section
        /// 3.1.</para>
        /// </summary>
        public static bool TryRenderRead(RemediationOperation op, out string sql, out string error)
        {
            sql = string.Empty; error = string.Empty;
            if (op is null) { error = "No operation."; return false; }
            switch (op.OpKind)
            {
                case RemediationOpKind.SpConfigure:
                    if (string.IsNullOrWhiteSpace(op.ConfigName) || !SafeConfigName.IsMatch(op.ConfigName))
                    {
                        error = $"Unsafe or empty sp_configure option name: '{op.ConfigName}'.";
                        return false;
                    }
                    sql = "SELECT value_in_use FROM sys.configurations WHERE name = '"
                        + op.ConfigName.Replace("'", "''") + "';";
                    return true;
                default:
                    error = $"Unsupported operation kind '{op.OpKind}'.";
                    return false;
            }
        }

        // ── DbSetOption op: per-database ALTER DATABASE ... SET, target list from offenders_query ──

        /// <summary>
        /// Renders <c>ALTER DATABASE [name] &lt;optionSql&gt;;</c> for ONE database. The database
        /// name is charset-guarded then bracket-quoted (same guard CreateIndex uses); optionSql
        /// is shipped corpus text, shape-guarded defensively by <see cref="IsSafeOptionSql"/>.
        /// Used identically by preview, apply, and rollback (rollback passes the inverted
        /// optionSql) — one render, one source of truth, so preview==apply parity holds by
        /// construction.
        /// <para>Since 2026-09-02 optionSql may also be the comma-separated multi-option form
        /// ("SET A ON, B ON"), which is still ONE auto-committed ALTER on ONE database — the
        /// render, the executor's per-database loop and the exec guard are all unchanged by it.
        /// It is NOT invertible, deliberately: see <see cref="TryInvertBooleanOptionSql"/>.</para>
        /// </summary>
        public static bool TryRenderDbSetOption(string databaseName, string optionSql, out string sql, out string error)
        {
            sql = string.Empty; error = string.Empty;
            if (string.IsNullOrWhiteSpace(optionSql) || !IsSafeOptionSql(optionSql.Trim()))
            {
                error = $"Unsafe or empty db_set_option option_sql: '{optionSql}'.";
                return false;
            }
            if (!TryQuoteIdentifier(databaseName, out var db, out error)) return false;
            sql = $"ALTER DATABASE {db} {optionSql.Trim()};";
            return true;
        }

        /// <summary>
        /// Inverts a simple boolean db_set_option clause ("SET &lt;NAME&gt; ON|OFF") to its
        /// opposite. This is the ONLY shape whose pre-apply state is deterministically knowable
        /// from offenders_query membership alone (see <see cref="SimpleBooleanOptionSql"/>).
        /// Returns false (no fabricated inverse) for compound clauses.
        /// <para>⚠ NOT widened alongside <see cref="IsSafeOptionSql"/> on 2026-09-02, and that is a
        /// correctness decision rather than a scope one. Membership of offenders_query proves the
        /// prior state of ONE option: an offender is not at the target, so it was at the opposite.
        /// For a LIST that inference collapses — a database can be an offender because
        /// AUTO_UPDATE_STATISTICS is OFF while AUTO_UPDATE_STATISTICS_ASYNC is ALREADY ON, and
        /// inverting the whole clause would set the async option to a state it was never in. The
        /// multi-option form therefore lands on the executor's honest "no inverse to run" arm.</para>
        /// </summary>
        public static bool TryInvertBooleanOptionSql(string optionSql, out string inverted)
        {
            inverted = string.Empty;
            var m = SimpleBooleanOptionSql.Match((optionSql ?? string.Empty).Trim());
            if (!m.Success) return false;
            var name = m.Groups[1].Value;
            var opposite = string.Equals(m.Groups[2].Value, "ON", StringComparison.Ordinal) ? "OFF" : "ON";
            inverted = $"SET {name} {opposite}";
            return true;
        }

        // ── AgentAlertPack op: operator + severity/error alerts + email notifications ──
        // Parameter keys the request carries (no bound integer value — a name and an email).
        public const string AgentAlertOperatorNameParam  = "AgentAlertPack.OperatorName";
        public const string AgentAlertOperatorEmailParam = "AgentAlertPack.OperatorEmail";
        public const string DefaultOperatorName = "SQLTriage DBA Team";

        // Severities 19-25 (the "must fix now" band SQL Server never raises via SSMS UI alone)
        // plus the three I/O/checksum corruption error numbers. Alert names are fixed text, never
        // user input, so no charset guard is needed for them (unlike ConfigName, which IS
        // semi-trusted overlay text) — these are compiled-in constants.
        public static readonly IReadOnlyList<int> AgentAlertSeverities = new[] { 19, 20, 21, 22, 23, 24, 25 };
        public static readonly IReadOnlyList<int> AgentAlertCorruptionErrorNumbers = new[] { 823, 824, 825 };

        /// <summary>The exact alert name for a severity alert, e.g. "SQLTriage Alert - Severity 19".</summary>
        public static string AgentAlertNameForSeverity(int severity) => $"SQLTriage Alert - Severity {severity}";

        /// <summary>The exact alert name for an error-number alert, e.g. "SQLTriage Alert - Error 823".</summary>
        public static string AgentAlertNameForErrorNumber(int errorNumber) => $"SQLTriage Alert - Error {errorNumber}";

        /// <summary>All 10 alert names the pack manages, in a fixed, stable order.</summary>
        public static IReadOnlyList<string> AllAgentAlertNames()
        {
            var names = new List<string>(10);
            foreach (var sev in AgentAlertSeverities) names.Add(AgentAlertNameForSeverity(sev));
            foreach (var err in AgentAlertCorruptionErrorNumbers) names.Add(AgentAlertNameForErrorNumber(err));
            return names;
        }

        // Deliberately conservative RFC-5322-ish shape check (not a full-spec validator): one '@',
        // at least one '.' after it, no whitespace, no quotes/semicolons — good enough to reject
        // garbage AND to keep the literal safe to single-quote-escape before embedding.
        private static readonly Regex BasicEmailShape =
            new(@"\A[^\s@'"";]+@[^\s@'"";]+\.[^\s@'"";]+\z", RegexOptions.Compiled);

        /// <summary>True if <paramref name="email"/> has a plausible email shape (basic, not RFC-complete).</summary>
        public static bool IsPlausibleEmail(string? email) => BasicEmailShape.IsMatch((email ?? string.Empty).Trim());

        /// <summary>The fully-guarded operator name/email resolved from request parameters.</summary>
        public sealed class AgentAlertPackSpec
        {
            public string OperatorName = DefaultOperatorName;
            public string OperatorEmail = string.Empty;
        }

        /// <summary>
        /// Resolves + guards the operator name/email from request parameters. Fails closed if the
        /// email is missing or not a plausible shape. The name is free text (an operator display
        /// name, e.g. "SQLTriage DBA Team") but is still charset-guarded via <see cref="TryQuoteLiteral"/>
        /// at render time — this method only requires it be non-empty after defaulting.
        /// </summary>
        public static bool TryResolveAgentAlertPackSpec(
            IReadOnlyDictionary<string, string> parameters, out AgentAlertPackSpec spec, out string error)
        {
            spec = new AgentAlertPackSpec(); error = string.Empty;
            if (parameters is null) { error = "No parameters supplied."; return false; }

            spec.OperatorName = parameters.TryGetValue(AgentAlertOperatorNameParam, out var n) && !string.IsNullOrWhiteSpace(n)
                ? n.Trim() : DefaultOperatorName;

            if (!parameters.TryGetValue(AgentAlertOperatorEmailParam, out var email) || string.IsNullOrWhiteSpace(email))
            {
                error = "Operator email is required.";
                return false;
            }
            email = email.Trim();
            if (!IsPlausibleEmail(email))
            {
                error = $"'{email}' is not a plausible email address.";
                return false;
            }
            spec.OperatorEmail = email;
            return true;
        }

        // Free-text literal quoting for names/emails: single-quote-escape only (no identifier
        // charset restriction — an operator name/email is a data value inside N'...', never an
        // identifier or object name, so it cannot break out of the bracket/schema surface the
        // identifier guard defends). Still rejects an embedded single quote by escaping it, so
        // there is no way to terminate the literal early.
        private static string QuoteDataLiteral(string raw) => "N'" + (raw ?? string.Empty).Replace("'", "''") + "'";

        /// <summary>
        /// Renders the ONE deterministic, idempotent apply batch: create the operator (if absent),
        /// then for each of the 10 alerts create it AND link the email notification, but ONLY when
        /// NO alert already exists for that CONDITION (severity or message_id). Every CREATE is
        /// guarded <c>IF NOT EXISTS</c>, so re-running the batch on an already-configured server is
        /// a no-op. The guard is keyed on the condition, not our alert name: SQL Server's
        /// sp_add_alert refuses ("already defined on this condition", msg 14501) a SECOND alert for
        /// a severity/message_id that already has one under ANY name — verified live against a
        /// server pre-configured by an earlier best-practice script under a different naming scheme
        /// (severity/error alerts are a decades-old, widely-circulated recommendation, so a server
        /// already having them under some other name is the COMMON case, not an edge case). When a
        /// condition is already covered by an existing alert, this batch leaves it untouched —
        /// it never renames, duplicates, or fights an existing alert.
        /// </summary>
        public static bool TryRenderAgentAlertPackApply(
            IReadOnlyDictionary<string, string> parameters, out string sql, out string error)
        {
            sql = string.Empty;
            if (!TryResolveAgentAlertPackSpec(parameters, out var spec, out error)) return false;

            var opName = QuoteDataLiteral(spec.OperatorName);
            var opEmail = QuoteDataLiteral(spec.OperatorEmail);

            var sb = new StringBuilder();
            sb.AppendLine("IF NOT EXISTS (SELECT 1 FROM msdb.dbo.sysoperators WHERE name = " + opName + ")");
            sb.AppendLine("    EXEC msdb.dbo.sp_add_operator @name = " + opName + ", @enabled = 1, @email_address = " + opEmail + ";");

            foreach (var name in AllAgentAlertNames())
            {
                var quotedName = QuoteDataLiteral(name);
                var conditionPredicate = ConditionPredicate(name);
                // Guard by CONDITION (not name): sp_add_alert throws msg 14501 if any alert already
                // claims this severity/message_id, regardless of its name.
                sb.AppendLine("IF NOT EXISTS (SELECT 1 FROM msdb.dbo.sysalerts WHERE " + conditionPredicate + ")");
                // sp_add_alert has NO @notify_email_operator_name parameter (verified live against
                // SQL Server 2017/2022 — an earlier draft assumed one and every alert create failed
                // with "is not a parameter for procedure sp_add_alert"). The alert->operator email
                // link is created entirely by the separate sp_add_notification call below.
                if (name.Contains("Severity"))
                {
                    var sev = ParseTrailingNumber(name);
                    sb.AppendLine("    EXEC msdb.dbo.sp_add_alert @name = " + quotedName + ", @severity = " + sev +
                                  ", @delay_between_responses = 60;");
                }
                else
                {
                    var errNum = ParseTrailingNumber(name);
                    sb.AppendLine("    EXEC msdb.dbo.sp_add_alert @name = " + quotedName + ", @message_id = " + errNum +
                                  ", @delay_between_responses = 60;");
                }
                // The notification is linked ONLY for the alert THIS batch owns (by our exact name)
                // — never attach our operator to a pre-existing alert under a different name (that
                // alert is not ours to modify; its own notification wiring, if any, is untouched).
                sb.AppendLine("IF NOT EXISTS (SELECT 1 FROM msdb.dbo.sysalerts a " +
                              "INNER JOIN msdb.dbo.sysnotifications n ON a.id = n.alert_id " +
                              "INNER JOIN msdb.dbo.sysoperators o ON n.operator_id = o.id " +
                              "WHERE a.name = " + quotedName + " AND o.name = " + opName + " AND n.notification_method = 1) " +
                              "AND EXISTS (SELECT 1 FROM msdb.dbo.sysalerts WHERE name = " + quotedName + ")");
                sb.AppendLine("    EXEC msdb.dbo.sp_add_notification @alert_name = " + quotedName +
                              ", @operator_name = " + opName + ", @notification_method = 1;");
            }
            sql = sb.ToString();
            return true;
        }

        // Every alert name ends in " <number>" (severity or error id) — pull it back out for the
        // @severity / @message_id parameter. Safe: names are compiled-in constants, never user text.
        private static int ParseTrailingNumber(string alertName)
        {
            var idx = alertName.LastIndexOf(' ');
            return int.Parse(alertName[(idx + 1)..], CultureInfo.InvariantCulture);
        }

        // The condition (severity or message_id) predicate for one of the 10 fixed alert names —
        // used to check "does ANY alert already cover this condition" (sysalerts has NO unique
        // constraint on name, only SQL Server's own runtime check on condition uniqueness).
        private static string ConditionPredicate(string alertName)
        {
            var n = ParseTrailingNumber(alertName);
            return alertName.Contains("Severity") ? "severity = " + n : "message_id = " + n;
        }

        /// <summary>
        /// Renders the read-only snapshot query: one row per one of the 10 conditions (severity or
        /// message_id) that ALREADY has an alert covering it before apply (under ANY name — see
        /// <see cref="TryRenderAgentAlertPackApply"/>), keyed by OUR alert name for that condition,
        /// plus a synthetic "(operator)" row if the operator already exists. The executor uses the
        /// absence of a name from this result set to know apply created (or WOULD create, had the
        /// condition been free) that specific object, so rollback/verify reason about the SAME
        /// identity apply used.
        /// </summary>
        public static string RenderAgentAlertPackSnapshot(IReadOnlyDictionary<string, string> parameters)
        {
            TryResolveAgentAlertPackSpec(parameters, out var spec, out _); // best-effort name for the operator row
            var opName = QuoteDataLiteral(string.IsNullOrWhiteSpace(spec.OperatorName) ? DefaultOperatorName : spec.OperatorName);
            var sb = new StringBuilder();
            sb.Append("SELECT '(operator)' AS existing_name WHERE EXISTS (SELECT 1 FROM msdb.dbo.sysoperators WHERE name = ")
              .Append(opName).Append(')');
            foreach (var name in AllAgentAlertNames())
            {
                sb.Append(" UNION ALL SELECT ").Append(QuoteDataLiteral(name))
                  .Append(" WHERE EXISTS (SELECT 1 FROM msdb.dbo.sysalerts WHERE ").Append(ConditionPredicate(name)).Append(')');
            }
            sb.Append(';');
            return sb.ToString();
        }

        /// <summary>
        /// Renders the read-only verify query: ok=1 iff the operator exists AND every one of the
        /// 10 CONDITIONS (severity/message_id) is covered by SOME alert (ours or pre-existing) AND
        /// every one of OUR 10 named alerts that exists has an email notification to our operator
        /// (an alert this batch left untouched under another name is not required to notify OUR
        /// operator — it is not ours to require anything of).
        /// </summary>
        public static bool TryRenderAgentAlertPackVerify(
            IReadOnlyDictionary<string, string> parameters, out string sql, out string error)
        {
            sql = string.Empty;
            if (!TryResolveAgentAlertPackSpec(parameters, out var spec, out error)) return false;
            var opName = QuoteDataLiteral(spec.OperatorName);
            var names = string.Join(", ", AllAgentAlertNames().Select(QuoteDataLiteral));
            var conditionChecks = string.Join(" AND ",
                AllAgentAlertNames().Select(n => "EXISTS (SELECT 1 FROM msdb.dbo.sysalerts WHERE " + ConditionPredicate(n) + ")"));
            sql =
                "SELECT CASE WHEN " +
                "EXISTS (SELECT 1 FROM msdb.dbo.sysoperators WHERE name = " + opName + ") AND " +
                conditionChecks + " AND " +
                "(SELECT COUNT(DISTINCT a.name) FROM msdb.dbo.sysalerts a " +
                "INNER JOIN msdb.dbo.sysnotifications n ON a.id = n.alert_id " +
                "INNER JOIN msdb.dbo.sysoperators o ON n.operator_id = o.id " +
                "WHERE a.name IN (" + names + ") AND o.name = " + opName + " AND n.notification_method = 1) " +
                "= (SELECT COUNT(*) FROM msdb.dbo.sysalerts WHERE name IN (" + names + ")) " +
                "THEN 1 ELSE 0 END;";
            return true;
        }

        /// <summary>
        /// Renders the rollback batch from the snapshot's pre-existing-name set: drops
        /// notification+alert for every one of the 10 alert names ABSENT from
        /// <paramref name="preExistingNames"/> (apply created them under our own name), and drops
        /// the operator only if <c>"(operator)"</c> is absent (apply created the operator too).
        /// Never touches a condition that was already covered before apply (whether under our name
        /// or another) — the whole point of a snapshot-driven rollback. Safe even though the
        /// snapshot set is keyed by "our name, if that condition was already covered": if the
        /// condition was covered by an alert under ANOTHER name, our own name was never created, so
        /// the <c>IF EXISTS ... WHERE name = ours</c> guard below is naturally false for it too.
        /// </summary>
        public static bool TryRenderAgentAlertPackRollback(
            IReadOnlyDictionary<string, string> parameters, IReadOnlySet<string> preExistingNames,
            out string sql, out string error)
        {
            sql = string.Empty;
            if (!TryResolveAgentAlertPackSpec(parameters, out var spec, out error)) return false;
            var opName = QuoteDataLiteral(spec.OperatorName);

            var sb = new StringBuilder();
            foreach (var name in AllAgentAlertNames())
            {
                if (preExistingNames.Contains(name)) continue; // condition already covered before apply — never touch it
                var quotedName = QuoteDataLiteral(name);
                sb.AppendLine("IF EXISTS (SELECT 1 FROM msdb.dbo.sysalerts WHERE name = " + quotedName + ")");
                sb.AppendLine("BEGIN");
                sb.AppendLine("    IF EXISTS (SELECT 1 FROM msdb.dbo.sysalerts a INNER JOIN msdb.dbo.sysnotifications n ON a.id = n.alert_id " +
                              "INNER JOIN msdb.dbo.sysoperators o ON n.operator_id = o.id WHERE a.name = " + quotedName + " AND o.name = " + opName + ")");
                sb.AppendLine("        EXEC msdb.dbo.sp_delete_notification @alert_name = " + quotedName + ", @operator_name = " + opName + ";");
                sb.AppendLine("    EXEC msdb.dbo.sp_delete_alert @name = " + quotedName + ";");
                sb.AppendLine("END");
            }
            if (!preExistingNames.Contains("(operator)"))
            {
                sb.AppendLine("IF EXISTS (SELECT 1 FROM msdb.dbo.sysoperators WHERE name = " + opName + ")");
                sb.AppendLine("    EXEC msdb.dbo.sp_delete_operator @name = " + opName + ";");
            }
            sql = sb.ToString();
            return true;
        }

        /// <summary>
        /// Renders the read-only Express/no-Agent gate probe. EngineEdition 4 = Express, which
        /// ships no SQL Server Agent — sp_add_alert/sp_add_operator would exist as stored procs
        /// (they live in msdb regardless of edition) but the Agent service to fire them does not,
        /// so applying the pack there would silently do nothing useful. Result: 1 = Agent available
        /// (pack may render/apply), 0 = unavailable (template must be disabled/greyed with a reason).
        /// </summary>
        public const string AgentAvailabilityProbe =
            "SELECT CASE WHEN CAST(SERVERPROPERTY('EngineEdition') AS int) = 4 THEN 0 ELSE 1 END;";

        /// <summary>
        /// Renders THE STATEMENT THE EXECUTOR WILL RUN for this operation and these operator
        /// parameters — the parameter-resolved text, not a representative one.
        ///
        /// <para>⚠ WHY THIS EXISTS (fix round 1, gate blocker 1). The apply path's proposal record
        /// used to attest <see cref="TryRenderForClassification"/>'s output, which is deliberately
        /// VALUE-INDEPENDENT: it renders the op's lower bound, ignores the operator's parameters
        /// entirely, and its own header says it is never executed. Live applies of MAXDOP=4,
        /// CTFP=42 and OPTIMIZEFORADHOC=1 therefore wrote three signed <c>RemediationProposed</c>
        /// entries all attesting <c>..., 0; RECONFIGURE;</c>. A statement nobody ran, in an
        /// HMAC-chained audit ledger, described as the authorised one. Nothing may quote SQL as
        /// "what runs" unless it IS what runs.</para>
        ///
        /// <para>THE VALUE IS THE EXECUTOR'S VALUE, and that is a property of
        /// <see cref="RemediationValueBounds"/> rather than a coincidence: stage 2
        /// (<c>TryApplyHostCeiling</c>) can only REFUSE a value, never change one, so the stage-1
        /// resolution used here is byte-identical to the <c>staged.Value</c> the executor renders
        /// from — or the executor refuses before anything runs, and no statement exists to attest.
        /// Stage 1 is also pure: it cannot reach a server, so building this record costs no read.</para>
        ///
        /// <para>Only <see cref="RemediationOpKind.SpConfigure"/> can answer here. Every other kind
        /// composes its statement at execution time out of what the server itself reports (the
        /// offenders list for a per-database SET, the DMV candidate for an index, msdb's own job
        /// rows), so no caller can know the text in advance — <paramref name="whyNot"/> says which,
        /// in the operator's words, and the caller must not quote SQL for it.</para>
        /// </summary>
        public static bool TryRenderExecutedStatement(
            RemediationOperation? op, IReadOnlyDictionary<string, string>? parameters,
            out string sql, out string whyNot)
        {
            sql = string.Empty;
            whyNot = string.Empty;
            if (op is null)
            {
                whyNot = "this template carries no structured operation";
                return false;
            }
            if (op.OpKind != RemediationOpKind.SpConfigure)
            {
                whyNot = $"a '{op.OpKind}' fix composes its statement while it runs, from what the "
                       + "server itself reports, so the exact text is not knowable before it runs";
                return false;
            }
            if (!TryResolveValueStatic(op, parameters ?? EmptyParameters, out var value, out var resolveError))
            {
                whyNot = string.IsNullOrWhiteSpace(resolveError)
                    ? "the value for this fix could not be resolved"
                    : resolveError;
                return false;
            }
            if (!TryRender(op!, value, out sql, out var renderError))
            {
                sql = string.Empty;
                whyNot = string.IsNullOrWhiteSpace(renderError)
                    ? "the statement could not be rendered"
                    : renderError;
                return false;
            }
            return true;
        }

        private static readonly IReadOnlyDictionary<string, string> EmptyParameters =
            new Dictionary<string, string>(0);

        /// <summary>
        /// Renders a value-independent representative form for the GATE's classification.
        /// The safety classification of an sp_configure write does not depend on the
        /// integer value, so the gate classifies this representative rendering (the op's
        /// lower bound); the executor renders + runs the real, bounds-checked value via
        /// <see cref="TryRender"/>. Returns false only if the op itself is malformed.
        ///
        /// <para>⚠ THIS RENDER IS NEVER EXECUTED AND MUST NEVER BE QUOTED AS IF IT WERE. It is the
        /// op's lower bound with the operator's parameters ignored. Anything writing SQL into an
        /// operator-facing sentence or an audit record wants
        /// <see cref="TryRenderExecutedStatement"/>; anything logging THIS must name it for what it
        /// is (fix round 1, gate blocker 1).</para>
        /// </summary>
        public static bool TryRenderForClassification(RemediationOperation op, out string sql, out string error)
        {
            // The safety classification of CREATE INDEX does not depend on which index — the
            // statement SHAPE is what's classified (a write, gated only by a registered key). So
            // the gate classifies a fixed representative CREATE INDEX; the executor renders the
            // real, fully-guarded identifiers from request parameters via TryRenderCreateIndex.
            if (op is not null && op.OpKind == RemediationOpKind.CreateIndex)
            {
                sql = "CREATE INDEX [ix_remediation_representative] ON [dbo].[t] ([c]);";
                error = string.Empty;
                return true;
            }
            // AgentAlertPack: the classification does not depend on the operator name/email — the
            // statement SHAPE (sp_add_operator + sp_add_alert + sp_add_notification) is what's
            // classified. Render a fixed representative batch (a placeholder email is enough since
            // this render is never executed, only classified) so the gate never needs real request
            // parameters to vet the template.
            if (op is not null && op.OpKind == RemediationOpKind.AgentAlertPack)
            {
                var repParams = new Dictionary<string, string>
                {
                    [AgentAlertOperatorEmailParam] = "dba@example.com"
                };
                return TryRenderAgentAlertPackApply(repParams, out sql, out error);
            }
            // InstallMaintenanceSolution: the classification does not depend on the real
            // BackupDirectory/schedule choices — the statement SHAPE (CREATE TABLE/PROCEDURE for
            // the install + sp_add_job/jobstep/schedule/jobserver for a schedule tickbox) is what's
            // classified. Render a fixed representative form (never executed, only classified).
            if (op is not null && op.OpKind == RemediationOpKind.InstallMaintenanceSolution)
            {
                sql = MaintenanceSolutionOpRenderer.RenderRepresentativeForClassification();
                error = string.Empty;
                return true;
            }
            // AgentJobPrimaryGuard: classification does not depend on which job or database —
            // the SHAPE (sp_add_jobstep + sp_update_job under an IF NOT EXISTS guard, plus the
            // sp_delete_jobstep inverse) is what's classified. Representative, never executed.
            if (op is not null && op.OpKind == RemediationOpKind.AgentJobPrimaryGuard)
            {
                sql = AgPrimaryGuardOpRenderer.RenderRepresentativeForClassification();
                error = string.Empty;
                return true;
            }
            // AgentJobSync / AgentJobDeleteExtra: the gate vets the sp_add_job/jobstep/schedule
            // (and sp_delete_job) SHAPE. Real step bodies are tenant T-SQL read from a connected
            // server's msdb and are never classified — see AgentJobSyncOpRenderer's header.
            if (op is not null && (op.OpKind == RemediationOpKind.AgentJobSync
                                || op.OpKind == RemediationOpKind.AgentJobDeleteExtra))
            {
                sql = AgentJobSyncOpRenderer.RenderRepresentativeForClassification();
                error = string.Empty;
                return true;
            }
            // BackupDatabaseNow / CheckDbNow (lane S5): classification does not depend on
            // the real database name/path — the statement SHAPE (BACKUP DATABASE / DBCC
            // CHECKDB) is what's classified. Representative forms are never executed.
            if (op is not null && op.OpKind == RemediationOpKind.BackupDatabaseNow)
            {
                sql = BackupCheckDbOpRenderer.RenderRepresentativeBackupForClassification();
                error = string.Empty;
                return true;
            }
            if (op is not null && op.OpKind == RemediationOpKind.CheckDbNow)
            {
                sql = BackupCheckDbOpRenderer.RenderRepresentativeCheckDbForClassification();
                error = string.Empty;
                return true;
            }
            // DbSetOption: classification does not depend on which real database is targeted
            // (that comes from offenders_query at preview/apply time) — the statement SHAPE
            // (ALTER DATABASE ... SET) is what's classified. Render a fixed representative
            // database name (never executed, only classified).
            if (op is not null && op.OpKind == RemediationOpKind.DbSetOption)
            {
                if (!TryRenderDbSetOption("ix_remediation_representative_db", op.OptionSql ?? string.Empty, out sql, out error))
                    return false;
                return true;
            }
            return TryRender(op, op?.MinValue ?? 0, out sql, out error);
        }
    }
}
