/* In the name of God, the Merciful, the Compassionate */

/*
 * THE SQL AGENT OPERATOR PICKER AND ITS MAIL-CHAIN VERDICT (2026-09-09).
 *
 * WHY THIS FILE EXISTS. /server-configuration used to take the operator name from ONE free-text box
 * and splice it into the baseline script. Adrian's ask: "is it possible to pull existing operators
 * from the SQL servers to select per instance? as the operators might be different, and could we
 * check these operators and the mail profiles and whether those mail profiles work, likely from
 * checking email logs on the SQL server for that operator and profile."
 *
 * Two halves, and the second is the one that carries the DD risk:
 *
 *   READ  - the operator inventory per instance (ReadOperatorsAsync), so the page can offer a real
 *           choice instead of asking a human to remember a name.
 *   JUDGE - the five-link mail chain per instance (ReadMailChainAsync + Evaluate), so the page can
 *           say whether a notification to that operator would ACTUALLY be delivered.
 *
 * CONFIGURATION ALONE NEVER YIELDS "WORKS". Every link can be wired and no mail ever leave the box -
 * that is exactly the state of .\new2022 on this bench (profile wired, Agent pointed at it, 0 sent
 * items ever, 20,797 failed items to this operator's address, all dated 2026-07-27). A checker that
 * reads configuration and prints a green tick would call that server healthy. WORKS is therefore
 * reserved for OBSERVED DELIVERY: a sent item to this operator's address inside
 * <see cref="AgentMailChainProbe.WorksWindowDays"/> days. Everything else is BROKEN, UNKNOWN,
 * NOT CONFIGURED or COULD NOT READ, and each of those names what it is talking about.
 *
 * A DENIED READ IS NOT A NEGATIVE FINDING. Proved live on both rigs 2026-09-09 under the
 * non-sysadmin login sqlt_plain: msdb.dbo.sysoperators, sysalerts, sysmail_profile,
 * sysmail_principalprofile, sysmail_allitems and sysmail_event_log all raise Msg 229 (SELECT
 * permission denied) - they do NOT silently return zero rows. sys.configurations and
 * xp_instance_regread both succeed for the same login. So every read below is guarded individually
 * and a failure becomes a NAMED GAP on the result (the shape
 * AccessSurfaceCollector.GuardedTraceServerReadAsync already ships), never an exception the page has
 * to render as "no mail configured".
 *
 * THE ONE READ THAT CAN LIE BY SILENCE. msdb.sys.service_queues is metadata-filtered rather than
 * denied: the same sqlt_plain login gets ZERO ROWS for ExternalMailQueue on both rigs, with no error.
 * That is why the queue state is a bool? that stays NULL when the row is absent, and why it is
 * DISPLAYED as evidence and is never a failing link. Making a stopped or unreadable queue fail link 1
 * would have printed NOT CONFIGURED over .\new2022 - a server whose real problem is 20,797 failed
 * deliveries.
 *
 * NO sp_configure ANYWHERE, AND THE WALL IS NOT WHAT HOLDS THAT (corrected 2026-09-09, gate
 * finding F3). This header used to say SqlSafetyValidator blocks any mention of sp_configure
 * outright. It blocks the free-form form, and it is false as a claim about THIS file: Validate
 * waives every blocked pattern for the WHOLE batch when one AllowedExceptions entry matches, and
 * the first entry matches a SELECT ... FROM sys. read - the exact shape of the link-1 read below.
 * The gate spliced sp_configure into DatabaseMailXpsSql and SqlSafetyValidatorClassifyTests stayed
 * 28/28 green. What holds the substitution is ServerConfigPageMarkupLintTests, which lints THIS
 * source. Link 1 reads sys.configurations - the same substitution corpus
 * check_433_Alerting_Mechanisms.sql:6 already makes. Every read here also classifies Safe, which is
 * asserted over the shipped constants, and that assertion is a floor rather than the net.
 *
 * NO SPLICED VALUES. Every operator name, address and profile name reaches the server as a
 * SqlParameter. One CommandText runs every statement in it (the MDS lesson), so an unquoted
 * interpolation here would be an injection primitive on a path whose values an operator picks.
 */

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data.Services
{
    /// <summary>One row of msdb.dbo.sysoperators, plus how many enabled alerts already notify it.</summary>
    public sealed class AgentOperatorRow
    {
        public int Id;
        public string Name = "";
        public bool Enabled;
        public string EmailAddress = "";

        /// <summary>Agent's own record of the last notification it sent this operator: yyyyMMdd, 0 = never.</summary>
        public int LastEmailDate;

        /// <summary>Agent's own record, HHmmss without leading zeros (44504 = 04:45:04). 0 = never.</summary>
        public int LastEmailTime;

        /// <summary>Enabled alerts whose notification list includes this operator. The default rule's first branch.</summary>
        public int EnabledAlertNotifications;

        public bool HasAddress => !string.IsNullOrWhiteSpace(EmailAddress);

        /// <summary>"2026-09-05 04:45:04", or null when Agent has never notified this operator.</summary>
        public string? LastEmailDescription
        {
            get
            {
                if (LastEmailDate <= 0) return null;
                var d = LastEmailDate.ToString(CultureInfo.InvariantCulture).PadLeft(8, '0');
                var t = LastEmailTime.ToString(CultureInfo.InvariantCulture).PadLeft(6, '0');
                return $"{d.Substring(0, 4)}-{d.Substring(4, 2)}-{d.Substring(6, 2)} "
                     + $"{t.Substring(0, 2)}:{t.Substring(2, 2)}:{t.Substring(4, 2)}";
            }
        }
    }

    /// <summary>What one instance's operators look like, with the reads that could not be made.</summary>
    public sealed class OperatorInventory
    {
        public string ServerName = "";

        public List<AgentOperatorRow> Operators { get; } = new();

        /// <summary>Agent's fail-safe operator. SHOWN as a last-resort candidate; never auto-picked.</summary>
        public string? FailSafeOperator;

        /// <summary>Reads that were denied or failed. A non-empty list means this inventory is PARTIAL.</summary>
        public List<MailChainGap> Gaps { get; } = new();

        public bool IsPartial => Gaps.Count > 0;

        /// <summary>
        /// WHAT THIS READ ACTUALLY ESTABLISHED (gate finding F2, 2026-09-09). The picker used to ask
        /// "is Inventory null?", and that question cannot tell a server with NO operators from a server
        /// whose operators nobody was allowed to read: both arrive as a non-null inventory with an empty
        /// list, and the page offered the free-text "operator to create" box - and left Apply enabled -
        /// over BOTH. A least-privilege login gets Msg 229 on msdb.dbo.sysoperators (proved on both rigs
        /// 2026-09-09), so the denied case is the common one, not the exotic one.
        ///
        /// <para>ANY gap makes it Unreadable, not only a gap on the operator list itself. Deliberately
        /// conservative: the notification counts decide the DEFAULT and the fail-safe read is offered as
        /// a candidate, so a partial answer is not a basis either for defaulting or for concluding "this
        /// instance has none". The cost is that a rare partial read (operators listed, one ancillary read
        /// denied) refuses the picker instead of offering a degraded one - and the badge names the read
        /// that was refused, so it is a STATED refusal rather than a silent guess.</para>
        /// </summary>
        public OperatorInventoryState State =>
            Gaps.Count > 0 ? OperatorInventoryState.Unreadable
            : Operators.Count == 0 ? OperatorInventoryState.Empty
            : OperatorInventoryState.Loaded;

        /// <summary>The first gap by link, as "&lt;read&gt;: &lt;reason&gt;". Null when nothing was refused.</summary>
        public string? UnreadableReason =>
            Gaps.OrderBy(g => g.Link).Select(g => g.Read + ": " + g.Reason).FirstOrDefault();
    }

    /// <summary>
    /// What an operator read established, as a state rather than a null check (gate finding F2).
    /// <see cref="NotRead"/> belongs to the PAGE's per-instance state - no read has happened yet - so
    /// an <see cref="OperatorInventory"/> is never NotRead.
    /// </summary>
    public enum OperatorInventoryState
    {
        NotRead,
        Unreadable,
        Empty,
        Loaded,
    }

    /// <summary>A read that could not be made, and which of the five links it belonged to (0 = the connection).</summary>
    public sealed record MailChainGap(int Link, string Read, string Reason);

    /// <summary>One profile joined to its account and SMTP server. An empty AccountName is a profile with no account.</summary>
    public sealed record MailProfileRow(
        int ProfileId, string ProfileName, string AccountName, string SmtpServer, int SmtpPort, string FromAddress);

    /// <summary>One msdb.dbo.sysmail_allitems row addressed to the selected operator.</summary>
    public sealed record MailItemRow(
        long MailItemId, int? ProfileId, string SentStatus, DateTime? SentDate, DateTime? LastModDate, string Recipients);

    /// <summary>One msdb.dbo.sysmail_event_log error row for the Agent profile's accounts.</summary>
    public sealed record MailLogRow(DateTime LogDate, string EventType, string Description);

    /// <summary>
    /// The RAW rows of the five links for one instance and one operator. Deliberately a plain data
    /// carrier with no logic: <see cref="AgentMailChainProbe.Evaluate"/> is a pure function over it, so
    /// every verdict in this file is unit-testable over captured msdb rows with no SQL Server.
    /// </summary>
    public sealed class MailChainRows
    {
        public string ServerName = "";
        public string OperatorName = "";

        /// <summary>The SERVER's own clock, read in the same round trip as link 5. The recency window is
        /// measured against this and never against the app host's clock, which is a different machine.</summary>
        public DateTime? ServerNow;

        // Link 1: Database Mail is enabled.
        public int? DatabaseMailXpsValue;
        public int? DatabaseMailXpsValueInUse;

        /// <summary>ExternalMailQueue's is_receive_enabled. NULL = the row was not visible (see the file
        /// header: metadata-filtered for a non-sysadmin, NOT denied). Evidence only, never a failing link.</summary>
        public bool? MailQueueReceiveEnabled;

        // Link 2: a profile with an account.
        public List<MailProfileRow> Profiles { get; } = new();
        public int PrincipalProfileGrants;
        public bool ProfileGrantedToPublic;

        // Link 3: Agent points at a profile.
        public string? AgentMailProfile;
        public int? AgentUseDatabaseMail;

        // Link 4: the operator is deliverable.
        public AgentOperatorRow? Operator;

        // Link 5: delivery evidence.
        public bool? IsSysadmin;
        public DateTime? LastSent;
        public DateTime? LastFailed;
        public int SentCount;
        public int FailedCount;
        public int MatchedCount;
        public DateTime? LastEventLogError;
        public string? LastEventLogMessage;
        public List<MailItemRow> RecentItems { get; } = new();
        public List<MailLogRow> RecentErrors { get; } = new();

        public List<MailChainGap> Gaps { get; } = new();
    }

    /// <summary>The five verdicts. See <see cref="AgentMailChainProbe.Evaluate"/> for the rule behind each.</summary>
    public enum MailChainState
    {
        Works,
        Broken,
        Unknown,
        NotConfigured,
        CouldNotRead,
    }

    /// <summary>
    /// One instance's answer to "would a notification to this operator arrive?". <c>Link</c> is the link
    /// the verdict is ABOUT (0 when it is about the connection); <c>Evidence</c> carries the server-side
    /// detail verbatim rather than a paraphrase.
    /// </summary>
    public sealed record MailChainVerdict(MailChainState State, string Headline, string? Evidence, int Link)
    {
        public string Label => State switch
        {
            MailChainState.Works => "Works",
            MailChainState.Broken => "Broken",
            MailChainState.Unknown => "Unknown",
            MailChainState.NotConfigured => "Not configured",
            _ => "Could not read",
        };

        /// <summary>
        /// True when links 1-4 were read and all hold, so there IS a profile to send through. The page
        /// offers its test-send button only over this: sending across a NOT CONFIGURED chain would fail
        /// for the reason the badge already names, and sending across a chain nobody could read would
        /// produce an error that says nothing about the chain.
        /// </summary>
        public bool LinksOneToFourHold =>
            State != MailChainState.NotConfigured
            && !(State == MailChainState.CouldNotRead && Link <= 4);

        /// <summary>
        /// True only for the stopped-queue rule (gate finding F1). The page appends a queue note to its
        /// evidence line whenever the queue is known stopped; this flag stops that note being printed
        /// twice over the one verdict that already IS about the queue.
        /// </summary>
        public bool NamesTheStoppedQueue { get; init; }

        /// <summary>The CSS modifier the page's badge uses. Kept beside the enum so the two cannot drift.</summary>
        public string CssModifier => State switch
        {
            MailChainState.Works => "works",
            MailChainState.Broken => "broken",
            MailChainState.Unknown => "unknown",
            MailChainState.NotConfigured => "notconfigured",
            _ => "couldnotread",
        };
    }

    /// <summary>The rows, the verdict over them, and when the read happened.</summary>
    public sealed record MailChainReport(MailChainRows Rows, MailChainVerdict Verdict, DateTime ReadAt);

    /// <summary>What one opt-in test send did, and what link 5 looked like afterwards.</summary>
    public sealed record TestNotificationResult(bool Accepted, string? Error, MailChainReport? Recheck);

    /// <summary>
    /// Reads the SQL Agent operator inventory and the Database Mail chain for one instance, and judges
    /// whether a notification to a given operator would actually be delivered.
    ///
    /// <para>Registered AddScoped. It holds NO state of its own - every result is returned to the caller
    /// and parked on the scoped <see cref="ServerConfigRunState"/> - so the lifetime is a formality
    /// rather than a correctness claim, and a singleton carrying a delegate seam would have to answer to
    /// DiLifetimeCensusTests for nothing gained.</para>
    /// </summary>
    public sealed class AgentMailChainProbe
    {
        /// <summary>A sent item OLDER than this is not evidence that mail works TODAY.</summary>
        public const int WorksWindowDays = 30;

        /// <summary>
        /// The evidence sentence for the stopped-queue verdict, ruled 2026-09-09 (gate finding F1). A
        /// constant because the page renders it and the tests assert it: a paraphrase in either place
        /// would be a claim nobody measured.
        /// </summary>
        public const string QueueStoppedEvidence =
            "Database Mail queue stopped (msdb ExternalMailQueue is_receive_enabled = 0).";

        /// <summary>How many recent items and error rows to bring back for display. The verdict itself is
        /// computed from server-side aggregates over EVERY matching row, never from this window.</summary>
        internal const int DisplayRowLimit = 10;

        private const int CommandTimeoutSeconds = 30;

        /// <summary>Long enough for Database Mail to pick the item off the queue and stamp a status on it.</summary>
        private static readonly TimeSpan RecheckDelay = TimeSpan.FromSeconds(3);

        /// <summary>The Details category every test-send audit entry carries. No new AuditEventType member:
        /// the enum's ordinals are an append-only contract (AuditEventTypeOrdinalContractTests), so the
        /// semantics go in Details - the eval-failure-visible precedent, DECISIONS 2026-09-08 07:08.</summary>
        internal const string TestNotificationAuditCategory = "MailChainTestNotification";

        private readonly ILogger<AgentMailChainProbe> _logger;
        private readonly SqlServerConnectionFactory _factory;
        private readonly AuditLogService? _audit;

        public AgentMailChainProbe(
            ILogger<AgentMailChainProbe> logger,
            SqlServerConnectionFactory factory,
            AuditLogService? audit = null)
        {
            _logger = logger;
            _factory = factory;
            _audit = audit;
        }

        /// <summary>
        /// TEST SEAM (InternalsVisibleTo SQLTriage.Tests), the same shape
        /// <see cref="ServerConfigScriptService.ConnectionFactoryOverride"/> uses. Null on every shipped path.
        /// </summary>
        internal Func<string, DbConnection>? ConnectionFactoryOverride { get; set; }

        // THE SHIPPED SQL. Every one of these is a plain SELECT or a pure xp_instance_regread, so
        // SqlSafetyValidator classifies each of them Safe - asserted over these very constants in
        // SqlSafetyValidatorClassifyTests. Each was PROVED live on .\old2017 and .\new2022 on
        // 2026-09-09, exit 0 on both.

        internal const string OperatorsSql = @"
SELECT o.id, o.name, o.enabled, ISNULL(o.email_address, N'') AS email_address,
       o.last_email_date, o.last_email_time
FROM msdb.dbo.sysoperators o
ORDER BY o.name;";

        internal const string NotificationCountsSql = @"
SELECT n.operator_id, COUNT(*) AS enabled_alerts
FROM msdb.dbo.sysnotifications n
JOIN msdb.dbo.sysalerts a ON a.id = n.alert_id
WHERE a.enabled = 1
GROUP BY n.operator_id;";

        /// <summary>OUTPUT-param form with an explicit NULL arm. xp_instance_regread does NOT throw on an
        /// absent key - it prints an informational message and leaves the parameter NULL - so a caller that
        /// codes only the happy path reads a non-throwing failure as a real value (AlertEvaluationService's
        /// r2-01 fail-closed lesson, 2026-08-26).</summary>
        internal const string FailSafeOperatorSql = @"
DECLARE @failsafe NVARCHAR(256);
EXEC master.dbo.xp_instance_regread N'HKEY_LOCAL_MACHINE',
     N'SOFTWARE\Microsoft\MSSQLServer\SQLServerAgent', N'AlertFailSafeOperator', @failsafe OUTPUT;
SELECT @failsafe AS fail_safe_operator;";

        internal const string DatabaseMailXpsSql = @"
SELECT CONVERT(int, value) AS value, CONVERT(int, value_in_use) AS value_in_use
FROM sys.configurations
WHERE name = N'Database Mail XPs';";

        /// <summary>What sysmail_help_status_sp reads, as a SELECT. A non-sysadmin sees ZERO ROWS here with
        /// no error (metadata visibility), which is why the caller keeps this as bool? and no verdict
        /// depends on it.</summary>
        internal const string MailQueueSql = @"
SELECT is_receive_enabled
FROM msdb.sys.service_queues
WHERE name = N'ExternalMailQueue';";

        internal const string ProfilesSql = @"
SELECT p.profile_id, p.name AS profile_name,
       ISNULL(a.name, N'') AS account_name,
       ISNULL(s.servername, N'') AS smtp_server,
       ISNULL(s.port, 0) AS smtp_port,
       ISNULL(a.email_address, N'') AS from_address
FROM msdb.dbo.sysmail_profile p
LEFT JOIN msdb.dbo.sysmail_profileaccount pa ON pa.profile_id = p.profile_id
LEFT JOIN msdb.dbo.sysmail_account a ON a.account_id = pa.account_id
LEFT JOIN msdb.dbo.sysmail_server s ON s.account_id = a.account_id
ORDER BY p.name;";

        internal const string PrincipalProfileSql = @"
SELECT CONVERT(varchar(200), pp.principal_sid, 1) AS principal_sid_hex, pp.is_default
FROM msdb.dbo.sysmail_principalprofile pp;";

        internal const string AgentMailRegistrySql = @"
DECLARE @agentProfile NVARCHAR(256), @useMail INT;
EXEC master.dbo.xp_instance_regread N'HKEY_LOCAL_MACHINE',
     N'SOFTWARE\Microsoft\MSSQLServer\SQLServerAgent', N'DatabaseMailProfile', @agentProfile OUTPUT;
EXEC master.dbo.xp_instance_regread N'HKEY_LOCAL_MACHINE',
     N'SOFTWARE\Microsoft\MSSQLServer\SQLServerAgent', N'UseDatabaseMail', @useMail OUTPUT;
SELECT @agentProfile AS agent_mail_profile, @useMail AS use_database_mail;";

        internal const string OperatorRowSql = @"
SELECT o.id, o.name, o.enabled, ISNULL(o.email_address, N'') AS email_address,
       o.last_email_date, o.last_email_time
FROM msdb.dbo.sysoperators o
WHERE o.name = @operatorName;";

        /// <summary>Link 5's gate AND the clock the recency window is measured against.</summary>
        internal const string SysadminAndClockSql = @"
SELECT IS_SRVROLEMEMBER('sysadmin') AS is_sysadmin, SYSDATETIME() AS server_now;";

        /// <summary>
        /// The verdict's arithmetic, done SERVER-SIDE over every matching row. A TOP-N window cannot
        /// answer "was anything ever sent" on a profile carrying 50,000 failures.
        ///
        /// <para>CHARINDEX under a BINARY collation, never a bare LIKE: a non-binary comparison
        /// over-matches U+FFFD, so a row whose recipients column holds replacement characters would count
        /// as a delivery to this operator (the 2026-08 census lesson).</para>
        ///
        /// <para><b>ANCHORED to a WHOLE list entry, after gate finding G-B1 (2026-09-09).</b> A bare
        /// CHARINDEX of the address is a SUBSTRING test, and one operator's address is routinely a suffix
        /// of another's. On the NEW2022 rig the operators are alerts@sqldba.org and sqlalerts@sqldba.org,
        /// so the substring form credited the SHORTER address with the longer one's rows - 50,523 failed
        /// instead of 29,726, proved live and shown on the page. sent_count, last_sent and last_failed come
        /// from this same predicate, so a SENT item on the longer address would have counted as a delivery
        /// to the shorter one and turned "no item has EVER been delivered" into a false healthy badge - the
        /// exact axis this lane exists to protect.</para>
        ///
        /// <para>The anchor is the semicolon, which is Database Mail's OWN separator: msdb's
        /// sysmail_verify_addressparams_sp rejects a comma outside double quotes and says in its own error
        /// text that users should use ";". Both sides are wrapped in ";" and have spaces removed, so
        /// "x@y.com; alerts@sqldba.org" matches and "x@y.com;sqlalerts@sqldba.org" does not. Removing
        /// spaces only ever deletes characters, so it cannot bridge a ";" and re-open the over-match.</para>
        ///
        /// <para><b>CASE-SENSITIVE, deliberately.</b> Latin1_General_BIN2 is byte-exact, so
        /// ALERTS@SQLDBA.ORG does not match alerts@sqldba.org. The collation is NOT relaxed to change that:
        /// it is what defeats the U+FFFD over-match above, and on the rigs no recipients value differs from
        /// its own lowercase form while both operator addresses are lowercase, so a case-insensitive form
        /// would buy nothing that has been shown to exist. Its error direction is an UNDER-match, which
        /// reads BROKEN - the safe direction under this lane's never-sent = BROKEN ruling.</para>
        ///
        /// <para><b>Known residual, deliberate.</b> A display-name entry - Alerts
        /// &lt;alerts@sqldba.org&gt; - does NOT match, because the address is not a whole entry. Neither rig
        /// holds one (zero rows contain "&lt;") and Database Mail's own validator never mentions the form,
        /// so the match is not widened past the evidence. It too fails towards BROKEN.</para>
        /// </summary>
        internal const string DeliveryAggregateSql = @"
SELECT MAX(CASE WHEN i.sent_status = 'sent' THEN i.sent_date END) AS last_sent,
       MAX(CASE WHEN i.sent_status = 'failed' THEN COALESCE(i.last_mod_date, i.sent_date) END) AS last_failed,
       SUM(CASE WHEN i.sent_status = 'sent' THEN 1 ELSE 0 END) AS sent_count,
       SUM(CASE WHEN i.sent_status = 'failed' THEN 1 ELSE 0 END) AS failed_count,
       COUNT(*) AS matched_count
FROM msdb.dbo.sysmail_allitems i
WHERE CHARINDEX(REPLACE(N';' + @address + N';', N' ', N'') COLLATE Latin1_General_BIN2, REPLACE(N';' + ISNULL(i.recipients, N'') + N';', N' ', N'') COLLATE Latin1_General_BIN2) > 0
   OR CHARINDEX(REPLACE(N';' + @address + N';', N' ', N'') COLLATE Latin1_General_BIN2, REPLACE(N';' + ISNULL(i.copy_recipients, N'') + N';', N' ', N'') COLLATE Latin1_General_BIN2) > 0
   OR CHARINDEX(REPLACE(N';' + @address + N';', N' ', N'') COLLATE Latin1_General_BIN2, REPLACE(N';' + ISNULL(i.blind_copy_recipients, N'') + N';', N' ', N'') COLLATE Latin1_General_BIN2) > 0;";

        internal const string DeliveryItemsSql = @"
SELECT TOP (@top) i.mailitem_id, i.profile_id, i.sent_status, i.sent_date, i.last_mod_date,
       LEFT(ISNULL(i.recipients, N''), 200) AS recipients
FROM msdb.dbo.sysmail_allitems i
WHERE CHARINDEX(REPLACE(N';' + @address + N';', N' ', N'') COLLATE Latin1_General_BIN2, REPLACE(N';' + ISNULL(i.recipients, N'') + N';', N' ', N'') COLLATE Latin1_General_BIN2) > 0
   OR CHARINDEX(REPLACE(N';' + @address + N';', N' ', N'') COLLATE Latin1_General_BIN2, REPLACE(N';' + ISNULL(i.copy_recipients, N'') + N';', N' ', N'') COLLATE Latin1_General_BIN2) > 0
   OR CHARINDEX(REPLACE(N';' + @address + N';', N' ', N'') COLLATE Latin1_General_BIN2, REPLACE(N';' + ISNULL(i.blind_copy_recipients, N'') + N';', N' ', N'') COLLATE Latin1_General_BIN2) > 0
ORDER BY i.mailitem_id DESC;";

        internal const string EventLogSql = @"
SELECT TOP (@top) l.log_date, l.event_type, LEFT(l.description, 1000) AS description
FROM msdb.dbo.sysmail_event_log l
WHERE l.event_type = 'error'
  AND (@profileId IS NULL OR l.account_id IS NULL
       OR l.account_id IN (SELECT pa.account_id FROM msdb.dbo.sysmail_profileaccount pa
                           WHERE pa.profile_id = @profileId))
ORDER BY l.log_date DESC;";

        /// <summary>Every statement this service sends to a server. The classification test walks THIS list,
        /// so a statement added above without being added here is not silently unclassified.</summary>
        internal static IReadOnlyList<string> ShippedReadStatements { get; } = new[]
        {
            OperatorsSql, NotificationCountsSql, FailSafeOperatorSql, DatabaseMailXpsSql, MailQueueSql,
            ProfilesSql, PrincipalProfileSql, AgentMailRegistrySql, OperatorRowSql, SysadminAndClockSql,
            DeliveryAggregateSql, DeliveryItemsSql, EventLogSql,
        };

        // ── READS ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The operator inventory for one instance. Never throws for a server-side reason: a refused
        /// connection or a denied read lands as a named gap on the returned inventory, so the page renders
        /// "could not read X" beside that instance instead of losing the whole picker.
        /// </summary>
        public Task<OperatorInventory> ReadOperatorsAsync(
            string connectionString, string serverName, CancellationToken ct = default) =>
            ReadOperatorsWithAsync(() => CreateConnection(connectionString), serverName, ct);

        /// <summary>
        /// Same read against the server the SINGLE-instance Apply targets, resolved through the SAME
        /// factory that path uses, so the picker can never show a different server's operators. The name
        /// is taken from the connection itself rather than re-derived here: the factory's own rule
        /// (GlobalInstanceSelector first, then CurrentServer's first server) is not duplicated.
        /// </summary>
        public Task<OperatorInventory> ReadOperatorsForCurrentServerAsync(CancellationToken ct = default) =>
            ReadOperatorsWithAsync(() => (DbConnection)_factory.CreateConnection("master"), serverName: null, ct);

        private async Task<OperatorInventory> ReadOperatorsWithAsync(
            Func<DbConnection> open, string? serverName, CancellationToken ct)
        {
            var inv = new OperatorInventory { ServerName = serverName ?? "" };

            DbConnection? conn = null;
            try
            {
                conn = open();
                if (string.IsNullOrWhiteSpace(inv.ServerName)) inv.ServerName = DescribeTarget(conn);
                await conn.OpenAsync(ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                conn?.Dispose();
                inv.Gaps.Add(new MailChainGap(0, $"connect to {Label(inv.ServerName)}", ex.Message));
                return inv;
            }

            using (conn)
            {
                await GuardedReadAsync(conn, 4, "Agent operators (msdb.dbo.sysoperators)", OperatorsSql, null,
                    async (r, c) => { while (await r.ReadAsync(c)) inv.Operators.Add(ReadOperatorRow(r)); },
                    inv.Gaps, ct);

                var counts = new Dictionary<int, int>();
                await GuardedReadAsync(conn, 4, "alert notifications (msdb.dbo.sysnotifications)",
                    NotificationCountsSql, null,
                    async (r, c) =>
                    {
                        while (await r.ReadAsync(c))
                            counts[Convert.ToInt32(r.GetValue(0), CultureInfo.InvariantCulture)] =
                                Convert.ToInt32(r.GetValue(1), CultureInfo.InvariantCulture);
                    }, inv.Gaps, ct);

                foreach (var op in inv.Operators)
                    if (counts.TryGetValue(op.Id, out var n)) op.EnabledAlertNotifications = n;

                await GuardedReadAsync(conn, 4, "Agent fail-safe operator (xp_instance_regread)",
                    FailSafeOperatorSql, null,
                    async (r, c) => { if (await r.ReadAsync(c)) inv.FailSafeOperator = r.IsDBNull(0) ? null : r.GetString(0); },
                    inv.Gaps, ct);
            }

            return inv;
        }

        // ── THE FIVE LINKS ───────────────────────────────────────────────────────────────────

        public Task<MailChainReport> ReadMailChainAsync(
            string connectionString, string serverName, string operatorName, CancellationToken ct = default) =>
            ReadMailChainWithAsync(() => CreateConnection(connectionString), serverName, operatorName, ct);

        public Task<MailChainReport> ReadMailChainForCurrentServerAsync(
            string operatorName, CancellationToken ct = default) =>
            ReadMailChainWithAsync(() => (DbConnection)_factory.CreateConnection("master"), serverName: null, operatorName, ct);

        private async Task<MailChainReport> ReadMailChainWithAsync(
            Func<DbConnection> open, string? serverName, string operatorName, CancellationToken ct)
        {
            var rows = new MailChainRows { ServerName = serverName ?? "", OperatorName = operatorName };

            DbConnection? conn = null;
            try
            {
                conn = open();
                if (string.IsNullOrWhiteSpace(rows.ServerName)) rows.ServerName = DescribeTarget(conn);
                await conn.OpenAsync(ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                conn?.Dispose();
                rows.Gaps.Add(new MailChainGap(0, $"connect to {Label(rows.ServerName)}", ex.Message));
                return Report(rows);
            }

            using (conn)
            {
                // Link 1 ───────────────────────────────────────────────────────────────────
                await GuardedReadAsync(conn, 1, "Database Mail XPs (sys.configurations)", DatabaseMailXpsSql, null,
                    async (r, c) =>
                    {
                        if (await r.ReadAsync(c))
                        {
                            rows.DatabaseMailXpsValue = IntOrNull(r, 0);
                            rows.DatabaseMailXpsValueInUse = IntOrNull(r, 1);
                        }
                    }, rows.Gaps, ct);

                // Evidence only. A zero-row answer here is "not visible", never "stopped" - see the header.
                await GuardedReadAsync(conn, 1, "Database Mail queue (msdb.sys.service_queues)", MailQueueSql, null,
                    async (r, c) =>
                    {
                        if (await r.ReadAsync(c) && !r.IsDBNull(0))
                            rows.MailQueueReceiveEnabled =
                                Convert.ToInt32(r.GetValue(0), CultureInfo.InvariantCulture) != 0;
                    }, rows.Gaps, ct);

                // Link 2 ───────────────────────────────────────────────────────────────────
                await GuardedReadAsync(conn, 2, "Database Mail profiles (msdb.dbo.sysmail_profile)", ProfilesSql, null,
                    async (r, c) =>
                    {
                        while (await r.ReadAsync(c))
                            rows.Profiles.Add(new MailProfileRow(
                                Convert.ToInt32(r.GetValue(0), CultureInfo.InvariantCulture),
                                Str(r, 1), Str(r, 2), Str(r, 3), IntOrNull(r, 4) ?? 0, Str(r, 5)));
                    }, rows.Gaps, ct);

                await GuardedReadAsync(conn, 2, "profile grants (msdb.dbo.sysmail_principalprofile)",
                    PrincipalProfileSql, null,
                    async (r, c) =>
                    {
                        while (await r.ReadAsync(c))
                        {
                            rows.PrincipalProfileGrants++;
                            // 0x00 is the "public" grant: every principal may use the profile.
                            if (string.Equals(Str(r, 0), "0x00", StringComparison.OrdinalIgnoreCase))
                                rows.ProfileGrantedToPublic = true;
                        }
                    }, rows.Gaps, ct);

                // Link 3 ───────────────────────────────────────────────────────────────────
                await GuardedReadAsync(conn, 3, "Agent mail profile (xp_instance_regread)", AgentMailRegistrySql, null,
                    async (r, c) =>
                    {
                        if (await r.ReadAsync(c))
                        {
                            rows.AgentMailProfile = r.IsDBNull(0) ? null : r.GetString(0);
                            rows.AgentUseDatabaseMail = IntOrNull(r, 1);
                        }
                    }, rows.Gaps, ct);

                // Link 4 ───────────────────────────────────────────────────────────────────
                await GuardedReadAsync(conn, 4, "the operator row (msdb.dbo.sysoperators)", OperatorRowSql,
                    cmd => AddParam(cmd, "@operatorName", operatorName),
                    async (r, c) => { if (await r.ReadAsync(c)) rows.Operator = ReadOperatorRow(r); },
                    rows.Gaps, ct);

                // Link 5 ───────────────────────────────────────────────────────────────────
                await GuardedReadAsync(conn, 5, "sysadmin membership and the server clock", SysadminAndClockSql, null,
                    async (r, c) =>
                    {
                        if (await r.ReadAsync(c))
                        {
                            rows.IsSysadmin = IntOrNull(r, 0) == 1;
                            rows.ServerNow = DateOrNull(r, 1);
                        }
                    }, rows.Gaps, ct);

                var address = rows.Operator?.EmailAddress ?? "";

                if (rows.IsSysadmin != true)
                {
                    // A non-sysadmin sees only the items IT sent, so reading them would produce a
                    // confident WRONG answer ("nothing was ever sent") rather than a gap. Not attempted.
                    rows.Gaps.Add(new MailChainGap(5,
                        "Database Mail delivery history (msdb.dbo.sysmail_allitems)",
                        "This connection is not a member of the sysadmin server role. Database Mail shows a "
                        + "non-sysadmin caller only the items that caller sent, so the delivery history for "
                        + "this operator was not read."));
                }
                else if (address.Length == 0)
                {
                    rows.Gaps.Add(new MailChainGap(5,
                        "Database Mail delivery history (msdb.dbo.sysmail_allitems)",
                        "The operator has no email address, so there is no address to match delivery against."));
                }
                else
                {
                    await GuardedReadAsync(conn, 5, "delivery totals (msdb.dbo.sysmail_allitems)", DeliveryAggregateSql,
                        cmd => AddParam(cmd, "@address", address),
                        async (r, c) =>
                        {
                            if (await r.ReadAsync(c))
                            {
                                rows.LastSent = DateOrNull(r, 0);
                                rows.LastFailed = DateOrNull(r, 1);
                                rows.SentCount = IntOrNull(r, 2) ?? 0;
                                rows.FailedCount = IntOrNull(r, 3) ?? 0;
                                rows.MatchedCount = IntOrNull(r, 4) ?? 0;
                            }
                        }, rows.Gaps, ct);

                    await GuardedReadAsync(conn, 5, "recent mail items (msdb.dbo.sysmail_allitems)", DeliveryItemsSql,
                        cmd => { AddParam(cmd, "@top", DisplayRowLimit); AddParam(cmd, "@address", address); },
                        async (r, c) =>
                        {
                            while (await r.ReadAsync(c))
                                rows.RecentItems.Add(new MailItemRow(
                                    Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture),
                                    IntOrNull(r, 1), Str(r, 2), DateOrNull(r, 3), DateOrNull(r, 4), Str(r, 5)));
                        }, rows.Gaps, ct);

                    var profileId = ResolveAgentProfileId(rows);
                    await GuardedReadAsync(conn, 5, "Database Mail error log (msdb.dbo.sysmail_event_log)", EventLogSql,
                        cmd => { AddParam(cmd, "@top", DisplayRowLimit); AddParam(cmd, "@profileId", profileId); },
                        async (r, c) =>
                        {
                            while (await r.ReadAsync(c))
                                rows.RecentErrors.Add(new MailLogRow(
                                    DateOrNull(r, 0) ?? default, Str(r, 1), Str(r, 2)));
                        }, rows.Gaps, ct);

                    if (rows.RecentErrors.Count > 0)
                    {
                        var newest = rows.RecentErrors[0];
                        rows.LastEventLogError = newest.LogDate;
                        rows.LastEventLogMessage = newest.Description;
                    }
                }
            }

            return Report(rows);
        }

        private static MailChainReport Report(MailChainRows rows)
        {
            var now = DateTime.Now;
            return new MailChainReport(rows, Evaluate(rows, now), now);
        }

        // ── THE VERDICT (pure) ───────────────────────────────────────────────────────────────

        /// <summary>
        /// The rule, in the order it is applied. Every branch names what it is talking about.
        ///
        /// <list type="number">
        /// <item><b>COULD NOT READ</b> when a read behind links 1-4 was denied or failed. A verdict about
        ///   configuration nobody could see would be a fabrication - the 2026-09-07 "NULL where not
        ///   evaluated" ruling.</item>
        /// <item><b>NOT CONFIGURED</b> when a link in 1-4 genuinely fails, naming the FIRST failing link.</item>
        /// <item><b>BROKEN</b> when the Database Mail queue is KNOWN stopped (is_receive_enabled = 0).
        ///   Ruled 2026-09-09 after gate finding F1: nothing queued from now on leaves the instance, so
        ///   this outranks every link-5 branch below it - a delivery inside the window, an unreadable
        ///   history, and the older failure evidence, which is appended rather than lost. NULL stays
        ///   evidence-only: msdb.sys.service_queues returns ZERO ROWS with no error to a non-sysadmin
        ///   (proved on both rigs), so absence is not proof.</item>
        /// <item><b>COULD NOT READ</b> when links 1-4 hold but link 5 could not be read - including a
        ///   non-sysadmin connection, which sees only its OWN items and would otherwise read as
        ///   "never sent".</item>
        /// <item><b>BROKEN</b> when a failed item or a Database Mail error-log entry exists with no sent item
        ///   more recent than it. NEVER-SENT COUNTS: 20,797 failures and nothing ever delivered is the most
        ///   broken a chain gets, and an "is the failure recent enough" window would have printed UNKNOWN
        ///   over it. No recency bound applies to a failure.</item>
        /// <item><b>WORKS</b> only when a SENT item to this operator's address exists within
        ///   <see cref="WorksWindowDays"/> days of the SERVER's clock.</item>
        /// <item><b>UNKNOWN</b> otherwise: nothing was ever sent or failed, or the only success is older than
        ///   the window with nothing since. A stale success is not evidence that mail works today; it is the
        ///   absence of evidence either way.</item>
        /// </list>
        ///
        /// <para><paramref name="now"/> is used only when the server did not return its own clock. Recency is
        /// otherwise measured against <see cref="MailChainRows.ServerNow"/>, because the rows carry the SQL
        /// Server's local time and the app may run on another machine in another zone.</para>
        /// </summary>
        public static MailChainVerdict Evaluate(MailChainRows rows, DateTime now)
        {
            ArgumentNullException.ThrowIfNull(rows);

            // 1. A read nobody could make, on the connection or behind links 1-4.
            var blindSpot = rows.Gaps.Where(g => g.Link <= 4).OrderBy(g => g.Link).FirstOrDefault();
            if (blindSpot is not null)
                return new MailChainVerdict(MailChainState.CouldNotRead,
                    $"Could not read {blindSpot.Read}.", blindSpot.Reason, blindSpot.Link);

            // 2. Links 1-4, in order. The first failure wins and is named.
            if (rows.DatabaseMailXpsValueInUse != 1)
                return new MailChainVerdict(MailChainState.NotConfigured,
                    "Link 1: Database Mail is not enabled on this instance.",
                    $"sys.configurations 'Database Mail XPs' value_in_use = {Describe(rows.DatabaseMailXpsValueInUse)}.", 1);

            if (!rows.Profiles.Any(p => !string.IsNullOrWhiteSpace(p.AccountName)))
                return new MailChainVerdict(MailChainState.NotConfigured,
                    "Link 2: no Database Mail profile has a mail account attached.",
                    rows.Profiles.Count == 0
                        ? "msdb.dbo.sysmail_profile returned no rows."
                        : $"{rows.Profiles.Count} profile(s) exist and none is joined to an account in sysmail_profileaccount.",
                    2);

            if (rows.AgentUseDatabaseMail != 1)
                return new MailChainVerdict(MailChainState.NotConfigured,
                    "Link 3: SQL Agent's alert system is not using Database Mail.",
                    $"SQLServerAgent\\UseDatabaseMail = {Describe(rows.AgentUseDatabaseMail)}.", 3);

            if (string.IsNullOrWhiteSpace(rows.AgentMailProfile))
                return new MailChainVerdict(MailChainState.NotConfigured,
                    "Link 3: SQL Agent has no Database Mail profile set.",
                    "SQLServerAgent\\DatabaseMailProfile is absent or empty.", 3);

            if (!rows.Profiles.Any(p => string.Equals(p.ProfileName, rows.AgentMailProfile, StringComparison.OrdinalIgnoreCase)))
                return new MailChainVerdict(MailChainState.NotConfigured,
                    $"Link 3: SQL Agent points at a profile that does not exist ('{rows.AgentMailProfile}').",
                    "Profiles on this instance: " + (rows.Profiles.Count == 0
                        ? "(none)"
                        : string.Join(", ", rows.Profiles.Select(p => p.ProfileName)
                                                         .Distinct(StringComparer.OrdinalIgnoreCase))), 3);

            if (rows.Operator is null)
                return new MailChainVerdict(MailChainState.NotConfigured,
                    $"Link 4: operator '{rows.OperatorName}' does not exist on this instance.",
                    "msdb.dbo.sysoperators has no row with that name.", 4);

            if (!rows.Operator.Enabled)
                return new MailChainVerdict(MailChainState.NotConfigured,
                    $"Link 4: operator '{rows.Operator.Name}' is disabled.",
                    "msdb.dbo.sysoperators.enabled = 0, so Agent will not notify it.", 4);

            if (!rows.Operator.HasAddress)
                return new MailChainVerdict(MailChainState.NotConfigured,
                    $"Link 4: operator '{rows.Operator.Name}' has no email address.",
                    "msdb.dbo.sysoperators.email_address is empty.", 4);

            // 3. THE QUEUE, when it is KNOWN stopped. This is a fact about the future - Database Mail
            //    is not receiving, so a notification queued now goes nowhere - which is why it outranks
            //    a delivery that happened in the past and a history nobody could read. The NEW2022 rig runs
            //    in exactly this state and refused the lane's one authorised send for exactly this
            //    reason. bool? and not bool: NULL means the read came back empty, which a non-sysadmin
            //    gets with no error, and a verdict off that would be a fabrication.
            if (rows.MailQueueReceiveEnabled == false)
            {
                var stoppedFailure = Later(rows.LastFailed, rows.LastEventLogError);
                return new MailChainVerdict(MailChainState.Broken,
                    $"Mail to {rows.Operator.EmailAddress} cannot leave this instance: Database Mail's "
                    + "queue is stopped.",
                    QueueStoppedEvidence + (stoppedFailure is null
                        ? ""
                        : " " + DescribeFailure(rows, stoppedFailure.Value)),
                    5) { NamesTheStoppedQueue = true };
            }

            // 4. Links 1-4 hold. Delivery evidence is the only thing that can say WORKS.
            var deliveryGap = rows.Gaps.Where(g => g.Link >= 5).OrderBy(g => g.Link).FirstOrDefault();
            if (deliveryGap is not null)
                return new MailChainVerdict(MailChainState.CouldNotRead,
                    $"Links 1-4 are configured. Could not read {deliveryGap.Read}.", deliveryGap.Reason, 5);

            var lastFailure = Later(rows.LastFailed, rows.LastEventLogError);

            // 5. BROKEN: a failure with nothing delivered after it. Never-sent counts.
            if (lastFailure is not null && (rows.LastSent is null || lastFailure > rows.LastSent))
                return new MailChainVerdict(MailChainState.Broken,
                    $"Mail to {rows.Operator.EmailAddress} is failing.",
                    DescribeFailure(rows, lastFailure.Value), 5);

            var clock = rows.ServerNow ?? now;

            // 6. WORKS: observed delivery, inside the window, on the server's own clock.
            if (rows.LastSent is not null && (clock - rows.LastSent.Value).TotalDays <= WorksWindowDays)
                return new MailChainVerdict(MailChainState.Works,
                    $"Mail was delivered to {rows.Operator.EmailAddress} on {rows.LastSent:yyyy-MM-dd HH:mm}.",
                    $"{rows.SentCount} sent item(s) to this address in msdb.dbo.sysmail_allitems; the most "
                    + $"recent is inside the {WorksWindowDays}-day window.", 5);

            // 7. UNKNOWN: a stale success, or nothing at all.
            if (rows.LastSent is not null)
                return new MailChainVerdict(MailChainState.Unknown,
                    $"The last delivery to {rows.Operator.EmailAddress} was {rows.LastSent:yyyy-MM-dd HH:mm}, more "
                    + $"than {WorksWindowDays} days ago.",
                    "Nothing has been sent or failed since, so this is not evidence either way. Send a test "
                    + "notification to find out.", 5);

            return new MailChainVerdict(MailChainState.Unknown,
                $"Links 1-4 are configured and no notification has ever been sent to {rows.Operator.EmailAddress}.",
                "msdb.dbo.sysmail_allitems holds no sent or failed item for this address. Send a test "
                + "notification to find out.", 5);
        }

        /// <summary>
        /// Adrian's ruling 2, implemented literally and in order: the operator that the most enabled alert
        /// notifications already target; else the ONLY enabled operator with an address; else null, and the
        /// instance cannot arm until a human picks one.
        ///
        /// <para>A TIE on the first branch has no winner under that rule, so it falls through rather than
        /// choosing by id: two operators on 41 alerts each is exactly the case where guessing is worse than
        /// asking. The first branch is NOT filtered by enabled or address either - "which operator does
        /// alerting already target" is a question about the estate, and when that operator is disabled the
        /// mail-chain verdict says so at link 4 instead of the picker quietly choosing somebody else.</para>
        /// </summary>
        public static string? DefaultOperator(OperatorInventory? inventory)
        {
            // An inventory nobody could fully read is not a basis for a default (gate finding F2): the
            // notification counts this rule ranks on are exactly what a denied read leaves empty.
            if (inventory is null || inventory.State != OperatorInventoryState.Loaded) return null;

            var notified = inventory.Operators.Where(o => o.EnabledAlertNotifications > 0).ToList();
            if (notified.Count > 0)
            {
                var top = notified.Max(o => o.EnabledAlertNotifications);
                var winners = notified.Where(o => o.EnabledAlertNotifications == top).ToList();
                if (winners.Count == 1) return winners[0].Name;
            }

            var deliverable = inventory.Operators.Where(o => o.Enabled && o.HasAddress).ToList();
            return deliverable.Count == 1 ? deliverable[0].Name : null;
        }

        // ── THE ONE WRITE ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Sends ONE test notification through Agent's own path and then re-reads link 5 so the page can
        /// show what became of it. Never automatic, never on page load, never in a batch: the caller reaches
        /// this only from a button behind the same run_scripts permission the Apply path uses.
        ///
        /// <para><b>The write path, stated.</b> This is a <c>CommandType.StoredProcedure</c> call with
        /// SqlParameters - byte for byte the shape <see cref="AgentJobControlService"/> uses for
        /// <c>sp_start_job</c> and <c>sp_update_job</c>, the closest existing analogue for writing to an msdb
        /// Agent object. SqlSafetyValidator is NOT on this path, and that is not a bypass: the wall
        /// classifies free-form SQL TEXT arriving from the dashboard, diagnostic and remediation runners
        /// (its five call sites), and neither this call nor the Apply path beside it sends any. The safety
        /// here is structural instead - a fixed procedure name in code, four parameters, and no
        /// caller-supplied text reaching a CommandText at all.</para>
        ///
        /// <para>Every send is audited before this returns, success or failure.</para>
        /// </summary>
        public Task<TestNotificationResult> SendTestNotificationAsync(
            string connectionString, string serverName, string profileName, string operatorName,
            CancellationToken ct = default) =>
            SendTestNotificationWithAsync(
                () => CreateConnection(connectionString),
                c => ReadMailChainAsync(connectionString, serverName, operatorName, c),
                serverName, profileName, operatorName, ct);

        /// <summary>The same send against the server the SINGLE-instance section targets.</summary>
        public Task<TestNotificationResult> SendTestNotificationForCurrentServerAsync(
            string profileName, string operatorName, CancellationToken ct = default) =>
            SendTestNotificationWithAsync(
                () => (DbConnection)_factory.CreateConnection("master"),
                c => ReadMailChainForCurrentServerAsync(operatorName, c),
                serverName: null, profileName, operatorName, ct);

        private async Task<TestNotificationResult> SendTestNotificationWithAsync(
            Func<DbConnection> open,
            Func<CancellationToken, Task<MailChainReport>> recheckAsync,
            string? serverName, string profileName, string operatorName,
            CancellationToken ct)
        {
            string? error = null;
            var accepted = false;

            try
            {
                using var conn = open();
                if (string.IsNullOrWhiteSpace(serverName)) serverName = DescribeTarget(conn);
                await conn.OpenAsync(ct);

                using var cmd = conn.CreateCommand();
                cmd.CommandType = CommandType.StoredProcedure;
                cmd.CommandText = "msdb.dbo.sp_notify_operator";
                cmd.CommandTimeout = CommandTimeoutSeconds;
                AddParam(cmd, "@profile_name", profileName);
                AddParam(cmd, "@name", operatorName);
                AddParam(cmd, "@subject", "SQLTriage test notification");
                AddParam(cmd, "@body",
                    "This is a test notification sent by SQLTriage to confirm that SQL Agent alerts reach "
                    + $"this operator on {Label(serverName)}. No server setting was changed.");

                await cmd.ExecuteNonQueryAsync(ct);
                accepted = true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                error = ex.Message;
                _logger.LogWarning(ex, "Test notification to operator {Operator} on {Server} failed",
                    operatorName, LogAnon.S(serverName));
            }

            _audit?.LogSecurityEvent(
                accepted
                    ? $"Database Mail test notification sent to operator '{operatorName}' on {Label(serverName)}"
                    : $"Database Mail test notification to operator '{operatorName}' on {Label(serverName)} failed",
                accepted ? AuditSeverity.Info : AuditSeverity.Warning,
                new Dictionary<string, string>
                {
                    ["Category"] = TestNotificationAuditCategory,
                    ["Server"] = Label(serverName),
                    ["Operator"] = operatorName,
                    ["Profile"] = profileName,
                    ["Outcome"] = accepted ? "Accepted by sp_notify_operator" : "Failed",
                    ["Error"] = error ?? string.Empty,
                });

            MailChainReport? recheck = null;
            if (accepted)
            {
                // Database Mail QUEUES the item and a separate process delivers it, so an immediate
                // re-read would always show "unsent" and would read as a failure.
                try
                {
                    await Task.Delay(RecheckDelay, ct);
                    recheck = await recheckAsync(ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Re-reading the mail chain after a test send failed");
                }
            }

            return new TestNotificationResult(accepted, error, recheck);
        }

        // ── Plumbing ─────────────────────────────────────────────────────────────────────────

        private DbConnection CreateConnection(string connectionString) =>
            ConnectionFactoryOverride?.Invoke(connectionString) ?? new SqlConnection(connectionString);

        /// <summary>
        /// One read, carrying the denial rule this whole file rests on: ANY failure becomes a named gap and
        /// the rest of the chain is still read. Mirrors AccessSurfaceCollector.GuardedTraceServerReadAsync.
        /// </summary>
        private async Task GuardedReadAsync(
            DbConnection conn, int link, string read, string sql,
            Action<DbCommand>? bind,
            Func<DbDataReader, CancellationToken, Task> onReader,
            List<MailChainGap> gaps, CancellationToken ct)
        {
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.CommandTimeout = CommandTimeoutSeconds;
                bind?.Invoke(cmd);
                using var reader = await cmd.ExecuteReaderAsync(ct);
                await onReader(reader, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Mail-chain read '{Read}' failed", read);
                gaps.Add(new MailChainGap(link, read, ex.Message));
            }
        }

        private static void AddParam(DbCommand cmd, string name, object? value)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        private static AgentOperatorRow ReadOperatorRow(DbDataReader r) => new()
        {
            Id = Convert.ToInt32(r.GetValue(0), CultureInfo.InvariantCulture),
            Name = Str(r, 1),
            Enabled = (IntOrNull(r, 2) ?? 0) != 0,
            EmailAddress = Str(r, 3),
            LastEmailDate = IntOrNull(r, 4) ?? 0,
            LastEmailTime = IntOrNull(r, 5) ?? 0,
        };

        private static int? ResolveAgentProfileId(MailChainRows rows) =>
            rows.Profiles
                .Where(p => string.Equals(p.ProfileName, rows.AgentMailProfile, StringComparison.OrdinalIgnoreCase))
                .Select(p => (int?)p.ProfileId)
                .FirstOrDefault();

        /// <summary>The server a connection points at, read off the connection rather than re-derived.
        /// Empty when the provider does not expose one, which the caller renders as "the current server".</summary>
        private static string DescribeTarget(DbConnection conn)
        {
            try { return conn.DataSource ?? ""; }
            catch (Exception) { return ""; }
        }

        private static string Label(string? serverName) =>
            string.IsNullOrWhiteSpace(serverName) ? "the current server" : serverName!;

        private static DateTime? Later(DateTime? a, DateTime? b) =>
            a is null ? b : b is null ? a : (a > b ? a : b);

        private static string Describe(int? value) =>
            value?.ToString(CultureInfo.InvariantCulture) ?? "not readable";

        private static string DescribeFailure(MailChainRows rows, DateTime lastFailure)
        {
            var parts = new List<string>
            {
                $"Last failure {lastFailure:yyyy-MM-dd HH:mm}",
                rows.LastSent is null
                    ? "no item has EVER been delivered to this address"
                    : $"the last delivery was {rows.LastSent:yyyy-MM-dd HH:mm}, before it",
                $"{rows.FailedCount} failed item(s), {rows.SentCount} sent",
            };

            if (!string.IsNullOrWhiteSpace(rows.LastEventLogMessage))
                parts.Add(rows.LastEventLogMessage!.Trim());

            return string.Join(". ", parts) + ".";
        }

        private static string Str(DbDataReader r, int i) =>
            r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "";

        private static int? IntOrNull(DbDataReader r, int i) =>
            r.IsDBNull(i) ? null : Convert.ToInt32(r.GetValue(i), CultureInfo.InvariantCulture);

        private static DateTime? DateOrNull(DbDataReader r, int i) =>
            r.IsDBNull(i) ? null : Convert.ToDateTime(r.GetValue(i), CultureInfo.InvariantCulture);
    }
}
