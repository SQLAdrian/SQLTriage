/* In the name of God, the Merciful, the Compassionate */

// ── The generic webhook's triggeredAtUtc field, actually verified UTC, 2026-08-20 ──────────────
//
// WHY THIS FILE EXISTS. A live delivery of the generic webhook channel carried
// "triggeredAtUtc": "2026-08-20T11:44:44+12:00" -- a field named UTC, carrying local time.
// AlertNotification.TriggeredAt defaulted to DateTime.Now, and NotificationChannelService just
// called .ToString("o") on it at the payload site with no conversion. Every OTHER writer of the
// field already used DateTime.UtcNow (AlertEvaluationService's two dispatch paths), and every
// reader on every channel labels or names the value as UTC without ever converting it -- six
// default templates say "Time (UTC)" or emit a literal triggered_at_utc/triggeredAtUtc JSON key,
// and the Slack/Teams footer builds its epoch stamp via
// new DateTimeOffset(TriggeredAt, TimeSpan.Zero), which is only correct if the value already IS
// UTC. So the fix moved the model's DEFAULT to DateTime.UtcNow rather than converting at each of
// the nine call sites individually -- one root cause, one fix.
//
// WHAT IS REAL HERE. This drives the exact call a person triggers from the Alerts page's "Send
// Test" button (TestWebhookAsync), through the REAL NotificationChannelService and its REAL
// HttpClient, to a REAL local HttpListener standing in for the operator's endpoint. The captured
// bytes are the ones that go over the wire; nothing here hand-composes a payload shape and
// compares field names.

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests;

public sealed class WebhookTriggeredAtUtcTests : IDisposable
{
    private readonly string _dir;

    public WebhookTriggeredAtUtcTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "webhook-utc-" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public async Task TestWebhookAsync_DeliversTriggeredAtUtc_AsRealUtc_NotLocalTime()
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

        var templates = new AlertTemplateService(NullLogger<AlertTemplateService>.Instance);
        var svc = new NotificationChannelService(NullLogger<NotificationChannelService>.Instance, templates);

        // Repointed to an isolated temp store, same discipline as ConfigStoreWriteGuardTests --
        // this must never read or write the shared test-output Config/ folder.
        Repoint(svc, "_configFilePath", Path.Combine(_dir, "notification-channels.json"));
        Invoke(svc, "LoadConfig");

        var outcome = svc.UpdateConfig(new NotificationChannelConfig
        {
            Webhook = new WebhookChannelConfig { Enabled = true, Url = url }
        });
        outcome.Should().Be(StoreWriteOutcome.Saved);

        var (success, message) = await svc.TestWebhookAsync();
        success.Should().BeTrue(message);

        var body = await receiveBody.WaitAsync(TimeSpan.FromSeconds(10));
        listener.Stop();

        using var doc = JsonDocument.Parse(body);
        var raw = doc.RootElement.GetProperty("triggeredAtUtc").GetString();
        raw.Should().NotBeNullOrEmpty();

        // The exact regression: DateTime.Now serialized with .ToString("o") produces a truthful
        // ISO-8601 timestamp carrying a LOCAL offset (the live incident shipped "+12:00") under a
        // field literally named triggeredAtUtc. A DateTime of Kind Utc always serializes with "Z".
        raw.Should().EndWith("Z",
            "the field is named triggeredAtUtc; any offset other than Z here is exactly the delivered defect");

        DateTimeOffset.Parse(raw!).Offset.Should().Be(TimeSpan.Zero);
    }
}
