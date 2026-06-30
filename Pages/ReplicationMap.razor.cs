/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.Data.SqlClient;
using SQLTriage.Data;
using SQLTriage.Data.Models;

namespace SQLTriage.Pages;

// Code-behind for Pages/ReplicationMap.razor. Kept out of the .razor so the
// Razor source generator doesn't conflate the heavy interpolated-MarkupString
// markup with these nested types/methods (it mis-scoped them when co-located).
public partial class ReplicationMap : ComponentBase
{
    private const double PubX = 360;    // publication node right edge / pin
    private const double SubX = 1040;   // subscription node left edge / pin
    private const double Top = 70;
    private const double RowGap = 50;

    private List<(string Key, string Display)> _targets = new();
    private string _selected = "";
    private bool _scanning, _scanned, _sample, _showSql;
    private string? _error;
    private string _distributor = "";
    private int _selEdge = -1;
    private double _vbH = 400;

    private readonly List<Node> _pubNodes = new();
    private readonly List<Node> _subNodes = new();
    private readonly List<Edge> _edges = new();

    private sealed class Node { public string Key = ""; public string Line1 = ""; public string Line2 = ""; public double Y; }

    private sealed class Edge
    {
        public string D = "", Status = "unknown", Agent = "", LastMessage = "", PubLabel = "", SubLabel = "", StatusText = "";
        public long? LatencyMs; public DateTime? LastActivity; public double Delay, Dur;
    }

    // Raw row shape from the scan (or sample).
    private sealed class Row
    {
        public string Publisher = "", PublisherDb = "", Publication = "", Subscriber = "", SubscriberDb = "", Agent = "", LastMessage = "";
        public int? RunStatus; public long? LatencyMs; public DateTime? LastActivity;
    }

    protected override void OnInitialized()
    {
        try
        {
            foreach (var conn in ConnectionManager.GetConnections())
                foreach (var srv in conn.GetServerList())
                    _targets.Add(($"{conn.Id}|{srv}", srv));
            _targets = _targets.GroupBy(t => t.Display, StringComparer.OrdinalIgnoreCase)
                               .Select(g => g.First()).OrderBy(t => t.Display).ToList();
        }
        catch { /* leave empty; Sample still works */ }
    }

    private void Clear()
    {
        _scanned = _sample = false; _error = null; _selEdge = -1;
        _pubNodes.Clear(); _subNodes.Clear(); _edges.Clear();
    }

    private async Task ScanAsync()
    {
        if (string.IsNullOrEmpty(_selected)) return;
        _scanning = true; _error = null; _sample = false; _selEdge = -1;
        _pubNodes.Clear(); _subNodes.Clear(); _edges.Clear();
        StateHasChanged();

        var parts = _selected.Split('|', 2);
        var conn = ConnectionManager.GetConnections().FirstOrDefault(c => c.Id == parts[0]);
        var server = parts.Length > 1 ? parts[1] : "";
        _distributor = server;

        try
        {
            if (conn == null || string.IsNullOrEmpty(server)) { _error = "Server selection is no longer valid."; return; }

            var rows = new List<Row>();
            var connString = conn.GetConnectionString(server, "master");
            using (var sql = new SqlConnection(connString))
            {
                await sql.OpenAsync();
                using var cmd = new SqlCommand(ReplicationQuery, sql) { CommandTimeout = 30 };
                using var rd = await cmd.ExecuteReaderAsync();
                while (await rd.ReadAsync())
                {
                    rows.Add(new Row
                    {
                        Publisher    = S(rd, "Publisher"),
                        PublisherDb  = S(rd, "PublisherDb"),
                        Publication  = S(rd, "Publication"),
                        Subscriber   = S(rd, "Subscriber"),
                        SubscriberDb = S(rd, "SubscriberDb"),
                        Agent        = S(rd, "Agent"),
                        RunStatus    = I(rd, "RunStatus"),
                        LatencyMs    = L(rd, "LatencyMs"),
                        LastMessage  = S(rd, "LastMessage"),
                        LastActivity = D(rd, "LastActivity"),
                    });
                }
            }

            AuditLog.LogSecurityEvent($"Replication Map scan of {server}", AuditSeverity.Info,
                new Dictionary<string, string> { ["Server"] = server, ["Links"] = rows.Count.ToString() });

            BuildLayout(rows);
            _scanned = true;
        }
        catch (Exception ex)
        {
            _error = $"Scan failed: {ex.Message}";
            _scanned = true; // show the empty/error state, not the initial prompt
        }
        finally { _scanning = false; StateHasChanged(); }
    }

    private void LoadSample()
    {
        _error = null; _scanned = false; _selEdge = -1; _distributor = "DIST01 (sample)";
        var now = DateTime.Now;
        var rows = new List<Row>
        {
            new(){ Publisher="SQLPUB01", PublisherDb="Sales",     Publication="Sales_Tran",   Subscriber="SQLSUB01", SubscriberDb="Sales_Replica", Agent="SQLPUB01-Sales-Sales_Tran-1", RunStatus=2, LatencyMs=1200,    LastMessage="2 transactions delivered.", LastActivity=now.AddSeconds(-8) },
            new(){ Publisher="SQLPUB01", PublisherDb="Sales",     Publication="Sales_Tran",   Subscriber="SQLSUB02", SubscriberDb="Sales_Replica", Agent="SQLPUB01-Sales-Sales_Tran-2", RunStatus=4, LatencyMs=800,     LastMessage="No replicated transactions are available.", LastActivity=now.AddSeconds(-3) },
            new(){ Publisher="SQLPUB01", PublisherDb="Inventory", Publication="Inv_Tran",     Subscriber="SQLSUB02", SubscriberDb="Inv_Replica",   Agent="SQLPUB01-Inv-Inv_Tran-1",     RunStatus=5, LatencyMs=480000,  LastMessage="Agent retrying after transient error (timeout).", LastActivity=now.AddMinutes(-7) },
            new(){ Publisher="SQLPUB02", PublisherDb="Orders",    Publication="Orders_Tran",  Subscriber="SQLSUB01", SubscriberDb="Orders_Replica",Agent="SQLPUB02-Orders-Orders_Tran-1",RunStatus=6, LatencyMs=null,    LastMessage="The process could not connect to Subscriber 'SQLSUB01'.", LastActivity=now.AddMinutes(-42) },
            new(){ Publisher="SQLPUB02", PublisherDb="Orders",    Publication="Orders_Tran",  Subscriber="SQLSUB03", SubscriberDb="Orders_Replica",Agent="SQLPUB02-Orders-Orders_Tran-2",RunStatus=2, LatencyMs=2300,    LastMessage="14 transactions delivered.", LastActivity=now.AddSeconds(-12) },
            new(){ Publisher="SQLPUB02", PublisherDb="HR",        Publication="HR_Tran",      Subscriber="SQLSUB03", SubscriberDb="HR_Replica",    Agent="SQLPUB02-HR-HR_Tran-1",        RunStatus=null,LatencyMs=null,    LastMessage="", LastActivity=null },
        };
        BuildLayout(rows);
        _sample = true;
    }

    private void BuildLayout(List<Row> rows)
    {
        _pubNodes.Clear(); _subNodes.Clear(); _edges.Clear();

        var pubIndex = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
        var subIndex = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in rows)
        {
            string pubKey = $"{r.Publisher}|{r.PublisherDb}|{r.Publication}";
            if (!pubIndex.TryGetValue(pubKey, out var pn))
            {
                pn = new Node { Key = pubKey, Line1 = Trunc(r.Publication, 22), Line2 = Trunc($"{r.Publisher}\\{r.PublisherDb}", 26) };
                pubIndex[pubKey] = pn; _pubNodes.Add(pn);
            }
            string subKey = $"{r.Subscriber}|{r.SubscriberDb}";
            if (!subIndex.TryGetValue(subKey, out var sn))
            {
                sn = new Node { Key = subKey, Line1 = Trunc(r.Subscriber, 24), Line2 = Trunc(r.SubscriberDb, 26) };
                subIndex[subKey] = sn; _subNodes.Add(sn);
            }
        }

        int rowsN = Math.Max(_pubNodes.Count, _subNodes.Count);
        double contentH = Math.Max(rowsN, 1) * RowGap;
        _vbH = Top + contentH + 40;
        for (int i = 0; i < _pubNodes.Count; i++) _pubNodes[i].Y = Top + contentH / _pubNodes.Count * (i + 0.5);
        for (int j = 0; j < _subNodes.Count; j++) _subNodes[j].Y = Top + contentH / _subNodes.Count * (j + 0.5);

        int k = 0;
        foreach (var r in rows)
        {
            var pn = pubIndex[$"{r.Publisher}|{r.PublisherDb}|{r.Publication}"];
            var sn = subIndex[$"{r.Subscriber}|{r.SubscriberDb}"];
            double x0 = PubX + 6, y0 = pn.Y, x1 = SubX - 6, y1 = sn.Y, dx = (x1 - x0) * 0.45;
            var (status, statusText) = Health(r.RunStatus, r.LatencyMs);
            _edges.Add(new Edge
            {
                D = $"M{N(x0)} {N(y0)} C{N(x0 + dx)} {N(y0)} {N(x1 - dx)} {N(y1)} {N(x1)} {N(y1)}",
                Status = status, StatusText = statusText,
                Agent = r.Agent, LatencyMs = r.LatencyMs, LastActivity = r.LastActivity, LastMessage = r.LastMessage,
                PubLabel = $"{r.Publisher}\\{r.PublisherDb}:{r.Publication}", SubLabel = $"{r.Subscriber}\\{r.SubscriberDb}",
                Delay = (k * 5) % 20 / 10.0,
                Dur = (2.0 + Math.Abs(y1 - y0) / 240.0) * 2.0,
            });
            k++;
        }
    }

    private void SelectEdge(int i) => _selEdge = (_selEdge == i) ? -1 : i;

    // runstatus: 1 start, 2 succeed, 3 in-progress, 4 idle, 5 retry, 6 fail
    private static (string, string) Health(int? runStatus, long? latencyMs)
    {
        if (runStatus is null) return ("unknown", "No agent history");
        switch (runStatus.Value)
        {
            case 6: return ("bad", "Failed");
            case 5: return ("warn", "Retrying");
            case 1: case 2: case 3: case 4:
                if (latencyMs is > 300000) return ("warn", "Running (high latency)");
                return ("ok", runStatus.Value == 4 ? "Idle (caught up)" : "Running");
            default: return ("unknown", $"Status {runStatus.Value}");
        }
    }

    // ── defensive reader helpers ──
    private static string S(SqlDataReader r, string c) { try { var o = r[c]; return o == DBNull.Value ? "" : o.ToString() ?? ""; } catch { return ""; } }
    private static int? I(SqlDataReader r, string c) { try { var o = r[c]; return o == DBNull.Value ? (int?)null : Convert.ToInt32(o); } catch { return null; } }
    private static long? L(SqlDataReader r, string c) { try { var o = r[c]; return o == DBNull.Value ? (long?)null : Convert.ToInt64(o); } catch { return null; } }
    private static DateTime? D(SqlDataReader r, string c) { try { var o = r[c]; return o == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(o); } catch { return null; } }

    private static string N(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);
    private static string Enc(string s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    private static string Trunc(string s, int n) { s ??= ""; return s.Length <= n ? s : s.Substring(0, n - 1) + "…"; }

    // Defensive transactional-replication discovery, run on the distributor.
    // Exposed in the UI ("Show query") so it can be validated/adjusted on-site.
    private const string ReplicationQuery = @"
SET NOCOUNT ON;

-- Distribution databases are discovered by catalog flag, not by name: the
-- default 'distribution' name is often customised, and one distributor can
-- host several distribution databases. Iterate them all and union the rows.
DECLARE @results TABLE (
    Publisher sysname NULL, PublisherDb sysname NULL, Publication sysname NULL,
    Subscriber sysname NULL, SubscriberDb sysname NULL, Agent nvarchar(256) NULL,
    RunStatus int NULL, LatencyMs bigint NULL,
    LastMessage nvarchar(max) NULL, LastActivity datetime NULL);

DECLARE @dist sysname, @sql nvarchar(max);
DECLARE dist_cur CURSOR LOCAL FAST_FORWARD FOR
    SELECT name FROM sys.databases WHERE is_distributor = 1;
OPEN dist_cur;
FETCH NEXT FROM dist_cur INTO @dist;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @sql = N'
        ;WITH hist AS (
            SELECT h.agent_id, h.runstatus, h.current_delivery_latency AS latency_ms,
                   h.[comments], h.[time],
                   ROW_NUMBER() OVER (PARTITION BY h.agent_id ORDER BY h.[time] DESC) AS rn
            FROM ' + QUOTENAME(@dist) + N'.dbo.MSdistribution_history h
        )
        SELECT
            COALESCE(pub.name, CAST(da.publisher_id  AS sysname)),
            da.publisher_db,
            da.publication,
            COALESCE(sub.name, CAST(da.subscriber_id AS sysname)),
            da.subscriber_db,
            da.name,
            lh.runstatus,
            CAST(lh.latency_ms AS bigint),
            lh.[comments],
            lh.[time]
        FROM ' + QUOTENAME(@dist) + N'.dbo.MSdistribution_agents da
        LEFT JOIN hist lh           ON lh.agent_id = da.id AND lh.rn = 1
        LEFT JOIN master.sys.servers pub ON pub.server_id = da.publisher_id
        LEFT JOIN master.sys.servers sub ON sub.server_id = da.subscriber_id
        WHERE da.subscriber_id <> -2 /* skip virtual/anonymous bookkeeping agents */;';
    BEGIN TRY
        INSERT INTO @results EXEC sys.sp_executesql @sql;
    END TRY BEGIN CATCH
        -- One unreadable distribution db (restoring, permissions) must not sink the scan.
    END CATCH;
    FETCH NEXT FROM dist_cur INTO @dist;
END
CLOSE dist_cur; DEALLOCATE dist_cur;

SELECT Publisher, PublisherDb, Publication, Subscriber, SubscriberDb, Agent,
       RunStatus, LatencyMs, LastMessage, LastActivity
FROM @results
ORDER BY Publisher, Publication, Subscriber;";
}
