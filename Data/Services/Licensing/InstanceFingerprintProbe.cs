/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services.Licensing
{
    /// <summary>
    /// Captures the <see cref="InstanceFingerprint"/> (the seat identity) for a SQL instance.
    ///
    /// CONTRACT:
    ///   • READ-ONLY. Four SERVERPROPERTY reads + one row from sys.databases. Nothing is written.
    ///   • ONE ROUND TRIP. A single command, single result row — no per-object chatter. This runs
    ///     once per server per run, alongside DetectEngineEditionAsync (the same precedent: X-1/FM-3).
    ///   • HONEST FAILURE. Returns null on ANY failure (unreachable, permission denied, timeout,
    ///     NULL MachineName). A null fingerprint means NOT SEATED, which means NOT REPORTED.
    ///     A fingerprint is NEVER invented, defaulted, or fabricated from a connection string —
    ///     that would let an unreachable server silently occupy a paid seat.
    ///
    /// The probe needs no elevated rights: SERVERPROPERTY is public, and sys.databases always
    /// returns the master row to any login (it is visible to all principals). So a low-privilege
    /// monitoring login still fingerprints fine.
    /// </summary>
    public static class InstanceFingerprintProbe
    {
        /// <summary>Same 10s ceiling as DetectEngineEditionAsync — a slow/blocked server must not
        /// stall the run just to be fingerprinted.</summary>
        private const int ProbeTimeoutSeconds = 10;

        /// <summary>
        /// The probe. Exactly the ruled shape (contract, 2026-07-17) — do not add columns without
        /// re-ruling: MasterCreateDate feeds the hash, so a change here re-fingerprints every
        /// seated instance in the estate and burns every swap.
        /// </summary>
        internal const string ProbeSql = @"
SELECT CAST(SERVERPROPERTY('MachineName')  AS nvarchar(256)) AS MachineName,
       CAST(SERVERPROPERTY('InstanceName') AS nvarchar(256)) AS InstanceName,
       CAST(SERVERPROPERTY('ServerName')   AS nvarchar(256)) AS ServerName,
       (SELECT create_date FROM sys.databases WHERE name = 'master') AS MasterCreateDate;";

        /// <summary>
        /// Fingerprints the instance reachable at <paramref name="connectionString"/>.
        /// Returns null on any failure — the caller MUST treat null as "not seated", never as
        /// "seat it anyway".
        /// </summary>
        public static async Task<InstanceFingerprint?> TryProbeAsync(
            string connectionString, CancellationToken ct = default)
        {
            try
            {
                using var conn = new SqlConnection(connectionString);
                await conn.OpenAsync(ct);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = ProbeSql;
                cmd.CommandTimeout = ProbeTimeoutSeconds;

                using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SingleRow, ct);
                if (!await reader.ReadAsync(ct))
                    return null;

                var machine = reader.IsDBNull(0) ? null : reader.GetString(0);
                var instance = reader.IsDBNull(1) ? null : reader.GetString(1);
                var serverName = reader.IsDBNull(2) ? null : reader.GetString(2);

                // MachineName and MasterCreateDate are BOTH load-bearing for the hash. If either is
                // absent the identity is not determinable, and a fingerprint built from a partial
                // read would be a fabricated identity that could collide with a real one. Refuse.
                if (string.IsNullOrWhiteSpace(machine) || reader.IsDBNull(3))
                    return null;

                var masterCreateDate = reader.GetDateTime(3);

                return new InstanceFingerprint
                {
                    MachineName = machine!,
                    InstanceName = instance,
                    ServerName = serverName,
                    MasterCreateDate = masterCreateDate,
                };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Caller-initiated cancellation is not a probe failure — let it propagate so a
                // cancelled run does not look like an unfingerprintable server.
                throw;
            }
            catch
            {
                // Unreachable / denied / timeout / malformed. No fingerprint => not seated =>
                // not reported. Deliberately silent here: the seat register logs the honest
                // "could not fingerprint" outcome once, with the server name, at the decision point.
                return null;
            }
        }
    }
}
