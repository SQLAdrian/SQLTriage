/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.Data.SqlClient;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Discovery;
using SQLTriage.Data.Services.Replication;

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

    // ServerSelector id-mode items: Id = "connectionId|serverName", Label = server name.
    private List<(string Id, string Label)> _items = new();
    private string? _selectedId;        // null / empty == the "All servers" chip.
    private bool _scanning, _scanned, _sample, _showSql;
    private string? _error;
    private string _distributor = "";
    private int _selEdge = -1;
    private double _vbH = 400;

    private readonly List<Node> _pubNodes = new();
    private readonly List<Node> _subNodes = new();
    private readonly List<Edge> _edges = new();

    // Scope A.2/A.4 state: the instance-grouped view and the addable participants.
    private readonly List<ReplicationLink> _links = new();
    private readonly List<ScanOutcome> _outcomes = new();
    private List<ReplicationInstanceGroup> _groups = new();
    private ParticipantCandidates _participants = new();
    private string? _unresolvedNotice;
    private string _scanSummary = "";
    private readonly Dictionary<string, ProbeState> _probes = new(StringComparer.Ordinal);

    private sealed class ProbeState
    {
        public bool Running;
        public bool Probed;
        public AlertEvaluationService.ServerReachability State = AlertEvaluationService.ServerReachability.Undetermined;
        public string Reason = "";
    }

    private sealed class Node { public string Key = ""; public string Line1 = ""; public string Line2 = ""; public bool Unresolved; public string? Detail; public double Y; }

    private sealed class Edge
    {
        public string D = "", Status = "unknown", Agent = "", LastMessage = "", PubLabel = "", SubLabel = "", StatusText = "";
        public string? PubDetail, SubDetail;
        public long? LatencyMs; public DateTime? LastActivity; public double Delay, Dur;
    }

    protected override void OnInitialized()
    {
        try
        {
            var items = new List<(string Id, string Label)>();
            foreach (var conn in ConnectionManager.GetConnections())
                foreach (var srv in conn.GetServerList())
                    items.Add(($"{conn.Id}|{srv}", srv));
            _items = items.GroupBy(t => t.Label, StringComparer.OrdinalIgnoreCase)
                          .Select(g => g.First()).OrderBy(t => t.Label, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch { /* leave empty; Sample still works */ }
    }

    private Task OnSelectionChanged(string? id)
    {
        _selectedId = id;
        return Task.CompletedTask;
    }

    private void Clear()
    {
        _scanned = _sample = false; _error = null; _selEdge = -1;
        _pubNodes.Clear(); _subNodes.Clear(); _edges.Clear();
        _links.Clear(); _outcomes.Clear(); _groups = new(); _participants = new();
        _unresolvedNotice = null; _scanSummary = ""; _probes.Clear();
        _distributor = "";
    }

    /// <summary>The servers this scan will target: the selected one, or every catalogued
    /// server when the "All servers" chip is active.</summary>
    private List<(ServerConnection Conn, string Server)> ResolveTargets()
    {
        var connections = ConnectionManager.GetConnections();
        var targets = new List<(ServerConnection, string)>();

        if (!string.IsNullOrEmpty(_selectedId))
        {
            var parts = _selectedId!.Split('|', 2);
            var c = connections.FirstOrDefault(x => x.Id == parts[0]);
            var s = parts.Length > 1 ? parts[1] : "";
            if (c != null && !string.IsNullOrEmpty(s)) targets.Add((c, s));
            return targets;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in connections)
            foreach (var s in c.GetServerList())
                if (!string.IsNullOrWhiteSpace(s) && seen.Add(s))
                    targets.Add((c, s));
        return targets;
    }

    private async Task ScanAsync()
    {
        var targets = ResolveTargets();
        if (targets.Count == 0) { _error = "No server is selected, and the catalogue has none to scan."; return; }

        _scanning = true; _error = null; _sample = false; _selEdge = -1;
        _pubNodes.Clear(); _subNodes.Clear(); _edges.Clear();
        _links.Clear(); _outcomes.Clear(); _probes.Clear();
        _groups = new(); _participants = new(); _unresolvedNotice = null; _scanSummary = "";
        StateHasChanged();

        // Which distributor named a participant — the probe borrows that connection's credentials.
        var provenance = new Dictionary<string, (string Distributor, string ConnectionId)>(StringComparer.Ordinal);

        try
        {
            foreach (var (conn, server) in targets)
            {
                var rows = new List<ReplicationLink>();
                try
                {
                    var connString = conn.GetConnectionString(server, "master");
                    using (var sql = new SqlConnection(connString))
                    {
                        await sql.OpenAsync();
                        using var cmd = new SqlCommand(ReplicationQuery, sql) { CommandTimeout = 30 };
                        using var rd = await cmd.ExecuteReaderAsync();
                        while (await rd.ReadAsync())
                        {
                            rows.Add(new ReplicationLink
                            {
                                Distributor   = server,
                                PublisherName = SN(rd, "Publisher"),
                                PublisherId   = I(rd, "PublisherId"),
                                PublisherDb   = S(rd, "PublisherDb"),
                                Publication   = S(rd, "Publication"),
                                SubscriberName= SN(rd, "Subscriber"),
                                SubscriberId  = I(rd, "SubscriberId"),
                                SubscriberDb  = S(rd, "SubscriberDb"),
                                Agent         = S(rd, "Agent"),
                                RunStatus     = I(rd, "RunStatus"),
                                LatencyMs     = L(rd, "LatencyMs"),
                                LastMessage   = S(rd, "LastMessage"),
                                LastActivity  = D(rd, "LastActivity"),
                            });
                        }
                    }

                    _links.AddRange(rows);
                    _outcomes.Add(new ScanOutcome
                    {
                        Server = server,
                        Kind = rows.Count > 0 ? ScanOutcomeKind.LinksFound : ScanOutcomeKind.NoReplicationMetadata,
                        Links = rows.Count
                    });

                    foreach (var l in rows)
                    {
                        foreach (var end in new[]
                        {
                            ReplicationNaming.Describe(l.PublisherName, l.PublisherId, l.Distributor),
                            ReplicationNaming.Describe(l.SubscriberName, l.SubscriberId, l.Distributor)
                        })
                        {
                            if (end.IsRealServerName && !provenance.ContainsKey(end.Key))
                                provenance[end.Key] = (server, conn.Id);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _outcomes.Add(new ScanOutcome
                    {
                        Server = server,
                        Kind = ScanOutcomeKind.Failed,
                        Detail = (ex.Message ?? "").Replace("\r", " ").Replace("\n", " ").Trim()
                    });
                }
            }

            AuditLog.LogSecurityEvent(
                $"Replication Map scan of {_outcomes.Count} server(s)", AuditSeverity.Info,
                new Dictionary<string, string>
                {
                    ["Servers"] = string.Join(", ", _outcomes.Select(o => o.Server)),
                    ["Links"] = _links.Count.ToString(),
                    ["Failed"] = _outcomes.Count(o => o.Kind == ScanOutcomeKind.Failed).ToString()
                });

            var distributors = _outcomes.Where(o => o.Kind == ScanOutcomeKind.LinksFound)
                                        .Select(o => o.Server).ToList();
            _distributor = distributors.Count == 1 ? distributors[0] : string.Join(", ", _outcomes.Select(o => o.Server));
            _scanSummary = ReplicationTopology.DescribeScan(_outcomes);
            _unresolvedNotice = ReplicationTopology.DescribeUnresolved(_links);
            _groups = ReplicationTopology.GroupByInstance(_links, distributors);
            _participants = ReplicationTopology.FindAddable(
                _groups, ConnectionManager.GetConnections().SelectMany(c => c.GetServerList()), provenance);

            BuildLayout(_links);
            _scanned = true;

            // Every failure is already in _outcomes and rendered per server; the page-level
            // error channel is used only when NOTHING could be read, so the summary line and
            // this banner can never disagree.
            if (_outcomes.Count > 0 && _outcomes.All(o => o.Kind == ScanOutcomeKind.Failed))
                _error = "No scanned server could be read. Per-server reasons are listed below.";
        }
        finally { _scanning = false; StateHasChanged(); }
    }

    private void LoadSample()
    {
        Clear();
        _distributor = "DIST01 (sample)";
        var now = DateTime.Now;
        var rows = new List<ReplicationLink>
        {
            new(){ Distributor="DIST01", PublisherName="SQLPUB01", PublisherDb="Sales",     Publication="Sales_Tran",   SubscriberName="SQLSUB01", SubscriberDb="Sales_Replica", Agent="SQLPUB01-Sales-Sales_Tran-1", RunStatus=2, LatencyMs=1200,    LastMessage="2 transactions delivered.", LastActivity=now.AddSeconds(-8) },
            new(){ Distributor="DIST01", PublisherName="SQLPUB01", PublisherDb="Sales",     Publication="Sales_Tran",   SubscriberName="SQLSUB02", SubscriberDb="Sales_Replica", Agent="SQLPUB01-Sales-Sales_Tran-2", RunStatus=4, LatencyMs=800,     LastMessage="No replicated transactions are available.", LastActivity=now.AddSeconds(-3) },
            new(){ Distributor="DIST01", PublisherName="SQLPUB01", PublisherDb="Inventory", Publication="Inv_Tran",     SubscriberName="SQLSUB02", SubscriberDb="Inv_Replica",   Agent="SQLPUB01-Inv-Inv_Tran-1",     RunStatus=5, LatencyMs=480000,  LastMessage="Agent retrying after transient error (timeout).", LastActivity=now.AddMinutes(-7) },
            new(){ Distributor="DIST01", PublisherName="SQLPUB02", PublisherDb="Orders",    Publication="Orders_Tran",  SubscriberName="SQLSUB01", SubscriberDb="Orders_Replica",Agent="SQLPUB02-Orders-Orders_Tran-1",RunStatus=6, LatencyMs=null,    LastMessage="The process could not connect to Subscriber 'SQLSUB01'.", LastActivity=now.AddMinutes(-42) },
            new(){ Distributor="DIST01", PublisherName="SQLPUB02", PublisherDb="Orders",    Publication="Orders_Tran",  SubscriberName="SQLSUB03", SubscriberDb="Orders_Replica",Agent="SQLPUB02-Orders-Orders_Tran-2",RunStatus=2, LatencyMs=2300,    LastMessage="14 transactions delivered.", LastActivity=now.AddSeconds(-12) },
            new(){ Distributor="DIST01", PublisherName="SQLPUB02", PublisherDb="HR",        Publication="HR_Tran",      SubscriberName="SQLSUB03", SubscriberDb="HR_Replica",    Agent="SQLPUB02-HR-HR_Tran-1",        RunStatus=null,LatencyMs=null,    LastMessage="", LastActivity=null },
            // A link whose subscriber_id resolved to no row in the distributor's sys.servers —
            // the shape that used to print a bare "-1" as if it were a server name.
            new(){ Distributor="DIST01", PublisherName="SQLPUB02", PublisherDb="HR",        Publication="HR_Tran",      SubscriberName=null, SubscriberId=-1, SubscriberDb="HR_Replica", Agent="SQLPUB02-HR-HR_Tran-2", RunStatus=4, LatencyMs=400, LastMessage="No replicated transactions are available.", LastActivity=now.AddSeconds(-20) },
        };
        _links.AddRange(rows);
        _outcomes.Add(new ScanOutcome { Server = "DIST01", Kind = ScanOutcomeKind.LinksFound, Links = rows.Count });
        _scanSummary = ReplicationTopology.DescribeScan(_outcomes);
        _unresolvedNotice = ReplicationTopology.DescribeUnresolved(_links);
        _groups = ReplicationTopology.GroupByInstance(_links, new List<string> { "DIST01" });
        // fabricated: true — every participant below is a name this method invented. It travels
        // on the candidates so the Add… hand-off and the probe are refused, not merely hidden.
        _participants = ReplicationTopology.FindAddable(
            _groups, ConnectionManager.GetConnections().SelectMany(c => c.GetServerList()),
            provenance: null, fabricated: true);
        BuildLayout(_links);
        _sample = true;
    }

    private void BuildLayout(List<ReplicationLink> rows)
    {
        _pubNodes.Clear(); _subNodes.Clear(); _edges.Clear();

        var pubIndex = new Dictionary<string, Node>(StringComparer.Ordinal);
        var subIndex = new Dictionary<string, Node>(StringComparer.Ordinal);
        var ends = new List<(ResolvedServerName Pub, ResolvedServerName Sub)>(rows.Count);

        foreach (var r in rows)
        {
            var pub = ReplicationNaming.Describe(r.PublisherName, r.PublisherId, r.Distributor);
            var sub = ReplicationNaming.Describe(r.SubscriberName, r.SubscriberId, r.Distributor);
            ends.Add((pub, sub));

            string pubKey = $"{pub.Key}|{r.PublisherDb}|{r.Publication}";
            if (!pubIndex.TryGetValue(pubKey, out var pn))
            {
                pn = new Node
                {
                    Key = pubKey,
                    Line1 = Trunc(r.Publication, 22),
                    Line2 = Trunc($"{pub.Label}\\{r.PublisherDb}", 26),
                    Unresolved = !pub.IsRealServerName,
                    Detail = pub.Detail
                };
                pubIndex[pubKey] = pn; _pubNodes.Add(pn);
            }
            string subKey = $"{sub.Key}|{r.SubscriberDb}";
            if (!subIndex.TryGetValue(subKey, out var sn))
            {
                sn = new Node
                {
                    Key = subKey,
                    Line1 = Trunc(sub.Label, 24),
                    Line2 = Trunc(r.SubscriberDb, 26),
                    Unresolved = !sub.IsRealServerName,
                    Detail = sub.Detail
                };
                subIndex[subKey] = sn; _subNodes.Add(sn);
            }
        }

        int rowsN = Math.Max(_pubNodes.Count, _subNodes.Count);
        double contentH = Math.Max(rowsN, 1) * RowGap;
        _vbH = Top + contentH + 40;
        for (int i = 0; i < _pubNodes.Count; i++) _pubNodes[i].Y = Top + contentH / _pubNodes.Count * (i + 0.5);
        for (int j = 0; j < _subNodes.Count; j++) _subNodes[j].Y = Top + contentH / _subNodes.Count * (j + 0.5);

        int k = 0;
        for (int idx = 0; idx < rows.Count; idx++)
        {
            var r = rows[idx];
            var (pub, sub) = ends[idx];
            var pn = pubIndex[$"{pub.Key}|{r.PublisherDb}|{r.Publication}"];
            var sn = subIndex[$"{sub.Key}|{r.SubscriberDb}"];
            double x0 = PubX + 6, y0 = pn.Y, x1 = SubX - 6, y1 = sn.Y, dx = (x1 - x0) * 0.45;
            var (status, statusText) = ReplicationHealth.Classify(r.RunStatus, r.LatencyMs);
            _edges.Add(new Edge
            {
                D = $"M{N(x0)} {N(y0)} C{N(x0 + dx)} {N(y0)} {N(x1 - dx)} {N(y1)} {N(x1)} {N(y1)}",
                Status = status, StatusText = statusText,
                Agent = r.Agent, LatencyMs = r.LatencyMs, LastActivity = r.LastActivity, LastMessage = r.LastMessage,
                PubLabel = $"{pub.Label}\\{r.PublisherDb}:{r.Publication}", SubLabel = $"{sub.Label}\\{r.SubscriberDb}",
                PubDetail = pub.Detail, SubDetail = sub.Detail,
                Delay = (k * 5) % 20 / 10.0,
                Dur = (2.0 + Math.Abs(y1 - y0) / 240.0) * 2.0,
            });
            k++;
        }
    }

    private void SelectEdge(int i) => _selEdge = (_selEdge == i) ? -1 : i;

    // ── A.4: participants named in the topology but absent from the server list ──

    private bool MayManageServers => UserState.IsAuthorized("manage_servers");

    private ProbeState ProbeOf(string key)
    {
        if (!_probes.TryGetValue(key, out var p)) { p = new ProbeState(); _probes[key] = p; }
        return p;
    }

    /// <summary>Reachability probe for one discovered participant, classified by the same
    /// three-state observation the circuit-breaker fix established. Read-only: it opens a
    /// connection and closes it. Nothing is written anywhere by this button.</summary>
    private async Task ProbeParticipantAsync(ParticipantCandidate c)
    {
        // A fabricated row has no server behind it. The button is not rendered for one, and the
        // handler refuses one too: hiding a control is not the same as refusing the action.
        if (!ParticipantOffer.MayProbe(c)) return;

        var state = ProbeOf(c.Key);
        if (state.Running) return;
        state.Running = true;
        StateHasChanged();
        try
        {
            var conn = ConnectionManager.GetConnections().FirstOrDefault(x => x.Id == c.ViaConnectionId);
            if (conn == null)
            {
                // Two different facts, told apart: an id was recorded and the connection holding
                // it has gone (a removal we can observe), versus no provenance recorded at all
                // (nothing observed about any removal). See ParticipantOffer.DescribeUnprobeable.
                state.State = AlertEvaluationService.ServerReachability.Undetermined;
                state.Reason = ParticipantOffer.DescribeUnprobeable(c, connectionStillInCatalogue: false);
            }
            else
            {
                var obs = await ServerReachabilityCheck.ProbeAsync(conn, c.Server, 5, CancellationToken.None);
                state.State = obs.State;
                state.Reason = obs.Reason;
            }
            state.Probed = true;
        }
        finally { state.Running = false; StateHasChanged(); }
    }

    /// <summary>Hands ONE discovered name to the existing add-connection flow on /servers,
    /// pre-filled. Nothing is written from this page: the operator reviews auth, tags and
    /// credentials in that dialog and saves through the guarded server-connections store.</summary>
    private void AddParticipant(ParticipantCandidate c)
    {
        if (!MayManageServers) return;
        // Sample rows are names this page generated. Handing one to /servers?add= would put a
        // fabricated name in front of the operator on a page that has no way to know it was
        // fabricated. Refused here as well as hidden in the markup.
        if (!ParticipantOffer.MayHandOffToAddDialog(c)) return;
        AuditLog.LogSecurityEvent(
            $"Replication Map handed discovered participant '{c.Server}' to the add-connection dialog",
            AuditSeverity.Info,
            new Dictionary<string, string> { ["Server"] = c.Server, ["ViaDistributor"] = c.ViaDistributor });
        Navigation.NavigateTo($"/servers?add={Uri.EscapeDataString(c.Server)}");
    }

    private static string ProbeLabel(ProbeState p)
    {
        if (p.Running) return "Probing…";
        if (!p.Probed) return "Not probed yet";
        return p.State switch
        {
            AlertEvaluationService.ServerReachability.Reached => "Reached",
            AlertEvaluationService.ServerReachability.Unreachable => "Not reached",
            _ => "Undetermined"
        };
    }

    private static string ProbeClass(ProbeState p)
    {
        if (!p.Probed || p.Running) return "s-unknown";
        return p.State switch
        {
            AlertEvaluationService.ServerReachability.Reached => "s-ok",
            AlertEvaluationService.ServerReachability.Unreachable => "s-bad",
            _ => "s-unknown"
        };
    }

    // ── defensive reader helpers ──
    private static string S(SqlDataReader r, string c) { try { var o = r[c]; return o == DBNull.Value ? "" : o.ToString() ?? ""; } catch { return ""; } }
    // Nullable string: a NULL name is the FACT that the id did not resolve — it must not
    // collapse to "" and be mistaken for "recorded as empty".
    private static string? SN(SqlDataReader r, string c) { try { var o = r[c]; return o == DBNull.Value ? null : o.ToString(); } catch { return null; } }
    private static int? I(SqlDataReader r, string c) { try { var o = r[c]; return o == DBNull.Value ? (int?)null : Convert.ToInt32(o); } catch { return null; } }
    private static long? L(SqlDataReader r, string c) { try { var o = r[c]; return o == DBNull.Value ? (long?)null : Convert.ToInt64(o); } catch { return null; } }
    private static DateTime? D(SqlDataReader r, string c) { try { var o = r[c]; return o == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(o); } catch { return null; } }

    private static string N(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);
    private static string Enc(string s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    private static string Trunc(string s, int n) { s ??= ""; return s.Length <= n ? s : s.Substring(0, n - 1) + "…"; }

    // Defensive transactional-replication discovery, run on the distributor.
    // Exposed in the UI ("Show query") so it can be validated/adjusted on-site.
    //
    // WHY THE HISTORY LIVES HERE AND NOT IN THE QUERY TEXT. "Show query" is a client-facing
    // pane: a DBA validating this against their own distributor needs the operational comments
    // (why the cursor over distribution databases, why the TRY/CATCH, why subscriber_id <> -2)
    // and has no use for our changelog. So the changelog stays in C#, above the const.
    //
    // The change itself: both ends of a link used to be selected as
    // COALESCE(pub.name, CAST(da.publisher_id AS sysname)). When the LEFT JOIN against
    // master.sys.servers found no row for that server_id, the COALESCE handed the id to the UI
    // wearing a server name's clothes, and it was rendered verbatim as a node label reading
    // "-1". The name and the id are now returned in separate columns. sys.servers.name is
    // never NULL, so a NULL name IS the finding that the join matched nothing; the app decides
    // what to print for that case (see ReplicationNaming.Describe) and the query invents no name.
    private const string ReplicationQuery = @"
SET NOCOUNT ON;

-- Distribution databases are discovered by catalog flag, not by name: the
-- default 'distribution' name is often customised, and one distributor can
-- host several distribution databases. Iterate them all and union the rows.
DECLARE @results TABLE (
    Publisher sysname NULL, PublisherId int NULL, PublisherDb sysname NULL, Publication sysname NULL,
    Subscriber sysname NULL, SubscriberId int NULL, SubscriberDb sysname NULL, Agent nvarchar(256) NULL,
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
            /* Each end returns its NAME and its server_id in separate columns. A NULL name
               means the LEFT JOIN below matched no row in master.sys.servers on this
               distributor for that server_id (sys.servers.name is never NULL). */
            pub.name,
            da.publisher_id,
            da.publisher_db,
            da.publication,
            sub.name,
            da.subscriber_id,
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

SELECT Publisher, PublisherId, PublisherDb, Publication, Subscriber, SubscriberId, SubscriberDb, Agent,
       RunStatus, LatencyMs, LastMessage, LastActivity
FROM @results
ORDER BY Publisher, Publication, Subscriber;";
}
