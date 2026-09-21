/* In the name of God, the Merciful, the Compassionate */

// -- What each channel actually PUTS ON THE WIRE, 2026-08-23 -----------------------------------
//
// WHY THIS FILE EXISTS. Two claims made by the A and C fixes were wider than the evidence behind
// them, which is the exact class this lane exists to close.
//
//   1. The N-1 prose said a client reading "the email, Slack card, webhook payload or ServiceNow
//      incident" had been told the alert fired once. Only the EMAIL template is ever rendered:
//      AlertTemplateService.Render has four call sites, all of them the email path, and every
//      other channel builds a hard-coded object with no hit-count field at all. Those channels
//      never printed "Hit Count: 1" and still print no hit count. (The Slack, webhook, PagerDuty
//      and ServiceNow ChannelTemplates in AlertTemplateConfig DO carry a {{hit_count}} token and
//      the UI advertises it, but nothing reads those templates -- see the last test.)
//
//   2. The N-2 sweep replaced eleven render sites, but only three of them had a test. A mutation
//      that put the raw reading back into the PagerDuty summary survived the whole 4672-test
//      suite.
//
// WHAT IS REAL HERE. One dispatch, all seven channels enabled, the service's own HttpClient
// repointed at a capturing handler, and every assertion read out of the BYTES that channel POSTed.
// Nothing is hand-composed and nothing is inferred from the source.
//
// MUTATIONS THAT MUST FAIL (all run):
//   * restore the hard-coded "1" for {{hit_count}} in AlertTemplateService -> the email test.
//   * write the PagerDuty summary from the raw CurrentValue -> the PagerDuty test.
//   * write the ServiceNow short_description from the raw CurrentValue -> the ServiceNow test.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
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

public sealed class ChannelPayloadReachCensusTests : IDisposable
{
    private const string NotMeasured = "not measured";

    private readonly string _dir;
    private readonly CapturingHandler _handler = new();
    private readonly NotificationChannelService _svc;

    public ChannelPayloadReachCensusTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wire-census-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        var templates = new AlertTemplateService(NullLogger<AlertTemplateService>.Instance,
            Path.Combine(_dir, "alert-templates.json"));
        _svc = new NotificationChannelService(NullLogger<NotificationChannelService>.Instance, templates);
        Repoint(_svc, "_configFilePath", Path.Combine(_dir, "notification-channels.json"));
        Invoke(_svc, "LoadConfig");
        Repoint(_svc, "_httpClient", new HttpClient(_handler));

        _svc.UpdateConfig(AllSevenChannels()).Should().Be(StoreWriteOutcome.Saved);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
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

    /// <summary>
    /// Every channel on, every one of them at "info" so nothing is filtered out by severity. The
    /// hostnames are unroutable on purpose: the capturing handler answers before DNS is consulted,
    /// and the PagerDuty and WhatsApp endpoints are hard-coded in the service anyway.
    /// </summary>
    private static NotificationChannelConfig AllSevenChannels() => new()
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
        },
        TeamsWebhook = new TeamsWebhookChannelConfig
        {
            Enabled = true,
            WebhookUrl = "https://teams.example.invalid/hook",
            MinimumSeverity = "info"
        },
        Slack = new SlackChannelConfig
        {
            Enabled = true,
            WebhookUrl = "https://slack.example.invalid/hook",
            MinimumSeverity = "info"
        },
        Webhook = new WebhookChannelConfig
        {
            Enabled = true,
            Url = "https://webhook.example.invalid/hook",
            MinimumSeverity = "info"
        },
        PagerDuty = new PagerDutyChannelConfig
        {
            Enabled = true,
            RoutingKey = "not-a-real-routing-key",
            MinimumSeverity = "info"
        },
        ServiceNow = new ServiceNowChannelConfig
        {
            Enabled = true,
            InstanceUrl = "https://snow.example.invalid",
            Username = "svc_sqltriage",
            Password = "not-a-real-password",
            Table = "incident",
            MinimumSeverity = "info"
        },
        WhatsApp = new WhatsAppChannelConfig
        {
            Enabled = true,
            PhoneNumberId = "123456789012345",
            AccessToken = "not-a-real-token",
            RecipientNumbers = new List<string> { "+6421000000" },
            MinimumSeverity = "info"
        }
    };

    /// <summary>
    /// The live 18:17:39 alert, as a notification: eleven hits recorded (alert_history id 5049 on
    /// the installed service carried hit_count = 11 for exactly this alert on this instance), and
    /// no reading, which is the state every test-send and every scheduled-task send is in.
    /// </summary>
    private static AlertNotification LiveShapedFiring() => new()
    {
        AlertName = "Buffer Cache Hit Ratio",
        Metric = "buffer_cache_hit",
        CurrentValue = null,
        ThresholdValue = null,
        BasisKind = "FixedThreshold",
        HitCount = 11,
        Severity = "critical",
        Message = "Buffer cache hit ratio is below the configured threshold.",
        InstanceName = ".\\NEW2022",
        TriggeredAt = new DateTime(2026, 8, 22, 6, 17, 39, DateTimeKind.Utc),
        SendEmail = true
    };

    private async Task DispatchOnceAsync()
    {
        var results = await _svc.DispatchAsync(LiveShapedFiring());
        results.Should().HaveCount(7, "all seven channels are enabled and none is filtered by severity");
        foreach (var r in results)
            r.Success.Should().BeTrue(r.Channel + ": " + r.Detail);
    }

    // -- The email: the only surface that renders a template at all -------------

    [Fact]
    public async Task The_email_is_the_only_channel_that_carries_a_hit_count_and_it_carries_the_real_one()
    {
        await DispatchOnceAsync();

        var mail = _handler.BodyFor("/sendMail");
        using var doc = JsonDocument.Parse(mail);
        var html = doc.RootElement.GetProperty("message").GetProperty("body").GetProperty("content").GetString()!;

        Cell(html, "Hit Count").Should().Be("11",
            "the hard-coded one is the defect; eleven is what the counter held");
        Cell(html, "Current Value").Should().Be(NotMeasured);

        // The reach claim, measured rather than asserted: no other channel puts a hit count on the
        // wire at all, so no other channel ever printed the fabricated one.
        foreach (var (url, payload) in _handler.OtherThanMail())
        {
            payload.Should().NotContain("hit_count", url + " carries no hit-count field");
            payload.Should().NotContain("Hit Count", url + " carries no hit-count field");
            payload.Should().NotContain("hitCount", url + " carries no hit-count field");
        }
    }

    // -- Every other channel's reading, read off its own payload ---------------

    [Fact]
    public async Task Teams_prints_the_phrase_in_its_Current_Value_fact()
    {
        await DispatchOnceAsync();

        using var doc = JsonDocument.Parse(_handler.BodyFor("teams.example.invalid"));
        var facts = doc.RootElement.GetProperty("attachments")[0]
            .GetProperty("content").GetProperty("body")[1].GetProperty("facts");

        FactValue(facts, "Current Value").Should().Be(NotMeasured);
    }

    [Fact]
    public async Task Slack_prints_the_phrase_in_its_Value_field()
    {
        await DispatchOnceAsync();

        using var doc = JsonDocument.Parse(_handler.BodyFor("slack.example.invalid"));
        var fields = doc.RootElement.GetProperty("attachments")[0].GetProperty("fields");

        FactValue(fields, "Value").Should().Be(NotMeasured);
    }

    [Fact]
    public async Task The_generic_webhook_serializes_null_and_never_a_zero()
    {
        await DispatchOnceAsync();

        var body = _handler.BodyFor("webhook.example.invalid");
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("currentValue").ValueKind.Should().Be(JsonValueKind.Null,
            "null is the honest wire value for a reading nobody took");
        body.Should().NotContain("0.00");
    }

    [Fact]
    public async Task PagerDuty_prints_the_phrase_in_its_summary_and_null_in_its_details()
    {
        await DispatchOnceAsync();

        var body = _handler.BodyFor("events.pagerduty.com");
        using var doc = JsonDocument.Parse(body);
        var pd = doc.RootElement.GetProperty("payload");

        pd.GetProperty("summary").GetString().Should().Contain("= " + NotMeasured + " ",
            "the one line a pager shows is the summary, and it used to carry a fabricated reading");
        pd.GetProperty("custom_details").GetProperty("current_value").ValueKind
            .Should().Be(JsonValueKind.Null);
        body.Should().NotContain("0.00");
    }

    [Fact]
    public async Task ServiceNow_prints_the_phrase_in_both_incident_fields()
    {
        await DispatchOnceAsync();

        var body = _handler.BodyFor("/api/now/table/incident");
        using var doc = JsonDocument.Parse(body);

        doc.RootElement.GetProperty("short_description").GetString()
            .Should().EndWith("= " + NotMeasured);
        doc.RootElement.GetProperty("description").GetString()
            .Should().Contain("Current Value: " + NotMeasured);
        body.Should().NotContain("0.00");
    }

    [Fact]
    public async Task WhatsApp_prints_the_phrase_in_its_message_text()
    {
        await DispatchOnceAsync();

        var body = _handler.BodyFor("graph.facebook.com");
        using var doc = JsonDocument.Parse(body);
        var text = doc.RootElement.GetProperty("text").GetProperty("body").GetString()!;

        text.Should().Contain("Value: " + NotMeasured + " (threshold:");
        body.Should().NotContain("0.00");
    }

    // -- Why the non-email templates were never the surface --------------------

    /// <summary>
    /// The census above shows no channel except email carrying a hit count. This says WHY, from
    /// the production objects rather than from a source read: the other five ChannelTemplates DO
    /// carry {{hit_count}}, and the Alerts page advertises that token to operators, yet nothing
    /// renders them. An operator editing the webhook template changes nothing on any wire.
    ///
    /// <para>This is a PIN, not an approval. Making those templates live, or removing them, is out
    /// of this lane's scope; the point is that a future change either turns this test red or
    /// leaves the dead configuration honestly recorded.</para>
    /// </summary>
    [Fact]
    public void The_non_email_channel_templates_carry_a_hit_count_token_that_nothing_renders()
    {
        var shipped = new AlertTemplateConfig();

        shipped.Email.Body.Should().Contain("{{hit_count}}");
        shipped.Slack.Body.Should().Contain("{{hit_count}}");
        shipped.Webhook.Body.Should().Contain("{{hit_count}}");
        shipped.ServiceNow.Body.Should().Contain("{{hit_count}}");

        // The token WOULD render honestly if anything called it. Nothing does: the Slack payload
        // the service builds is the hard-coded field set the census read off the wire.
        AlertTemplateService.Render(shipped.Slack.Body, LiveShapedFiring())
            .Should().Contain("11");
    }

    /// <summary>
    /// AlertTemplateService bound its templates file to a STATIC path under the test assembly and
    /// writes the defaults there on first run, so every test in this suite that constructed one
    /// shared that file. This service now takes a path, the same seam AlertDefinitionService uses,
    /// and every test in this lane hands it a temp one.
    ///
    /// <para>MUTATION THAT MUST FAIL: make the constructor ignore the override and compose the
    /// path under AppDomain.CurrentDomain.BaseDirectory again.</para>
    /// </summary>
    [Fact]
    public void The_template_service_writes_only_into_the_path_it_was_given()
    {
        var path = Path.Combine(_dir, "isolated-templates.json");

        var svc = new AlertTemplateService(NullLogger<AlertTemplateService>.Instance, path);

        File.Exists(path).Should().BeTrue(
            "Load writes the defaults on first run, and it must write them where it was pointed");
        svc.Config.Email.Body.Should().Contain("{{hit_count}}");
    }

    // -- helpers ---------------------------------------------------------------

    private static string Cell(string html, string label)
    {
        var m = Regex.Match(html, label + "</td>\\s*<td[^>]*>([^<]*)</td>");
        m.Success.Should().BeTrue("the shipped email template must still carry a " + label + " row");
        return m.Groups[1].Value.Trim();
    }

    private static string? FactValue(JsonElement facts, string title)
    {
        foreach (var f in facts.EnumerateArray())
            if (f.GetProperty("title").GetString() == title)
                return f.GetProperty("value").GetString();
        return null;
    }

    /// <summary>
    /// Records every request the service makes, keyed by URL, and answers each one the way its
    /// real endpoint would on success. Repointed onto the service's private HttpClient: the Graph
    /// mail, PagerDuty and WhatsApp endpoints are hard-coded and no production seam is added for
    /// a test.
    /// </summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly List<(string Url, string Body)> _requests = new();

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

            var body = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            lock (_requests) _requests.Add((url, body));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"result\":{\"number\":\"INC0000001\"},\"messages\":[{\"id\":\"wamid.test\"}]}",
                    Encoding.UTF8, "application/json")
            };
        }

        public string BodyFor(string urlFragment)
        {
            lock (_requests)
            {
                var hit = _requests
                    .Where(r => r.Url.Contains(urlFragment, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                hit.Should().ContainSingle(
                    "exactly one request should have gone to " + urlFragment + "; saw "
                    + string.Join(", ", _requests.Select(r => r.Url)));
                return hit[0].Body;
            }
        }

        public IReadOnlyList<(string Url, string Body)> OtherThanMail()
        {
            lock (_requests)
                return _requests
                    .Where(r => !r.Url.Contains("/sendMail", StringComparison.OrdinalIgnoreCase))
                    .ToList();
        }
    }
}
