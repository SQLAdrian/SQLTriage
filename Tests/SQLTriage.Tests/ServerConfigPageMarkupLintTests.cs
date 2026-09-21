/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// mc-preview-usability (2026-09-08). A TEXT LINT over the real shipped markup of
    /// <c>Pages/ServerConfiguration.razor</c> - the same idiom <c>ServerConfigSuiteGateTests</c>
    /// uses, over the same copy (<c>SQLTriage.Tests.csproj</c> copies the page to <c>Markup\</c>).
    ///
    /// <para><b>⚠ CORRECTED after gate finding F2 (2026-09-08).</b> This header used to say "there
    /// is no bUnit in this tree, so the wiring is held by a text lint". The premise is true and the
    /// conclusion was false: this tree renders real pages through <c>HtmlRenderer</c> without bUnit
    /// (twelve <c>*RenderTests</c> do it; <c>Gated/RemediationBatchApplyRenderTests</c> renders a
    /// Content-Removed page with a faked bundle). The three usability affordances are now proved by
    /// RENDER in <c>Gated/ServerConfigurationRenderTests</c>. These lints stay because they cover
    /// what a render cannot: refusals ("this page opens nothing on the host"), the subscribe /
    /// unsubscribe pair, statement PLACEMENT inside a branch, and the inline-style ratchet.</para>
    ///
    /// <para><b>Lint ceiling, stated up front.</b> These assertions prove the calls are WRITTEN.
    /// They do not prove a branch renders - the render tests do that - and they cannot prove a
    /// browser actually downloads a file, which is exercised in a real browser or is an honest
    /// UNTESTED residual.</para>
    /// </summary>
    public sealed class ServerConfigPageMarkupLintTests
    {
        private static string Markup()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Markup", "ServerConfiguration.razor");
            Assert.True(File.Exists(path),
                "Pages/ServerConfiguration.razor is copied to the test output by SQLTriage.Tests.csproj; "
                + "if this fails every assertion below would vacuously pass");
            return File.ReadAllText(path);
        }

        /// <summary>
        /// Strips razor <c>@* *@</c> blocks and C# <c>//</c> line comments, so a rule about what the
        /// page DOES is not defeated - or satisfied - by prose. The page deliberately NAMES the
        /// idioms it refuses to use, in comments, so a future reader knows why; without this the
        /// refusal-lints below would fail on their own documentation.
        /// </summary>
        private static string StripComments(string source)
        {
            var noRazorComments = Regex.Replace(source, @"@\*.*?\*@", " ", RegexOptions.Singleline);

            return string.Join("\n", noRazorComments
                .Split('\n')
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        }

        // ── Item 1: the Detail cell offers a DOWNLOAD, on both tables ────────

        [Fact]
        public void BothDetailCellsOfferADownload_ThroughTheAppsExistingBrowserDownloadHelper()
        {
            var code = StripComments(Markup());

            Assert.Contains("blazorDownloadFile", code, StringComparison.Ordinal);

            // Both tables, not only the preview one Adrian happened to be looking at.
            var affordances = Regex.Matches(code, @"class=""svcfg-dl""").Count;
            Assert.True(affordances >= 2,
                $"expected a download affordance in BOTH Detail cells (preview and apply); found {affordances}");
        }

        /// <summary>
        /// THE TRAP THIS LANE HAD TO AVOID. The app's usual "open the file" idiom is
        /// <c>Process.Start("explorer.exe", "/select,...")</c>, which opens a window on the machine
        /// running the process - under the installed Windows service, on the SERVER, where an
        /// operator driving a browser can never see it. And <c>ServerMode.IsRunning</c>, the only
        /// host-detection idiom in <c>Pages/</c>, reads FALSE on that same installed service: it is
        /// the exact defect that locked Adrian out of /login on 2026-09-08. This page therefore
        /// always offers a download and asks no host question at all.
        /// </summary>
        [Fact]
        public void ThePageOpensNothingOnTheHost_AndAsksNoHostQuestion()
        {
            var code = StripComments(Markup());

            Assert.DoesNotContain("explorer.exe", code, StringComparison.Ordinal);
            Assert.DoesNotContain("Process.Start", code, StringComparison.Ordinal);
            Assert.DoesNotContain("ServerMode.IsRunning", code, StringComparison.Ordinal);
        }

        /// <summary>
        /// The comment stripper must not be the reason the rule above passes. A real call outside a
        /// comment is still caught, and a comment mentioning it is still ignored - both directions
        /// asserted, because a stripper that ate everything would make the lint silently vacuous.
        /// </summary>
        [Fact]
        public void TheCommentStripperStillCatchesARealCall()
        {
            const string withRealCall = "@code {\n    void X() { Process.Start(\"explorer.exe\"); }\n}";
            Assert.Contains("Process.Start", StripComments(withRealCall), StringComparison.Ordinal);

            const string onlyInAComment = "@* never Process.Start(\"explorer.exe\") here *@\n<div>ok</div>";
            Assert.DoesNotContain("Process.Start", StripComments(onlyInAComment), StringComparison.Ordinal);

            const string onlyInALineComment = "@code {\n    // never Process.Start here\n    void X() { }\n}";
            Assert.DoesNotContain("Process.Start", StripComments(onlyInALineComment), StringComparison.Ordinal);

            // …and it leaves the live tokens this file asserts on intact.
            var code = StripComments(Markup());
            Assert.Contains("blazorDownloadFile", code, StringComparison.Ordinal);
            Assert.Contains("RunState.Changed", code, StringComparison.Ordinal);
        }

        // ── Item 2: the run state outlives the page ──────────────────────────

        [Fact]
        public void ThePageBindsToTheScopedRunState_SubscribesOnInit_AndUnsubscribesOnDispose()
        {
            var code = StripComments(Markup());

            Assert.Contains("@inject ServerConfigRunState RunState", code, StringComparison.Ordinal);
            Assert.Contains("@implements IDisposable", code, StringComparison.Ordinal);
            Assert.Contains("RunState.Changed += OnRunStateChanged", code, StringComparison.Ordinal);

            // The unsubscribe is the half that gets forgotten, and forgetting it leaks a dead
            // component into a live circuit's event for as long as the circuit lasts.
            Assert.Contains("RunState.Changed -=", code, StringComparison.Ordinal);
        }

        [Fact]
        public void TheStatusDictionariesAreNoLongerPageFields()
        {
            var code = StripComments(Markup());

            // The exact fields Adrian's complaint was about: page state, destroyed on navigation.
            Assert.DoesNotContain("_mcStatuses", code, StringComparison.Ordinal);
            Assert.DoesNotContain("_mcApplyStatuses", code, StringComparison.Ordinal);

            // The tables read the service instead.
            Assert.Contains("RunState.PreviewStatuses.Values", code, StringComparison.Ordinal);
            Assert.Contains("RunState.ApplyStatuses.Values", code, StringComparison.Ordinal);
        }

        [Fact]
        public void TheLicenceAndRoleGatesThatPrecedeARunAreStillOnThePage()
        {
            // The run loops moved to a service; the gates did NOT move with them. Pinned here as
            // well as in ServerConfigSuiteGateTests because a refactor is exactly when a gate gets
            // carried off with the code it was guarding.
            var code = StripComments(Markup());

            Assert.Contains("!UserState.IsAuthorized(\"run_scripts\")", code, StringComparison.Ordinal);
            Assert.Contains("!Capability.IsGranted", code, StringComparison.Ordinal);
            Assert.Contains("!UserSettings.GetNoPantsMode()", code, StringComparison.Ordinal);
        }

        // ── Item 3: the counts are markup now, not only a toast ──────────────

        [Fact]
        public void TheDoneRefusedFailedTallyIsRenderedAsMarkup_NotOnlyAsAToast()
        {
            var code = StripComments(Markup());

            foreach (var expected in new[]
                     {
                         "RunState.PreviewSummary.Done",
                         "RunState.PreviewSummary.Refused",
                         "RunState.PreviewSummary.Failed",
                         "RunState.ApplySummary.Done",
                         "RunState.ApplySummary.Refused",
                         "RunState.ApplySummary.Failed",
                     })
            {
                Assert.Contains(expected, code, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void BothTablesOfferAConsolidatedViewAndAConsolidatedDownload()
        {
            var code = StripComments(Markup());

            Assert.Contains("TogglePreviewConsolidated", code, StringComparison.Ordinal);
            Assert.Contains("ToggleApplyConsolidated", code, StringComparison.Ordinal);
            Assert.Contains("DownloadPreviewConsolidatedAsync", code, StringComparison.Ordinal);
            Assert.Contains("DownloadApplyConsolidatedAsync", code, StringComparison.Ordinal);

            var panels = Regex.Matches(code, @"class=""svcfg-consolidated""").Count;
            Assert.True(panels >= 2, $"expected a consolidated panel on both tables; found {panels}");
        }

        // ── Presentation lives in the stylesheet, not in the markup ──────────

        [Fact]
        public void EveryNewSelectorIsPrefixed_AndTheSheetIsImported()
        {
            var root = RawPassedScan.RepoRoot().FullName;
            var sheet = Path.Combine(root, "wwwroot", "css", "ServerConfiguration.css");
            Assert.True(File.Exists(sheet), sheet + " is missing");

            var css = File.ReadAllText(sheet);

            // app.css @imports every one of these into the GLOBAL sheet, so a bare selector here
            // restyles the whole app (the hoisted-stylesheet trap, measured 2026-08).
            // Only the text preceding a rule's opening brace is a selector list. Comments are
            // stripped first, or a note inside one would read as a bare selector.
            var stripped = Regex.Replace(css, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            var selectors = Regex.Matches(stripped, @"([^{}]+)\{")
                .SelectMany(m => m.Groups[1].Value.Split(','))
                .Select(v => v.Trim())
                .Where(v => v.Length > 0 && !v.StartsWith("@", StringComparison.Ordinal))
                .ToList();

            Assert.NotEmpty(selectors);

            var bare = selectors.Where(v => !v.StartsWith(".svcfg-", StringComparison.Ordinal)).ToList();
            Assert.True(bare.Count == 0,
                "every selector in ServerConfiguration.css must start .svcfg-; app.css hoists this file "
                + "globally. Offenders: " + string.Join(" | ", bare));

            var appCss = File.ReadAllText(Path.Combine(root, "wwwroot", "css", "app.css"));
            Assert.Contains("@import \"ServerConfiguration.css\";", appCss, StringComparison.Ordinal);
        }

        [Fact]
        public void ThePageAddedNoInlineStyleAttribute()
        {
            // UiDebtRatchetTests freezes this file at 75 and fails on growth. Pinned here too so the
            // lane that touches this page sees the number in its own filter.
            var path = Path.Combine(RawPassedScan.RepoRoot().FullName, "Pages", "ServerConfiguration.razor");
            var count = Regex.Matches(File.ReadAllText(path), @"style=[""']").Count;

            Assert.True(count <= 75, $"Pages/ServerConfiguration.razor is frozen at 75 inline style attributes; measured {count}");
        }

        // ── Gate finding F4: the arm is consumed only by a run that happened ─────

        /// <summary>
        /// <b>Gate finding F4, 2026-09-08.</b> <c>RunApplyAsync</c> returns false WITHOUT touching
        /// anything when an apply is already in flight in this circuit. The three arm-reset
        /// statements sat outside the <c>if (ran)</c> block, so that refusal silently threw away an
        /// arm the operator had ticked - the checkbox, the frozen instance set and the frozen
        /// operator name - for a run that never started. Preview already returns early on
        /// <c>!ran</c>; this asserts apply keeps the same rule.
        ///
        /// <para>This is a PLACEMENT claim, which is why it is a lint and not a render test: a
        /// render shows the arm unticked and cannot say which of the two paths unticked it, and
        /// driving the double-run refusal through a rendered page would need a live SQL Server.
        /// Brace-matched from the <c>if (ran)</c> that follows the call, so the answer is about that
        /// block and not about the method. (The interpolated strings inside the block have balanced
        /// braces, so the match is not fooled by them.)</para>
        /// </summary>
        [Fact]
        public void TheApplyArmIsConsumedOnlyWhenTheRunActuallyRan()
        {
            var code = StripComments(Markup());

            var call = code.IndexOf("await RunState.RunApplyAsync(", StringComparison.Ordinal);
            Assert.True(call >= 0,
                "RunMultiInstanceApply no longer calls RunState.RunApplyAsync, so this lint is "
                + "measuring nothing. Re-point it at whatever runs the apply lane.");

            var guard = code.IndexOf("if (ran)", call, StringComparison.Ordinal);
            Assert.True(guard >= 0,
                "The apply path no longer guards on the run's return value. RunApplyAsync returns "
                + "false when an apply is already in flight; without the guard the arm is consumed "
                + "by a run that never happened.");

            var open = code.IndexOf('{', guard);
            Assert.True(open >= 0, "the if (ran) guard has no block");
            var block = BraceBlock(code, open);

            foreach (var statement in new[]
                     {
                         "_mcApplyArmed = false;",
                         "_mcArmedInstances = new List<string>();",
                     })
            {
                Assert.True(block.Contains(statement, StringComparison.Ordinal),
                    $"'{statement}' is not inside the if (ran) block of RunMultiInstanceApply. The "
                    + "single-use arm must be consumed only when the run actually ran: on the "
                    + "already-running refusal the operator's tick, their frozen instance set and "
                    + "their frozen operator name have to survive so they can retry without "
                    + "re-arming. Move the reset inside the guard rather than relaxing this test.");
            }

            // The symmetry the finding named: preview has always returned early on the same refusal.
            Assert.Contains("if (!ran) return;", code, StringComparison.Ordinal);
        }

        // ── operator-picker-mailchain (2026-09-09) ───────────────────────────────────────────

        /// <summary>
        /// The operator is chosen PER INSTANCE now. The old single free-text box could only ever
        /// record one name across a whole armed run, which is the property Adrian asked to drop:
        /// "the operators might be different".
        /// </summary>
        [Fact]
        public void TheOperatorIsChosenPerInstance_FromTheInstancesOwnOperators()
        {
            var code = StripComments(Markup());

            Assert.Contains("RunState.GetOrAddOperatorState(", code, StringComparison.Ordinal);
            Assert.Contains("OnInstanceOperatorChanged(", code, StringComparison.Ordinal);
            Assert.Contains("class=\"svcfg-picker-field\"", code, StringComparison.Ordinal);

            // The single frozen name is gone, and with it the "same name for every instance" rule.
            Assert.DoesNotContain("_mcArmedOperatorName", code, StringComparison.Ordinal);

            // The free-text box survives only as the ZERO-OPERATORS fallback for the single-instance
            // section, which is the baseline script's create path.
            Assert.Contains("_operatorName", code, StringComparison.Ordinal);
        }

        /// <summary>
        /// THE INVARIANT THAT REPLACED THE ARM-TIME SNAPSHOT. The run reads a map frozen before it
        /// starts, so a selection changed mid-run cannot reach it. This is a PLACEMENT claim - the
        /// freeze has to precede the call - which is why it is a lint; ServerConfigRunStateTests owns
        /// the behaviour, and the render tests own the markup.
        /// </summary>
        [Fact]
        public void TheOperatorMapIsFrozenBeforeTheApplyRunStarts()
        {
            var code = StripComments(Markup());

            var freeze = code.IndexOf("RunState.FreezeSelectedOperators(", StringComparison.Ordinal);
            var run = code.IndexOf("await RunState.RunApplyAsync(", StringComparison.Ordinal);

            Assert.True(freeze >= 0, "RunMultiInstanceApply no longer freezes the per-instance operators.");
            Assert.True(run >= 0, "RunMultiInstanceApply no longer calls RunApplyAsync.");
            Assert.True(freeze < run,
                "the operator map is frozen AFTER the run starts, so a selection changed mid-run could "
                + "reach an instance that has not had its turn yet. Freeze first.");

            // Ruling 2, third branch: an armed instance with operators and no choice is refused rather
            // than defaulted. "DBA" is not a safe guess on somebody else's server.
            Assert.Contains("InstancesAwaitingOperatorChoice(", code, StringComparison.Ordinal);
        }

        /// <summary>
        /// NO SQL ON THE PAGE. Every read and the one write live in AgentMailChainProbe, where they
        /// are classified by SqlSafetyValidatorClassifyTests and judged by AgentMailChainProbeTests. A
        /// query inlined here would be covered by neither.
        /// </summary>
        [Fact]
        public void ThePageCarriesNoSqlOfItsOwn()
        {
            var code = StripComments(Markup());

            foreach (var token in new[] { "sysoperators", "sysmail_", "sp_notify_operator", "xp_instance_regread" })
                Assert.DoesNotContain(token, code, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The test send is behind BOTH gates: the run_scripts permission the Apply path already uses,
        /// and a mail chain whose first four links hold. The second half is also proved by render
        /// (Gated/ServerConfigurationRenderTests); the FIRST half is proved here and not there, because
        /// the render harness runs as the desktop host, where AppUserState.IsAuthorized is true by
        /// construction - stated rather than left as an unexplained gap.
        /// </summary>
        [Fact]
        public void TheTestSendIsBehindRunScriptsAndAWorkingConfiguration()
        {
            var code = StripComments(Markup());

            var gate = code.IndexOf("private bool CanSendTest(", StringComparison.Ordinal);
            Assert.True(gate >= 0, "CanSendTest is gone, so the send button's gate is no longer measurable here.");

            var body = code.Substring(gate, Math.Min(600, code.Length - gate));
            Assert.Contains("UserState.IsAuthorized(\"run_scripts\")", body, StringComparison.Ordinal);
            Assert.Contains("LinksOneToFourHold", body, StringComparison.Ordinal);

            // The handler re-checks rather than trusting the button, the same belt-and-braces the
            // Apply handler uses (RunMultiInstanceApply's first line).
            var handler = code.IndexOf("private async Task SendTestAsync(", StringComparison.Ordinal);
            Assert.True(handler >= 0, "SendTestAsync is gone");
            Assert.Contains("UserState.IsAuthorized(\"run_scripts\")",
                code.Substring(handler, Math.Min(400, code.Length - handler)), StringComparison.Ordinal);
        }

        /// <summary>
        /// The probe's own shipped source: the ONE write is a parameterised stored-procedure call and
        /// it is audited. A spliced operator name here would be an injection primitive on a value the
        /// page lets an operator pick (the MDS lesson: one CommandText runs every statement in it).
        /// </summary>
        [Fact]
        public void TheTestSendIsAParameterisedProcedureCall_AndIsAudited()
        {
            var probe = StripCsComments(ProbeSource());

            Assert.Contains("cmd.CommandType = CommandType.StoredProcedure;", probe, StringComparison.Ordinal);
            Assert.Contains("cmd.CommandText = \"msdb.dbo.sp_notify_operator\";", probe, StringComparison.Ordinal);

            foreach (var p in new[] { "\"@profile_name\"", "\"@name\"", "\"@subject\"", "\"@body\"" })
                Assert.Contains(p, probe, StringComparison.Ordinal);

            // Audited on BOTH outcomes, reusing an existing AuditEventType member: appending one
            // breaks the append-only ordinal contract (AuditEventTypeOrdinalContractTests).
            Assert.Contains("_audit?.LogSecurityEvent(", probe, StringComparison.Ordinal);
            Assert.Contains("TestNotificationAuditCategory", probe, StringComparison.Ordinal);
            Assert.DoesNotContain("AuditEventType.", probe, StringComparison.Ordinal);

            // THE NAME IS NEVER COMPOSED. It reaches the command as one literal; no line that
            // mentions it is an interpolated string. (It legitimately appears a second time as the
            // audit entry's Outcome value, which is a record of what accepted the send, not SQL.)
            var composed = probe
                .Split('\n')
                .Where(l => l.Contains("sp_notify_operator", StringComparison.Ordinal)
                            && l.Contains("$\"", StringComparison.Ordinal))
                .ToList();

            Assert.True(composed.Count == 0,
                "the stored-procedure name is built by string interpolation somewhere. One CommandText "
                + "runs every statement in it, so a composed name on this path is an injection "
                + "primitive:\n  " + string.Join("\n  ", composed));
        }

        /// <summary>
        /// The two procedures this lane rejected, absent from the probe's shipped source.
        /// sp_get_sqlagent_properties is DENIED to a non-sysadmin on both rigs where xp_instance_regread
        /// is not, so link 3 would have gone blind on exactly the reduced-privilege connections that
        /// need it.
        ///
        /// <para><b>⚠ CORRECTED after gate finding F3 (2026-09-09).</b> This doc used to say
        /// sp_configure is "blocked outright by SqlSafetyValidator", and the probe's own header said the
        /// same. It is false for THIS file: Validate waives every blocked pattern for the whole batch
        /// when one AllowedExceptions entry matches, and the first entry is
        /// <c>\bSELECT\b.*\bFROM\b\s+sys\.</c> - the shape of the substitute this lane chose. The gate
        /// spliced sp_configure into DatabaseMailXpsSql and SqlSafetyValidatorClassifyTests stayed
        /// 28/28 GREEN. THIS LINT IS THE NET, which is why it is stated here rather than assumed.</para>
        /// </summary>
        [Fact]
        public void TheProbeUsesNeitherRejectedProcedure()
        {
            var probe = StripCsComments(ProbeSource());

            Assert.DoesNotContain("sp_configure", probe, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sp_get_sqlagent_properties", probe, StringComparison.OrdinalIgnoreCase);

            // …and the substitute really is there, so the two assertions above are not passing over an
            // empty file or a stripper that ate everything.
            Assert.Contains("sys.configurations", probe, StringComparison.Ordinal);
            Assert.Contains("xp_instance_regread", probe, StringComparison.Ordinal);
        }

        // ── F4 (2026-09-09): no SQL in the probe is ever BUILT ────────────────────────────
        //
        // The gate replaced the operator name's SqlParameter with a splice into the CommandText and
        // all sixteen lane classes stayed 310/310 green: the tip was correct and nothing held it.
        // These are regexes over one file. They prove what is WRITTEN - not a compiler, not a
        // boundary - and the operator name they protect is user-typed on the free-text branch.

        /// <summary>
        /// Every CommandText the probe assigns is a named constant or a bare string literal. An
        /// interpolation or a concatenation is how a caller-supplied value reaches a statement as text,
        /// and one CommandText runs every statement in the batch it carries (the MDS lesson).
        /// </summary>
        [Fact]
        public void EveryCommandTextInTheProbeIsAConstantOrALiteral_NeverBuilt()
        {
            var probe = StripCsComments(ProbeSource());
            // To END OF LINE, not to the next ";": the bind delegates are expressions ending in a
            // comma, so a splice written as one would slip a ";"-anchored pattern entirely.
            var assignments = Regex.Matches(probe, @"CommandText\s*=\s*([^
]+)");

            Assert.True(assignments.Count >= 2,
                $"expected the read path's and the write path's CommandText, found {assignments.Count}");

            foreach (Match m in assignments)
            {
                var rhs = m.Groups[1].Value.Trim().TrimEnd(';', ',').Trim();

                Assert.True(
                    Regex.IsMatch(rhs, @"^[A-Za-z_][A-Za-z0-9_.]*$") || Regex.IsMatch(rhs, @"^""[^""]*""$"),
                    $"a CommandText is built rather than named, which is how a picked or typed operator "
                    + $"name becomes SQL text: {rhs}");
            }
        }

        /// <summary>
        /// Every SQL constant is a PLAIN verbatim literal. An interpolated one would be invisible to the
        /// rule above, because the CommandText assignment would still read as a bare identifier.
        /// </summary>
        [Fact]
        public void EverySqlConstantInTheProbeIsAPlainVerbatimLiteral()
        {
            var probe = StripCsComments(ProbeSource());

            var declared = Regex.Matches(probe, "const string \\w*Sql\\s*=").Count;
            var verbatim = Regex.Matches(probe, "const string \\w*Sql\\s*=\\s*@\"").Count;

            Assert.Equal(AgentMailChainProbe.ShippedReadStatements.Count, declared);
            Assert.Equal(declared, verbatim);
            Assert.DoesNotContain("$@\"", probe, StringComparison.Ordinal);
        }

        /// <summary>
        /// Every parameter token in the shipped SQL is bound BY NAME in the source. Tokens DECLAREd
        /// inside a batch are T-SQL locals - the two xp_instance_regread reads own their own variables -
        /// and are excluded; everything else is a runtime value and must reach the server as a
        /// parameter.
        /// </summary>
        [Fact]
        public void EveryParameterTokenInTheProbesSqlIsBoundByName()
        {
            var probe = StripCsComments(ProbeSource());
            var checkedTokens = 0;

            foreach (var sql in AgentMailChainProbe.ShippedReadStatements)
            {
                var locals = Regex.Matches(sql, @"DECLARE\s+([^;]+);", RegexOptions.IgnoreCase)
                    .SelectMany(m => Regex.Matches(m.Groups[1].Value, @"@\w+").Select(t => t.Value))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (Match token in Regex.Matches(sql, @"@\w+"))
                {
                    if (locals.Contains(token.Value)) continue;

                    Assert.True(probe.Contains($"\"{token.Value}\"", StringComparison.Ordinal),
                        $"{token.Value} appears in a shipped statement but is bound nowhere, so its value "
                        + "is reaching the server as text");
                    checkedTokens++;
                }
            }

            // The loop is not vacuous: the address, the operator name, the row cap and the profile id
            // all reach the server as parameters.
            Assert.True(checkedTokens >= 4, $"only {checkedTokens} parameter tokens were checked");
        }

        // ── F2/F6/F7 (2026-09-09): the page's own placements ──────────────────────────────

        /// <summary>
        /// GATE FINDING F2. The free-text "operator to create" box belongs to ONE state: a read that
        /// SUCCEEDED and found no operators. It used to sit in an <c>else if (st.Inventory is not
        /// null)</c> branch, which a DENIED read satisfies just as well - so a server nobody could read
        /// was drawn as a server with no operators, and the name typed there reached Apply.
        /// </summary>
        [Fact]
        public void TheFreeTextOperatorBoxIsOfferedOnlyForAReadThatSucceeded()
        {
            var code = StripComments(Markup());

            var box = code.IndexOf("placeholder=\"operator to create\"", StringComparison.Ordinal);
            Assert.True(box > 0, "the multi-instance free-text operator box is gone entirely");

            var branch = code.Substring(Math.Max(0, box - 500), Math.Min(500, box));
            Assert.Contains("OperatorInventoryState.Empty", branch, StringComparison.Ordinal);
            Assert.DoesNotContain("st.Inventory is not null", branch, StringComparison.Ordinal);

            // The single-instance box is guarded the other way round - every state EXCEPT unreadable -
            // because it is also the box for a read that has not happened yet.
            Assert.Contains("single.InventoryState != OperatorInventoryState.Unreadable",
                            code, StringComparison.Ordinal);
        }

        /// <summary>
        /// GATE FINDING F2's second half: Apply. The multi-instance button and the last-moment refusal
        /// both read InstancesAwaitingOperatorChoice, which now includes an unreadable instance; the
        /// single-instance button is gated on its own state, because _operatorName still holds "DBA".
        /// </summary>
        [Fact]
        public void ApplyIsGatedOnAnUnreadableInventoryAsWellAsOnAMissingChoice()
        {
            var code = StripComments(Markup());

            Assert.Contains("SingleOperators.InventoryState == OperatorInventoryState.Unreadable",
                            code, StringComparison.Ordinal);
            Assert.Contains("ArmedInstancesAwaitingChoice.Count > 0", code, StringComparison.Ordinal);

            // The last-moment refusal still runs BEFORE the freeze, so an instance that cannot be
            // applied never reaches a run at all.
            var refusal = code.IndexOf("RunState.InstancesAwaitingOperatorChoice(targets)", StringComparison.Ordinal);
            var freeze = code.IndexOf("RunState.FreezeSelectedOperators(targets)", StringComparison.Ordinal);

            Assert.True(refusal > 0 && freeze > refusal,
                "the awaiting-choice refusal must run before the operator map is frozen");
        }

        /// <summary>
        /// GATE FINDINGS F6 and F7: two things the page read or produced and never rendered. The
        /// fail-safe operator cost a shipped statement and a round trip per instance; the test send's
        /// outcome existed only as a 5,000 ms toast, and a REFUSAL leaves the badge unchanged.
        /// </summary>
        [Fact]
        public void TheFailSafeOperatorAndTheLastSendOutcomeBothHaveARenderSite()
        {
            var code = StripComments(Markup());

            Assert.Contains("svcfg-failsafe", code, StringComparison.Ordinal);
            Assert.Contains("Fail-safe operator:", code, StringComparison.Ordinal);
            Assert.Contains("FailSafeOperator", code, StringComparison.Ordinal);

            Assert.Contains("svcfg-send-result", code, StringComparison.Ordinal);

            // Recorded on BOTH outcomes and on the exception path: a refusal is the one worth keeping.
            Assert.True(Regex.Matches(code, @"RunState\.RecordTestSendResult\(").Count >= 2,
                "the send outcome is parked on only one of the accepted/refused/threw paths");
        }

        private static string ProbeSource()
        {
            var path = Path.Combine(RawPassedScan.RepoRoot().FullName, "Data", "Services", "AgentMailChainProbe.cs");
            Assert.True(File.Exists(path), path + " is missing");
            return File.ReadAllText(path);
        }

        /// <summary>Strips C# block and line comments. The probe NAMES the procedures it refuses to
        /// use, in its header, so without this the refusal lints would fail on their own reasoning.</summary>
        private static string StripCsComments(string source)
        {
            var noBlocks = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);

            return string.Join("\n", noBlocks
                .Split('\n')
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        }

        /// <summary>The braced block starting at <paramref name="openIndex"/>, braces included.</summary>
        private static string BraceBlock(string text, int openIndex)
        {
            var depth = 0;
            for (var i = openIndex; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}' && --depth == 0) return text.Substring(openIndex, i - openIndex + 1);
            }

            return text.Substring(openIndex);
        }
    }
}
