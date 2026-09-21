/* In the name of God, the Merciful, the Compassionate */

// ── N-4: Success reported for zero deliveries, 2026-08-22 ────────────────────────────────────
//
// WHY THIS FILE EXISTS. SendWhatsAppAsync guarded on the RAW recipient list
//     if (cfg.RecipientNumbers.Count == 0) return (..., false, "No recipient numbers configured.");
// and then looped over the FILTERED one
//     foreach (var recipient in cfg.RecipientNumbers.Where(n => !string.IsNullOrWhiteSpace(n)))
// So RecipientNumbers = ["  ", ""] passed the guard, sent nothing, collected no failures, and
// fell into
//     if (failures.Count == 0) return new ChannelDispatchResult(channel, true, $"Sent to {sentCount} recipient(s).");
// returning Success with the detail "Sent to 0 recipient(s)." TestWhatsAppAsync surfaces
// result.Detail as the operator's confirmation, so one stray space in the recipients box produced
// a green "delivered" message and no alert would ever arrive.
//
// SendEmailCoreAsync had the identical shape: a raw ToAddresses.Count guard in front of two
// transports that both filter blanks, so an all-blank recipient list connected to the mail host
// to send a message with no recipients.
//
// This is verify-cardinality-not-just-presence: "no failures" is not "delivered". Only a positive
// delivery count is.
//
// WHAT IS REAL HERE. The whitespace cases need no network at all -- the defect is upstream of the
// first HTTP call, which is what made it provable without the sink the original probe lacked. The
// cardinality control repoints the service's own HttpClient at a counting handler and asserts
// EXACTLY ONE request for one recipient: a fix that turned "0 sent" into "1 sent" by counting
// something other than deliveries would pass the first test and fail this one.
//
// AIM AT THE LAYER THAT HAD THE DEFECT (corrected 2026-08-23). The first version of this file
// exercised WhatsApp only through TestWhatsAppAsync, whose own new guard returns before
// SendWhatsAppAsync is ever reached. Reverting SendWhatsAppAsync ALONE therefore passed all 4672
// tests, while the real dispatch path answered "success=True, Sent to 0 recipient(s)." with zero
// HTTP requests made: the exact client-facing defect, alive behind a green suite. The two
// DispatchAsync tests below are aimed at that path.
//
// MUTATIONS THAT MUST FAIL: (1) restore the raw-count guards and drop "&& sentCount > 0" -- the
// whitespace tests go red; (2) revert ONLY SendWhatsAppAsync's guard and relax its success
// condition, leaving the Test button's guard fixed -- the WhatsApp dispatch test goes red on its
// own, which is what the first version could not do.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests;

public sealed class ZeroDeliverySuccessTests : IDisposable
{
    private readonly string _dir;

    public ZeroDeliverySuccessTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "zero-delivery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
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

    private NotificationChannelService NewService(HttpMessageHandler? handler = null)
    {
        var templates = new AlertTemplateService(NullLogger<AlertTemplateService>.Instance,
            Path.Combine(_dir, "alert-templates.json"));
        var svc = new NotificationChannelService(NullLogger<NotificationChannelService>.Instance, templates);
        Repoint(svc, "_configFilePath", Path.Combine(_dir, "notification-channels.json"));
        Invoke(svc, "LoadConfig");
        if (handler != null) Repoint(svc, "_httpClient", new HttpClient(handler));
        return svc;
    }

    // ── WhatsApp ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WhatsApp_all_blank_recipients_is_a_failure_and_reaches_no_network()
    {
        var handler = new CountingHandler();
        var svc = NewService(handler);

        svc.UpdateConfig(new NotificationChannelConfig
        {
            WhatsApp = new WhatsAppChannelConfig
            {
                Enabled = true,
                PhoneNumberId = "123456789012345",
                AccessToken = "not-a-real-token",
                RecipientNumbers = new List<string> { "   ", "" }
            }
        }).Should().Be(StoreWriteOutcome.Saved);

        var (success, message) = await svc.TestWhatsAppAsync();

        success.Should().BeFalse(
            "the shipped code returned Success with the detail \"Sent to 0 recipient(s).\"");
        message.Should().Contain("blank");
        handler.Requests.Should().Be(0, "nothing sendable was configured, so nothing should be attempted");
    }

    [Fact]
    public async Task WhatsApp_one_real_recipient_sends_exactly_one_message()
    {
        var handler = new CountingHandler();
        var svc = NewService(handler);

        svc.UpdateConfig(new NotificationChannelConfig
        {
            WhatsApp = new WhatsAppChannelConfig
            {
                Enabled = true,
                PhoneNumberId = "123456789012345",
                AccessToken = "not-a-real-token",
                // One blank alongside one real number: the blank must be dropped, not counted.
                RecipientNumbers = new List<string> { "  ", " +6421000000 " }
            }
        }).Should().Be(StoreWriteOutcome.Saved);

        var (success, message) = await svc.TestWhatsAppAsync();

        success.Should().BeTrue(message);
        message.Should().Contain("1 recipient");
        handler.Requests.Should().Be(1,
            "one usable recipient is exactly one delivery; a success detail that counts anything "
            + "other than deliveries is the same defect wearing a different number");
        using var doc = System.Text.Json.JsonDocument.Parse(handler.LastBody);
        doc.RootElement.GetProperty("to").GetString().Should().Be("+6421000000",
            "the recipient must reach the wire trimmed, not with the surrounding spaces");
    }

    /// <summary>
    /// The DEFECT'S OWN PATH. N-4 lives in SendWhatsAppAsync, which is what a real alert fire
    /// reaches through DispatchAsync; TestWhatsAppAsync is the button beside it and has its own
    /// guard. The first version of this file tested only the button, and the button's new guard
    /// returns first -- so with SendWhatsAppAsync reverted on its own, the real dispatch path
    /// answered "WhatsApp success=True, Sent to 0 recipient(s)." with zero HTTP requests made, and
    /// all 4672 tests stayed green. This test is aimed at the layer that had the defect.
    /// </summary>
    [Fact]
    public async Task WhatsApp_dispatch_with_all_blank_recipients_reports_failure_not_success()
    {
        var handler = new CountingHandler();
        var svc = NewService(handler);

        svc.UpdateConfig(new NotificationChannelConfig
        {
            WhatsApp = new WhatsAppChannelConfig
            {
                Enabled = true,
                PhoneNumberId = "123456789012345",
                AccessToken = "not-a-real-token",
                RecipientNumbers = new List<string> { "   ", "" },
                MinimumSeverity = "info"
            }
        }).Should().Be(StoreWriteOutcome.Saved);

        var results = await svc.DispatchAsync(new AlertNotification
        {
            AlertName = "Buffer Cache Hit Ratio",
            Metric = "buffer_cache_hit",
            Severity = "critical",
            Message = "A real alert, going nowhere."
        });

        results.Should().ContainSingle();
        results[0].Channel.Should().Be("WhatsApp");
        results[0].Success.Should().BeFalse(
            "the shipped code returned Success with the detail \"Sent to 0 recipient(s).\"");
        results[0].Detail.Should().Contain("blank");
        results[0].Detail.Should().NotContain("Sent to 0",
            "no delivery happened, so nothing may report a send count as if one had");
        handler.Requests.Should().Be(0);
    }

    /// <summary>
    /// The cardinality control on the DISPATCH path, not the button: one usable recipient is
    /// exactly one HTTP request and exactly one success. A fix that turned "0 sent" into "1 sent"
    /// by counting something other than deliveries would pass the test above and fail this one.
    /// </summary>
    [Fact]
    public async Task WhatsApp_dispatch_with_one_usable_recipient_sends_exactly_one_message()
    {
        var handler = new CountingHandler();
        var svc = NewService(handler);

        svc.UpdateConfig(new NotificationChannelConfig
        {
            WhatsApp = new WhatsAppChannelConfig
            {
                Enabled = true,
                PhoneNumberId = "123456789012345",
                AccessToken = "not-a-real-token",
                RecipientNumbers = new List<string> { "  ", " +6421000000 " },
                MinimumSeverity = "info"
            }
        }).Should().Be(StoreWriteOutcome.Saved);

        var results = await svc.DispatchAsync(new AlertNotification
        {
            AlertName = "Buffer Cache Hit Ratio",
            Metric = "buffer_cache_hit",
            Severity = "critical",
            Message = "A real alert, going somewhere."
        });

        results.Should().ContainSingle();
        results[0].Success.Should().BeTrue(results[0].Detail);
        results[0].Detail.Should().Contain("1 recipient");
        handler.Requests.Should().Be(1);
    }

    // ── SMTP, the same shape ────────────────────────────────────────────────────

    [Fact]
    public async Task Smtp_all_blank_recipients_is_a_failure_and_reaches_no_network()
    {
        var handler = new CountingHandler();
        var svc = NewService(handler);

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
                ToAddresses = new List<string> { " ", "" }
            }
        }).Should().Be(StoreWriteOutcome.Saved);

        var (success, message) = await svc.TestSmtpAsync();

        success.Should().BeFalse();
        message.Should().Contain("blank");
        handler.Requests.Should().Be(0,
            "the shipped guard let this through to a token request and a sendMail with no recipients");
    }

    /// <summary>
    /// Gate finding 2026-08-23 (low): the Test button's success message re-joined the RAW
    /// ToAddresses list, so a blank entry was printed as though a test email had gone to it.
    /// The message must name only the recipients the send actually addressed.
    /// Mutation that must fail: restore string.Join(", ", smtp.ToAddresses) in TestSmtpAsync.
    /// </summary>
    [Fact]
    public async Task Smtp_test_success_message_names_only_the_filtered_recipients()
    {
        var handler = new CountingHandler();
        var svc = NewService(handler);

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
                ToAddresses = new List<string> { "b@x.invalid", "   " }
            }
        }).Should().Be(StoreWriteOutcome.Saved);

        var (success, message) = await svc.TestSmtpAsync();

        success.Should().BeTrue();
        message.Should().Contain("b@x.invalid");
        message.Should().NotContain(",   ",
            "the blank entry was filtered out of the send and must not be printed as a recipient");
        message.Should().NotContain(", ,");
        message.Trim().Should().NotEndWith(",");
    }

    [Fact]
    public async Task Smtp_dispatch_with_all_blank_recipients_reports_failure_not_success()
    {
        var handler = new CountingHandler();
        var svc = NewService(handler);

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
                ToAddresses = new List<string> { "  " },
                MinimumSeverity = "info"
            }
        }).Should().Be(StoreWriteOutcome.Saved);

        var results = await svc.DispatchAsync(new AlertNotification
        {
            AlertName = "Buffer Cache Hit Ratio",
            Metric = "buffer_cache_hit",
            Severity = "critical",
            Message = "A real alert, going nowhere.",
            SendEmail = true
        });

        results.Should().ContainSingle();
        results[0].Success.Should().BeFalse();
        results[0].Detail.Should().Contain("blank");
        handler.Requests.Should().Be(0);
    }

    /// <summary>
    /// Counts requests and keeps the last body. Answers 2xx to everything so that a delivery
    /// which DOES happen is not scored as a failure for the wrong reason. Repointed onto the
    /// service's private HttpClient: the WhatsApp Graph endpoint and the Microsoft Graph mail
    /// endpoint are both hard-coded and have no config seam, and none is added for a test.
    /// </summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);
        public string LastBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            if (request.Content != null)
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);

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

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"messages\":[{\"id\":\"wamid.test\"}]}",
                    Encoding.UTF8, "application/json")
            };
        }
    }
}
