/* In the name of God, the Merciful, the Compassionate */
/*
 * BatchCandidateSelector — the pure join behind the PREVIEW-ONLY batch surface on /remediation
 * (plan item 1.1, Phase 1). Given the findings of the last audit run and the shipped
 * CheckResolutionLookup, it answers ONE question: which registered one-click templates would
 * clear a finding that is failing on the server the page is operating on?
 *
 * WHY THIS IS NOT IN THE PAGE'S @code BLOCK. Every predicate here decides what an operator is
 * offered to change on a production server, and a predicate that lives only in a .razor block
 * cannot be exercised by a test — the same reasoning that moved IndexAnalysisService and
 * RemediationTemplateStore.IsPreviewOnlyCheck out of their pages. BatchCandidateSelectorTests
 * drives every branch below directly.
 *
 * FOUR THINGS IT DELIBERATELY DOES, each of which is a way the naive version would lie:
 *
 *   1. IT FILTERS BY SERVER. CheckResult carries InstanceName, and a "run all servers" audit
 *      leaves results for EVERY connection in one list (CheckExecutionService writes the same
 *      server string this page resolves as OperatingServer). Batching server B's findings onto
 *      server A is the worst failure this surface could have, so a finding measured elsewhere is
 *      excluded and COUNTED — OtherServerCount — rather than silently dropped.
 *
 *   2. IT DEDUPES BY TEMPLATE. Three MAXDOP findings resolve to ONE MAXDOP template, and after
 *      the nine dedupe links wired in this same lane that is the NORMAL case, not an edge one.
 *      A batch that listed the fix three times would price it three times and apply it three
 *      times. One candidate carries all the findings it clears.
 *
 *   3. IT KEEPS "RESOLVES SOMEWHERE ELSE" APART FROM "RESOLVES NOWHERE". CheckResolutionLookup
 *      returns two shapes: a one-click template, and a review-only maintenance generator on
 *      /maintenance-recommendations. Only the first can enter a batch. Folding the second into
 *      "no fix available" would tell an operator nothing exists when a script generator does.
 *
 *   4. IT DOES NOT DECIDE WHETHER AN ITEM IS PREVIEWABLE — <see cref="IsBatchablePhase1"/> does,
 *      as a separate named predicate, because Phase 1 batches only the Configuration rows the
 *      page already renders with a target input. A template that resolves a finding but is not
 *      batchable this phase is still returned, so the page can NAME it rather than quietly
 *      shrinking the list.
 *
 * WHAT IT DOES NOT DO: it never looks at credits, never touches the ledger, never contacts a
 * server, and never decides whether an item would change anything — that last one is the
 * driver's DetectNoChange, read from the preview's own current-vs-target line.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services.Remediation
{
    /// <summary>
    /// One registered one-click template, together with every failing finding on the selected
    /// server that it would clear. The findings are the OPERATOR'S reason to include it; the
    /// template is what the batch would actually run.
    /// </summary>
    public sealed class BatchCandidate
    {
        public RemediationTemplate Template { get; init; } = default!;

        /// <summary>
        /// The failing findings this template resolves, on the selected server, deduped by check
        /// id and ordered. Never empty — a candidate exists BECAUSE something is failing.
        /// </summary>
        public IReadOnlyList<CheckResult> Findings { get; init; } = Array.Empty<CheckResult>();

        public string TemplateKey => Template.Key;

        /// <summary>
        /// True when Phase 1 can render this item in the combined preview. See
        /// <see cref="BatchCandidateSelector.IsBatchablePhase1"/> for what that means and why the
        /// answer is not simply "yes".
        /// </summary>
        public bool IsBatchablePhase1 => BatchCandidateSelector.IsBatchablePhase1(Template);
    }

    /// <summary>
    /// The whole reading, including what was excluded and why. Every count here exists so the page
    /// can state a denominator instead of implying one: "6 findings, 4 of them fixable here" is an
    /// honest sentence; a list of 4 with no context is not.
    /// </summary>
    public sealed class BatchCandidateSet
    {
        /// <summary>Templates with at least one failing finding on the selected server.</summary>
        public IReadOnlyList<BatchCandidate> Candidates { get; init; } = Array.Empty<BatchCandidate>();

        /// <summary>
        /// Failing findings whose only registered resolution is a review-only maintenance script
        /// generator (/maintenance-recommendations). Corpus-ruled "maintenance work, not a state
        /// flip" — never one-click, so never batchable, but a real resolution that exists.
        /// </summary>
        public IReadOnlyList<CheckResult> MaintenanceOnly { get; init; } = Array.Empty<CheckResult>();

        /// <summary>Failing findings on the selected server with no registered resolution of either shape.</summary>
        public int UnresolvedCount { get; init; }

        /// <summary>
        /// Failing findings excluded because they were measured on a DIFFERENT server than the one
        /// selected in the top bar. Counted, never silently dropped: a "run all servers" audit is
        /// the ordinary way this list gets populated, and an operator who sees 4 candidates from a
        /// 30-finding run deserves to know 26 of them are about somewhere else.
        ///
        /// <para>Counted over the SAME per-server-deduped set as
        /// <see cref="FindingsOnThisServer"/>, so the two add up to every open finding the run
        /// carries. They did not always: see the note in
        /// <see cref="BatchCandidateSelector.Select"/>.</para>
        /// </summary>
        public int OtherServerCount { get; init; }

        /// <summary>
        /// Failing findings on the selected server, deduped by check id, before any resolution was
        /// looked up.
        /// </summary>
        public int FindingsOnThisServer { get; init; }

        /// <summary>The subset of <see cref="Candidates"/> Phase 1 can actually preview.</summary>
        public IReadOnlyList<BatchCandidate> Batchable =>
            Candidates.Where(c => c.IsBatchablePhase1).ToList();

        /// <summary>
        /// Candidates that resolve a finding but cannot enter a Phase-1 batch. Kept visible so the
        /// surface never implies the batch covers a fix it does not.
        /// </summary>
        public IReadOnlyList<BatchCandidate> NotBatchableThisPhase =>
            Candidates.Where(c => !c.IsBatchablePhase1).ToList();
    }

    public static class BatchCandidateSelector
    {
        /// <summary>
        /// What counts as a FINDING for this surface. Deliberately the SAME predicate
        /// Pages/QuickCheck.razor's FailedCount uses, so the batch can never offer to fix
        /// something the audit page does not call a finding, nor miss one it does:
        /// scorable (not SKIP / INFO / WARN), not passed, not client-accepted, and carrying no
        /// execution error.
        ///
        /// <para>The ErrorMessage clause is redundant today — <see cref="CheckClassification.IsSkip"/>
        /// already treats any ErrorMessage as a skip — and it is kept anyway, because it is kept in
        /// the count this must agree with. Dropping it here would make the two definitions differ
        /// by a clause nobody could see, which is exactly how they drifted the last time
        /// (see CheckClassification's own header).</para>
        /// </summary>
        /// <remarks>
        /// Written as ONE line on purpose. RawPassedGuardTests keys its allowlist on the normalised
        /// source line, and the house pattern for a legitimate <c>.Passed</c> read is that the
        /// <see cref="CheckClassification.IsScorable"/> guard sits on the SAME line — so a reader of
        /// the allowlist can see the guard without opening the file. Split across five lines, the
        /// allowlist entry would read "&amp;&amp; !r.Passed" and prove nothing.
        /// </remarks>
        public static bool IsOpenFinding(CheckResult r) =>
            r is not null && CheckClassification.IsScorable(r) && !r.Passed && !r.IsAccepted && string.IsNullOrEmpty(r.ErrorMessage);

        /// <summary>
        /// Whether a resolving template can appear in the PHASE-1 preview batch.
        ///
        /// <para>Configuration templates with a structured Operation are the set the page already
        /// renders as rows with a target input, so the batch reuses that row's value and the
        /// runner's own parameter shape — no second copy of the parameter rule. The keyed sections
        /// (Agent alert pack, Maintenance Solution, Backup Now, CHECKDB Now, Add index) each carry
        /// their own parameter shape, confirmation tick or offenders list, and folding them in
        /// would mean re-authoring those confirmations inside a batch. They stay out of Phase 1
        /// and the page says so by name.</para>
        ///
        /// <para>This is a SCOPE predicate, not a safety one. Nothing here decides risk: the
        /// runner's five gates do that per item, unchanged.</para>
        /// </summary>
        public static bool IsBatchablePhase1(RemediationTemplate? t) =>
            t is not null
            && t.Kind == RemediationKind.Configuration
            && t.Operation is not null;

        /// <summary>
        /// Join the last run's findings against the registered resolutions for ONE server.
        /// Pure: no I/O, no ledger, no server contact.
        /// </summary>
        /// <param name="serverName">
        /// The server the page is operating on (the top bar's "Connected to:"). Null or blank
        /// yields an empty set — with no server there is nothing to batch against, and guessing
        /// one from the results would pick a server the operator did not select.
        /// </param>
        public static BatchCandidateSet Select(
            string? serverName,
            IEnumerable<CheckResult>? results,
            CheckResolutionLookup? lookup)
        {
            if (string.IsNullOrWhiteSpace(serverName) || results is null || lookup is null)
                return new BatchCandidateSet();

            var open = results.Where(r => r is not null && IsOpenFinding(r)).ToList();

            // ⚠ DEDUPE FIRST, ACROSS EVERY SERVER, THEN SPLIT. One check id contributes once PER
            // SERVER. A single audit run should not produce the same id twice for one server, and
            // this list is also fed by imported and rehydrated runs, where a duplicate does occur.
            //
            // The dedupe happens BEFORE the server split on purpose, and this is a fix from the
            // Phase-1 gate (2026-09-01): OtherServerCount used to be computed as
            // `open.Count - onThisServer.Count` — the RAW list minus the deduped one — while
            // FindingsOnThisServer was counted over the deduped set. The page prints both in one
            // sentence ("N open findings on this server ... M more were measured on other servers"),
            // so two different denominators made that sentence fail to add up the moment any
            // duplicate existed anywhere. Every count this method returns is now taken over the
            // SAME deduped set.
            //
            // A finding with no check id is never deduped: there is no key to dedupe it by, and
            // collapsing them all onto one another would hide real findings.
            var seenPerServer = new HashSet<(string Server, string CheckId)>();
            var deduped = new List<CheckResult>(open.Count);
            foreach (var r in open)
            {
                // The server half of the key is upper-cased because instance names are matched
                // case-insensitively everywhere else in this method; the check-id half stays exact,
                // because check ids are ordinal identifiers.
                if (!string.IsNullOrWhiteSpace(r.CheckId)
                    && !seenPerServer.Add(((r.InstanceName ?? string.Empty).ToUpperInvariant(), r.CheckId))) continue;
                deduped.Add(r);
            }

            // Server names are matched case-insensitively: SQL Server instance names are, and the
            // string on both sides comes from the same ServerConnection.GetServerList() entry, so a
            // case difference here would be a display artefact, never a different machine.
            var mine = deduped
                .Where(r => string.Equals(r.InstanceName, serverName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var byTemplate = new Dictionary<string, (RemediationTemplate Template, List<CheckResult> Findings)>(
                StringComparer.Ordinal);
            var maintenance = new List<CheckResult>();
            int unresolved = 0;

            foreach (var r in mine)
            {
                var resolution = lookup.Resolve(r.CheckId);
                if (resolution is null) { unresolved++; continue; }

                if (resolution.IsOneClick && resolution.Template is not null)
                {
                    var key = resolution.Template.Key;
                    if (!byTemplate.TryGetValue(key, out var entry))
                    {
                        entry = (resolution.Template, new List<CheckResult>());
                        byTemplate[key] = entry;
                    }
                    entry.Findings.Add(r);
                    continue;
                }

                if (resolution.IsMaintenance) { maintenance.Add(r); continue; }

                // A resolution object that is neither shape is not a category this code knows how
                // to present, so it is counted as unresolved rather than rendered as a fix.
                unresolved++;
            }

            var candidates = byTemplate.Values
                .Select(e => new BatchCandidate
                {
                    Template = e.Template,
                    Findings = e.Findings
                        .OrderBy(f => f.CheckId, StringComparer.Ordinal)
                        .ToList(),
                })
                // Deterministic order, matching the fix table above it on the page.
                .OrderBy(c => c.Template.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Template.Key, StringComparer.Ordinal)
                .ToList();

            return new BatchCandidateSet
            {
                Candidates = candidates,
                MaintenanceOnly = maintenance
                    .OrderBy(f => f.CheckId, StringComparer.Ordinal)
                    .ToList(),
                UnresolvedCount = unresolved,
                // Both counts over the SAME deduped set, so the page's one-sentence denominator
                // adds up: FindingsOnThisServer + OtherServerCount == every deduped open finding.
                OtherServerCount = deduped.Count - mine.Count,
                FindingsOnThisServer = mine.Count,
            };
        }
    }
}
