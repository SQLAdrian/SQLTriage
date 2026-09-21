/* In the name of God, the Merciful, the Compassionate */

// ── N-1: the notification hit count was the literal string "1", 2026-08-22 ────────────────────
//
// WHY THIS FILE EXISTS. AlertTemplateService.Render ended with
//     .Replace("{{hit_count}}", "1"); // NotificationChannelService can pass richer data if needed
// so every notification this product RENDERED told the reader the alert had fired ONCE.
// AlertState.HitCount is incremented on every re-fire (AlertEvaluationService, both the special
// and the main path) and AlertNotification simply had no field to carry it. A Buffer Cache Hit
// Ratio alert that had been re-firing every five minutes for six hours reached the DBA's inbox as
// "Hit Count: 1", a number no counter ever produced.
//
// SCOPE (corrected 2026-08-23 after the wire census). The EMAIL is the only surface involved.
// AlertTemplateService.Render is only ever handed the Email template; the Slack card, webhook
// payload, ServiceNow incident, Teams card, PagerDuty event and WhatsApp message are hard-coded
// objects carrying no hit count at all, before this fix or after it. That is measured, not read:
// ChannelPayloadReachCensusTests dispatches one notification to all seven channels and asserts on
// the captured bytes.
//
// This is the same class as the 2026-08-05 C3 threshold fix one property above it in the model,
// and it is fixed the same way: plumb the measured count, and print a PHRASE, not a number, when
// the sender never counted.
//
// WHAT IS REAL HERE.
//   * Tier 1 renders the REAL shipped default email template (ChannelTemplate.DefaultEmail(),
//     the bytes an install actually gets) through the REAL AlertTemplateService.Render, and reads
//     the value out of the rendered Hit Count CELL. Nothing here hand-composes a template.
//   * Tier 2 drives the REAL NotificationChannelService.DispatchAsync down the REAL Microsoft
//     Graph email path with the service's own HttpClient repointed at a capturing handler, and
//     asserts on the BYTES the service POSTed to /sendMail. The email body under test is the one
//     that would have gone to the mailbox.
//
//   * Tier 3 constructs the REAL AlertEvaluationService and drives its own dispatch with a real
//     AlertState, so the PLUMB -- HitCount = state.HitCount, the line the whole fix hangs on --
//     is exercised rather than read. Deleting that line used to be invisible to the entire suite.
//
// MUTATION THAT MUST FAIL (run before trusting this file): restore
//     .Replace("{{hit_count}}", "1")
// in AlertTemplateService.cs. Tiers 1 and 2 both go red on the HitCount=7 case. The null case
// alone would NOT catch it, which is why the seven is here.
//
// SECOND MUTATION THAT MUST FAIL (2026-08-23): delete "HitCount = state.HitCount" from BOTH
// dispatch sites in AlertEvaluationService (the first fire and the escalation). Tier 3 goes red on
// both. Before tier 3 existed that deletion returned every notification to "not recorded" with no
// signal anywhere in 4672 tests.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Caching;
using SQLTriage.Data.Models;
using SQLTriage.Data.Scheduling;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests;

public sealed class AlertHitCountHonestyTests : IDisposable
{
    private readonly string _dir;

    public AlertHitCountHonestyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hitcount-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ── reflection helpers (the pattern ConfigStoreWriteGuardTests established) ──

    private static void Repoint(object target, string field, object value)
    {
        var f = target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(f);
        f!.SetValue(target, value);
    }

    private static void Invoke(object target, string method)
    {
        var m = target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(m);
        m!.Invoke(target, null);
    }

    /// <summary>
    /// Reads the value out of the rendered "Hit Count" row of the shipped HTML email template.
    /// Reading the CELL rather than searching the whole document is deliberate: the body carries
    /// other numbers (padding, font sizes, the metric value), and a substring search would pass
    /// on a document that never rendered the token at all.
    /// </summary>
    private static string HitCountCell(string renderedHtml)
    {
        var m = Regex.Match(renderedHtml, @"Hit Count</td>\s*<td[^>]*>([^<]*)</td>");
        m.Success.Should().BeTrue("the shipped email template must still carry a Hit Count row");
        return m.Groups[1].Value.Trim();
    }

    private static AlertNotification Firing(int? hitCount) => new()
    {
        AlertName = "Buffer Cache Hit Ratio",
        Metric = "buffer_cache_hit",
        CurrentValue = 88.5,
        ThresholdValue = 90,
        BasisKind = "FixedThreshold",
        HitCount = hitCount,
        Severity = "critical",
        Message = "Buffer cache hit ratio is below the configured threshold.",
        InstanceName = @".\NEW2022",
        TriggeredAt = new DateTime(2026, 8, 22, 6, 17, 39, DateTimeKind.Utc),
        SendEmail = true
    };

    // ── Tier 1: the real Render, the real shipped template ──────────────────────

    [Fact]
    public void Render_prints_the_measured_hit_count_not_a_hard_coded_one()
    {
        AlertTemplateService.Render("{{hit_count}}", Firing(7)).Should().Be("7");
    }

    [Fact]
    public void Render_prints_not_recorded_when_the_sender_never_counted()
    {
        AlertTemplateService.Render("{{hit_count}}", Firing(null)).Should().Be("not recorded");
    }

    [Fact]
    public void The_shipped_email_template_renders_the_seventh_hit_as_seven()
    {
        var template = ChannelTemplate.DefaultEmail();

        HitCountCell(AlertTemplateService.Render(template.Body, Firing(7))).Should().Be("7");
        HitCountCell(AlertTemplateService.Render(template.Body, Firing(1))).Should().Be("1");
        HitCountCell(AlertTemplateService.Render(template.Body, Firing(null))).Should().Be("not recorded");
    }

    // ── Tier 2: the bytes the service actually POSTs ────────────────────────────

    [Fact]
    public async Task DispatchAsync_delivers_the_real_hit_count_in_the_email_body()
    {
        var handler = new GraphCapturingHandler();

        var templates = new AlertTemplateService(NullLogger<AlertTemplateService>.Instance,
            Path.Combine(_dir, "alert-templates.json"));
        var svc = new NotificationChannelService(NullLogger<NotificationChannelService>.Instance, templates);

        // Isolated temp store, same discipline as WebhookTriggeredAtUtcTests: this must never
        // read or write the shared test-output Config/ folder.
        Repoint(svc, "_configFilePath", Path.Combine(_dir, "notification-channels.json"));
        Invoke(svc, "LoadConfig");
        Repoint(svc, "_httpClient", new HttpClient(handler));

        var outcome = svc.UpdateConfig(new NotificationChannelConfig
        {
            Smtp = new SmtpChannelConfig
            {
                Enabled = true,
                UseOAuth2 = true,
                TenantId = "contoso.example",
                ClientId = "00000000-0000-0000-0000-000000000001",
                ClientSecret = "not-a-real-secret",
                FromAddress = "alerts@example.invalid",
                ToAddresses = new List<string> { "dba@example.invalid" },
                MinimumSeverity = "info"
            }
        });
        outcome.Should().Be(StoreWriteOutcome.Saved);

        var results = await svc.DispatchAsync(Firing(7));

        results.Should().ContainSingle();
        results[0].Success.Should().BeTrue(results[0].Detail);

        handler.SendMailBody.Should().NotBeNull("the service must have POSTed a sendMail request");

        using var doc = System.Text.Json.JsonDocument.Parse(handler.SendMailBody!);
        var html = doc.RootElement.GetProperty("message").GetProperty("body").GetProperty("content").GetString()!;

        // The exact regression: this cell carried "1" for every alert ever sent, whatever the
        // real count was. Reading it off the captured wire bytes is the point of this tier.
        HitCountCell(html).Should().Be("7");
    }

    // -- Tier 3: the plumb, through the real AlertEvaluationService --------------

    /// <summary>
    /// The line the whole of N-1 rests on is <c>HitCount = state.HitCount</c> in
    /// AlertEvaluationService. Deleting it returns every notification to "not recorded", and
    /// before this test that deletion passed the entire suite: tiers 1 and 2 build an
    /// AlertNotification by hand, so they never reach the code that fills one in.
    ///
    /// <para>This constructs the REAL evaluation service (the collaborator set
    /// AlertBreakerLiveEndpointTests already proves is constructible) and reflection-invokes its
    /// own private DispatchNotification with a real AlertState whose HitCount is 11 -- the value
    /// the live alert_history row 5049 carried for the buffer_cache_hit alert whose notification
    /// said one. The assertion is the Hit Count cell of the email the service POSTed to /sendMail.
    /// No SQL Server is touched: dispatch is downstream of evaluation.</para>
    /// </summary>
    [Fact]
    public async Task The_evaluation_service_carries_the_states_own_count_all_the_way_to_the_email()
    {
        var handler = new GraphCapturingHandler();
        var templates = new AlertTemplateService(NullLogger<AlertTemplateService>.Instance,
            Path.Combine(_dir, "alert-templates.json"));
        var channels = new NotificationChannelService(NullLogger<NotificationChannelService>.Instance, templates);
        Repoint(channels, "_configFilePath", Path.Combine(_dir, "notification-channels.json"));
        Invoke(channels, "LoadConfig");
        Repoint(channels, "_httpClient", new HttpClient(handler));
        channels.UpdateConfig(GraphMailOnly()).Should().Be(StoreWriteOutcome.Saved);

        using var cache = new liveQueriesCacheStore();
        using var svc = new AlertEvaluationService(
            NullLogger<AlertEvaluationService>.Instance,
            new AlertDefinitionService(NullLogger<AlertDefinitionService>.Instance),
            new AlertHistoryService(NullLogger<AlertHistoryService>.Instance),
            new AlertingService(NullLogger<AlertingService>.Instance),
            new ServerConnectionManager(NullLogger<ServerConnectionManager>.Instance),
            new ToastService(),
            channels,
            cache,
            new NullOrchestrator());

        var alert = new AlertDefinition
        {
            Id = "buffer_cache_hit",
            Name = "Buffer Cache Hit Ratio",
            Operator = "less_than",
            SendEmail = true
        };
        var state = new AlertState
        {
            AlertId = alert.Id,
            AlertName = alert.Name,
            ServerName = ".\\NEW2022",
            Severity = "Critical",
            LastValue = 100,
            ThresholdValue = 90,
            BasisKind = "FixedThreshold",
            HitCount = 11,
            FirstTriggered = new DateTime(2026, 8, 22, 6, 17, 39, DateTimeKind.Utc),
            LastTriggered = new DateTime(2026, 8, 22, 12, 17, 39, DateTimeKind.Utc),
            Message = "Buffer cache hit ratio is below the configured threshold."
        };

        var dispatch = typeof(AlertEvaluationService).GetMethod(
            "DispatchNotification", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(dispatch);
        dispatch!.Invoke(svc, new object[] { alert, state });

        // DispatchNotification hands the send off with a discarded task, so wait for the bytes.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (handler.SendMailBody == null && DateTime.UtcNow < deadline)
            await Task.Delay(25);

        handler.SendMailBody.Should().NotBeNull("the evaluation service must have sent the alert email");

        using var doc = System.Text.Json.JsonDocument.Parse(handler.SendMailBody!);
        var html = doc.RootElement.GetProperty("message").GetProperty("body").GetProperty("content").GetString()!;

        HitCountCell(html).Should().Be("11",
            "the state counted eleven fires and the email must say eleven");
    }

    /// <summary>
    /// The escalation path carries the same count, from the same production factory.
    ///
    /// <para>It is asserted on the object rather than on the wire for a measured reason, pinned
    /// in the second half of this test: an escalation notification never sets SendEmail, so
    /// DispatchAsync selects no channel, and SMTP is the only channel that renders a hit count at
    /// all. The count therefore reaches no surface today. That is a PIN of what is, not an
    /// approval; it is recorded here so the gap is visible rather than silent.</para>
    /// </summary>
    [Fact]
    public async Task The_escalation_notification_carries_the_same_count_and_reaches_no_mailbox()
    {
        var alert = new AlertDefinition { Id = "buffer_cache_hit", Name = "Buffer Cache Hit Ratio" };
        var state = new AlertState { ServerName = ".\\NEW2022", HitCount = 11, LastValue = 100 };

        var escalation = AlertEvaluationService.BuildEscalationNotification(alert, state, "escalated");

        escalation.HitCount.Should().Be(11);
        escalation.HitCountText.Should().Be("11");

        // The pin: SMTP is enabled and set to accept everything, and this notification still
        // reaches no channel, because the escalation shape leaves SendEmail false.
        var handler = new GraphCapturingHandler();
        var templates = new AlertTemplateService(NullLogger<AlertTemplateService>.Instance,
            Path.Combine(_dir, "alert-templates.json"));
        var channels = new NotificationChannelService(NullLogger<NotificationChannelService>.Instance, templates);
        Repoint(channels, "_configFilePath", Path.Combine(_dir, "escalation-channels.json"));
        Invoke(channels, "LoadConfig");
        Repoint(channels, "_httpClient", new HttpClient(handler));
        channels.UpdateConfig(GraphMailOnly()).Should().Be(StoreWriteOutcome.Saved);

        escalation.SendEmail.Should().BeFalse();
        var results = await channels.DispatchAsync(escalation);
        results.Should().BeEmpty("an escalation never sets SendEmail, so no channel is selected");
        handler.SendMailBody.Should().BeNull();
    }

    private static NotificationChannelConfig GraphMailOnly() => new()
    {
        Smtp = new SmtpChannelConfig
        {
            Enabled = true,
            UseOAuth2 = true,
            TenantId = "contoso.example",
            ClientId = "00000000-0000-0000-0000-000000000001",
            ClientSecret = "not-a-real-secret",
            FromAddress = "alerts@example.invalid",
            ToAddresses = new List<string> { "dba@example.invalid" },
            MinimumSeverity = "info"
        }
    };

    /// <summary>
    /// The evaluation service needs an orchestrator to construct. Nothing in this file evaluates
    /// an alert, so nothing is ever enqueued through it.
    /// </summary>
    private sealed class NullOrchestrator : IQueryOrchestrator
    {
        public async Task<QueryResult> EnqueueAsync(
            QueryRequest request, QueryPriority priority, CancellationToken cancellationToken = default)
        {
            await request.Work(cancellationToken);
            return new QueryResult { QueryId = request.QueryId, Success = true };
        }

        public Task<OrchestratorHealth> GetHealthAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new OrchestratorHealth());
        public Task<OrchestratorMetrics> GetMetricsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new OrchestratorMetrics());
        public void UpdateLimits(int globalConcurrency, int perServerConcurrency) { }
        public void Start() { }
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>
    /// Stands in for login.microsoftonline.com and graph.microsoft.com. Serves the client-
    /// credentials token, then captures the sendMail body verbatim. No production seam exists or
    /// is needed: the service's HttpClient field is repointed by reflection, exactly as the
    /// existing config-store tests repoint its file path.
    /// </summary>
    private sealed class GraphCapturingHandler : HttpMessageHandler
    {
        public string? SendMailBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();

            if (url.Contains("/oauth2/v2.0/token", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"access_token\":\"test-token\",\"expires_in\":3600}",
                        Encoding.UTF8, "application/json")
                };
            }

            if (url.Contains("/sendMail", StringComparison.OrdinalIgnoreCase))
            {
                SendMailBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
