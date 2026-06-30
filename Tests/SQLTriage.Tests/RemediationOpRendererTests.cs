/* In the name of God, the Merciful, the Compassionate */

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
    }
}
