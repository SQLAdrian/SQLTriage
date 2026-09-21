/* In the name of God, the Merciful, the Compassionate */
/*
 * What the estate's connection numbers are allowed to say.
 *
 * Three findings from the 2026-08-28 honesty hunt, all on the one number an operator checks first
 * and most often - "servers connected":
 *
 *   pages-r1-03  IsConnected / LastConnected / SuccessfulServers persist to disk and survive a
 *                restart, and the tray row and the /servers hero rendered them as current. The
 *                live store on this box proved a 39-day-old "connected" claim for an instance
 *                whose SQL service was stopped, with no timestamp on the card.
 *   pages-r2-07  The Test button had no per-server try/catch: the first failure unwound the whole
 *                loop and persisted an EMPTY successful-server list, discarding servers that had
 *                connected moments earlier. Its "Connected to N of M servers" arm was unreachable
 *                dead code, and a connection with no server names at all took the equal arm and
 *                reported "Successfully connected" having opened nothing.
 *   pages-r2-08  Refresh All flattened the estate into (connection, server) pairs, processed them
 *                in batches of 10 and REPLACED a connection's whole successful-server list per
 *                batch, so a connection straddling a batch boundary lost its first batch's
 *                successes. Its per-result grouping key was the server NAME, so two connections
 *                sharing a name mis-attributed each other's results.
 *
 * Everything here is pure: no SQL, no UI, no persistence. That is the point - the numbers and the
 * sentences are testable without an estate, and the pages call them rather than composing their
 * own.
 */

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services;

/// <summary>How current a connection's rendered status actually is.</summary>
public enum ConnectionStatusKind
{
    /// <summary>Probed in this process and it answered.</summary>
    ConnectedNow,
    /// <summary>Probed in this process and it did not answer.</summary>
    DisconnectedNow,
    /// <summary>Not probed in this process. Whatever is stored is a record of an older probe.</summary>
    NotCheckedThisSession
}

/// <summary>Colour/shape hint for an estate-wide summary, so callers do not re-derive it.</summary>
public enum ConnectionEstateKind { NoneConfigured, AllConnected, NoneConnected, Mixed, NothingChecked }

public static class ConnectionStatusText
{
    public static ConnectionStatusKind Classify(ServerConnection c) =>
        c is null || !c.StatusMeasuredThisSession ? ConnectionStatusKind.NotCheckedThisSession
        : c.IsConnected ? ConnectionStatusKind.ConnectedNow
        : ConnectionStatusKind.DisconnectedNow;

    /// <summary>
    /// The line that must sit beside a connection's status dot. A stored status always carries
    /// when it was measured; a status measured in this session says so.
    /// </summary>
    public static string DescribeRow(ServerConnection c, DateTime now) => Classify(c) switch
    {
        ConnectionStatusKind.ConnectedNow => "Connected - checked this session.",
        ConnectionStatusKind.DisconnectedNow => "Did not answer - checked this session.",
        _ => c?.LastConnected is { } last
            ? $"Not checked this session. Last connected {last:d MMM yyyy HH:mm} ({DescribeAge(now - last)} ago)."
            : "Not checked this session, and no successful connection has ever been recorded."
    };

    /// <summary>Coarse age, in the largest unit that is not a lie.</summary>
    public static string DescribeAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero) return "0 minutes";
        if (age.TotalMinutes < 1) return "under a minute";
        if (age.TotalHours < 1) return Plural((int)age.TotalMinutes, "minute");
        if (age.TotalDays < 1) return Plural((int)age.TotalHours, "hour");
        return Plural((int)age.TotalDays, "day");
    }

    private static string Plural(int n, string unit) => $"{n} {unit}{(n == 1 ? "" : "s")}";

    /// <summary>
    /// The tray row. It used to read "Servers: 2/5 connected" off persisted values with no notion
    /// of when they were measured, on a menu the operator sees without opening the app at all.
    /// </summary>
    public static (string Text, ConnectionEstateKind Kind) DescribeTray(IReadOnlyList<ServerConnection>? all)
    {
        if (all is null || all.Count == 0) return ("No servers configured", ConnectionEstateKind.NoneConfigured);

        var checkedConns = all.Count(c => c.StatusMeasuredThisSession);
        var connected = all.Count(c => Classify(c) == ConnectionStatusKind.ConnectedNow);
        var unchecked_ = all.Count - checkedConns;

        if (checkedConns == 0)
            return ($"Servers: {all.Count} configured, none checked yet", ConnectionEstateKind.NothingChecked);

        if (unchecked_ > 0)
            return ($"Servers: {connected}/{checkedConns} connected ({unchecked_} not checked)",
                    ConnectionEstateKind.Mixed);

        var down = all.Count - connected;
        if (down == 0) return ($"Servers: {connected}/{all.Count} connected", ConnectionEstateKind.AllConnected);
        if (connected == 0) return ($"Servers: 0/{all.Count} connected", ConnectionEstateKind.NoneConnected);
        return ($"Servers: {connected}/{all.Count} connected  ({down} down)", ConnectionEstateKind.Mixed);
    }

    /// <summary>
    /// What the /servers hero must add under its "Connected" number, or null when every
    /// connection was measured in this session and the number needs no qualifier.
    /// </summary>
    public static string? DescribeHeroCaption(IReadOnlyList<ServerConnection>? all)
    {
        if (all is null || all.Count == 0) return null;

        var unchecked_ = all.Count(c => !c.StatusMeasuredThisSession);
        if (unchecked_ == 0) return null;

        return unchecked_ == all.Count
            ? $"None of the {all.Count} configured connections has been checked in this session."
            : $"{unchecked_} of {all.Count} connections have not been checked in this session; "
              + "their stored status is from an earlier run.";
    }
}

/// <summary>One connection's Test result: what was planned, what answered, what did not.</summary>
public sealed record ConnectionTestReport(
    int Planned,
    IReadOnlyList<string> Succeeded,
    IReadOnlyList<ConnectionTestFailure> Failed,
    bool Cancelled)
{
    /// <summary>
    /// True only when this run actually measured something: at least one server was planned, and
    /// every planned server was attempted. A cancelled MFA run leaves untested servers, and
    /// persisting a partial list would record them as failures nobody observed.
    ///
    /// <para><b><c>Planned &gt; 0</c> is load-bearing (lane9-07, 2026-08-28).</b> Without it a
    /// connection whose <c>ServerNames</c> is whitespace produced <c>Complete == true</c>
    /// vacuously - 0 attempted of 0 planned - and the caller wrote that "result" to the store.
    /// <c>UpdateSuccessfulServers(id, [])</c> sets <c>StatusMeasuredThisSession = true</c>,
    /// <c>IsConnected = false</c> and <c>LastConnected = now</c>, so the row then rendered "Did not
    /// answer - checked this session." for a connection nothing had contacted, directly
    /// contradicting the message the same click wrote ("This connection has no server names, so
    /// nothing was contacted.") and putting a red dot in the tray. Proved against the shipped
    /// assembly. This property is the gate on persistence, so an empty plan is not a measurement
    /// and must not read as one.</para>
    /// </summary>
    public bool Complete => Planned > 0 && !Cancelled && Succeeded.Count + Failed.Count == Planned;
}

public sealed record ConnectionTestFailure(string ServerName, string Reason);

public static class ConnectionTestText
{
    /// <summary>The Test button's message. Never claims a server that was not contacted.</summary>
    public static string Describe(ConnectionTestReport r)
    {
        if (r.Planned == 0)
            return "This connection has no server names, so nothing was contacted.";

        if (r.Cancelled)
            return r.Succeeded.Count == 0
                ? "Authentication was cancelled, so nothing was contacted and the stored status is unchanged."
                : $"Authentication was cancelled after {r.Succeeded.Count} of {r.Planned} server(s) connected. "
                  + "The rest were not contacted.";

        if (r.Failed.Count == 0 && r.Succeeded.Count == r.Planned)
            return r.Planned == 1 ? "Successfully connected" : $"Connected to all {r.Planned} servers";

        if (r.Succeeded.Count == 0)
            return $"Connection failed: {r.Failed[0].Reason}";

        // The arm that used to be unreachable: a partial success is a partial success, and the
        // servers that DID connect are not discarded to report it.
        return $"Connected to {r.Succeeded.Count} of {r.Planned} servers. "
             + $"{r.Failed.Count} did not answer - {r.Failed[0].ServerName}: {r.Failed[0].Reason}";
    }

    private const string IntegratedOnlyMarker = "Integrated authentication only";

    /// <summary>
    /// The mixed-mode hint, kept attached to the failure that earns it rather than to the whole
    /// run. Null when no failure mentions it.
    /// </summary>
    public static string? DescribeAuthHint(ConnectionTestReport r) =>
        r.Failed.Any(f => f.Reason.Contains(IntegratedOnlyMarker, StringComparison.OrdinalIgnoreCase))
            ? "\n\nThe SQL Server instance is configured for Windows Authentication only. "
              + "To use SQL Authentication, enable Mixed Mode in SQL Server Management Studio: "
              + "Server Properties > Security > Server Authentication > SQL Server and Windows Authentication mode, "
              + "then restart the SQL Server service."
            : null;
}

/// <summary>
/// Accumulates an estate-wide refresh across batches, keyed by CONNECTION, and reports a
/// connection only once every server planned for it has been recorded.
/// <para>
/// pages-r2-08, both arms: the old code grouped a batch's results by server NAME (so two
/// connections sharing a name absorbed each other's results) and wrote each connection's whole
/// successful-server list per batch (so a connection straddling a batch boundary lost the first
/// batch's successes, and was tallied twice into the summary counts).
/// </para>
/// </summary>
public sealed class ConnectionProbeTally
{
    private sealed class State
    {
        public int Planned;
        public int Recorded;
        public readonly List<string> Successes = new();
        public bool Drained;
    }

    private readonly Dictionary<string, State> _byConnection = new(StringComparer.Ordinal);

    public ConnectionProbeTally(IEnumerable<(string ConnectionId, string ServerName)> planned)
    {
        foreach (var (id, _) in planned ?? Array.Empty<(string, string)>())
        {
            if (!_byConnection.TryGetValue(id, out var s)) _byConnection[id] = s = new State();
            s.Planned++;
        }
    }

    public int PlannedServers => _byConnection.Values.Sum(s => s.Planned);
    public int PlannedConnections => _byConnection.Count;
    public int SucceededServers => _byConnection.Values.Sum(s => s.Successes.Count);

    /// <summary>Connections whose every planned server answered. A connection with no servers is not one.</summary>
    public int FullyConnectedConnections =>
        _byConnection.Values.Count(s => s.Planned > 0 && s.Successes.Count == s.Planned);

    public IReadOnlyList<string> SuccessesFor(string connectionId) =>
        _byConnection.TryGetValue(connectionId, out var s) ? s.Successes : Array.Empty<string>();

    public int PlannedFor(string connectionId) =>
        _byConnection.TryGetValue(connectionId, out var s) ? s.Planned : 0;

    public void Record(string connectionId, string serverName, bool answered)
    {
        if (!_byConnection.TryGetValue(connectionId, out var s))
            throw new InvalidOperationException(
                $"Result for connection '{connectionId}', which was not in the plan. The result would be "
                + "attributed to whichever connection happened to share the server name.");

        s.Recorded++;
        if (answered) s.Successes.Add(serverName);
    }

    /// <summary>
    /// Connection ids whose every planned server has now been recorded, each returned exactly
    /// once. This is what a caller may persist: a connection is written when it is finished, never
    /// mid-sweep, so a partial list is never mistaken for a total failure.
    /// </summary>
    public IReadOnlyList<string> DrainCompletedConnections()
    {
        var done = new List<string>();
        foreach (var (id, s) in _byConnection)
        {
            if (s.Drained || s.Recorded < s.Planned) continue;
            s.Drained = true;
            done.Add(id);
        }
        return done;
    }

    /// <summary>The status line for the whole sweep, counted over connections, not over batches.</summary>
    public string DescribeSweep()
    {
        if (PlannedConnections == 0) return "No connections are configured, so nothing was contacted.";
        if (PlannedServers == 0) return "No connection has a server name, so nothing was contacted.";

        var withServers = _byConnection.Values.Count(s => s.Planned > 0);
        if (SucceededServers == PlannedServers && FullyConnectedConnections == withServers)
            return PlannedServers == 1 ? "Successfully connected" : $"Successfully connected to all {PlannedServers} servers";

        if (SucceededServers > 0) return $"Connected to {SucceededServers} of {PlannedServers} servers";
        return "Failed to connect to any servers";
    }
}
