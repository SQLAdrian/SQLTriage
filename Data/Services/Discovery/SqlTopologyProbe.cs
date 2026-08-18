// In the name of God, the Merciful, the Compassionate
// Read-only per-server topology probe: discovers replication / AG / mirroring / log-shipping / linked-server
// neighbours + an aggregate client count. NEVER writes. Each source is independently guarded so a missing
// feature (no AG, no distributor, no msdb log-shipping tables) never fails the probe.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services.Discovery
{
    public sealed class SqlTopologyProbe
    {
        private readonly ILogger<SqlTopologyProbe>? _log;
        public SqlTopologyProbe(ILogger<SqlTopologyProbe>? log = null) => _log = log;

        public async Task<ProbeResult> ProbeAsync(ServerConnection seed, string serverName, int timeoutSeconds, CancellationToken ct)
        {
            var result = new ProbeResult { Server = serverName };
            try
            {
                var connStr = seed.GetConnectionString(serverName, "master");
                using var conn = new SqlConnection(connStr);
                await conn.OpenAsync(ct).ConfigureAwait(false);
                result.Reachable = true;

                using (var cmd = new SqlCommand(TopologySql, conn) { CommandTimeout = timeoutSeconds })
                using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        var target = reader.IsDBNull(0) ? "" : reader.GetString(0);
                        var kindStr = reader.IsDBNull(1) ? "" : reader.GetString(1);
                        var detail = reader.IsDBNull(2) ? "" : reader.GetString(2);
                        if (string.IsNullOrWhiteSpace(target)) continue;
                        if (TryMapKind(kindStr, out var kind))
                            result.Edges.Add((target.Trim(), kind, detail ?? ""));
                    }
                }

                using (var cmd = new SqlCommand(ClientCountSql, conn) { CommandTimeout = timeoutSeconds })
                {
                    var obj = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                    result.ClientCount = (obj is int i) ? i : Convert.ToInt32(obj ?? 0);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                result.Reachable = false;
                result.Error = ex.Message;
                _log?.LogDebug(ex, "Topology probe failed for {Server}", serverName);
            }
            return result;
        }

        private static bool TryMapKind(string s, out EdgeKind kind)
        {
            switch (s)
            {
                case "linked-server":          kind = EdgeKind.LinkedServer; return true;
                case "replication-publisher":  kind = EdgeKind.ReplicationPublisher; return true;
                case "replication-subscriber": kind = EdgeKind.ReplicationSubscriber; return true;
                case "ag-replica":             kind = EdgeKind.AgReplica; return true;
                case "mirror-partner":         kind = EdgeKind.MirrorPartner; return true;
                case "logship-primary":        kind = EdgeKind.LogShipPrimary; return true;
                case "logship-secondary":      kind = EdgeKind.LogShipSecondary; return true;
                default:                        kind = EdgeKind.LinkedServer; return false;
            }
        }

        // Distinct client hosts currently connected (aggregate only — never per-client nodes).
        private const string ClientCountSql =
            "SET NOCOUNT ON; SELECT COUNT(DISTINCT host_name) FROM sys.dm_exec_sessions " +
            "WHERE is_user_process = 1 AND host_name IS NOT NULL AND host_name <> '';";

        // Read-only. Returns (TargetServer, Kind, Detail). Each source guarded by TRY/CATCH.
        private const string TopologySql = @"
SET NOCOUNT ON;
DECLARE @me sysname = @@SERVERNAME;
DECLARE @r TABLE (TargetServer nvarchar(256), Kind varchar(32), Detail nvarchar(256));

BEGIN TRY
  INSERT INTO @r (TargetServer, Kind, Detail)
  SELECT name, 'linked-server', ISNULL(provider, '') FROM sys.servers WHERE is_linked = 1 AND name <> @me;
END TRY BEGIN CATCH END CATCH;

BEGIN TRY
  INSERT INTO @r (TargetServer, Kind, Detail)
  SELECT name, 'replication-publisher', 'sys.servers is_publisher' FROM sys.servers WHERE is_publisher = 1 AND name <> @me;
END TRY BEGIN CATCH END CATCH;

BEGIN TRY
  INSERT INTO @r (TargetServer, Kind, Detail)
  SELECT name, 'replication-subscriber', 'sys.servers is_subscriber' FROM sys.servers WHERE is_subscriber = 1 AND name <> @me;
END TRY BEGIN CATCH END CATCH;

BEGIN TRY
  INSERT INTO @r (TargetServer, Kind, Detail)
  SELECT DISTINCT ar.replica_server_name, 'ag-replica', ag.name
  FROM sys.availability_replicas ar
  JOIN sys.availability_groups ag ON ag.group_id = ar.group_id
  WHERE ar.replica_server_name <> @me;
END TRY BEGIN CATCH END CATCH;

BEGIN TRY
  INSERT INTO @r (TargetServer, Kind, Detail)
  SELECT DISTINCT mirroring_partner_instance, 'mirror-partner', ISNULL(DB_NAME(database_id), '')
  FROM sys.database_mirroring
  WHERE mirroring_partner_instance IS NOT NULL;
END TRY BEGIN CATCH END CATCH;

BEGIN TRY
  INSERT INTO @r (TargetServer, Kind, Detail)
  SELECT DISTINCT secondary_server, 'logship-secondary', ISNULL(secondary_database, '')
  FROM msdb.dbo.log_shipping_primary_secondaries;
END TRY BEGIN CATCH END CATCH;

BEGIN TRY
  INSERT INTO @r (TargetServer, Kind, Detail)
  SELECT DISTINCT primary_server, 'logship-primary', ISNULL(primary_database, '')
  FROM msdb.dbo.log_shipping_secondary;
END TRY BEGIN CATCH END CATCH;

-- Replication subscribers from the distribution database (best-effort; schema varies by version).
BEGIN TRY
  IF EXISTS (SELECT 1 FROM sys.databases WHERE is_distributor = 1)
  BEGIN
    DECLARE @dist sysname = (SELECT TOP 1 name FROM sys.databases WHERE is_distributor = 1);
    DECLARE @sql nvarchar(max) = N'
      SELECT DISTINCT s.srvname, ''replication-subscriber'', ISNULL(p.publication, '''')
      FROM ' + QUOTENAME(@dist) + N'.dbo.MSsubscriptions sub
      JOIN ' + QUOTENAME(@dist) + N'.dbo.MSpublications p ON p.publication_id = sub.publication_id
      JOIN master.sys.sysservers s ON s.srvid = sub.subscriber_id
      WHERE s.srvname <> @me';
    INSERT INTO @r (TargetServer, Kind, Detail) EXEC sys.sp_executesql @sql, N'@me sysname', @me = @me;
  END
END TRY BEGIN CATCH END CATCH;

SELECT TargetServer, Kind, Detail FROM @r
WHERE TargetServer IS NOT NULL AND LTRIM(RTRIM(TargetServer)) <> '';";
    }
}
