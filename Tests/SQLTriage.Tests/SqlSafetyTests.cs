/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using System.Linq;
using SQLTriage.Data;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Pins the write-safety seams on the SQL lanes. Each of these guards something that was
    /// previously asserted only in a comment:
    ///   • the @ForChangeControl substitution that decides preview vs APPLY,
    ///   • the GO batch splitter that must not cut a line-leading GOTO,
    ///   • the session-safety preamble applied to production reads.
    /// </summary>
    public class SqlSafetyTests
    {
        // ── @ForChangeControl substitution ──────────────────────────────────

        // Verbatim shape of the shipped declare (TAB separator, APPLY side).
        private const string ShippedDeclare = "DECLARE\t@ForChangeControl\tBIT = 0";

        [Fact]
        public void CountChangeControlDeclares_ShippedShape_IsExactlyOne()
        {
            Assert.Equal(1, ServerConfigScriptService.CountChangeControlDeclares(ShippedDeclare));
        }

        [Theory]
        // A reformatted or renamed declare the pattern no longer recognises. Each of these
        // returns 0, which is what makes the substitution a silent no-op — and a no-op leaves
        // the script on its shipped APPLY default.
        [InlineData("DECLARE @ForChangeControl BIT = 2")]          // out-of-range value
        [InlineData("DECLARE @ForChangeControl AS BIT = 0")]       // AS keyword
        [InlineData("DECLARE @ForChangeControlMode BIT = 0")]      // renamed
        [InlineData("DECLARE @ForChangeControl TINYINT = 0")]      // retyped
        public void CountChangeControlDeclares_ReformattedDeclare_IsZero(string sql)
        {
            Assert.Equal(0, ServerConfigScriptService.CountChangeControlDeclares(sql));
        }

        [Theory]
        // IgnoreCase: T-SQL is not case-sensitive about BIT, so a lowercase declare must still
        // be found rather than counted as an absent one.
        [InlineData("DECLARE @ForChangeControl bit = 0")]
        [InlineData("DECLARE @ForChangeControl Bit = 0")]
        public void CountChangeControlDeclares_IsCaseInsensitive(string sql)
        {
            Assert.Equal(1, ServerConfigScriptService.CountChangeControlDeclares(sql));
        }

        [Fact]
        public void CountChangeControlDeclares_ShippedScriptFile_IsExactlyOne()
        {
            var scriptPath = FindRepoFile(Path.Combine("ConfigScripts", "Server Configuration and Hardening.sql"));

            // Not a soft skip: if the script cannot be located the test fails, so this can never
            // pass by reading nothing.
            Assert.True(scriptPath != null, "Could not locate ConfigScripts/Server Configuration and Hardening.sql from the test output directory.");

            var sql = File.ReadAllText(scriptPath!);
            Assert.Equal(1, ServerConfigScriptService.CountChangeControlDeclares(sql));
        }

        // ── GO batch splitter ───────────────────────────────────────────────

        [Fact]
        public void SqlGoBatchSplitter_DoesNotCutLineLeadingGoto()
        {
            // The shape the SQLWATCH post-deploy job scripts use. A substring split on "\nGO"
            // cuts "GOTO EndSave" into "GO" + "TO EndSave"; the anchored line split must not.
            const string sql =
                "BEGIN TRANSACTION\r\n" +
                "IF (@@ERROR <> 0) GOTO QuitWithRollback\r\n" +
                "COMMIT TRANSACTION\r\n" +
                "GOTO EndSave\r\n" +
                "QuitWithRollback:\r\n" +
                "    IF (@@TRANCOUNT > 0) ROLLBACK TRANSACTION\r\n" +
                "EndSave:\r\n";

            var batches = SqlGoBatchSplitter.Split(sql);

            Assert.Single(batches);
            Assert.Contains("GOTO EndSave", batches[0]);
            Assert.Contains("GOTO QuitWithRollback", batches[0]);
        }

        [Fact]
        public void SqlGoBatchSplitter_SplitsOnRealGoLines()
        {
            // Control for the test above: the splitter DOES split when GO is a whole line, so
            // the single-batch assertion there is a real negative rather than a broken pattern.
            const string sql = "SELECT 1\r\nGO\r\nSELECT 2\r\nGO\r\nSELECT 3\r\n";

            var batches = SqlGoBatchSplitter.Split(sql).Where(b => !string.IsNullOrWhiteSpace(b)).ToArray();

            Assert.Equal(3, batches.Length);
        }

        [Theory]
        [InlineData("SELECT 1\r\ngo\r\nSELECT 2\r\n")]          // lowercase
        [InlineData("SELECT 1\r\n  GO  \r\nSELECT 2\r\n")]      // indented / trailing space
        [InlineData("SELECT 1\r\nGO -- next batch\r\nSELECT 2\r\n")] // trailing comment
        public void SqlGoBatchSplitter_HandlesGoLineVariants(string sql)
        {
            var batches = SqlGoBatchSplitter.Split(sql).Where(b => !string.IsNullOrWhiteSpace(b)).ToArray();

            Assert.Equal(2, batches.Length);
        }

        // ── Session safety preamble ─────────────────────────────────────────

        [Fact]
        public void SessionSafety_DefaultPrefix_CarriesBothControls()
        {
            var prefix = SqlSessionSafety.BuildPrefix();

            Assert.Contains("SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;", prefix);
            Assert.Contains("SET LOCK_TIMEOUT 5000;", prefix);
            Assert.Contains("SET ANSI_WARNINGS ON;", prefix);
        }

        [Fact]
        public void SessionSafety_NegativeLockTimeout_LeavesServerDefault()
        {
            var prefix = SqlSessionSafety.BuildPrefix(readUncommitted: true, lockTimeoutMs: -1);

            Assert.DoesNotContain("SET LOCK_TIMEOUT", prefix);
            Assert.Contains("SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;", prefix);
        }

        [Fact]
        public void SessionSafety_ReadUncommittedOff_OmitsIsolationChange()
        {
            var prefix = SqlSessionSafety.BuildPrefix(readUncommitted: false, lockTimeoutMs: 5000);

            Assert.DoesNotContain("READ UNCOMMITTED", prefix);
            Assert.Contains("SET LOCK_TIMEOUT 5000;", prefix);
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        /// <summary>
        /// Walks up from the test output directory looking for a repo-relative file. Returns null
        /// when it is not found anywhere on the way up (the caller asserts on that).
        /// </summary>
        private static string? FindRepoFile(string relativePath)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 10 && dir != null; i++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, relativePath);
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }
    }
}
