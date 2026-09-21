/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// r2-06 (the single worst finding of the 188-item hunt): the REST alert surface reported the
    /// AlertingService notification queue, whose only writer — EvaluateAlerts — has no call site
    /// anywhere in the repository. So <c>/api/v1/status</c> returned <c>activeAlerts:0</c> and
    /// <c>/api/v1/alerts</c> returned <c>{unacknowledgedCount:0, total:0, alerts:[]}</c> on every
    /// install, always, while alerts fired. A Grafana/Zabbix poller got 200 OK, valid JSON, and a
    /// plausible zero that could never be anything else.
    ///
    /// <para>These drive the SHIPPED surface over a real socket, exactly as ServerModeService
    /// composes it, with a real active alert seeded into the same history db the handlers read — the
    /// state the Alerts page and NOC render. Pre-fix (the endpoints read AlertingService) the alert
    /// is invisible here; post-fix it appears.</para>
    /// </summary>
    public sealed class AlertsApiLiveStateTests
    {
        [Fact]
        public async Task StatusAndAlerts_reportARealActiveAlert_notAPermanentZero()
        {
            await using var host = await ApiSurfaceHost.StartAsync(apiKey: ApiSurfaceHost.TestApiKey);
            var history = host.Service<AlertHistoryService>();

            var alertId = "r2-06-probe-" + Guid.NewGuid().ToString("N");
            const string server = "R2-06-PROBE-SRV";
            var rowId = SeedActiveAlert(history, alertId, server);

            try
            {
                using var client = KeyedClient(host);

                // /api/v1/alerts must now surface the real active alert (metric == alertId).
                var alertsBody = await GetString(client, "/api/v1/alerts");
                Assert.Contains(alertId, alertsBody);

                using (var doc = JsonDocument.Parse(alertsBody))
                {
                    Assert.True(doc.RootElement.GetProperty("unacknowledgedCount").GetInt32() >= 1);
                    Assert.True(doc.RootElement.GetProperty("total").GetInt32() >= 1);
                    var mine = doc.RootElement.GetProperty("alerts").EnumerateArray()
                        .First(a => a.GetProperty("metric").GetString() == alertId);
                    Assert.Equal("R2-06 probe", mine.GetProperty("alertName").GetString());
                    Assert.Equal(server, mine.GetProperty("instanceName").GetString());
                    Assert.False(mine.GetProperty("isAcknowledged").GetBoolean());
                }

                // /api/v1/status activeAlerts must reflect the real Active count.
                var statusBody = await GetString(client, "/api/v1/status");
                using (var doc = JsonDocument.Parse(statusBody))
                {
                    Assert.True(doc.RootElement.GetProperty("activeAlerts").GetInt32() >= 1,
                        "activeAlerts read the dead notification queue and was 0 on every install; it "
                        + "must now report the real count of Active alerts.");
                }
            }
            finally
            {
                history.ResolveAlert(alertId, server); // leave the shared history db as we found it
            }
        }

        [Fact]
        public async Task Acknowledge_actuallyAcknowledgesTheRealAlert_andHonestlyReports404ForAnUnknownId()
        {
            await using var host = await ApiSurfaceHost.StartAsync(apiKey: ApiSurfaceHost.TestApiKey);
            var history = host.Service<AlertHistoryService>();

            var alertId = "r2-06-ack-" + Guid.NewGuid().ToString("N");
            const string server = "R2-06-ACK-SRV";
            var rowId = SeedActiveAlert(history, alertId, server);

            try
            {
                using var client = KeyedClient(host);

                var ok = await client.PostAsync($"/api/v1/alerts/{rowId}/acknowledge", content: null);
                Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

                var acked = history.GetActiveAlerts().First(r => r.Id == rowId);
                Assert.Equal("Acknowledged", acked.Status);

                // A row id that is not an active alert is answered honestly, not a blanket success.
                var missing = await client.PostAsync("/api/v1/alerts/999999999/acknowledge", content: null);
                Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            }
            finally
            {
                history.ResolveAlert(alertId, server);
            }
        }

        private static long SeedActiveAlert(AlertHistoryService history, string alertId, string server)
            => history.UpsertAlert(new AlertState
            {
                AlertId = alertId,
                AlertName = "R2-06 probe",
                ServerName = server,
                Severity = "Critical",
                Status = AlertStatus.Active,
                LastValue = 99,
                ThresholdValue = 1,
                BasisKind = "FixedThreshold",
                HitCount = 1,
                FirstTriggered = DateTime.UtcNow,
                LastTriggered = DateTime.UtcNow,
                Message = "R2-06 probe alert",
            });

        private static HttpClient KeyedClient(ApiSurfaceHost host)
        {
            var client = ApiSurfaceHost.Client(host.NonLoopbackBase);
            client.DefaultRequestHeaders.Add("X-API-Key", ApiSurfaceHost.TestApiKey);
            return client;
        }

        private static async Task<string> GetString(HttpClient client, string path)
        {
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadAsStringAsync();
        }
    }
}
