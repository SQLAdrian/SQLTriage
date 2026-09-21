/* In the name of God, the Merciful, the Compassionate */

// ── The panel-body honesty fix, RENDERED, 2026-08-21 ─────────────────────────────────────────
//
// WHY THIS FILE EXISTS. Measured on .\new2022: 3-4 of pevents' 5 opted-out native panels TIME OUT
// at 30s, and DataGrid's own empty-state branch rendered the exact same "No data available" text
// for a query that failed as for a query that ran clean and genuinely found nothing. The
// page-level "Data Load Warnings" banner discloses the failure; the panel itself did not. This
// wires DynamicDashboard's per-panel _panelErrors through DynamicPanel into DataGrid's new
// ErrorMessage parameter (see DynamicDashboard.razor's RenderChartOrGrid and DataGrid.razor's
// third render branch) and this file proves the MARKUP, not just the wiring.
//
// WHAT IS REAL HERE. DataGrid.razor takes no injected services (pure [Parameter] leaf component),
// so it is rendered directly through the real Blazor HtmlRenderer with its real markup — the same
// class of instrument as AuditRestartBannerRenderTests, scoped to a component simple enough not to
// need that file's CapturingHost/page-driving machinery.
//
// WHAT IS NOT. DynamicDashboard's own wiring (which _panelErrors entry reaches which DataGrid
// instance, across a heavy async multi-panel load) is NOT re-proven here — that graph is too heavy
// for a HtmlRenderer unit test and is left for the verifier's live pass. This file proves only:
// given ErrorMessage is set (or not), DataGrid renders the right body — the classification DynamicDashboard's wiring depends on being correct at the leaf.

using System.Collections.Generic;
using System.Data;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SQLTriage.Components.Shared;
using Xunit;

namespace SQLTriage.Tests
{
    public class DataGridErrorBodyRenderTests
    {
        private static string VisibleText(string html)
            => System.Net.WebUtility.HtmlDecode(
                Regex.Replace(Regex.Replace(html, "<[^>]+>", " "), @"\s+", " ").Trim());

        private static async Task<string> RenderAsync(DataTable? data, string? errorMessage)
        {
            var services = new ServiceCollection();
            services.AddLogging(b => b.SetMinimumLevel(LogLevel.None));
            // DataGrid injects IJSRuntime for its per-action confirmation (ruling R2(a),
            // 2026-09-01). A component with an unresolvable injected service cannot be constructed,
            // so these render tests supply one; no interop fires during a render.
            services.AddSingleton<Microsoft.JSInterop.IJSRuntime>(new FakeJsRuntime());
            await using var provider = services.BuildServiceProvider();
            var loggerFactory = provider.GetRequiredService<ILoggerFactory>();

            await using var renderer = new HtmlRenderer(provider, loggerFactory);
            return await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["Data"] = data,
                    ["ErrorMessage"] = errorMessage,
                });
                var output = await renderer.RenderComponentAsync<DataGrid>(parameters);
                return output.ToHtmlString();
            });
        }

        private static DataTable OneRowTable()
        {
            var dt = new DataTable();
            dt.Columns.Add("wait_type");
            var row = dt.NewRow();
            row["wait_type"] = "SOS_SCHEDULER_YIELD";
            dt.Rows.Add(row);
            return dt;
        }

        [Fact]
        public async Task Honest_empty_with_no_error_still_renders_the_original_empty_state()
        {
            // Negative control: an ErrorMessage-unaware caller (every DataGrid site before this
            // fix) must see byte-identical behaviour to before — a real zero-row result with no
            // failure is not a defect and must not gain a warning body it never earned.
            var html = await RenderAsync(data: null, errorMessage: null);

            html.Should().Contain("datagrid-empty");
            html.Should().NotContain("datagrid-error");
            VisibleText(html).Should().Contain("No data available");
        }

        [Fact]
        public async Task A_failed_panel_renders_a_visibly_distinct_error_body_not_the_empty_state()
        {
            // The defect this closes: memory_broker (and 2-3 siblings) time out at 30s on
            // .\new2022 and, before this fix, rendered this exact branch as "No data available".
            const string timeoutMessage =
                "Panel 'pevents.memory_broker' error: Execution Timeout Expired. The timeout " +
                "period elapsed prior to completion of the operation or the server is not responding.";

            var html = await RenderAsync(data: null, errorMessage: timeoutMessage);

            html.Should().Contain("datagrid-error",
                "a FAILED load must take a different render branch, not the honest-empty one");
            html.Should().NotContain("datagrid-empty",
                "the two states must never share the same markup again");
            var text = VisibleText(html);
            text.Should().NotBe("No data available",
                "byte-identical to honest-empty is exactly the bug being fixed");
            text.Should().Contain("did not complete",
                "the body must name the failure class, not just show a generic icon");
            text.Should().Contain(timeoutMessage,
                "the real exception message, not a fabricated summary — DD: never invent what the query actually reported");
        }

        [Fact]
        public async Task Real_rows_win_over_a_stale_error_message()
        {
            // Precedence control: Data present must always render, even if a caller passed a
            // stale ErrorMessage from a prior failed cycle — real rows are never hidden behind an
            // error body.
            var html = await RenderAsync(data: OneRowTable(), errorMessage: "stale error from a previous cycle");

            html.Should().NotContain("datagrid-error");
            html.Should().NotContain("datagrid-empty");
            VisibleText(html).Should().Contain("SOS_SCHEDULER_YIELD");
        }
    }
}
