/* In the name of God, the Merciful, the Compassionate */

using SQLTriage.Data.Parser;
using Xunit;

namespace SQLTriage.Tests.Parser
{
    /// <summary>B1 §3 derivation rules — branch coverage.</summary>
    public class DerivationsTests
    {
        // ─────────── ExpectedValue (§3.1) ───────────

        [Fact]
        public void ExpectedValue_yaml_int_in_prose_is_used()
        {
            var (v, info) = Derivations.DeriveExpectedValue("X", "Returns 1 when bad.", "");
            Assert.Equal(1, v);
            Assert.Null(info); // YAML-sourced, no derivation info
        }

        [Fact]
        public void ExpectedValue_default_zero_when_yaml_absent()
        {
            var (v, info) = Derivations.DeriveExpectedValue("X", null, "SELECT 1");
            Assert.Equal(0, v);
            Assert.NotNull(info);
            Assert.Equal("ExpectedValue", info!.Field);
        }

        [Fact]
        public void ExpectedValue_default_zero_when_yaml_lacks_int()
        {
            var (v, info) = Derivations.DeriveExpectedValue("X", "Returns PASS when clean.", "");
            Assert.Equal(0, v);
            Assert.NotNull(info);
        }

        // ─────────── ExecutionType (§3.2) ───────────

        [Fact]
        public void ExecutionType_text_path_when_PassFail_with_SELECT_PASS()
        {
            var (v, _) = Derivations.DeriveExecutionType("X",
                "DECLARE @r VARCHAR(10); SELECT 'PASS' AS result;",
                "PassFail", null);
            Assert.Equal("Scalar", v);
        }

        [Fact]
        public void ExecutionType_binary_when_CASE_WHEN_THEN_1_ELSE_0()
        {
            var (v, info) = Derivations.DeriveExecutionType("X",
                "SELECT CASE WHEN EXISTS(SELECT 1) THEN 1 ELSE 0 END",
                null, null);
            Assert.Equal("Binary", v);
            Assert.NotNull(info);
        }

        [Fact]
        public void ExecutionType_rowcount_when_rowCountCondition_present()
        {
            var (v, _) = Derivations.DeriveExecutionType("X",
                "SELECT * FROM sys.databases", null, "greater_than");
            Assert.Equal("RowCount", v);
        }

        [Fact]
        public void ExecutionType_safe_default_Scalar()
        {
            var (v, info) = Derivations.DeriveExecutionType("X", "SELECT 1", null, null);
            Assert.Equal("Scalar", v);
            Assert.NotNull(info);
        }

        // ─────────── ScoreWeight (§3.3) ───────────

        [Fact]
        public void ScoreWeight_yaml_value_passthrough_clamped()
        {
            var (v, info) = Derivations.DeriveScoreWeight("X", 60, "High", "high");
            Assert.Equal(50, v); // clamp upper
            Assert.Null(info);   // YAML-sourced

            (v, _) = Derivations.DeriveScoreWeight("X", -5, "High", "high");
            // negative falls through to derivation; severity High × high effort = 15 × 2.0 = 30
            Assert.Equal(30, v);
        }

        [Theory]
        [InlineData("Critical", "low", 25)]    // 25 × 1.0
        [InlineData("Critical", "high", 50)]   // 25 × 2.0 (clamp at 50)
        [InlineData("High", "medium", 22)] // 15 × 1.5 = 22.5 → 22 (banker's: round-half-to-even, .NET default)
        [InlineData("High", "low", 15)]
        [InlineData("Medium", "high", 20)]   // 10 × 2.0
        [InlineData("Low", "low", 5)]
        [InlineData("Low", "high", 10)]   // 5 × 2.0
        [InlineData("UNKNOWN", null, 10)]     // unknown sev → medium baseline (10) × 1.0
        public void ScoreWeight_derived_table(string sev, string? effort, int expected)
        {
            var (v, info) = Derivations.DeriveScoreWeight("X", null, sev, effort);
            Assert.Equal(expected, v);
            Assert.NotNull(info);
        }

        // ─────────── IsBad (§3.4) ───────────

        [Fact]
        public void IsBad_yaml_true_passthrough() =>
            Assert.True(Derivations.DeriveIsBad("X", true).value);

        [Fact]
        public void IsBad_yaml_false_passthrough() =>
            Assert.False(Derivations.DeriveIsBad("X", false).value);

        [Fact]
        public void IsBad_default_false_when_absent()
        {
            var (v, info) = Derivations.DeriveIsBad("X", null);
            Assert.False(v);
            Assert.NotNull(info);
        }

        // ─────────── ResultInterpretation (added 2026-05-21) ───────────

        [Fact]
        public void ResultInterpretation_null_when_sql_has_no_text_tokens()
        {
            var (v, info) = Derivations.DeriveResultInterpretation("X",
                "SELECT CASE WHEN EXISTS(SELECT 1) THEN 1 ELSE 0 END");
            Assert.Null(v);
            Assert.Null(info);
        }

        [Theory]
        [InlineData("SELECT 'PASS' AS result UNION ALL SELECT 'FAIL'", "PassFail")]
        [InlineData("SELECT 'PASS'; SELECT 'WARN'; SELECT 'FAIL'", "PassWarnFail")]
        [InlineData("SELECT 'PASS' UNION SELECT 'FAIL' UNION SELECT 'SKIP'", "PassFailSkip")]
        [InlineData("SELECT 'PASS' UNION SELECT 'INFO'", "PassInfo")]
        [InlineData("SELECT @r = 'PASS'", "PassFail")] // single token → conservative PassFail
        [InlineData("SELECT N'PASS' AS result", "PassFail")] // unicode prefix
        public void ResultInterpretation_derived_from_sql_text_tokens(string sql, string expected)
        {
            var (v, info) = Derivations.DeriveResultInterpretation("X", sql);
            Assert.Equal(expected, v);
            Assert.NotNull(info);
        }

        [Fact]
        public void ResultInterpretation_ignores_pass_in_unrelated_context()
        {
            // 'PASS' must appear inside SELECT 'PASS' to count; column names, comments don't trigger
            var (v, _) = Derivations.DeriveResultInterpretation("X",
                "SELECT PassRate FROM dbo.MyTable -- check PASS rate trend");
            Assert.Null(v);
        }

        // Reproducer for the 2026-05-21 audit-run residual: 158 checks reported
        // resultInterpretation=null + crash "input string 'INFO' was not in a
        // correct format" despite the SQL emitting SELECT 'SKIP' / 'INFO'.
        [Fact]
        public void ResultInterpretation_handles_CASE_WHEN_THEN_token_shape()
        {
            // The 2026-05-22 runtime bug: 158 corpus checks use the CASE-WHEN
            // shape, not direct SELECT 'token'. Earlier regex missed them.
            const string sql = "DECLARE @cnt INT; SELECT @cnt = COUNT(*) FROM tbl; " +
                "SELECT CASE WHEN @cnt = 0 THEN 'PASS' ELSE 'INFO' END AS result, " +
                "CASE WHEN @cnt = 0 THEN 'OK' ELSE CONCAT(@cnt,' items') END AS message;";
            var (v, info) = Derivations.DeriveResultInterpretation("X", sql);
            Assert.NotNull(v);
            Assert.Contains("Pass", v);
            Assert.NotNull(info);
        }

        [Fact]
        public void ResultInterpretation_ignores_tokens_inside_line_comments()
        {
            // Comment-strip means 'PASS' inside a -- comment does NOT trigger.
            const string sql = "SELECT 1 -- the 'PASS' threshold for this metric";
            var (v, _) = Derivations.DeriveResultInterpretation("X", sql);
            Assert.Null(v);
        }

        [Fact]
        public void ResultInterpretation_handles_real_BPCHECK_001_multi_statement_sql()
        {
            const string sql = "DECLARE @ver INT, @start DATETIME, @uptime_hours INT, @result VARCHAR(10), @msg NVARCHAR(1000);\nSET @ver=CONVERT(INT,SERVERPROPERTY('ProductMajorVersion')); IF @ver<10 BEGIN SELECT 'SKIP' AS result,CONCAT('Requires 2008+. Current: ',@ver) AS message; RETURN; END;\nSELECT @start=sqlserver_start_time FROM sys.dm_os_sys_info;\nSET @uptime_hours=DATEDIFF(HOUR,@start,GETDATE());\nSELECT 'INFO' AS result, CONCAT('Uptime: ',@uptime_hours,' hours.') AS message;";
            var (v, info) = Derivations.DeriveResultInterpretation("BPCHK_001", sql);
            Assert.NotNull(v);   // MUST NOT be null — SQL emits SKIP and INFO literals
            Assert.NotNull(info);
        }
    }
}
