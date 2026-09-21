/* In the name of God, the Merciful, the Compassionate */

using SQLTriage.Data;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Two callers resolve a live plan by SPID: Sessions (ambient connection) and Blocking
    /// Forensics (a specific instance). They MUST run the same text — if one drifts, one page
    /// silently shows plans the other can't find. The single const is the mechanism; these
    /// tests pin the properties that make it safe to share.
    /// </summary>
    public class SessionPlanSqlParityTests
    {
        [Fact]
        public void Takes_the_spid_as_a_parameter_never_concatenated()
        {
            Assert.Contains("@Spid", SessionDataService.LivePlanForSpidSql);
        }

        [Fact]
        public void Resolves_the_plan_from_the_request_so_callers_need_no_handle()
        {
            var sql = SessionDataService.LivePlanForSpidSql;
            Assert.Contains("sys.dm_exec_requests", sql);
            Assert.Contains("sys.dm_exec_query_plan(r.plan_handle)", sql);
            Assert.Contains("r.session_id = @Spid", sql);
        }

        [Fact]
        public void Filters_out_null_plans_so_a_hit_is_always_renderable()
        {
            Assert.Contains("qp.query_plan IS NOT NULL", SessionDataService.LivePlanForSpidSql);
        }

        [Fact]
        public void Is_read_only()
        {
            var sql = SessionDataService.LivePlanForSpidSql.ToUpperInvariant();
            // NB: "EXEC" is not a usable needle here — the DMV names themselves are
            // sys.dm_EXEC_requests / sys.dm_EXEC_query_plan. Pin the statement verb instead.
            Assert.StartsWith("SELECT", sql.TrimStart());
            Assert.DoesNotContain("INSERT", sql);
            Assert.DoesNotContain("UPDATE", sql);
            Assert.DoesNotContain("DELETE", sql);
            Assert.DoesNotContain("DROP", sql);
            Assert.DoesNotContain("EXECUTE ", sql);
        }
    }
}
