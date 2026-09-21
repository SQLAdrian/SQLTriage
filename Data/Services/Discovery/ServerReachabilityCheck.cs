/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services.Discovery
{
    /// <summary>
    /// One-shot "can we reach this SQL instance right now" probe, classified with the SAME
    /// three-state observation the circuit-breaker fix established
    /// (<see cref="AlertEvaluationService.ServerReachability"/>, commit e231bcc). The enum is
    /// reused, not re-invented, because the rule it encodes is the point:
    /// <list type="bullet">
    /// <item><b>Reached</b> — a connection opened. The server is up and answering.</item>
    /// <item><b>Unreachable</b> — the attempt failed at the SQL/transport layer. A real failure.</item>
    /// <item><b>Undetermined</b> — nothing was learned (cancelled, or the attempt fell over for a
    /// reason that is ours, not the server's). Saying "reachable" here would be the alert-loop
    /// bug again; saying "unreachable" would condemn a healthy server for our own defect.</item>
    /// </list>
    /// </summary>
    internal static class ServerReachabilityCheck
    {
        internal readonly struct Observation
        {
            public AlertEvaluationService.ServerReachability State { get; }
            /// <summary>The measured reason. Empty only for <see cref="AlertEvaluationService.ServerReachability.Reached"/>.</summary>
            public string Reason { get; }

            public Observation(AlertEvaluationService.ServerReachability state, string reason)
            {
                State = state;
                Reason = reason ?? "";
            }
        }

        /// <summary>Maps one attempt's outcome onto the three states. Pure — the test seam.</summary>
        internal static Observation Classify(Exception? error, bool cancelled)
        {
            if (cancelled || error is OperationCanceledException)
                return new Observation(AlertEvaluationService.ServerReachability.Undetermined,
                    "the probe was cancelled before it learned anything about this server");

            if (error == null)
                return new Observation(AlertEvaluationService.ServerReachability.Reached, "");

            // Failures that are ABOUT the server: the SQL layer or the transport refused, timed
            // out, or could not be reached.
            if (error is SqlException || error is TimeoutException || error is SocketException)
                return new Observation(AlertEvaluationService.ServerReachability.Unreachable, Flatten(error));

            // Everything else says nothing about the server — a platform, argument or
            // configuration fault is ours. Undetermined, and the reason is printed as ours.
            return new Observation(AlertEvaluationService.ServerReachability.Undetermined,
                "the probe could not be carried out, so nothing was learned about this server: " + Flatten(error));
        }

        /// <summary>Opens a connection to <paramref name="server"/> using the credentials of an
        /// existing connection, and classifies the outcome. Read-only: it opens and closes.</summary>
        internal static async Task<Observation> ProbeAsync(
            ServerConnection seed, string server, int timeoutSeconds, CancellationToken ct)
        {
            try
            {
                var connString = seed.GetConnectionString(server, "master");
                var builder = new SqlConnectionStringBuilder(connString)
                {
                    ConnectTimeout = Math.Max(1, timeoutSeconds)
                };
                using var conn = new SqlConnection(builder.ConnectionString);
                await conn.OpenAsync(ct).ConfigureAwait(false);
                return Classify(null, false);
            }
            catch (OperationCanceledException)
            {
                return Classify(null, true);
            }
            catch (Exception ex)
            {
                return Classify(ex, ct.IsCancellationRequested);
            }
        }

        private static string Flatten(Exception ex)
        {
            var msg = (ex.Message ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return msg.Length == 0 ? ex.GetType().Name : msg;
        }
    }
}
