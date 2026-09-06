/* In the name of God, the Merciful, the Compassionate */
/*
 * RemediationBatchPreviewPageTests — the BEHAVIOURAL half of the preview-only batch surface
 * (plan item 1.1, Phase 1 build lane, 2026-09-01). RemediationBatchPreviewUiTests scans the source
 * text; this file drives the real page object.
 *
 * ⚠ WHY THIS IS IN Gated/. It reaches SQLTriage.Pages.Remediation, which buildprofile.targets
 * Content-Removes from a COMMUNITY build. Compiled into the community suite it would throw at run
 * time — the exact shape Gated/README.md exists for. The Gated\**\*.cs glob removes the folder from
 * the community test build, so this runs only where the page exists.
 *
 * Both tests below drive methods that touch NO dependency-injected member, which is why a bare
 * Activator.CreateInstance page is a legitimate subject: the page's collections are
 * field-initialised, and SelectedBatchItems / ResetTransientStateForServerChange read only fields.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Remediation;
using Xunit;

namespace SQLTriage.Tests.Gated
{
    public sealed class RemediationBatchPreviewPageTests
    {
        private const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;

        private static RemediationTemplateStore Store() =>
            new(NullLogger<RemediationTemplateStore>.Instance);

        private static object NewRemediationPage(out Type type)
        {
            var asm = typeof(RemediationRunner).Assembly;
            type = asm.GetType("SQLTriage.Pages.Remediation")
                   ?? asm.GetTypes().Single(x => x.Name == "Remediation" && x.Namespace == "SQLTriage.Pages");
            return Activator.CreateInstance(type)!;
        }

        /// <summary>Builds the page's private FixRow for one template, with the operator's target value.</summary>
        private static object NewFixRow(Type pageType, RemediationTemplate template, int? value)
        {
            var rowType = pageType.GetNestedType("FixRow", BindingFlags.NonPublic)
                          ?? throw new InvalidOperationException("Pages/Remediation.razor no longer declares FixRow.");
            var row = Activator.CreateInstance(rowType, nonPublic: true)!;
            rowType.GetProperty("Template")!.SetValue(row, template);
            rowType.GetProperty("Value")!.SetValue(row, value);
            return row;
        }

        private static void SetRows(object page, Type t, params object[] rows)
        {
            var list = (IList)t.GetField("_rows", F)!.GetValue(page)!;
            list.Clear();
            foreach (var r in rows) list.Add(r);
        }

        private static BatchCandidateSet SetOf(params RemediationTemplate[] templates) =>
            new()
            {
                Candidates = templates.Select(t => new BatchCandidate
                {
                    Template = t,
                    // A candidate exists BECAUSE something is failing; the finding text is not what
                    // these tests are about, so one placeholder per template is enough.
                    Findings = new[]
                    {
                        new CheckResult { CheckId = "SQLT-ZZ-" + t.Key, InstanceName = "SRV", Passed = false },
                    },
                }).ToList(),
            };

        // ── The target rule: an item the app cannot price a value for never enters the batch ──

        [Fact]
        public void ATickedFixWithNoTargetValue_IsNotSentToTheDriver()
        {
            // MAXSERVERMEMORY ships with NO RecommendedValue on purpose — the cap is a function of
            // host RAM and what else runs on the box, so the operator types it. The single-fix
            // Preview blocks on exactly this (NeedsATarget), and the batch must block the same way:
            // sending a target-less item would spend a round trip to render a refusal the operator
            // can only clear in the table above.
            var page = NewRemediationPage(out var t);
            var store = Store();
            var maxdop = store.TryGet("MAXDOP")!;
            var maxMem = store.TryGet("MAXSERVERMEMORY")!;

            SetRows(page, t,
                NewFixRow(t, maxdop, 4),        // has a target
                NewFixRow(t, maxMem, null));    // has none

            // The operator ticked BOTH.
            var selected = (ISet<string>)t.GetField("_batchSelected", F)!.GetValue(page)!;
            selected.Add("MAXDOP");
            selected.Add("MAXSERVERMEMORY");

            var items = (List<BatchRemediationItem>)t.GetMethod("SelectedBatchItems", F)!
                .Invoke(page, new object[] { SetOf(maxdop, maxMem) })!;

            var only = Assert.Single(items);
            Assert.Equal("MAXDOP", only.TemplateKey);

            // And the value that DOES travel is the row's, under the template's own parameter name —
            // the same shape the single-fix path sends, not a second copy of the rule.
            Assert.NotNull(only.Parameters);
            Assert.Equal("4", only.Parameters!["MaxDop"]);
        }

        [Fact]
        public void FillingInTheTarget_MakesTheSameFixSendable()
        {
            // The negative control for the test above: the exclusion is about the missing VALUE,
            // not about MAXSERVERMEMORY being special.
            var page = NewRemediationPage(out var t);
            var maxMem = Store().TryGet("MAXSERVERMEMORY")!;

            SetRows(page, t, NewFixRow(t, maxMem, 24576));
            ((ISet<string>)t.GetField("_batchSelected", F)!.GetValue(page)!).Add("MAXSERVERMEMORY");

            var items = (List<BatchRemediationItem>)t.GetMethod("SelectedBatchItems", F)!
                .Invoke(page, new object[] { SetOf(maxMem) })!;

            var only = Assert.Single(items);
            Assert.Equal("24576", only.Parameters!["MaxServerMemoryMb"]);
        }

        [Fact]
        public void AnUntickedFix_IsNotSent_EvenWhenItIsAValidCandidate()
        {
            var page = NewRemediationPage(out var t);
            var maxdop = Store().TryGet("MAXDOP")!;
            SetRows(page, t, NewFixRow(t, maxdop, 4));
            // Nothing ticked.

            var items = (List<BatchRemediationItem>)t.GetMethod("SelectedBatchItems", F)!
                .Invoke(page, new object[] { SetOf(maxdop) })!;

            Assert.Empty(items);
        }

        [Fact]
        public void ATickedFixWithNoRowOnThePage_IsNotSent()
        {
            // Fails closed on a shape that should not occur: a ticked key the page renders no row
            // for has no target and no parameter source, so there is nothing honest to send.
            var page = NewRemediationPage(out var t);
            var maxdop = Store().TryGet("MAXDOP")!;
            SetRows(page, t); // no rows at all
            ((ISet<string>)t.GetField("_batchSelected", F)!.GetValue(page)!).Add("MAXDOP");

            var items = (List<BatchRemediationItem>)t.GetMethod("SelectedBatchItems", F)!
                .Invoke(page, new object[] { SetOf(maxdop) })!;

            Assert.Empty(items);
        }

        // ── The per-server reset ─────────────────────────────────────────────────

        [Fact]
        public void AServerChange_DropsTheBatchPreviewAndTheTickedSet()
        {
            // Same shape as the hardening-preview residual (DECISIONS 2026-08-25): the candidate
            // list is built from findings measured on server A. Carrying the ticks over would
            // pre-select fixes for findings nobody has measured on server B; carrying the reading
            // over would print server A's prices under server B's name.
            var page = NewRemediationPage(out var t);

            t.GetField("_batchPreview", F)!.SetValue(page, new BatchPreviewResult { RunId = "server-A-run" });
            t.GetField("_batchError", F)!.SetValue(page, "a stale error from server A");
            var selected = (ISet<string>)t.GetField("_batchSelected", F)!.GetValue(page)!;
            selected.Add("MAXDOP");

            Assert.NotNull(t.GetField("_batchPreview", F)!.GetValue(page)); // sanity before the switch

            t.GetMethod("ResetTransientStateForServerChange", F)!.Invoke(page, null);

            Assert.Null(t.GetField("_batchPreview", F)!.GetValue(page));
            Assert.Null(t.GetField("_batchError", F)!.GetValue(page));
            Assert.Empty(selected);
        }

        [Fact]
        public void ChangingTheSelection_InvalidatesThePreviousReading()
        {
            // A preview describes the set that was previewed. Leaving it on screen after the
            // operator ticks something else would quote one batch's price for another.
            var page = NewRemediationPage(out var t);
            t.GetField("_batchPreview", F)!.SetValue(page, new BatchPreviewResult { RunId = "earlier" });

            t.GetMethod("SetBatchSelected", F)!.Invoke(page, new object[] { "MAXDOP", true });

            Assert.Null(t.GetField("_batchPreview", F)!.GetValue(page));
            Assert.Contains("MAXDOP", (ISet<string>)t.GetField("_batchSelected", F)!.GetValue(page)!);

            t.GetMethod("SetBatchSelected", F)!.Invoke(page, new object[] { "MAXDOP", false });
            Assert.Empty((ISet<string>)t.GetField("_batchSelected", F)!.GetValue(page)!);
        }
    }
}
