/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Remediation;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// The structured-op renderer is the SINGLE SOURCE the gate classifies and the executor
    /// runs — so "the gate vets what runs" reduces to "render is pure + injection-free + its
    /// output classifies as Remediation under the key". These tests pin exactly that, plus the
    /// value resolution/bounds the executor relies on.
    /// </summary>
    public class RemediationOpRendererTests
    {
        private static RemediationOperation MaxDopOp() => new()
        {
            OpKind = RemediationOpKind.SpConfigure,
            ConfigName = "max degree of parallelism",
            AdvancedOption = true,
            ValueParam = "MaxDop",
            MinValue = 0,
            MaxValue = 64,
        };

        private static Dictionary<string, string> P(params (string k, string v)[] ps)
        {
            var d = new Dictionary<string, string>();
            foreach (var (k, v) in ps) d[k] = v;
            return d;
        }

        // ── Render produces the exact bounded sp_configure batch ────────────

        [Fact]
        public void Render_MaxDop_ProducesAdvancedOptionThenSettingThenReconfigure()
        {
            Assert.True(RemediationOpRenderer.TryRender(MaxDopOp(), 4, out var sql, out var err));
            Assert.Equal(string.Empty, err);
            Assert.Contains("EXEC sp_configure 'show advanced options', 1; RECONFIGURE;", sql);
            Assert.Contains("EXEC sp_configure 'max degree of parallelism', 4; RECONFIGURE;", sql);
        }

        [Fact]
        public void Render_NonAdvancedOption_OmitsShowAdvancedOptions()
        {
            var op = MaxDopOp();
            op.AdvancedOption = false;
            Assert.True(RemediationOpRenderer.TryRender(op, 2, out var sql, out _));
            Assert.DoesNotContain("show advanced options", sql);
            Assert.Contains("EXEC sp_configure 'max degree of parallelism', 2; RECONFIGURE;", sql);
        }

        // ── The rendered change classifies as Remediation under the key (gate-1 proof) ──

        [Fact]
        public void RenderedChange_ClassifiesAsRemediation_UnderRegisteredKey()
        {
            Assert.True(RemediationOpRenderer.TryRender(MaxDopOp(), 4, out var sql, out _));
            // The rendered change is a write Validate() blocks, promoted ONLY by the registered context.
            Assert.Equal(SqlClassification.Blocked, SqlSafetyValidator.Classify(sql));
            Assert.Equal(SqlClassification.Remediation,
                SqlSafetyValidator.Classify(sql, new RemediationContext("MAXDOP")));
        }

        [Fact]
        public void ClassificationRender_IsValueIndependent()
        {
            // The gate classifies a representative render; the value must not change the verdict.
            Assert.True(RemediationOpRenderer.TryRenderForClassification(MaxDopOp(), out var repSql, out _));
            Assert.True(RemediationOpRenderer.TryRender(MaxDopOp(), 64, out var hiSql, out _));
            var ctx = new RemediationContext("MAXDOP");
            Assert.Equal(SqlClassification.Remediation, SqlSafetyValidator.Classify(repSql, ctx));
            Assert.Equal(SqlClassification.Remediation, SqlSafetyValidator.Classify(hiSql, ctx));
        }

        // ── Derived read query is a single-statement Safe read (no smuggling) ──

        [Fact]
        public void RenderRead_ProducesSingleStatementSysConfigurationsRead_ClassifiesSafe()
        {
            Assert.True(RemediationOpRenderer.TryRenderRead(MaxDopOp(), out var sql, out _));
            Assert.Equal("SELECT value_in_use FROM sys.configurations WHERE name = 'max degree of parallelism';", sql);
            // It is read-only Safe (and stays Safe even with a registered context — Classify never downgrades a read).
            Assert.True(SqlSafetyValidator.Validate(sql).IsSafe);
            Assert.Equal(SqlClassification.Safe, SqlSafetyValidator.Classify(sql, new RemediationContext("MAXDOP")));
        }

        // ── Every shipped Configuration template's op is well-formed end to end ──

        [Fact]
        public void AllShippedConfigTemplates_RenderAuthorisedWrites_AndSafeDerivedReads()
        {
            var store = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance);
            int configTemplates = 0;
            foreach (var t in store.All())
            {
                if (t.Kind != RemediationKind.Configuration || t.Operation is null) continue;
                configTemplates++;
                var ctx = new RemediationContext(t.Key);

                // The representative render classifies as an authorised write under the key.
                Assert.True(RemediationOpRenderer.TryRenderForClassification(t.Operation, out var write, out var e1), $"{t.Key}: {e1}");
                Assert.Equal(SqlClassification.Remediation, SqlSafetyValidator.Classify(write, ctx, store.RegisteredKeys()));

                // The derived read is a single-statement Safe read.
                Assert.True(RemediationOpRenderer.TryRenderRead(t.Operation, out var read, out var e2), $"{t.Key}: {e2}");
                Assert.True(SqlSafetyValidator.Validate(read).IsSafe, $"{t.Key} read not safe: {read}");

                // Both bounds render (so the operator-chosen value across the range is applyable).
                Assert.True(RemediationOpRenderer.TryRender(t.Operation, t.Operation.MinValue, out _, out _), $"{t.Key} min");
                Assert.True(RemediationOpRenderer.TryRender(t.Operation, t.Operation.MaxValue, out _, out _), $"{t.Key} max");
            }
            Assert.True(configTemplates >= 4, $"expected >= 4 shipped Configuration templates, saw {configTemplates}");
        }

        [Fact]
        public void RenderRead_UnsafeConfigName_Refuses()
        {
            var op = MaxDopOp();
            op.ConfigName = "x'; EXEC sp_configure 'clr enabled',1; --";
            Assert.False(RemediationOpRenderer.TryRenderRead(op, out var sql, out var err));
            Assert.Equal(string.Empty, sql);
            Assert.False(string.IsNullOrEmpty(err));
        }

        // ── Value resolution + bounds (what the executor enforces) ──────────

        [Theory]
        [InlineData(0, true)]
        [InlineData(64, true)]
        [InlineData(-1, false)]
        [InlineData(65, false)]
        public void ResolveValue_EnforcesBounds(int value, bool ok)
        {
            var resolved = RemediationOpRenderer.TryResolveValue(
                MaxDopOp(), P(("MaxDop", value.ToString())), out var got, out var err);
            Assert.Equal(ok, resolved);
            if (ok) { Assert.Equal(value, got); Assert.Equal(string.Empty, err); }
            else Assert.False(string.IsNullOrEmpty(err));
        }

        [Theory]
        [InlineData("lots")]
        [InlineData("")]
        public void ResolveValue_NonIntegerOrMissing_Fails(string raw)
        {
            Assert.False(RemediationOpRenderer.TryResolveValue(MaxDopOp(), P(("MaxDop", raw)), out _, out var err));
            Assert.False(string.IsNullOrEmpty(err));
        }

        [Fact]
        public void ResolveValue_MissingParam_Fails()
        {
            Assert.False(RemediationOpRenderer.TryResolveValue(MaxDopOp(), P(), out _, out var err));
            Assert.False(string.IsNullOrEmpty(err));
        }

        // ── Injection guard: a crafted config name (overlay) can't render ───

        [Theory]
        [InlineData("max degree'; DROP DATABASE x; --")]
        [InlineData("xp_cmdshell")] // underscore not in the safe charset
        [InlineData("")]
        public void Render_UnsafeConfigName_Refuses(string name)
        {
            var op = MaxDopOp();
            op.ConfigName = name;
            Assert.False(RemediationOpRenderer.TryRender(op, 1, out var sql, out var err));
            Assert.Equal(string.Empty, sql);
            Assert.False(string.IsNullOrEmpty(err));
        }

        // ── value_fixed: a fixed target resolves without any request parameter (task #39) ──

        [Fact]
        public void ResolveValue_ValueFixed_ResolvesWithoutAnyParameter()
        {
            var op = new RemediationOperation
            {
                OpKind = RemediationOpKind.SpConfigure,
                ConfigName = "xp_cmdshell",
                AdvancedOption = true,
                ValueFixed = 0,
            };
            Assert.True(RemediationOpRenderer.TryResolveValue(op, P(), out var value, out var err));
            Assert.Equal(0, value);
            Assert.Equal(string.Empty, err);
        }

        // ── DbSetOption op: per-database ALTER DATABASE ... SET (task #39) ──────────────────

        [Fact]
        public void RenderDbSetOption_ProducesBracketQuotedAlterDatabase()
        {
            Assert.True(RemediationOpRenderer.TryRenderDbSetOption("MyDb", "SET DB_CHAINING OFF", out var sql, out var err));
            Assert.Equal("ALTER DATABASE [MyDb] SET DB_CHAINING OFF;", sql);
            Assert.Equal(string.Empty, err);
        }

        [Theory]
        [InlineData("My'Db]; DROP TABLE x; --")]
        [InlineData("")]
        public void RenderDbSetOption_UnsafeDatabaseName_Refuses(string dbName)
        {
            Assert.False(RemediationOpRenderer.TryRenderDbSetOption(dbName, "SET DB_CHAINING OFF", out var sql, out var err));
            Assert.Equal(string.Empty, sql);
            Assert.False(string.IsNullOrEmpty(err));
        }

        [Fact]
        public void RenderDbSetOption_UnsafeOptionSql_Refuses()
        {
            Assert.False(RemediationOpRenderer.TryRenderDbSetOption("MyDb", "SET X OFF; DROP TABLE y; --", out var sql, out var err));
            Assert.Equal(string.Empty, sql);
            Assert.False(string.IsNullOrEmpty(err));
        }

        [Theory]
        [InlineData("SET DB_CHAINING OFF", "SET DB_CHAINING ON")]
        [InlineData("SET AUTO_UPDATE_STATISTICS ON", "SET AUTO_UPDATE_STATISTICS OFF")]
        public void InvertBooleanOptionSql_TogglesSimpleOnOff(string original, string expectedInverse)
        {
            Assert.True(RemediationOpRenderer.TryInvertBooleanOptionSql(original, out var inverted));
            Assert.Equal(expectedInverse, inverted);
        }

        [Theory]
        [InlineData("SET QUERY_STORE = ON (OPERATION_MODE = READ_WRITE)")] // compound target, >2 states
        [InlineData("SET PAGE_VERIFY CHECKSUM WITH NO_WAIT")]              // 3-way enum, not ON/OFF
        [InlineData("SET AUTO_CREATE_STATISTICS ON (INCREMENTAL = ON)")]   // compound suboption
        public void InvertBooleanOptionSql_RefusesCompoundClauses(string optionSql)
        {
            // No fabricated inverse for shapes with more than two states — DD-honest fail-closed.
            Assert.False(RemediationOpRenderer.TryInvertBooleanOptionSql(optionSql, out var inverted));
            Assert.Equal(string.Empty, inverted);
        }

        [Fact]
        public void DbSetOption_RepresentativeClassification_IsRemediation_UnderRegisteredKey()
        {
            var op = new RemediationOperation
            {
                OpKind = RemediationOpKind.DbSetOption,
                OptionSql = "SET DB_CHAINING OFF",
                OffendersQuery = "SELECT name FROM sys.databases WHERE is_db_chaining_on = 1;",
            };
            Assert.True(RemediationOpRenderer.TryRenderForClassification(op, out var sql, out var err));
            Assert.Equal(string.Empty, err);
            Assert.Contains("ALTER DATABASE", sql);

            // Free-form (no context) is Blocked; a registered key promotes it to Remediation —
            // same anti-bypass invariant every other op kind proves. Corpus-fed keys (check ids)
            // are authorised via the RemediationRunner-supplied registeredKeys set (the template
            // store's RegisteredKeys()), not the static built-in fallback — mirror that here.
            Assert.Equal(SqlClassification.Blocked, SqlSafetyValidator.Classify(sql));
            var registeredKeys = new HashSet<string>(StringComparer.Ordinal) { "SQLT-VA-DB-CHAINING" };
            Assert.Equal(SqlClassification.Remediation,
                SqlSafetyValidator.Classify(sql, new RemediationContext("SQLT-VA-DB-CHAINING"), registeredKeys));
        }
    }
}
