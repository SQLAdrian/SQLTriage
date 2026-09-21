/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Generic;
using SQLTriage.Data;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Remediation;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// The add-missing-index remediation is the FIRST write past sp_configure — it renders
    /// CREATE INDEX from a missing-index DMV candidate. The identifier guard is the security
    /// wall: every object/column name is charset-checked AND bracket-quoted. These tests pin
    /// (1) the guard rejects injection attempts, (2) the gate classifies the rendered CREATE
    /// INDEX as Remediation ONLY under the registered key, and (3) the rendered DDL is well-formed.
    /// </summary>
    public class RemediationCreateIndexTests
    {
        private static Dictionary<string, string> Spec(
            string db = "AppDb", string schema = "dbo", string table = "Orders",
            string name = "IX_Orders_CustomerId_sqlt", string keys = "CustomerId,OrderDate",
            string includes = "Total,Status")
            => new()
            {
                [RemediationOpRenderer.IndexDatabaseParam] = db,
                [RemediationOpRenderer.IndexSchemaParam] = schema,
                [RemediationOpRenderer.IndexTableParam] = table,
                [RemediationOpRenderer.IndexNameParam] = name,
                [RemediationOpRenderer.IndexKeyColumnsParam] = keys,
                [RemediationOpRenderer.IndexIncludedColumnsParam] = includes,
            };

        // ── Happy path: well-formed DDL ─────────────────────────────────────

        [Fact]
        public void TryRenderCreateIndex_ValidSpec_RendersBracketedDdl()
        {
            Assert.True(RemediationOpRenderer.TryRenderCreateIndex(Spec(), out var sql, out var err), err);
            Assert.Equal(
                "CREATE INDEX [IX_Orders_CustomerId_sqlt] ON [dbo].[Orders] ([CustomerId], [OrderDate]) INCLUDE ([Total], [Status]);",
                sql);
        }

        [Fact]
        public void TryRenderDropIndex_IsTheCleanInverse()
        {
            Assert.True(RemediationOpRenderer.TryRenderDropIndex(Spec(), out var sql, out var err), err);
            Assert.Equal("DROP INDEX [IX_Orders_CustomerId_sqlt] ON [dbo].[Orders];", sql);
        }

        [Fact]
        public void TryRenderIndexExistsRead_IsAReadOnlySelect()
        {
            Assert.True(RemediationOpRenderer.TryRenderIndexExistsRead(Spec(), out var sql, out var err), err);
            Assert.Contains("FROM sys.indexes", sql);
            // The exists-read is read-only — it must pass the binary Validate gate.
            Assert.True(SqlSafetyValidator.Validate(sql).IsSafe);
        }

        [Fact]
        public void TryRenderCreateIndex_NoIncludedColumns_OmitsIncludeClause()
        {
            Assert.True(RemediationOpRenderer.TryRenderCreateIndex(Spec(includes: ""), out var sql, out _));
            Assert.DoesNotContain("INCLUDE", sql);
        }

        // ── The identifier guard rejects injection attempts ─────────────────

        [Theory]
        [InlineData("Orders]); DROP TABLE Users; --")] // bracket break-out
        [InlineData("Orders; DROP TABLE Users")]       // statement terminator
        [InlineData("Orders'")]                        // quote
        [InlineData("dbo.Orders")]                     // dotted (would change scope)
        [InlineData("Orders--comment")]                // comment
        [InlineData("")]                               // empty
        public void TryRenderCreateIndex_RejectsUnsafeTable(string maliciousTable)
        {
            Assert.False(RemediationOpRenderer.TryRenderCreateIndex(Spec(table: maliciousTable), out _, out var err));
            Assert.False(string.IsNullOrEmpty(err));
        }

        [Theory]
        [InlineData("Id]); DROP TABLE X; --")]
        [InlineData("Id, (SELECT 1)")]
        [InlineData("Id';")]
        public void TryRenderCreateIndex_RejectsUnsafeColumn(string maliciousKey)
        {
            // A single malicious key column must fail the whole render closed.
            Assert.False(RemediationOpRenderer.TryRenderCreateIndex(Spec(keys: maliciousKey), out _, out var err));
            Assert.False(string.IsNullOrEmpty(err));
        }

        [Fact]
        public void TryResolveIndexSpec_RequiresAtLeastOneKeyColumn()
        {
            Assert.False(RemediationOpRenderer.TryResolveIndexSpec(Spec(keys: ""), out _, out var err));
            Assert.Contains("key column", err);
        }

        [Fact]
        public void TryResolveIndexSpec_RequiresDatabase()
        {
            Assert.False(RemediationOpRenderer.TryResolveIndexSpec(Spec(db: ""), out _, out var err));
            Assert.Contains("database", err);
        }

        // ── The gate: CREATE INDEX is a write, promoted only by the registered key ──

        [Fact]
        public void Validate_BlocksFreeFormCreateAndDropIndex()
        {
            Assert.False(SqlSafetyValidator.Validate("CREATE INDEX [ix] ON [dbo].[t] ([c]);").IsSafe);
            Assert.False(SqlSafetyValidator.Validate("CREATE NONCLUSTERED INDEX [ix] ON [dbo].[t] ([c]);").IsSafe);
            Assert.False(SqlSafetyValidator.Validate("DROP INDEX [ix] ON [dbo].[t];").IsSafe);
        }

        [Fact]
        public void Classify_CreateIndex_IsRemediationOnlyUnderRegisteredKey()
        {
            Assert.True(RemediationOpRenderer.TryRenderCreateIndex(Spec(), out var sql, out _));

            // No context → Blocked (a write is never promoted by text alone).
            Assert.Equal(SqlClassification.Blocked, SqlSafetyValidator.Classify(sql, null));
            // Unregistered key → Blocked.
            Assert.Equal(SqlClassification.Blocked, SqlSafetyValidator.Classify(sql, new RemediationContext("NOTREGISTERED")));
            // The registered ADDMISSINGINDEX key → Remediation.
            Assert.Equal(SqlClassification.Remediation, SqlSafetyValidator.Classify(sql, new RemediationContext("ADDMISSINGINDEX")));
        }

        [Fact]
        public void RepresentativeClassificationRender_ClassifiesAsRemediation()
        {
            // The gate classifies a representative CREATE INDEX (identifier-independent).
            var op = new RemediationOperation { OpKind = RemediationOpKind.CreateIndex };
            Assert.True(RemediationOpRenderer.TryRenderForClassification(op, out var sql, out var err), err);
            Assert.Equal(SqlClassification.Remediation,
                SqlSafetyValidator.Classify(sql, new RemediationContext("ADDMISSINGINDEX")));
        }
    }
}
