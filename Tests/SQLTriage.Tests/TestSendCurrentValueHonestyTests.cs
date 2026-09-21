/* In the name of God, the Merciful, the Compassionate */

// ── N-2: a connectivity test-send shipped "Current Value 0.00", 2026-08-22 ───────────────────
//
// WHY THIS FILE EXISTS. AlertNotification.CurrentValue was a plain double. Every one of the seven
// channel Test buttons builds a notification, measures nothing, and sends it -- so the reading a
// person saw in the test email, Teams card, Slack message, webhook payload, PagerDuty event,
// ServiceNow incident and WhatsApp message was 0.00, a number that came from the CLR's default
// and not from any instance. Two of the paths (TestSmtpAsync, TestTeamsAsync) even assigned
// CurrentValue = 0 explicitly, one line above the C3 comment explaining why ThresholdValue must
// stay null in exactly that situation.
//
// WHAT IS REAL HERE. Each test drives the REAL Test button call the operator clicks
// (TestWebhookAsync / TestTeamsAsync / TestSmtpAsync) through the REAL NotificationChannelService
// and its REAL HttpClient, and asserts on the BYTES that went over the wire -- to a real local
// HttpListener for the two webhook channels, and to a capturing handler for the Graph mail path,
// whose endpoint is hard-coded and has no config seam. Nothing here hand-composes a payload.
//
// THE PHRASE NAMES NO CAUSE (verifier finding, 2026-08-23). The first fix printed
// "not measured (connectivity test)", which is a claim about WHY nothing was measured, and it is
// false for at least one sender that is not a test: ScheduledTaskEngine builds an AlertNotification
// for a completed scheduled task and never sets a reading. The phrase is now the neutral
// "not measured" -- the word this product already uses for an absent number
// (IndexAnalysisRendering.Unmeasured) -- and the last test in this file holds it to that.
//
// MUTATIONS THAT MUST FAIL (all three were run):
//   1. re-add "CurrentValue = 0," to TestSmtpAsync and TestTeamsAsync -> the Teams and SMTP tests.
//   2. write the webhook payload as "currentValue = notification.CurrentValue ?? 0" -> the
//      webhook test. That one matters because the webhook path never assigned the zero: it
//      inherited it from the field's type, which is the half of the defect a code review misses.
//   3. restore "not measured (connectivity test)" -> every test in this file, and the last one
//      says why in its own message.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests;

public sealed class TestSendCurrentValueHonestyTests : IDisposable
{
    private const string NotMeasured = "not measured";

    private readonly string _dir;

    public TestSendCurrentValueHonestyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "testsend-cv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static int FreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

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

    private NotificationChannelService NewService()
    {
        var templates = new AlertTemplateService(NullLogger<AlertTemplateService>.Instance,
            Path.Combine(_dir, "alert-templates.json"));
        var svc = new NotificationChannelService(NullLogger<NotificationChannelService>.Instance, templates);
        Repoint(svc, "_configFilePath", Path.Combine(_dir, "notification-channels.json"));
        Invoke(svc, "LoadConfig");
        return svc;
    }

    // ── The decision itself ─────────────────────────────────────────────────────

    [Fact]
    public void CurrentValueText_prints_a_phrase_when_nothing_was_measured_and_the_reading_when_it_was()
    {
        new AlertNotification { CurrentValue = null }.CurrentValueText.Should().Be(NotMeasured);
        new AlertNotification { CurrentValue = 88.5 }.CurrentValueText.Should().Be("88.50");

        AlertTemplateService.Render("{{current_value}}", new AlertNotification { CurrentValue = null })
            .Should().Be(NotMeasured);
        AlertTemplateService.Render("{{current_value}}", new AlertNotification { CurrentValue = 88.5 })
            .Should().Be("88.50");
    }

    // ── The generic webhook Test button ─────────────────────────────────────────

    [Fact]
    public async Task TestWebhookAsync_sends_a_null_currentValue_not_a_zero()
    {
        var port = FreeLoopbackPort();
        var url = $"http://127.0.0.1:{port}/";

        using var listener = new HttpListener();
        listener.Prefixes.Add(url);
        listener.Start();

        var receiveBody = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            using var reader = new StreamReader(ctx.Request.InputStream);
            var body = await reader.ReadToEndAsync();
            ctx.Response.StatusCode = 200;
            ctx.Response.OutputStream.Close();
            return body;
        });

        var svc = NewService();
        svc.UpdateConfig(new NotificationChannelConfig
        {
            Webhook = new WebhookChannelConfig { Enabled = true, Url = url }
        }).Should().Be(StoreWriteOutcome.Saved);

        var (success, message) = await svc.TestWebhookAsync();
        success.Should().BeTrue(message);

        var body = await receiveBody.WaitAsync(TimeSpan.FromSeconds(10));
        listener.Stop();

        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("currentValue").ValueKind.Should().Be(JsonValueKind.Null,
            "null is the honest wire value for a reading that was never taken; 0 is a measurement claim");

        // The whole payload, not just that one field: no fabricated reading anywhere in it.
        body.Should().NotContain("0.00");
    }

    // ── The Teams Test button ───────────────────────────────────────────────────

    [Fact]
    public async Task TestTeamsAsync_prints_the_phrase_in_the_Current_Value_fact()
    {
        var port = FreeLoopbackPort();
        var url = $"http://127.0.0.1:{port}/";

        using var listener = new HttpListener();
        listener.Prefixes.Add(url);
        listener.Start();

        var receiveBody = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            using var reader = new StreamReader(ctx.Request.InputStream);
            var body = await reader.ReadToEndAsync();
            ctx.Response.StatusCode = 200;
            ctx.Response.OutputStream.Close();
            return body;
        });

        var svc = NewService();
        svc.UpdateConfig(new NotificationChannelConfig
        {
            TeamsWebhook = new TeamsWebhookChannelConfig { Enabled = true, WebhookUrl = url }
        }).Should().Be(StoreWriteOutcome.Saved);

        var (success, message) = await svc.TestTeamsAsync();
        success.Should().BeTrue(message);

        var body = await receiveBody.WaitAsync(TimeSpan.FromSeconds(10));
        listener.Stop();

        using var doc = JsonDocument.Parse(body);
        var facts = doc.RootElement
            .GetProperty("attachments")[0]
            .GetProperty("content")
            .GetProperty("body")[1]
            .GetProperty("facts");

        string? currentValue = null;
        foreach (var fact in facts.EnumerateArray())
        {
            if (fact.GetProperty("title").GetString() == "Current Value")
                currentValue = fact.GetProperty("value").GetString();
        }

        currentValue.Should().Be(NotMeasured,
            "the Teams test-send assigned CurrentValue = 0 and the card printed 0.00 as the reading");
        body.Should().NotContain("0.00");
    }

    // ── The SMTP Test button, down the real Graph mail path ─────────────────────

    [Fact]
    public async Task TestSmtpAsync_prints_the_phrase_in_the_rendered_email_body()
    {
        var handler = new GraphCapturingHandler();
        var svc = NewService();
        Repoint(svc, "_httpClient", new HttpClient(handler));

        svc.UpdateConfig(new NotificationChannelConfig
        {
            Smtp = new SmtpChannelConfig
            {
                Enabled = true,
                Host = "smtp.example.invalid",
                UseOAuth2 = true,
                TenantId = "contoso.example",
                ClientId = "00000000-0000-0000-0000-000000000001",
                ClientSecret = "not-a-real-secret",
                FromAddress = "alerts@example.invalid",
                ToAddresses = new List<string> { "dba@example.invalid" }
            }
        }).Should().Be(StoreWriteOutcome.Saved);

        var (success, message) = await svc.TestSmtpAsync();
        success.Should().BeTrue(message);

        handler.SendMailBody.Should().NotBeNull();
        using var doc = JsonDocument.Parse(handler.SendMailBody!);
        var html = doc.RootElement.GetProperty("message").GetProperty("body").GetProperty("content").GetString()!;

        var cell = Regex.Match(html, @"Current Value</td>\s*<td[^>]*>([^<]*)</td>");
        cell.Success.Should().BeTrue("the shipped email template must still carry a Current Value row");
        cell.Groups[1].Value.Trim().Should().Be(NotMeasured);

        html.Should().NotContain("0.00");
    }

    // -- The phrase must be true for a sender that is NOT a connectivity test --

    /// <summary>
    /// The counter-example that scoped the wording. Every render site prints
    /// <see cref="AlertNotification.CurrentValueText"/>, and the Test buttons are not its only
    /// callers: a COMPLETED SCHEDULED TASK dispatches a notification with no reading at all. When
    /// the phrase read "not measured (connectivity test)" that card explained a finished report as
    /// a connectivity test -- a sentence outrunning its measurement, in the lane that exists to
    /// close that class.
    ///
    /// <para>The notification here is built by PRODUCTION code
    /// (<see cref="ScheduledTaskEngine.BuildTaskCompletionNotification"/>, the factory the engine
    /// itself calls), not by a shape copied into the test, and it is carried to a real local
    /// HttpListener as a real Teams card. The channel minimum severity is lowered to "info"
    /// because a task completion IS info: that is the operator setting under which a client sees
    /// this card.</para>
    /// </summary>
    [Fact]
    public async Task A_completed_scheduled_task_prints_a_phrase_that_names_no_cause()
    {
        var notification = ScheduledTaskEngine.BuildTaskCompletionNotification(
            "Nightly index report", "task-nightly-index", @".\NEW2022", 412, 3.75);

        notification.CurrentValue.Should().BeNull("a completed task reads no metric");
        notification.CurrentValueText.Should().Be(NotMeasured);
        notification.CurrentValueText.Should().NotContain("connectivity",
            "this sender is not a connectivity test, so the phrase may not say that it is");

        var port = FreeLoopbackPort();
        var url = $"http://127.0.0.1:{port}/";

        using var listener = new HttpListener();
        listener.Prefixes.Add(url);
        listener.Start();

        var receiveBody = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            using var reader = new StreamReader(ctx.Request.InputStream);
            var body = await reader.ReadToEndAsync();
            ctx.Response.StatusCode = 200;
            ctx.Response.OutputStream.Close();
            return body;
        });

        var svc = NewService();
        svc.UpdateConfig(new NotificationChannelConfig
        {
            TeamsWebhook = new TeamsWebhookChannelConfig
            {
                Enabled = true,
                WebhookUrl = url,
                MinimumSeverity = "info"
            }
        }).Should().Be(StoreWriteOutcome.Saved);

        var results = await svc.DispatchAsync(notification);
        results.Should().ContainSingle();
        results[0].Success.Should().BeTrue(results[0].Detail);

        var body = await receiveBody.WaitAsync(TimeSpan.FromSeconds(10));
        listener.Stop();

        using var doc = JsonDocument.Parse(body);
        var facts = doc.RootElement
            .GetProperty("attachments")[0]
            .GetProperty("content")
            .GetProperty("body")[1]
            .GetProperty("facts");

        string? currentValue = null;
        foreach (var fact in facts.EnumerateArray())
        {
            if (fact.GetProperty("title").GetString() == "Current Value")
                currentValue = fact.GetProperty("value").GetString();
        }

        currentValue.Should().Be(NotMeasured);
        body.Should().NotContain("connectivity",
            "the card for a finished scheduled task must not describe itself as a connectivity test");
        body.Should().NotContain("0.00");
    }

    /// <summary>
    /// Stands in for login.microsoftonline.com and graph.microsoft.com, both of which are
    /// hard-coded in the service and have no config seam. The HttpClient field is repointed by
    /// reflection rather than a production seam being added for a test.
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
