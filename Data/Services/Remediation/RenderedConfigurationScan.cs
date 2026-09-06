/* In the name of God, the Merciful, the Compassionate */
/*
 * RenderedConfigurationScan — reads the RENDERED remediation batch and reports every
 * sp_configure option the batch will WRITE.
 *
 * WHY THIS FILE EXISTS (Phase-2 item 2.3, spike S1 section 3.10). Before-state capture used to be
 * derived from the TEMPLATE's target field: the executor captured exactly one option, the one named
 * on the operation. The renderer, however, emits more than one statement. Every advanced option is
 * preceded by
 *
 *     EXEC sp_configure 'show advanced options', 1; RECONFIGURE;
 *
 * and nothing captured, restored, or recorded that. On a server where 'show advanced options'
 * already sat at 1 (both test instances did, by accident of the fixture) the side effect was
 * invisible. On a server where it starts at 0, applying any advanced fix flipped a second server
 * setting and left it flipped, unrecorded, un-restored, and absent from the rollback.
 *
 * The fix is structural rather than a second hard-coded name: capture derives from the statements
 * the renderer ACTUALLY produced, so a future render that touches a third option is captured the day
 * it is written, without anybody remembering to add it here.
 *
 * SCOPE, stated. This reads sp_configure WRITES only (a name AND a value). The read form
 * `sp_configure 'x'` with no value changes nothing and is deliberately not reported. It is a scanner
 * over text this app rendered itself from a charset-guarded option name, not a T-SQL parser, and it
 * errs toward reporting MORE options rather than fewer: an unrecognised shape is simply not
 * reported, and the caller's own target option is always captured independently of this scan.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SQLTriage.Data.Services.Remediation
{
    /// <summary>One <c>sp_configure '&lt;name&gt;', &lt;value&gt;</c> write found in a rendered batch.</summary>
    public sealed record ConfiguredOptionWrite(string OptionName, int Value);

    public static class RenderedConfigurationScan
    {
        // sp_configure ['name'|@configname='name'], [value|@configvalue=value].
        // The option name is single-quoted (the renderer escapes an embedded quote by doubling it);
        // the value is an integer literal, which is all RemediationOpRenderer ever emits.
        private static readonly Regex ConfigureWrite = new(
            @"\bsp_configure\b\s*(?:@configname\s*=\s*)?N?'(?<name>(?:[^']|'')*)'\s*,\s*(?:@configvalue\s*=\s*)?(?<value>[+-]?\d{1,10})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

        /// <summary>
        /// Every sp_configure WRITE in <paramref name="renderedSql"/>, in the order the batch runs
        /// them. A batch that writes the same option twice reports it twice: the order is what a
        /// caller restoring side effects needs, and collapsing it here would hide a double write.
        /// </summary>
        public static IReadOnlyList<ConfiguredOptionWrite> OptionWrites(string? renderedSql)
        {
            var writes = new List<ConfiguredOptionWrite>();
            if (string.IsNullOrWhiteSpace(renderedSql)) return writes;

            foreach (Match m in ConfigureWrite.Matches(renderedSql))
            {
                var name = m.Groups["name"].Value.Replace("''", "'");
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!int.TryParse(m.Groups["value"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                    continue;
                writes.Add(new ConfiguredOptionWrite(name, v));
            }
            return writes;
        }

        /// <summary>
        /// The distinct option names the batch writes, in first-appearance order. This is the set the
        /// before-state capture must cover.
        /// </summary>
        public static IReadOnlyList<string> OptionNamesTouched(string? renderedSql)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var names = new List<string>();
            foreach (var w in OptionWrites(renderedSql))
                if (seen.Add(w.OptionName))
                    names.Add(w.OptionName);
            return names;
        }

        /// <summary>
        /// The options this batch writes OTHER than <paramref name="targetOption"/> — the render's
        /// own side effects ('show advanced options' today). Returned in REVERSE render order,
        /// because that is the order a restore must run in: the prelude that made the target
        /// settable has to be put back AFTER the target itself, never before.
        /// </summary>
        public static IReadOnlyList<string> SideEffectOptions(string? renderedSql, string? targetOption)
        {
            var side = new List<string>();
            foreach (var name in OptionNamesTouched(renderedSql))
                if (!string.Equals(name, (targetOption ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
                    side.Add(name);
            side.Reverse();
            return side;
        }
    }
}
