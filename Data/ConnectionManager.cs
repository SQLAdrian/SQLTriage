/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Models;

namespace SQLTriage.Data
{
    public class ServerConnectionManager : IServerConnectionManager
    {
        private readonly ILogger<ServerConnectionManager> _logger;
        private readonly string _connectionsFilePath;

        /// <summary>
        /// Instance-seat licence gate. Nullable optional DI param (repo convention): null ⇒ the
        /// guard is inert and connection handling is byte-for-byte what it was before seats existed.
        /// Registered late in ServiceCollectionExtensions so tests and lightweight hosts need not
        /// stand up the register.
        /// </summary>
        private readonly SQLTriage.Data.Services.Licensing.ISeatRegister? _seats;
        private List<ServerConnection> _connections = new();
        private readonly object _lock = new();
        private string? _currentServerId;

        /// <summary>
        /// How Config/server-connections.json came off disk.
        ///
        /// <para><b>TIER 1 — the write is REFUSED, not announced.</b> This store holds the SQL auth
        /// usernames and the <c>CredentialProtector</c>-wrapped passwords for every monitored
        /// instance, and they are the ONLY copies on the box: nothing regenerates them, no other
        /// file carries them, and a DPAPI-wrapped password cannot be recovered from a report. It is
        /// the same class as portal-settings.json's intake SAS and notification-channels.json's SMTP
        /// credentials, both of which refuse.</para>
        ///
        /// <para><b>Measured by the 2026-08-04 cold gate, on this class, on this file.</b> With a
        /// damaged store holding PROD-SQL01 and its password, the manager loaded 0 connections, a
        /// single <c>AddConnection</c> returned <c>Succeeded=True</c>, the file's hash moved
        /// 3D1B…→5750…, the original connection was gone, and no <c>.rejected-</c> copy existed
        /// because nothing on this path ever asked for one. The damage was loud; the rewrite was
        /// silent — which is the whole argument for the guard.</para>
        ///
        /// <para><b>The way out is not in the UI, deliberately.</b> There is no
        /// <see cref="StoreWriteIntent.ReplaceUnreadableStore"/> entry point here, because a
        /// "replace it anyway" button on this file destroys credentials for a click, and the two
        /// routes that do exist are better: restore the <c>.rejected-</c> copy the loader kept, or
        /// delete the damaged file — <see cref="ConfigLoadOutcome.Missing"/> is NOT damage, so an
        /// install with no file at all configures normally from Onboarding.</para>
        /// </summary>
        private ConfigLoadOutcome _load = ConfigLoadOutcome.Missing;

        private string? _quarantine;

        /// <summary>
        /// SHA-256 of the store as this process last read or wrote it. Re-taken immediately before
        /// every save and compared: a difference means another writer has been here since, and the
        /// whole-file rewrite this class does would delete their work. See
        /// <see cref="StoreWriteOutcome.RefusedStoreChangedOnDisk"/> for the measurement that made
        /// this necessary. <c>null</c> means "there was no file", which is a legitimate state.
        /// </summary>
        private string? _diskFingerprint;

        /// <summary>
        /// True when server-connections.json exists and did not load, so this process holds none of
        /// the operator's connections and must not write over them.
        /// </summary>
        public bool IsStoreDamaged
        {
            get { lock (_lock) return _load is ConfigLoadOutcome.Unreadable or ConfigLoadOutcome.Empty; }
        }

        /// <summary>What to do about it. The ONE register — never a locally written sentence.</summary>
        public string DescribeStoreRecovery()
        {
            lock (_lock) return ConfigFileHelper.DescribeStoreRecovery(_load, _quarantine);
        }

        // Discovery cache — populated once after the first successful SQLWATCH scan.
        // Invalidated whenever connections are added, updated, or removed so that
        // the next dashboard load re-runs discovery and picks up the new topology.
        private bool _discoveryCompleted;
        private string[] _cachedInstances = Array.Empty<string>();
        private readonly Dictionary<string, string> _cachedInstanceToConnectionId = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _cachedConnectionsWithSqlWatch = new();

        public event Action? OnConnectionChanged;

        private static readonly JsonSerializerOptions DeserializeOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private static readonly JsonSerializerOptions SerializeOptions = new()
        {
            WriteIndented = true
        };

        /// <param name="seats">
        /// Instance-seat licence gate. Nullable optional (repo DI convention) — null ⇒ inert guard.
        /// </param>
        /// <param name="connectionsFilePath">
        /// Test seam (mirrors AcceptedFindingsService's dbPath / DemoRunLedger's pathOverride):
        /// overrides the store location so a test never reads or writes the install's real
        /// Config/server-connections.json. Null ⇒ the real path, exactly as before.
        /// </param>
        public ServerConnectionManager(
            ILogger<ServerConnectionManager> logger,
            SQLTriage.Data.Services.Licensing.ISeatRegister? seats = null,
            string? connectionsFilePath = null)
        {
            _logger = logger;
            _seats = seats;
            _connectionsFilePath = connectionsFilePath
                ?? Path.Combine(AppContext.BaseDirectory, "Config", "server-connections.json");
            LoadConnections();
        }

        public List<ServerConnection> GetConnections()
        {
            lock (_lock) return _connections.ToList();
        }

        public List<ServerConnection> GetEnabledConnections()
        {
            lock (_lock) return _connections.Where(c => c.IsEnabled).ToList();
        }

        public string[] GetEnabledServerNames()
        {
            lock (_lock) return _connections.Where(c => c.IsEnabled).SelectMany(c => c.GetServerList()).ToArray();
        }

        public ServerConnection? GetConnection(string id)
        {
            lock (_lock) return _connections.FirstOrDefault(c => c.Id == id);
        }

        public ServerConnection? GetDefaultConnection()
        {
            lock (_lock) return _connections.FirstOrDefault();
        }

        public ServerConnection? CurrentServer
        {
            get
            {
                lock (_lock)
                {
                    if (string.IsNullOrEmpty(_currentServerId)) return null;
                    return _connections.FirstOrDefault(c => c.Id == _currentServerId);
                }
            }
        }

        /// <summary>
        /// THE single write of the process-wide current server, and the only one there can be —
        /// <c>_currentServerId</c> is private and nothing else in this class assigns it.
        ///
        /// <para><b>The grant is a parameter because three rounds of gating call sites did not
        /// hold.</b> Rounds 1 and 2 gated the selectors, then the init path; round 3's gate found a
        /// FIFTH write inside the dashboard's discovery loop — on the very init path the guarding
        /// comment claimed to cover, and invisible to a census that read only the body of the
        /// method it believed was the chokepoint. A caller now cannot reach this line without
        /// naming a decision, and <c>default(ConnectionRetargetGrant)</c> is a refusal, so
        /// forgetting to decide fails closed.</para>
        ///
        /// <para><b>EstablishOnly is verified here, not trusted.</b> The bootstrap calls in
        /// DashboardToolbar and GlobalServerSelector were reviewed-and-accepted with the note that
        /// they "can only move the target from none to the first enabled connection". That was an
        /// assertion about code somewhere else; it is now a condition this method checks, so a
        /// second circuit opening a tab cannot drag everybody else onto its own first connection.
        /// This is the case a ONE-CONNECTION probe cannot see: with a single enabled connection an
        /// unrestricted bootstrap writes the same id back and looks like a no-op.</para>
        /// </summary>
        public ConnectionRetargetOutcome SetCurrentServer(string? serverId, ConnectionRetargetGrant grant)
        {
            lock (_lock)
            {
                var before = _currentServerId;

                switch (grant.Kind)
                {
                    case ConnectionRetargetKind.Authorised:
                        break;

                    case ConnectionRetargetKind.EstablishOnly:
                        if (!string.IsNullOrEmpty(before))
                        {
                            _logger.LogInformation(
                                "Connection retarget refused: an establish-only caller may not move the "
                                + "current server once one is selected.");
                            return ConnectionRetargetOutcome.Refuse(
                                before,
                                "This application is already connected to a server, and establishing a "
                                + "connection may not move it.");
                        }
                        break;

                    default:
                        _logger.LogInformation(
                            "Connection retarget refused: caller is not authorised for {Permission}.",
                            grant.Permission ?? "(no permission named)");
                        return ConnectionRetargetOutcome.Refuse(
                            before,
                            grant.Permission is null
                                ? "Changing the server this application is connected to requires authorisation."
                                : "Changing the server this application is connected to requires authorisation for "
                                  + grant.Permission + ".");
                }

                _currentServerId = serverId;

                // Raised unconditionally, exactly as it was before the grant existed: subscribers
                // treat it as "re-read me", and making it conditional on movement would be a second
                // change riding along with this one.
                OnConnectionChanged?.Invoke();
                return ConnectionRetargetOutcome.Took(before, serverId);
            }
        }

        // ── Instance-seat guard ──────────────────────────────────────────────
        //
        // The guard lives HERE, in the manager, and deliberately NOT in the pages. Five UI surfaces
        // mutate connections (Servers.razor ×2, ConnectionDialog.razor ×3, EnvironmentView.razor,
        // Onboarding.razor, Settings.razor.cs config-import) and they share no base class — a guard
        // in any one of them leaks through the other four. This is the only choke point they all
        // pass through.
        //
        // Seats are counted per SPLIT SERVER STRING, never per connection Id: a ServerConnection
        // holds MULTIPLE server strings (GetServerList splits on \n \r , and ;), so one profile
        // listing 6 servers is 6 instances, not 1. Counting profiles would turn a 6-seat licence
        // into 6 profiles × 50 servers.
        //
        // Nullable optional DI param (repo convention) — null in tests and in any host that has not
        // registered the register, in which case the guard is inert and behaviour is exactly as it
        // was before seats existed.

        /// <summary>Total real instances configured, counting every split server string.</summary>
        private int CountInstances(IEnumerable<ServerConnection> connections) =>
            connections.SelectMany(c => c.GetServerList())
                       .Select(s => s.Trim())
                       .Where(s => !string.IsNullOrEmpty(s))
                       .Distinct(StringComparer.OrdinalIgnoreCase)
                       .Count();

        /// <summary>
        /// Frees the seats of instances that are no longer configured anywhere. Call AFTER the
        /// mutation, with the server strings the change dropped.
        ///
        /// This is the ONLY event that legitimately frees a seat, and therefore the only thing that
        /// makes the swap budget reachable at all: seatsUsed only ever falls through a release, and
        /// ClaimOnProbe refuses every new claim while seats are full. Without this call, deleting a
        /// decommissioned server left its fingerprint Seated forever — the replacement could never be
        /// covered (silently unreported), each attempt still grew D and burned a swap, and the estate
        /// locked with dead boxes holding every seat and no in-app way out. That is the exact
        /// "estate goes dark" outcome the fail-open ruling exists to prevent.
        ///
        /// Only an instance no OTHER remaining profile still lists is released: two profiles may name
        /// the same server, and it holds ONE seat (seats are counted per distinct split string), so
        /// removing one profile must not free a seat the other still relies on.
        ///
        /// Fully isolated: a register fault must never fail the operator's edit. The mutation has
        /// already been persisted by the time we get here; a failed release costs a seat, not the edit.
        /// </summary>
        private void ReleaseSeatsNoLongerConfigured(IEnumerable<string> droppedServers)
        {
            if (_seats == null) return;

            var stillConfigured = new HashSet<string>(
                _connections.SelectMany(c => c.GetServerList()).Select(s => s.Trim()),
                StringComparer.OrdinalIgnoreCase);

            foreach (var server in droppedServers.Select(s => s.Trim())
                                                 .Where(s => !string.IsNullOrEmpty(s))
                                                 .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (stillConfigured.Contains(server)) continue;
                try
                {
                    var decision = _seats.ReleaseInstance(server);
                    if (decision.Allowed)
                        _logger.LogInformation("[SEATS] Released the seat held by {Server} — no longer configured", server);
                    else
                        _logger.LogInformation("[SEATS] Seat for {Server} was not released: {Reason}",
                            server, decision.Reason);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[SEATS] Seat release failed for {Server} (the change itself stands)", server);
                }
            }
        }

        public ConnectionChangeResult AddConnection(ServerConnection connection)
        {
            lock (_lock)
            {
                // Validate connection name before storing
                if (!IsValidConnectionName(connection.Id))
                {
                    throw new ArgumentException($"Invalid connection name: {connection.Id}");
                }

                // Seat guard: would the RESULTING instance count exceed the licence?
                if (_seats != null)
                {
                    var resulting = CountInstances(_connections.Concat(new[] { connection }));
                    var decision = _seats.CanAdmit(resulting);
                    if (!decision.Allowed)
                    {
                        _logger.LogInformation(
                            "[SEATS] Refused add of {Id} — would configure {Count} instances beyond licence",
                            connection.Id, resulting);
                        return ConnectionChangeResult.Refused(decision.Reason ?? "Not permitted by your licence.");
                    }
                }

                _connections.Add(connection);
                InvalidateDiscoveryCache();

                var write = SaveConnections();
                if (write != StoreWriteOutcome.Saved)
                {
                    // Rolled back so what this process reports equals what is on disk. Keeping it
                    // would show the operator a connection that vanishes at the next restart — the
                    // same defect the RBAC WriteFailed path was found with on 2026-08-04.
                    _connections.Remove(connection);
                    InvalidateDiscoveryCache();
                    return ConnectionChangeResult.Refused(DescribeWriteFailure(write));
                }

                return ConnectionChangeResult.Ok;
            }
        }

        public ConnectionChangeResult UpdateConnection(ServerConnection connection)
        {
            lock (_lock)
            {
                // Validate connection name before updating
                if (!IsValidConnectionName(connection.Id))
                {
                    throw new ArgumentException($"Invalid connection name: {connection.Id}");
                }

                var index = _connections.FindIndex(c => c.Id == connection.Id);
                if (index < 0)
                    return ConnectionChangeResult.Refused("That server connection no longer exists.");

                if (_seats != null)
                {
                    // Count the estate as it WOULD be with this edit applied — an update can add
                    // server strings to an existing profile, which is the sneakiest way past a
                    // per-profile count and exactly why the check is per split string.
                    var proposed = _connections.ToList();
                    proposed[index] = connection;
                    var resulting = CountInstances(proposed);
                    var decision = _seats.CanAdmit(resulting);
                    if (!decision.Allowed)
                    {
                        _logger.LogInformation(
                            "[SEATS] Refused update of {Id} — would configure {Count} instances beyond licence",
                            connection.Id, resulting);
                        return ConnectionChangeResult.Refused(decision.Reason ?? "Not permitted by your licence.");
                    }

                    // When the list is LOCKED (seats full AND swaps exhausted), editing a SEATED
                    // entry is how an operator would silently repoint a seat at a different box.
                    // Refuse it with the honest lock reason. Note this only bites when the edit
                    // actually changes the server strings — renaming a profile or retagging its
                    // environment stays free, because that burns no swap and moves no seat.
                    var existing = _connections[index];
                    bool serversChanged = !existing.GetServerList()
                        .SequenceEqual(connection.GetServerList(), StringComparer.OrdinalIgnoreCase);
                    if (serversChanged && _seats.IsLocked)
                    {
                        var status = _seats.Status();
                        _logger.LogInformation("[SEATS] Refused server-list edit of {Id} — register locked", connection.Id);
                        return ConnectionChangeResult.Refused(
                            status.LockReason ?? "Your licence does not permit changing instances.");
                    }
                }

                // An edit that DROPS a server string retires that instance just as surely as removing
                // the whole profile does; capture the before-list while it is still the live one.
                var droppedByEdit = _connections[index].GetServerList()
                    .Except(connection.GetServerList(), StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var replaced = _connections[index];
                _connections[index] = connection;
                InvalidateDiscoveryCache();

                var write = SaveConnections();
                if (write != StoreWriteOutcome.Saved)
                {
                    _connections[index] = replaced;
                    InvalidateDiscoveryCache();
                    return ConnectionChangeResult.Refused(DescribeWriteFailure(write));
                }

                // Only after the write landed: a seat released against an edit that never reached
                // disk would free a seat the estate still holds at the next restart.
                ReleaseSeatsNoLongerConfigured(droppedByEdit);
                return ConnectionChangeResult.Ok;
            }
        }

        public ConnectionChangeResult RemoveConnection(string id)
        {
            lock (_lock)
            {
                var existing = _connections.FirstOrDefault(c => c.Id == id);
                if (existing == null)
                    return ConnectionChangeResult.Ok;   // already gone — idempotent, not an error

                // A REMOVE is refused only while the register is locked AND this profile holds a
                // seat. Rationale: with seats full and swaps spent, remove-then-add is precisely the
                // move that would rotate a seat onto a new box for free. An UNSEATED profile (never
                // reachable, never fingerprinted, or over-allocated) can always be removed — it
                // holds nothing, so letting it go costs the licence nothing and refusing would just
                // trap the operator with clutter they cannot clear.
                if (_seats != null && _seats.IsLocked)
                {
                    bool holdsSeat = existing.GetServerList().Any(s => _seats.IsSeated(s));
                    if (holdsSeat)
                    {
                        var status = _seats.Status();
                        _logger.LogInformation("[SEATS] Refused remove of {Id} — register locked and profile holds a seat", id);
                        return ConnectionChangeResult.Refused(
                            status.LockReason ?? "Your licence does not permit changing instances.");
                    }
                }

                var dropped = existing.GetServerList().ToList();

                var removedAt = _connections.FindIndex(c => c.Id == id);
                _connections.RemoveAll(c => c.Id == id);
                InvalidateDiscoveryCache();

                var write = SaveConnections();
                if (write != StoreWriteOutcome.Saved)
                {
                    _connections.Insert(Math.Min(removedAt, _connections.Count), existing);
                    InvalidateDiscoveryCache();
                    return ConnectionChangeResult.Refused(DescribeWriteFailure(write));
                }

                // Removing a profile is what frees its seats — see ReleaseSeatsNoLongerConfigured.
                // Runs AFTER the removal so "still configured elsewhere" is asked of the estate as it
                // now IS, not as it was.
                ReleaseSeatsNoLongerConfigured(dropped);
                return ConnectionChangeResult.Ok;
            }
        }

        public void UpdateSuccessfulServers(string connectionId, List<string> successfulServers)
        {
            lock (_lock)
            {
                var connection = _connections.FirstOrDefault(c => c.Id == connectionId);
                if (connection != null)
                {
                    connection.SuccessfulServers = successfulServers;
                    connection.LastConnected = DateTime.Now;
                    connection.IsConnected = successfulServers.Count > 0;

                    // Not rolled back on a refusal, and this is the one place that is right: these
                    // three fields are live probe status the UI renders now, not the operator's
                    // configuration. The refusal still applies to the FILE — this path runs with no
                    // operator present, which makes it the likeliest way a damaged store would have
                    // been overwritten silently.
                    var write = SaveConnections();
                    if (write != StoreWriteOutcome.Saved)
                        _logger.LogWarning(
                            "[Connections] Connection status for {Id} was not persisted ({Outcome}); it is "
                            + "in memory only and will be lost at restart.", connectionId, write);
                }
            }
        }

        // ── Discovery cache ──────────────────────────────────────────────

        /// <summary>True after the first successful SQLWATCH instance discovery.</summary>
        public bool DiscoveryCompleted { get { lock (_lock) return _discoveryCompleted; } }

        /// <summary>
        /// Returns a snapshot of the discovery results for use by DynamicDashboard.
        /// All collections are copied so callers hold independent references.
        /// </summary>
        public (string[] instances,
                Dictionary<string, string> instanceToConnId,
                HashSet<string> connectionsWithSqlWatch) GetDiscoveryCache()
        {
            lock (_lock)
            {
                return (
                    _cachedInstances.ToArray(),
                    new Dictionary<string, string>(_cachedInstanceToConnectionId, StringComparer.OrdinalIgnoreCase),
                    new HashSet<string>(_cachedConnectionsWithSqlWatch)
                );
            }
        }

        /// <summary>
        /// Stores discovery results from DynamicDashboard so subsequent dashboard
        /// navigations can skip the SQL roundtrips.
        /// </summary>
        public void CacheDiscoveryResults(
            string[] instances,
            Dictionary<string, string> instanceToConnId,
            HashSet<string> connectionsWithSqlWatch)
        {
            lock (_lock)
            {
                _cachedInstances = instances.ToArray();

                _cachedInstanceToConnectionId.Clear();
                foreach (var kvp in instanceToConnId)
                    _cachedInstanceToConnectionId[kvp.Key] = kvp.Value;

                _cachedConnectionsWithSqlWatch.Clear();
                foreach (var id in connectionsWithSqlWatch)
                    _cachedConnectionsWithSqlWatch.Add(id);

                _discoveryCompleted = true;
            }
        }

        /// <summary>
        /// Clears the discovery cache. Must be called (inside _lock) whenever the
        /// connection topology changes so the next load re-discovers instance names.
        /// </summary>
        private void InvalidateDiscoveryCache()
        {
            _discoveryCompleted = false;
            _cachedInstances = Array.Empty<string>();
            _cachedInstanceToConnectionId.Clear();
            _cachedConnectionsWithSqlWatch.Clear();
        }

        private void LoadConnections()
        {
            _connections = ConfigFileHelper.Load<List<ServerConnection>>(
                _connectionsFilePath, DeserializeOptions, out _load, out _quarantine);

            // Taken on EVERY load, including the damaged one: a damaged store that someone then
            // repairs on disk must not be overwritten by this process's empty default either.
            _diskFingerprint = ConfigFileHelper.FingerprintStore(_connectionsFilePath);

            if (IsStoreDamaged)
            {
                // Nothing to migrate — _connections is this code's empty default, not the
                // operator's estate. Said at Error because every subsequent connection edit on
                // this process will be refused, and this is the line that explains why.
                _logger.LogError(
                    "[Connections] {Path} exists and did not load ({Outcome}). NO server connection is "
                    + "configured in this process, and every add/update/remove will be REFUSED — the stored "
                    + "SQL logins and their protected passwords are the only copies on this machine and a save "
                    + "would replace them with nothing. {Recovery}",
                    _connectionsFilePath, _load,
                    ConfigFileHelper.DescribeStoreRecovery(_load, _quarantine));
                return;
            }

            // Migrate and encrypt legacy passwords on load
            foreach (var conn in _connections.Where(c => !string.IsNullOrEmpty(c.Password)))
            {
                if (!CredentialProtector.IsEncrypted(conn.Password))
                {
                    try
                    {
                        // conn.Password is still legacy PLAINTEXT here (IsEncrypted was false).
                        // SetPassword encrypts exactly once. The previous code encrypted first
                        // and then let SetPassword encrypt AGAIN, so GetDecryptedPassword (one
                        // decrypt) handed the inner ciphertext to SQL auth and every migrated
                        // connection silently failed to log in (H1, 2026-07-07).
                        conn.SetPassword(conn.Password);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to encrypt password for connection {ConnectionId}", conn.Id);
                    }
                }
            }
        }

        /// <summary>
        /// Persists the in-memory estate, REFUSING when the store <b>did not load at startup</b>.
        /// Callers roll their mutation back on anything but <see cref="StoreWriteOutcome.Saved"/>,
        /// so what this object reports always equals what IT last wrote.
        ///
        /// <para>⚠ <b>Both qualifiers are load-bearing, and the sentence said neither before the
        /// 2026-08-05 gate drove it.</b> The refusal consults <c>_load</c>, which is the outcome of
        /// the load THIS PROCESS did at startup — nothing is re-read at write time. So it does not
        /// see damage that lands afterwards, and it does not see an external edit at all. Measured:
        /// a healthy 1-connection store was loaded, a second connection with a credential was added
        /// to the file on disk by another writer, and the unattended <c>UpdateSuccessfulServers</c>
        /// then rewrote the whole estate from memory — hash <c>508E…</c>→<c>93CD…</c>, the second
        /// connection and its credential silently gone.</para>
        ///
        /// <para>That is <b>last-writer-wins</b>, and it predates this guard: this store has always
        /// been a whole-file rewrite from a process-lifetime cache. The guard narrows the window it
        /// was built for — a store that was ALREADY unreadable when this process started — and
        /// claims nothing about concurrent or post-load writers. Closing that properly means either
        /// re-inspecting the file immediately before the write, or not persisting the entire estate
        /// to record three probe-status fields. Neither is done here; see the decision log.</para>
        /// </summary>
        private StoreWriteOutcome SaveConnections(StoreWriteIntent intent = StoreWriteIntent.FromLoadedStore)
        {
            // Re-read the file's identity NOW, not at startup. The startup outcome cannot see a
            // store that changed after we loaded it, and the case that actually deleted a credential
            // was a HEALTHY file someone else had added a connection to — nothing for a damage probe
            // to find. Checked before the damage branch because "someone else's newer file" is the
            // more specific fact and deserves its own answer.
            var current = ConfigFileHelper.FingerprintStore(_connectionsFilePath);
            if (intent != StoreWriteIntent.ReplaceUnreadableStore && current != _diskFingerprint)
            {
                _logger.LogError(
                    "[Connections] REFUSED a write to {Path}: the file changed since this process read it, so "
                    + "writing the {Count} connection(s) held in memory would delete whatever was added or edited "
                    + "since — including any stored login and protected password. The file is untouched. "
                    + "Re-reading it now so the next attempt works against what is actually there.",
                    _connectionsFilePath, _connections.Count);

                // ADOPT THE NEWER FILE, do not just refuse. This class is a DI SINGLETON and
                // LoadConnections previously ran only in its constructor, so without this line one
                // external edit refuses every subsequent save until the process restarts — while the
                // operator-facing sentence tells them to reload and try again. Advice that cannot be
                // followed is the defect this whole branch keeps re-finding, and the 2026-08-05 delta
                // gate measured it here: after the first refusal, the second attempt refused
                // identically. Refusing once is the guard; refusing forever is a bug.
                //
                // Safe to do from here: the caller holds _lock (LoadConnections takes none of its
                // own), and every mutator rolls its in-memory change back on a non-Saved outcome, so
                // there is no half-applied edit to lose. This re-takes _diskFingerprint too, which is
                // what lets the retry succeed.
                LoadConnections();
                InvalidateDiscoveryCache();
                return StoreWriteOutcome.RefusedStoreChangedOnDisk;
            }

            if (ConfigFileHelper.WouldOverwriteUnreadStore(_load, intent))
            {
                _logger.LogError(
                    "[Connections] REFUSED a write to {Path}: it exists and did not load ({Outcome}), so writing "
                    + "the {Count} connection(s) this process holds would delete every stored server, login and "
                    + "protected password it could not read. The file is untouched. {Recovery}",
                    _connectionsFilePath, _load, _connections.Count,
                    ConfigFileHelper.DescribeStoreRecovery(_load, _quarantine));
                return StoreWriteOutcome.RefusedStoreUnreadable;
            }

            try
            {
                // Was a raw File.WriteAllText — a torn write on a full disk or a crash produced
                // exactly the truncated store this guard now has to refuse. Save is temp-file +
                // atomic move.
                ConfigFileHelper.Save(_connectionsFilePath, _connections, SerializeOptions);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save server connections to {Path}", _connectionsFilePath);
                return StoreWriteOutcome.WriteFailed;
            }

            _load = ConfigLoadOutcome.Loaded;
            _quarantine = null;
            // Our own write is now the file's identity, or the next save would refuse itself.
            _diskFingerprint = ConfigFileHelper.FingerprintStore(_connectionsFilePath);
            return StoreWriteOutcome.Saved;
        }

        /// <summary>
        /// The operator-facing reason for a write that did not land. Composed from the register, so
        /// the recovery sentence is the same one the log and every other store prints, and is
        /// conditioned on whether a <c>.rejected-</c> copy was actually taken.
        /// </summary>
        private string DescribeWriteFailure(StoreWriteOutcome outcome) => outcome switch
        {
            StoreWriteOutcome.RefusedStoreUnreadable =>
                "This install's saved server connections exist and could not be read, so this change was not "
                + "written — saving it would have replaced every stored server, login and password with nothing. "
                + "Nothing was changed. " + DescribeStoreRecovery(),

            // Deliberately NOT falling through to the message below, and not reusing the one above.
            // This store is healthy and NEWER than ours, so "check the disk" is wrong and the
            // .rejected- recovery sentence is wrong twice over — there is no damage and no copy.
            // Letting a distinct state inherit another's advice is the defect the 2026-08-04 gate
            // found in the RBAC prose.
            StoreWriteOutcome.RefusedStoreChangedOnDisk =>
                "This install's saved server connections were changed by something else after this page loaded "
                + "them, so this change was not written — saving it would have deleted whatever was added or "
                + "edited since, including any stored login and password. Nothing was changed. The current list "
                + "has been re-read from disk; review it and make the change again.",

            _ =>
                "The change could not be written to disk, so it has not been saved and nothing was changed. "
                + "Check the disk space and the permissions on the Config folder, then try again.",
        };

        /// <summary>
        /// Validates connection name to prevent path traversal and injection attacks.
        /// </summary>
        private bool IsValidConnectionName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;

            // Reject paths, special characters that could be used for injection
            if (name.Contains("\\") || name.Contains("/") || name.Contains("..")) return false;
            if (name.Contains("<") || name.Contains(">") || name.Contains("&") ||
                name.Contains("'") || name.Contains("\"") || name.Contains(";")) return false;

            // Only allow alphanumeric, underscore, hyphen, dot
            foreach (char c in name)
            {
                if (!char.IsLetterOrDigit(c) && c != '_' && c != '-' && c != '.') return false;
            }

            return true;
        }

        /// <summary>
        /// Validates password format before encryption.
        /// </summary>
        private bool IsValidPassword(string password)
        {
            if (string.IsNullOrEmpty(password)) return false;

            // Password must be at least 8 characters for security
            if (password.Length < 8) return false;

            // Reject passwords with only printable ASCII (common in config files)
            foreach (char c in password)
            {
                if (!char.IsControl(c) && !char.IsPunctuation(c) && !char.IsLetterOrDigit(c))
                    return true; // Contains special chars - valid
            }

            return password.Length >= 8;
        }
    }
}
