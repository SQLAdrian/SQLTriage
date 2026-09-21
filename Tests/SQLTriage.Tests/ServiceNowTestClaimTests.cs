/* In the name of God, the Merciful, the Compassionate */

// ── N-3: the ServiceNow Test button claimed more than it exercised, 2026-08-22 ────────────────
//
// WHY THIS FILE EXISTS. TestServiceNowAsync issues
//     GET {instanceUrl}/api/now/table/{table}?sysparm_limit=1
// and on a 2xx returned "Connected to ServiceNow (...). Table 'incident' is accessible."
// AlertingConfig.razor puts that sentence straight on screen. What actually fires an alert is
// SendServiceNowAsync, which POSTs an incident to the same table. A ServiceNow account with a
// read-only ACL on that table passes the Test button and fails every alert that follows, and the
// operator has a green message saying the channel is configured correctly.
//
// This is the house class in its prose form: the claim is not gated by the measurement that was
// taken. The fix is not a better probe -- the Table API has no side-effect-free create -- it is a
// sentence that names the verb that ran.
//
// WHAT IS REAL HERE. The test stands a real HttpListener up as the ServiceNow instance, points
// the REAL NotificationChannelService at it, and calls the REAL TestServiceNowAsync that the page
// calls. It asserts on the captured request (method and query) and on the returned sentence.
//
// MUTATION THAT MUST FAIL: restore
//     return (true, $"Connected to ServiceNow ({instanceUrl}). Table '{table}' is accessible.");
// The wording test goes red.

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests;

public sealed class ServiceNowTestClaimTests : IDisposable
{
    private readonly string _dir;

    public ServiceNowTestClaimTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "snow-claim-" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public async Task TestServiceNowAsync_reads_one_row_and_says_only_that_it_read()
    {
        var port = FreeLoopbackPort();
        var baseUrl = $"http://127.0.0.1:{port}";

        using var listener = new HttpListener();
        listener.Prefixes.Add(baseUrl + "/");
        listener.Start();

        var receive = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            var method = ctx.Request.HttpMethod;
            var target = ctx.Request.Url!.PathAndQuery;
            var auth = ctx.Request.Headers["Authorization"];

            var payload = System.Text.Encoding.UTF8.GetBytes("{\"result\":[]}");
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(payload);
            ctx.Response.OutputStream.Close();
            return (method, target, auth);
        });

        var templates = new AlertTemplateService(NullLogger<AlertTemplateService>.Instance);
        var svc = new NotificationChannelService(NullLogger<NotificationChannelService>.Instance, templates);
        Repoint(svc, "_configFilePath", Path.Combine(_dir, "notification-channels.json"));
        Invoke(svc, "LoadConfig");

        svc.UpdateConfig(new NotificationChannelConfig
        {
            ServiceNow = new ServiceNowChannelConfig
            {
                Enabled = true,
                InstanceUrl = baseUrl,
                Username = "svc_sqltriage",
                Password = "not-a-real-password",
                Table = "incident"
            }
        }).Should().Be(StoreWriteOutcome.Saved);

        var (success, message) = await svc.TestServiceNowAsync();
        var captured = await receive.WaitAsync(TimeSpan.FromSeconds(10));
        listener.Stop();

        success.Should().BeTrue(message);

        // What was actually exercised: one authenticated READ.
        captured.method.Should().Be("GET",
            "the probe is a read; anything else here would change what the sentence may claim");
        captured.target.Should().Contain("/api/now/table/incident");
        captured.target.Should().Contain("sysparm_limit=1");
        captured.auth.Should().StartWith("Basic ");

        // What the sentence is now allowed to say.
        message.Should().Contain("HTTP GET");
        message.Should().Contain("does not prove");
        message.Should().Contain("CREATE");

        // The exact wording that shipped, and the reading it invited.
        message.Should().NotContain("is accessible",
            "an operator reads 'the table is accessible' as 'my alerts will land', and a read-only "
            + "ACL passes this probe and fails every incident POST that follows");
    }
}
