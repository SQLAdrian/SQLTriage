/* In the name of God, the Merciful, the Compassionate */
/*
 * RemediationValueBounds — the ONE place an operator-typed sp_configure value is bounded, parsed
 * and refused, with a message that says what the allowed range is and why.
 *
 * WHY THIS FILE EXISTS (Phase-2 item 6a, Adrian's mid-lane instruction 2026-09-01: "make it idiot
 * proof, I will be driving it"). The template's own MinValue/MaxValue are a SCHEMA range, not a sane
 * one. MAXSERVERMEMORY ships [128, 2147483647]: typing 128 is inside the template's range and
 * starves the engine, and every digit up to two billion is accepted for a box with 16 GB in it.
 * A fat-fingered cap is a production outage, and the operator finds out after the credit is spent.
 *
 * WHAT THIS ADDS on top of the template range:
 *   1. A per-option SANE range where one exists, intersected with the template's own range. The
 *      tighter of the two always wins, so this can only ever refuse more, never authorise more.
 *   2. A discovered ceiling for 'max server memory (MB)': the host's physical RAM, when the caller
 *      could read it. A cap above installed RAM is not a cap.
 *   3. STRICT parsing. int.TryParse accepts thousands separators and surrounding whitespace under
 *      some styles; this accepts an optional sign and digits, nothing else, so "8 GB", "1,024",
 *      "8; DROP DATABASE", "0x10" and "" are all refused as the garbage they are rather than
 *      silently becoming a number.
 *   4. A refusal that NAMES the allowed range and the reason. A refusal an operator cannot act on
 *      is a refusal they will work around.
 *
 * WHERE IT IS ENFORCED: RemediationOpRenderer.TryResolveValue / ResolveValueStagedAsync, the single
 * boundary BOTH the preview and the apply path resolve a value through.
 *
 * ⚠ THE ORDER IS THE GUARANTEE, AND IT WAS WRONG (fix round, 2026-09-01 gate blocker 1).
 * ────────────────────────────────────────────────────────────────────────────────────────────────
 * This header used to say, and TryResolveValue's own comment used to say, that "a refusal here
 * happens before any read or write reaches the server". THAT SENTENCE WAS FALSE FOR ONE OPTION AND
 * THE GATE PROVED IT LIVE: 'max server memory (MB)' needs the host's installed RAM for its ceiling,
 * the executor read that RAM FIRST and passed it in, so MaxServerMemoryMb="banana" printed
 * "Nothing was sent to the server" while sys.dm_os_sys_info's execution_count went 0 -> 1. The
 * refusal was correct; the sentence beside it was a lie, and an operator-facing one.
 *
 * The cure is ORDER, not wording:
 *
 *   STAGE 1 (TryResolveStatic) — strict parse + the STATIC range (template ∩ sane table). Pure. It
 *     cannot touch a server because it is handed no way to. Garbage, empty, negative and
 *     statically-absurd values are refused HERE, and only these refusals may say
 *     "refused before anything reached the server".
 *
 *   STAGE 2 (TryApplyHostCeiling) — reached ONLY by a value that already parsed and already passed
 *     stage 1, and only for the one host-relative option. Its refusal says exactly what ran:
 *     "A read-only check of host memory ran; nothing was changed."
 *
 * ResolveStagedAsync sequences the two around a caller-supplied host reader, so the order is a
 * property of this file rather than a habit of each call site — and a test can count the reader's
 * invocations without a server (RemediationValueBoundsTests.TheHostIsNotReadForAValueThatCannotParse).
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SQLTriage.Data.Services;

namespace SQLTriage.Data.Services.Remediation
{
    /// <summary>
    /// The effective range for one option, and the sentence explaining where it came from.
    /// <paramref name="HasHostCeiling"/> marks a range whose upper bound is further tightened at
    /// preview/apply time by a read of the host's installed RAM — the one bound a surface cannot
    /// compute for itself, and the one it must not silently present as final.
    /// </summary>
    public sealed record RemediationValueRange(int Min, int Max, string Reason, bool HasHostCeiling = false);

    /// <summary>
    /// The outcome of a staged resolution: what was decided, and WHAT RAN to decide it. The two
    /// booleans exist so a caller's prose can be checked against the measurement instead of
    /// restating an assumption — the exact failure the fix round closed.
    /// </summary>
    /// <param name="HostCheckRan">True when the host-memory reader was actually invoked.</param>
    /// <param name="HostMemoryMb">What that read returned; null when it was not run OR failed.</param>
    public sealed record StagedValueResolution(
        bool Ok, int Value, string Error, bool HostCheckRan, int? HostMemoryMb)
    {
        /// <summary>The host was asked and could not answer, so the cap was NOT checked against it.</summary>
        public bool HostCheckFailed => HostCheckRan && HostMemoryMb is null;
    }

    public static class RemediationValueBounds
    {
        /// <summary>
        /// The floor for 'max server memory (MB)'. SQL Server accepts 128, and a 128 MB cap on a real
        /// instance is an outage: the engine cannot keep its own structures resident and the box
        /// spends its life paging. 1024 is deliberately conservative rather than clever, and it is a
        /// floor on what SQLTriage will TYPE, never a recommendation.
        /// </summary>
        public const int MaxServerMemoryFloorMb = 1024;

        /// <summary>Read-only probe for the host's installed RAM, used as the max-memory ceiling.</summary>
        public const string PhysicalMemoryMbQuery =
            "SELECT CAST(physical_memory_kb / 1024 AS int) FROM sys.dm_os_sys_info;";

        // Optional sign then 1-10 digits, anchored \A..\z so a trailing newline cannot slip through
        // ($ matches before one in .NET). Anything else is garbage, not a number.
        private static readonly Regex StrictInteger = new(@"\A[+-]?[0-9]{1,10}\z", RegexOptions.Compiled);

        // Per-option sane ranges. Keyed on the sp_configure option name, which is SHIPPED text.
        // Every entry is a documented engine range or a stated product floor, never a guess.
        private static readonly Dictionary<string, (int Min, int Max, string Reason)> SaneRanges =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["max degree of parallelism"] =
                    (0, 64, "0 means 'use every processor'; 64 is the engine's maximum."),
                ["cost threshold for parallelism"] =
                    (0, 32767, "0 to 32767 is the engine's documented range."),
                ["max server memory (MB)"] =
                    (MaxServerMemoryFloorMb, int.MaxValue,
                     "A cap below " + MaxServerMemoryFloorMb + " MB starves the engine."),
                ["min server memory (MB)"] =
                    (0, int.MaxValue, "0 means 'no floor'."),
                ["fill factor (%)"] =
                    (0, 100, "A fill factor is a percentage."),
                ["max worker threads"] =
                    (0, 65535, "0 means 'let the engine decide'; 65535 is the engine's maximum."),
                ["recovery interval (min)"] =
                    (0, 32767, "0 means 'let the engine decide'."),
                ["blocked process threshold (s)"] =
                    (0, 86400, "0 turns the report off; 86400 is one day."),
            };

        /// <summary>The refusal sentence for a stage-1 (pure, zero-SQL) refusal. ONE constant,
        /// because it is a claim about what ran and it must never be pasted onto a path where
        /// something did.</summary>
        public const string RefusedBeforeAnySqlSentence = "It was refused before anything reached the server.";

        /// <summary>The refusal sentence for a stage-2 refusal, where the read-only host probe DID
        /// run. It says what ran and what did not.</summary>
        public const string HostCheckRanSentence = "A read-only check of host memory ran; nothing was changed.";

        /// <summary>
        /// The STATIC range for an operation: the template's own range tightened by the sane table.
        /// Pure — no host, no connection, no SQL, ever. This is the range a UI can bind an input to,
        /// and the range stage 1 refuses against.
        /// </summary>
        public static RemediationValueRange ResolveStatic(RemediationOperation? op)
        {
            if (op is null) return new RemediationValueRange(0, 0, "No operation.");

            int min = op.MinValue;
            int max = op.MaxValue;
            var reasons = new List<string>();

            if (!string.IsNullOrWhiteSpace(op.ConfigName)
                && SaneRanges.TryGetValue(op.ConfigName.Trim(), out var sane))
            {
                if (sane.Min > min) min = sane.Min;
                if (sane.Max < max) max = sane.Max;
                reasons.Add(sane.Reason);
            }

            bool hostCeiling = IsMaxServerMemory(op.ConfigName);
            if (hostCeiling)
                reasons.Add("The cap is checked against this host's installed RAM as well, by a "
                          + "read-only query, once the number itself is valid.");

            // A tightened floor above a tightened ceiling would refuse everything with no way out.
            // It cannot happen with the shipped table; if a future entry makes it happen, say so
            // instead of silently inverting the range.
            if (min > max)
                return new RemediationValueRange(min, min,
                    "The allowed range for this setting is inconsistent. Report this: floor " + min + ", ceiling " + max + ".",
                    hostCeiling);

            return new RemediationValueRange(min, max,
                reasons.Count == 0 ? "The range this fix ships with." : string.Join(" ", reasons),
                hostCeiling);
        }

        /// <summary>
        /// The static range with the discovered host ceiling applied. Never widens the static one.
        /// </summary>
        /// <param name="physicalMemoryMb">
        /// The host's installed RAM in MB when it was read, else null. A null returns the static
        /// range UNCHANGED and makes no claim about a host read either way — saying "the host could
        /// not be read" on a range nobody asked a host about is the same over-claim in reverse.
        /// </param>
        public static RemediationValueRange ResolveWithHost(RemediationOperation? op, int? physicalMemoryMb)
        {
            var stat = ResolveStatic(op);
            if (op is null || !IsMaxServerMemory(op.ConfigName)) return stat;
            if (physicalMemoryMb is not int ram || ram <= 0) return stat;

            int max = ram < stat.Max ? ram : stat.Max;
            if (max < stat.Min)
                return new RemediationValueRange(stat.Min, stat.Min,
                    $"This host reports {ram} MB of RAM, which is below the {stat.Min} MB floor for this setting. "
                    + "Report this.", true);

            return new RemediationValueRange(stat.Min, max,
                $"This host has {ram} MB of RAM, so a cap above that caps nothing.", true);
        }

        /// <summary>
        /// Back-compatible entry point. With no host reading it IS the static range; with one it is
        /// the static range tightened by the host.
        /// </summary>
        public static RemediationValueRange Resolve(RemediationOperation op, int? physicalMemoryMb = null)
            => physicalMemoryMb is null ? ResolveStatic(op) : ResolveWithHost(op, physicalMemoryMb);

        /// <summary>True when this operation is the max-server-memory cap (the one host-relative bound).</summary>
        public static bool IsMaxServerMemory(string? configName) =>
            string.Equals((configName ?? string.Empty).Trim(), "max server memory (MB)", StringComparison.OrdinalIgnoreCase);

        private static string Label(RemediationOperation? op) =>
            string.IsNullOrWhiteSpace(op?.ConfigName) ? "This setting" : $"'{op!.ConfigName}'";

        private static string Allowed(RemediationValueRange range) =>
            $"Enter a whole number from {range.Min} to {range.Max}. {range.Reason}";

        /// <summary>
        /// STAGE 1. Strict parse plus the STATIC range. A pure function with no way to reach a
        /// server: it is handed no connection, no reader and no host value, which is why its
        /// refusals are the only ones allowed to say nothing reached the server.
        /// </summary>
        public static bool TryResolveStatic(
            RemediationOperation? op, string? raw, out int value, out string error)
        {
            value = 0;
            var range = ResolveStatic(op);
            var label = Label(op);
            var allowed = Allowed(range);

            var text = (raw ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                error = $"Refused: no value was supplied for {label}. {RefusedBeforeAnySqlSentence} {allowed}";
                return false;
            }

            if (!StrictInteger.IsMatch(text)
                || !int.TryParse(text, NumberStyles.None | NumberStyles.AllowLeadingSign,
                                 CultureInfo.InvariantCulture, out var parsed))
            {
                error = $"Refused: '{text}' is not a whole number, so it was not used for {label}. "
                      + $"{RefusedBeforeAnySqlSentence} {allowed}";
                return false;
            }

            if (parsed < range.Min || parsed > range.Max)
            {
                error = $"Refused: {parsed} is outside the allowed range for {label}. "
                      + $"{RefusedBeforeAnySqlSentence} {allowed}";
                return false;
            }

            value = parsed;
            error = string.Empty;
            return true;
        }

        /// <summary>
        /// STAGE 2. The host-relative ceiling, applied to a value that ALREADY parsed and ALREADY
        /// passed stage 1. Its refusal states what ran — a read-only memory probe — because
        /// something did.
        /// </summary>
        public static bool TryApplyHostCeiling(
            RemediationOperation? op, int parsed, int? physicalMemoryMb, out string error)
        {
            error = string.Empty;
            if (op is null || !IsMaxServerMemory(op.ConfigName)) return true;
            if (physicalMemoryMb is not int ram || ram <= 0) return true;   // unreadable host: no ceiling to apply

            var range = ResolveWithHost(op, ram);
            if (parsed >= range.Min && parsed <= range.Max) return true;

            error = $"Refused: {parsed} is outside the allowed range for {Label(op)} on this host. "
                  + $"{HostCheckRanSentence} {Allowed(range)}";
            return false;
        }

        /// <summary>
        /// Parses and bounds-checks one operator-supplied value in the two stages, in order, given a
        /// host reading the CALLER already took. Prefer <see cref="ResolveStagedAsync"/>, which owns
        /// the order; this overload exists for callers that have no server to read at all.
        /// </summary>
        public static bool TryResolve(
            RemediationOperation op, string? raw, int? physicalMemoryMb, out int value, out string error)
        {
            if (!TryResolveStatic(op, raw, out value, out error)) return false;
            if (!TryApplyHostCeiling(op, value, physicalMemoryMb, out error)) { value = 0; return false; }
            return true;
        }

        /// <summary>
        /// THE ORDERED RESOLUTION. Stage 1 runs first, on nothing but text. The host reader is
        /// invoked ONLY when a validly-parsed, statically-sane value needs the one host-relative
        /// bound — so a garbage value cannot cause a query, and the refusal sentence that says so
        /// is true by construction rather than by comment.
        /// </summary>
        /// <param name="readHostMemoryMb">
        /// Reads the host's installed RAM in MB, returning null when it cannot. May be null when the
        /// caller has no server (a preview with no registered connection), in which case no host
        /// ceiling is applied and none is claimed.
        /// </param>
        public static async Task<StagedValueResolution> ResolveStagedAsync(
            RemediationOperation? op, string? raw,
            Func<CancellationToken, Task<int?>>? readHostMemoryMb, CancellationToken ct)
        {
            if (!TryResolveStatic(op, raw, out var value, out var error))
                return new StagedValueResolution(false, 0, error, HostCheckRan: false, HostMemoryMb: null);

            if (readHostMemoryMb is null || op is null || !IsMaxServerMemory(op.ConfigName))
                return new StagedValueResolution(true, value, string.Empty, HostCheckRan: false, HostMemoryMb: null);

            int? ram = await readHostMemoryMb(ct).ConfigureAwait(false);
            if (!TryApplyHostCeiling(op, value, ram, out var hostError))
                return new StagedValueResolution(false, 0, hostError, HostCheckRan: true, HostMemoryMb: ram);

            return new StagedValueResolution(true, value, string.Empty, HostCheckRan: true, HostMemoryMb: ram);
        }
    }
}
