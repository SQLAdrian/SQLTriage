/* In the name of God, the Merciful, the Compassionate */
/*
 * RemediationPreviewSentence — the ONE definition of the Configuration preview's
 * "current vs target" sentence, shared by the code that WRITES it and the code that READS it.
 *
 * WHY THIS FILE EXISTS (Phase-1 gate, 2026-09-01). DbatoolsRemediationExecutor built the sentence
 * with an interpolated string literal. BatchRemediationDriver.DetectNoChange parsed it back with a
 * hand-written regular expression declared in a different file. The offline tests for DetectNoChange
 * hand-wrote the sentence a THIRD time. Three copies of one format, none of them bound to the
 * others: an executor wording change — a space, a colon, a rename of "target" — would leave every
 * test green while the batch surface silently stopped detecting no-change items, and a no-change
 * item that goes undetected is priced as a change. The producer and the parser are now the same
 * declaration, and the parser is BUILT from the producer's format string rather than restating it.
 *
 * WHAT IS AND IS NOT GUARANTEED. Deriving the pattern from the format makes the two structurally
 * inseparable: change the literal and the pattern changes with it. It does NOT stop somebody
 * building the sentence somewhere else by hand — that is a source property, and
 * BatchRemediationDriverTests.TheExecutorBuildsThePreviewSentenceThroughTheSharedFormat scans the
 * executor for exactly that.
 */

using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SQLTriage.Data.Services.Remediation
{
    public static class RemediationPreviewSentence
    {
        /// <summary>
        /// What the sentence says when the current value could not be read. It is deliberately not
        /// a number and not blank: <see cref="BatchRemediationDriver.DetectNoChange"/> must answer
        /// "unknown" over it rather than "no change", and an empty slot would read as a value.
        /// </summary>
        public const string UnreadCurrent = "(unread)";

        /// <summary>
        /// The sentence itself. {0} = the sp_configure option name, {1} = the current value as read
        /// (or <see cref="UnreadCurrent"/>), {2} = the target.
        ///
        /// <para>⚠ EDITING THIS STRING CHANGES WHAT THE BATCH SURFACE CAN DETECT. The pattern below
        /// is built from it, so the parser follows automatically; the round-trip test
        /// (BatchRemediationDriverTests) is what proves the pair still agree after an edit.</para>
        /// </summary>
        public const string CurrentVersusTargetFormat = "Current '{0}' = {1}; target = {2}.";

        /// <summary>The lines above the sentence: what would run, and where.</summary>
        public const string PreambleFormat = "Would run on {0}:\n{1}\n\n";

        /// <summary>
        /// The current-vs-target sentence, exactly as the executor emits it.
        /// </summary>
        /// <param name="current">
        /// The value read back from the server, or null when the read failed or was not attempted.
        /// Null — and only null — renders as <see cref="UnreadCurrent"/>, which is the behaviour
        /// this replaced verbatim: an empty string is a read that returned nothing, and pretending
        /// that is the same as "we never looked" would be a third state collapsed into a second.
        /// </param>
        public static string CurrentVersusTarget(string? configName, string? current, int target) =>
            string.Format(
                CultureInfo.InvariantCulture,
                CurrentVersusTargetFormat,
                configName ?? string.Empty,
                current ?? UnreadCurrent,
                target.ToString(CultureInfo.InvariantCulture));

        /// <summary>
        /// The WHOLE Configuration preview text: the T-SQL that would run, then the sentence.
        /// <see cref="DbatoolsRemediationExecutor"/> calls this and nothing else, so an offline test
        /// that calls it is driving the real production formatting rather than a copy of it.
        /// </summary>
        public static string ConfigurationPreview(
            string? serverName, string? applySql, string? configName, string? current, int target) =>
            string.Format(CultureInfo.InvariantCulture, PreambleFormat, serverName ?? string.Empty, applySql ?? string.Empty)
            + CurrentVersusTarget(configName, current, target);

        /// <summary>
        /// The parser for <see cref="CurrentVersusTargetFormat"/>, BUILT FROM IT. Groups:
        /// <c>config</c>, <c>current</c>, <c>target</c>. <c>current</c> is captured as raw text
        /// rather than as a number, so <see cref="UnreadCurrent"/> matches the sentence shape and
        /// then fails to parse as a value — which is how "unknown" stays a third answer instead of
        /// looking like a sentence that was never emitted.
        /// </summary>
        public static readonly Regex CurrentVersusTargetPattern = BuildPattern();

        private static Regex BuildPattern()
        {
            // The placeholders must appear in index order for the split below to line up with the
            // groups. Asserted rather than assumed: reordering the format would otherwise produce a
            // pattern that matches nothing, and a parser that matches nothing answers "unknown" for
            // every item — silent, and exactly the failure this file exists to prevent.
            int a = CurrentVersusTargetFormat.IndexOf("{0}", StringComparison.Ordinal);
            int b = CurrentVersusTargetFormat.IndexOf("{1}", StringComparison.Ordinal);
            int c = CurrentVersusTargetFormat.IndexOf("{2}", StringComparison.Ordinal);
            if (a < 0 || b < 0 || c < 0 || !(a < b && b < c))
                throw new InvalidOperationException(
                    "RemediationPreviewSentence.CurrentVersusTargetFormat must carry {0}, {1} and {2} "
                    + "in that order; the derived parser is built by splitting on them.");

            var literals = CurrentVersusTargetFormat.Split(new[] { "{0}", "{1}", "{2}" }, StringSplitOptions.None);
            var groups = new[]
            {
                "(?<config>[^']*)",   // an sp_configure option name, up to the closing quote
                "(?<current>[^;]*)",  // raw text: a number, or "(unread)"
                @"(?<target>-?\d+)",  // the target is always an int (RemediationOperation values are)
            };

            var pattern = Regex.Escape(literals[0]);
            for (int i = 0; i < groups.Length; i++)
                pattern += groups[i] + Regex.Escape(literals[i + 1]);

            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                             TimeSpan.FromSeconds(1));
        }
    }
}
