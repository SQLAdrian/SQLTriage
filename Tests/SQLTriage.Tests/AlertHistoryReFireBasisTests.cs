/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// alerts-r1-13: <see cref="AlertHistoryService.UpsertAlert"/>'s UPDATE branch refreshed
    /// value/severity/message on an already-Active row but NOT threshold_value or basis_kind — only
    /// INSERT wrote those. So an alert that re-fired on a DIFFERENT basis (a fixed threshold no longer
    /// breached, a learned fence or a trend now firing) kept the first fire's stale threshold and
    /// basis on the persisted row, which <c>ThresholdFromRow</c> then handed back as a measurement
    /// because the stale basis said it was one. The layer outlives the process, so a restart re-read
    /// the stale number.
    ///
    /// <para>Driven against the real encrypted history database beside the test assembly (the same
    /// store the API and NOC read), with a unique alert id per test so parallel runs do not collide,
    /// and cleaned up on the way out.</para>
    /// </summary>
    public sealed class AlertHistoryReFireBasisTests
    {
        [Fact]
        public void ReFiringOnANewBasis_MovesThresholdAndBasisOnTheRow_NotJustValueAndSeverity()
        {
            using var history = new AlertHistoryService(NullLogger<AlertHistoryService>.Instance);

            var alertId = "r1-13-" + Guid.NewGuid().ToString("N");
            var server = "R1-13-SRV-" + Guid.NewGuid().ToString("N");
            var now = DateTime.UtcNow;

            try
            {
                // First fire: a fixed-threshold breach. threshold 5.0, basis FixedThreshold — a real
                // measurement the reader can sanity-check.
                history.UpsertAlert(new AlertState
                {
                    AlertId = alertId, AlertName = "Re-fire", ServerName = server,
                    Severity = "Warning", Status = AlertStatus.Active,
                    LastValue = 6.0, ThresholdValue = 5.0, BasisKind = "FixedThreshold",
                    HitCount = 1, FirstTriggered = now, LastTriggered = now, Message = "first fire",
                });

                // Re-fire on a DIFFERENT basis: a Trend, which carries NO threshold at all. Same row
                // (same alert id + server, still Active) so the UPDATE branch runs.
                history.UpsertAlert(new AlertState
                {
                    AlertId = alertId, AlertName = "Re-fire", ServerName = server,
                    Severity = "Critical", Status = AlertStatus.Active,
                    LastValue = 2.0, ThresholdValue = null, BasisKind = "Trend",
                    HitCount = 1, FirstTriggered = now, LastTriggered = now.AddSeconds(1),
                    Message = "now trending",
                });

                var row = history.GetHistoryByAlert(alertId).Single(r => r.ServerName == server);

                // Everything the old UPDATE already moved.
                Assert.Equal("now trending", row.Message);
                Assert.Equal(2.0, row.Value);
                Assert.Equal("Critical", row.Severity);

                // The r1-13 fix: basis and threshold moved with the fire. Before it, BasisKind stayed
                // "FixedThreshold" and ThresholdValue read back 5.0 — a stale number presented as a
                // measurement on a trend fire that measured no threshold.
                Assert.Equal("Trend", row.BasisKind);
                Assert.Null(row.ThresholdValue);
            }
            finally
            {
                history.ResolveAlert(alertId, server);
            }
        }
    }
}
