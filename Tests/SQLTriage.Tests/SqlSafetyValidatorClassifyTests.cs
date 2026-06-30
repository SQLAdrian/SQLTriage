/* In the name of God, the Merciful, the Compassionate */

using SQLTriage.Data;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Build step 1 of the gated remediation lane: SqlSafetyValidator.Classify.
    /// These tests pin the anti-bypass invariant — Remediation is reachable ONLY
    /// through a registered context, never from SQL text. Validate() is unchanged
    /// and exercised by its own existing tests.
    /// </summary>
    public class SqlSafetyValidatorClassifyTests
    {
        // The one bounded template build step 1 registers.
        private static readonly RemediationContext MaxDopContext = new("MAXDOP");

        private const string ReadQuery =
            "SELECT name, value FROM sys.configurations WHERE name = 'max degree of parallelism';";

        private const string MaxDopWrite =
            "EXEC sp_configure 'max degree of parallelism', 4; RECONFIGURE;";

        // ── Contract case 1: reads are Safe (with or without context) ────────

        [Fact]
        public void Classify_ReadQuery_NoContext_ReturnsSafe()
        {
            Assert.Equal(SqlClassification.Safe, SqlSafetyValidator.Classify(ReadQuery));
        }

        [Fact]
        public void Classify_ReadQuery_WithContext_StillSafe()
        {
            // A context must never downgrade a read; it only ever promotes a blocked write.
            Assert.Equal(SqlClassification.Safe, SqlSafetyValidator.Classify(ReadQuery, MaxDopContext));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Classify_NullOrEmptySql_ReturnsSafe(string? sql)
        {
            // Mirrors Validate(): empty input is Safe (nothing to run).
            Assert.Equal(SqlClassification.Safe, SqlSafetyValidator.Classify(sql!));
        }

        // ── Contract case 2: free-form write → Blocked ──────────────────────

        [Fact]
        public void Classify_FreeFormSpConfigure_NoContext_ReturnsBlocked()
        {
            Assert.Equal(SqlClassification.Blocked, SqlSafetyValidator.Classify(MaxDopWrite));
        }

        [Theory]
        [InlineData("GRANT CONTROL SERVER TO [evil];")]
        [InlineData("ALTER LOGIN sa WITH PASSWORD = 'x';")]
        [InlineData("DROP DATABASE payroll;")]
        [InlineData("EXEC xp_cmdshell 'whoami';")]
        public void Classify_FreeFormDangerousWrites_NoContext_ReturnsBlocked(string sql)
        {
            Assert.Equal(SqlClassification.Blocked, SqlSafetyValidator.Classify(sql));
        }

        // ── Contract case 3: registered MAXDOP template → Remediation ───────

        [Fact]
        public void Classify_MaxDopWrite_WithRegisteredContext_ReturnsRemediation()
        {
            Assert.Equal(SqlClassification.Remediation, SqlSafetyValidator.Classify(MaxDopWrite, MaxDopContext));
        }

        // ── Contract case 4 (THE WALL): write text without a registered ──────
        //    template context can NEVER reach Remediation. ────────────────────

        [Fact]
        public void Classify_MaxDopWrite_NoContext_StaysBlocked()
        {
            Assert.Equal(SqlClassification.Blocked, SqlSafetyValidator.Classify(MaxDopWrite, null));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void Classify_MaxDopWrite_EmptyTemplateKey_StaysBlocked(string key)
        {
            // An empty/whitespace key is not an authorisation — the wall holds.
            Assert.Equal(SqlClassification.Blocked, SqlSafetyValidator.Classify(MaxDopWrite, new RemediationContext(key)));
        }

        [Theory]
        [InlineData("NOT-A-TEMPLATE")]
        [InlineData("DROPDB")]
        [InlineData("maxdop")]   // key match is case-sensitive; lower-case is NOT the registered key
        [InlineData(" MAXDOP ")] // padded key is not the registered key
        public void Classify_MaxDopWrite_UnregisteredKey_StaysBlocked(string key)
        {
            // Only an exact, registered key promotes a blocked write to Remediation.
            // Anything else — including near-misses — stays Blocked.
            Assert.Equal(SqlClassification.Blocked, SqlSafetyValidator.Classify(MaxDopWrite, new RemediationContext(key)));
        }

        [Fact]
        public void Classify_UnrelatedWrite_WithRegisteredContext_PromotesToRemediation_DocumentsStep1Boundary()
        {
            // STEP-1 BOUNDARY (deliberate, documented): Classify keys on REGISTRATION
            // only. It does NOT yet verify that the SQL text matches the template's own
            // SQL — that text↔template binding is gate 1/5 in the RemediationRunner
            // (build step 4). So at step 1 a registered context promotes ANY blocked
            // write it is paired with.
            //
            // This is safe because nothing executes a Remediation classification yet:
            // there is no runner. The wall that matters today — "no context, no
            // promotion" — is fully enforced and covered above. This test pins the
            // current behaviour so the later text↔template gate is an intentional,
            // visible tightening rather than a silent change.
            const string unrelated = "DROP DATABASE payroll;";
            Assert.Equal(SqlClassification.Remediation, SqlSafetyValidator.Classify(unrelated, MaxDopContext));
        }

        // ── Validate(): read-path coverage (batch-level exemption is intentional; see Validate) ──

        [Theory]
        // A sys.* read that contains a blocked keyword as DATA (definition scan / permission read /
        // multi-read batch) stays Safe — trusted diagnostic scripts rely on this.
        [InlineData("SELECT definition FROM sys.sql_modules WHERE definition LIKE '%xp_cmdshell%';")]
        [InlineData("SELECT * FROM sys.database_permissions WHERE state_desc = 'GRANT';")]
        [InlineData("SELECT 1 FROM sys.databases; SELECT 2 FROM sys.tables;")]
        [InlineData("SELECT name, value FROM sys.configurations;")]
        public void Validate_LegitSysReads_StaySafe(string sql)
            => Assert.True(SqlSafetyValidator.Validate(sql).IsSafe);

        [Theory]
        // A free-form write with no sys.* read present is blocked.
        [InlineData("EXEC sp_configure 'show advanced options', 1; RECONFIGURE;")]
        [InlineData("DROP DATABASE payroll;")]
        public void Validate_PlainWrites_AreBlocked(string sql)
            => Assert.False(SqlSafetyValidator.Validate(sql).IsSafe);

        // MANDATORY GUARD: every shipped diagnostic script must pass Validate. The prior
        // per-statement hardening attempt broke sp_Blitz and NO test caught it — this is that
        // test. It also documents WHY the wall stays batch-level: sp_Blitz/sp_triage genuinely
        // EXECUTE xp_cmdshell and usp_bpcheck toggles it via RECONFIGURE; a per-statement wall
        // blocks these trusted diagnostics. (Validate only ever sees trusted shipped SQL.)
        [Fact]
        public void EveryShippedScript_StillPassesValidate()
        {
            var dir = FindScriptsDir();
            Assert.True(dir != null, "Could not locate the shipped scripts directory.");
            var files = System.IO.Directory.GetFiles(dir!, "*.sql", System.IO.SearchOption.AllDirectories);
            Assert.NotEmpty(files);

            var failures = new System.Collections.Generic.List<string>();
            foreach (var f in files)
            {
                var result = SqlSafetyValidator.Validate(System.IO.File.ReadAllText(f));
                if (!result.IsSafe)
                    failures.Add($"{System.IO.Path.GetFileName(f)}: {result.Reason} [{result.MatchedPattern}]");
            }
            Assert.True(failures.Count == 0,
                "Shipped scripts blocked by Validate (statement-aware hardening regression):\n" + string.Join("\n", failures));
        }

        private static string? FindScriptsDir()
        {
            // Prefer the scripts copied next to the test binary; else walk up to the repo's scripts/.
            var local = System.IO.Path.Combine(System.AppContext.BaseDirectory, "scripts");
            if (System.IO.Directory.Exists(local)
                && System.IO.Directory.GetFiles(local, "*.sql", System.IO.SearchOption.AllDirectories).Length > 0)
                return local;
            var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = System.IO.Path.Combine(dir.FullName, "scripts");
                if (System.IO.File.Exists(System.IO.Path.Combine(candidate, "sp_Blitz.sql")))
                    return candidate;
                dir = dir.Parent;
            }
            return null;
        }
    }
}
