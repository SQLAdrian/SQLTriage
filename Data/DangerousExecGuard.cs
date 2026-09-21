/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SQLTriage.Data
{
    /// <summary>
    /// A NARROW, per-statement gate for the OS/system-exec class of T-SQL, applied to the
    /// operator-authored free-form surfaces (scheduled <c>SqlQuery</c> tasks and the /query
    /// executor) that deliberately do NOT go through <see cref="SqlSafetyValidator"/>'s
    /// read-only wall.
    ///
    /// <para><b>Why this is separate code, not a change to <see cref="SqlSafetyValidator"/>.</b>
    /// The shared wall is B2-calibrated for the SHIPPED diagnostic corpus: it exempts a whole
    /// batch that contains a <c>SELECT … FROM sys.*</c> read, because sp_Blitz / sp_triage /
    /// usp_bpcheck genuinely execute <c>xp_cmdshell</c> and toggle <c>sp_configure</c>, and there
    /// is no syntactic way to tell a trusted diagnostic from a malicious payload — so the wall
    /// trusts by source. That batch waiver is exactly what an attacker abuses here: prepend one
    /// <c>SELECT 1 FROM sys.databases</c> and the wall waives a trailing
    /// <c>EXEC xp_cmdshell</c>. The wall's own note prescribes the fix — "If an untrusted-input
    /// path is ever added, gate IT specifically — do not tighten this shared wall." This guard is
    /// that specific gate.</para>
    ///
    /// <para><b>Narrow by design.</b> It blocks ONLY OS/system-exec, OLE automation, registry
    /// writes/deletes/enumerates (pure registry reads pass), filesystem enumeration
    /// (<c>xp_dirtree</c> / <c>xp_subdirs</c> / <c>xp_fileexist</c> / <c>xp_getfiledetails</c>),
    /// CLR-loading, and the <c>sp_configure</c> toggles that enable those. It deliberately does
    /// NOT block maintenance DDL/DML — BACKUP, DBCC CHECKDB, TRUNCATE, index DDL, UPDATE
    /// STATISTICS, plain DML, Agent-job DDL, and the shipped Ola <c>EXEC dbo.IndexOptimize</c> /
    /// <c>DatabaseBackup</c> paths all PASS. The operator surfaces exist so a DBA can run those.</para>
    ///
    /// <para><b>Shielding-immune, and here is exactly why.</b> The batch is split into statements on
    /// <c>;</c> and standalone <c>GO</c>, and each chunk is tested independently. But T-SQL does NOT
    /// require a semicolon, so a chunk can still hold several statements — <c>SELECT 1\nDELETE FROM t</c>
    /// is ONE chunk. The immunity therefore does NOT come from the splitting; it comes from every rule
    /// being POSITION-INDEPENDENT: each pattern matches its keyword wherever it appears in the chunk,
    /// so a leading benign statement (a <c>sys.*</c> read included) cannot shield a trailing dangerous
    /// one. No RULE in this file may be anchored at <c>^</c>. The single deliberate exception is
    /// <c>GoSeparator</c>, which is part of the SPLITTER rather than a rule: a batch separator must
    /// occupy a whole line, so it is anchored on purpose.
    /// <para>MEASURED 2026-09-07, fix round 2: the first cut of the destructive class anchored the
    /// unqualified-DELETE and GRANT/DENY/REVOKE rules at <c>^[\s;]*</c>, and a cold gate proved live on
    /// <c>.\new2022</c> that <c>SELECT 1\nDELETE FROM dbo.Orders</c> passed the guard, reached the server
    /// through a scheduled task and emptied the table. The anchoring was there to stop the bare word
    /// GRANT in a permissions READ from tripping the rule; that job is now done by a lookbehind that
    /// excludes a bracketed/quoted/qualified occurrence, which is position-independent and keeps the
    /// immunity. The DELETE rule's WHERE test is likewise bounded to the DELETE's OWN statement extent,
    /// so neither a preceding nor a following statement's WHERE can shield it.</para></para>
    ///
    /// <para><b>Widened 2026-09-07 (lane exec-guard-ddl-class), by surface.</b> The paragraph above
    /// describes the <see cref="ExecSurfacePolicy.OsExecOnly"/> policy, which is still the default and
    /// still exactly what every attended caller gets. A caller may now ALSO ask for the DESTRUCTIVE
    /// class (DROP / TRUNCATE / DELETE with no WHERE clause / MERGE ... NOT MATCHED BY SOURCE THEN DELETE /
    /// ALTER TABLE ... DROP COLUMN / ALTER DATABASE / ALTER LOGIN|ROLE / GRANT|DENY|REVOKE / RESTORE /
    /// BACKUP to a UNC path or a URL / SHUTDOWN / KILL / BULK INSERT / sp_rename / DISABLE TRIGGER /
    /// REPAIR_ALLOW_DATA_LOSS / role-member changes) and the DYNAMIC-EXEC class
    /// (<c>sp_executesql</c>, <c>EXEC(…)</c>, <c>EXEC @var</c>, <c>sp_MSforeachtable/db</c>). Those two are ON for the scheduled-task
    /// surfaces, which execute on a timer with nobody watching, and OFF everywhere else — see
    /// <see cref="ExecSurfacePolicy"/> for the measured reason.</para>
    /// </summary>
    /// <summary>
    /// Which classes of statement a surface refuses. The default, <see cref="OsExecOnly"/>, is the
    /// guard's original 2026-08-31 behaviour and is what every ATTENDED caller keeps: a human typed
    /// the statement, clicked a button, and is looking at the result.
    ///
    /// <para><b>Why this is scoped per surface and not switched on globally</b> (ruled and MEASURED
    /// 2026-09-07). The same <see cref="DangerousExecGuard.Inspect(string?)"/> is shared by six call
    /// sites, and two of them legitimately carry the very statements this widening blocks:
    /// the remediation render/apply path renders <c>DROP INDEX</c> (RemediationOpRenderer.cs:252),
    /// <c>ALTER DATABASE</c> (RemediationOpRenderer.cs:610), <c>DROP TABLE</c>/<c>DROP PROCEDURE</c>
    /// (MaintenanceSolutionOpRenderer.cs:324/329) — that is its entire purpose, under a preview, an operator
    /// confirmation and a rendered inverse; and the SHIPPED dashboard panels build per-database
    /// dynamic SQL with <c>EXEC(@sql)</c> and <c>EXEC sp_executesql @sql</c> (Config/dashboard-config.json,
    /// eleven statements measured at ac694cd). Turning these classes on globally would refuse the
    /// remediation feature and drop shipped panels out of the executable cache. So the widening is
    /// applied where the ruling's evidence sits: the UNATTENDED scheduled-task surfaces.</para>
    ///
    /// <para>CITATION CORRECTED 2026-09-07 (fix round 2): an earlier version of this comment also cited
    /// <c>BACKUP DATABASE</c> (BackupCheckDbOpRenderer.cs:382) as a render a global switch would refuse.
    /// It would not, in that render's ordinary form: the site emits
    /// <c>BACKUP DATABASE {db} TO DISK = {pathLiteral} WITH …</c> with a LOCAL path, and a local-path
    /// backup is deliberately ALLOWED (only a UNC path or a <c>TO URL</c> destination is refused). The
    /// scoping conclusion is unchanged — it rests on the three remaining renders and on 11 of 11 shipped
    /// dashboard panels — but the claim is now no wider than the evidence.</para>
    /// </summary>
    [System.Flags]
    public enum ExecSurfacePolicy
    {
        /// <summary>The original narrow class only: OS/system-exec, OLE, registry, CLR, config toggles.</summary>
        OsExecOnly = 0,

        /// <summary>Adds the destructive DDL/DML/permission class: DROP, TRUNCATE, DELETE with no WHERE
        /// clause, MERGE … WHEN NOT MATCHED BY SOURCE THEN DELETE, ALTER TABLE … DROP COLUMN,
        /// ALTER DATABASE/LOGIN/ROLE, GRANT/DENY/REVOKE, role-member changes, RESTORE, BACKUP to a UNC
        /// path or a URL, SHUTDOWN, KILL, BULK INSERT, sp_rename, DISABLE TRIGGER and
        /// REPAIR_ALLOW_DATA_LOSS. A DELETE or MERGE that DOES carry a row-restricting clause is allowed:
        /// the guard refuses the unrestricted whole-table forms and does not evaluate predicates.</summary>
        Destructive = 1,

        /// <summary>Adds run-time-built SQL (<c>sp_executesql</c>, <c>EXEC(…)</c>, <c>EXEC @var</c>,
        /// <c>sp_MSforeachtable</c>/<c>sp_MSforeachdb</c>), which no static classifier can see through.</summary>
        DynamicExec = 2,

        /// <summary>A surface that executes with nobody watching: both extra classes.</summary>
        Unattended = Destructive | DynamicExec
    }

    public static class DangerousExecGuard
    {
        // Identifier / execution patterns. Tested against a copy of each statement whose STRING
        // LITERAL CONTENTS have been blanked, so a name that appears only as data inside a string
        // (a read/search such as `… WHERE definition LIKE '%xp_cmdshell%'`) does NOT trip them —
        // only an actual identifier/exec of the dangerous proc does.
        private static readonly List<(Regex Pattern, string Reason)> ExecPatterns = new()
        {
            // xp_cmdshell — direct OS command execution. Never a maintenance operation.
            (new Regex(@"\bxp_cmdshell\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "xp_cmdshell (OS command execution) is not permitted on this surface"),

            // OLE Automation (sp_OA*) — instantiates and drives arbitrary COM objects, an OS-reach
            // primitive equivalent to xp_cmdshell. Covers the ruled five entry points.
            (new Regex(@"\bsp_OA(Create|Method|GetProperty|SetProperty|Destroy)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "OLE Automation (sp_OACreate/OAMethod/OAGetProperty/OASetProperty/OADestroy) is not permitted on this surface"),

            // Registry STATE-CHANGE — xp_reg* / xp_instance_reg* WRITES, DELETES, and ENUMERATES.
            // Narrowed (fix round 1, 2026-08-31): pure registry READS (xp_regread /
            // xp_instance_regread) are information retrieval, not OS state-change or code execution, and
            // a shipped read-only telemetry panel legitimately calls xp_instance_regread to DISPLAY host
            // configuration (Config/dashboard-config.json panel security.failed_logins_1h reads the
            // MSSQLServer AuditLevel before counting failed logins). Blocking those reads dropped a
            // shipped panel from the cache for no security gain. Only the verbs that MUTATE or ENUMERATE
            // the host registry stay blocked, and the blocked verbs are enumerated EXPLICITLY — clearer
            // and safer than a negative lookahead that means "everything except read".
            (new Regex(@"\bxp_(instance_)?reg(write|deletevalue|deletekey|enumvalues|enumkeys|addmultistring|removemultistring)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "Registry write/delete/enumerate (xp_regwrite / xp_regdelete* / xp_regenum* / xp_reg*multistring) is not permitted on this surface; pure reads (xp_regread / xp_instance_regread) are allowed"),

            // Filesystem enumeration — the undocumented extended procs that reach the host OS
            // filesystem: xp_dirtree walks an arbitrary directory tree, xp_subdirs lists
            // subdirectories, xp_fileexist probes for a path, xp_getfiledetails returns a file's
            // metadata. They are an OS-filesystem-reach primitive (recon / path-disclosure), not a
            // SQL operation, so they belong to the blocked OS/system-exec class this guard gates.
            // Added fix round 2 (2026-08-31, on Adrian's ruling). The blocked procs are enumerated
            // EXPLICITLY in the alternation (clear + safe) rather than a broad xp_* prefix. This
            // does NOT touch the product's own backup/restore file handling: RestoreVerify reads
            // media paths from msdb (backupset ⋈ backupmediafamily) and the Ola solution's internal
            // xp_dirtree/xp_fileexist calls live in the installed proc bodies on the server (reached
            // via `EXEC dbo.DatabaseBackup`, which passes) — neither submits these tokens as
            // free-form text through this gated SqlQuery surface.
            (new Regex(@"\bxp_(dirtree|subdirs|fileexist|getfiledetails)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "Filesystem enumeration (xp_dirtree / xp_subdirs / xp_fileexist / xp_getfiledetails) is not permitted on this surface"),

            // CLR assembly loading — loads .NET code into the engine, an arbitrary-code-execution
            // primitive. CREATE/ALTER ASSEMBLY and sp_add_trusted_assembly (which whitelists an
            // assembly hash so an untrusted one can load).
            (new Regex(@"\bCREATE\s+ASSEMBLY\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "CREATE ASSEMBLY (CLR code loading) is not permitted on this surface"),
            (new Regex(@"\bALTER\s+ASSEMBLY\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "ALTER ASSEMBLY (CLR code loading) is not permitted on this surface"),
            (new Regex(@"\bsp_add_trusted_assembly\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "sp_add_trusted_assembly (CLR trust) is not permitted on this surface"),
        };

        // sp_configure toggle patterns. Tested against each statement with STRING LITERALS INTACT,
        // because the payload is the option NAME carried in a string literal
        // (`sp_configure 'xp_cmdshell', 1`). Only the four options that ENABLE the OS/CLR-exec
        // classes are blocked — sp_configure of a maintenance option (max degree of parallelism,
        // show advanced options, cost threshold, …) PASSES. Blocking the setter statement blocks
        // the batch, so the RECONFIGURE that would enable it never runs; a bare RECONFIGURE is
        // left alone because maintenance scripts use it after benign sp_configure calls.
        private static readonly List<(Regex Pattern, string Reason)> ConfigTogglePatterns = new()
        {
            (new Regex(@"\bsp_configure\b[^']*'\s*xp_cmdshell\s*'", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "sp_configure toggling 'xp_cmdshell' is not permitted on this surface"),
            (new Regex(@"\bsp_configure\b[^']*'\s*Ole\s+Automation\s+Procedures\s*'", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "sp_configure toggling 'Ole Automation Procedures' is not permitted on this surface"),
            (new Regex(@"\bsp_configure\b[^']*'\s*clr\s+enabled\s*'", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "sp_configure toggling 'clr enabled' is not permitted on this surface"),
            (new Regex(@"\bsp_configure\b[^']*'\s*clr\s+strict\s+security\s*'", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "sp_configure toggling 'clr strict security' is not permitted on this surface"),
        };

        // ── DESTRUCTIVE class (added 2026-09-07, lane exec-guard-ddl-class) ─────────────────────
        // Applied ONLY when the caller passes ExecSurfacePolicy.Destructive. Tested against the
        // literals-blanked copy, exactly like ExecPatterns, so a destructive word that appears only
        // as string DATA (`… WHERE definition LIKE '%DROP TABLE%'`) never trips them.
        //
        // WHY THIS CLASS EXISTS: proved live 2026-09-06 — a scheduled task carrying
        // `DROP TABLE dbo.FreshEyesGateProbe_DoesNotExist;` REACHED .\new2022 and executed there
        // (the server returned the object-not-found error, so the statement left the app), while
        // `SELECT 1 FROM sys.databases; EXEC master..xp_cmdshell 'dir';` was stopped before contact.
        // The guard had no destructive class at all.
        //
        // CALIBRATED AGAINST THE MAINTENANCE LIST the surfaces exist to run — every one of these
        // still PASSES and is pinned by test: UPDATE STATISTICS, sp_updatestats,
        // ALTER INDEX … REBUILD/REORGANIZE, DBCC CHECKDB, EXEC sp_Blitz, SELECT … FROM sys.tables,
        // CREATE INDEX (including WITH (DROP_EXISTING = ON)), ALTER TABLE … DROP CONSTRAINT/COLUMN,
        // BACKUP to a LOCAL disk path, and every drop of a #temp table or @table variable.
        private static readonly List<(Regex Pattern, string Reason)> DestructivePatterns = new()
        {
            (new Regex(@"\bDROP\s+DATABASE\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "DROP DATABASE (destructive DDL) is not permitted on an unattended surface"),

            // Temp tables (#t, ##t) and table variables are session-scoped scratch that maintenance
            // scripts drop constantly — exempted by the lookahead, which is why this is its own entry.
            // The temp-table exemption is written as ONE negative lookahead that spans the optional
            // IF EXISTS, not as an optional group followed by a lookahead. MEASURED 2026-09-07: the
            // obvious form `(?:IF\s+EXISTS\s+)?(?![#@])` is defeated by its own optionality — the
            // engine drops the group to zero width, matches `DROP TABLE ` against the text "IF",
            // passes the lookahead on the 'I', and blocks `DROP TABLE IF EXISTS #staging`. An atomic
            // group does not fix it either: (?>…)? still backtracks the QUANTIFIER to zero. Pinned by
            // MaintenanceStatements_StillPass_UnderTheUnattendedPolicy.
            // The `\[?` inside the lookahead is the fix for a LOW defect measured 2026-09-07: the
            // lookahead inspects the character after the whitespace, and for a bracket-quoted scratch
            // table that character is '[', not '#', so `DROP TABLE [#staging]` was refused while
            // `DROP TABLE #staging` passed. A maintenance script that brackets its scratch names is
            // legitimate. Pinned by MaintenanceStatements_StillPass_UnderTheUnattendedPolicy.
            (new Regex(@"\bDROP\s+TABLE\s+(?!(?:IF\s+EXISTS\s+)?\[?[#@])", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "DROP TABLE (destructive DDL) is not permitted on an unattended surface"),

            (new Regex(@"\bDROP\s+(?:INDEX|PROCEDURE|PROC|VIEW|FUNCTION|TRIGGER|SCHEMA|SEQUENCE|SYNONYM)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "DROP of a database object (destructive DDL) is not permitted on an unattended surface"),

            // CERTIFICATE / ASYMMETRIC KEY / SYMMETRIC KEY / MASTER KEY added fix round 2: dropping the
            // certificate that protects a TDE database makes every existing backup of it unrestorable,
            // which is the most irreversible thing in this list. Measured allowed before the fix.
            (new Regex(@"\bDROP\s+(?:LOGIN|USER|SERVER\s+ROLE|ROLE|CREDENTIAL|CERTIFICATE|ASYMMETRIC\s+KEY|SYMMETRIC\s+KEY|MASTER\s+KEY)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "DROP of a login, user, role or key (destructive security DDL) is not permitted on an unattended surface"),

            (new Regex(@"\bTRUNCATE\s+TABLE\s+(?!\[?[#@])", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "TRUNCATE TABLE (destructive DML) is not permitted on an unattended surface"),

            // ALTER TABLE … DROP COLUMN destroys a column of data and is irreversible without a restore.
            // It was on this guard's own calibrated PASS list until fix round 2, where the gate pointed
            // out that the calibration was inherited from the OsExecOnly class: an operator watching a
            // /query result can undo a mistake, an unattended timer cannot. ALTER TABLE … DROP CONSTRAINT
            // stays allowed — it removes a rule, not data — and is pinned by test.
            (new Regex(@"\bALTER\s+TABLE\b[\s\S]{0,400}?\bDROP\s+COLUMN\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "ALTER TABLE ... DROP COLUMN (destroys a column of data) is not permitted on an unattended surface"),

            // MERGE … WHEN NOT MATCHED BY SOURCE THEN DELETE removes every target row absent from the
            // source, so `USING (SELECT 1) s ON 1=0 … NOT MATCHED BY SOURCE THEN DELETE` empties the
            // table. Proved live 2026-09-07: 3 rows -> 0 through a scheduled task. A MERGE whose only
            // delete action is WHEN MATCHED is row-targeted by the join and is deliberately allowed,
            // on the same rule as a DELETE that carries a WHERE.
            (new Regex(@"\bMERGE\b[\s\S]{0,4000}?\bWHEN\s+NOT\s+MATCHED\s+BY\s+SOURCE\b[\s\S]{0,200}?\bTHEN\s+DELETE\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "MERGE ... WHEN NOT MATCHED BY SOURCE THEN DELETE (whole-table delete) is not permitted on an unattended surface"),

            // SHUTDOWN stops the instance. There is no maintenance reading of it, and on a timer with
            // nobody watching it is an outage. Never executed to prove this — refused at the guard.
            (new Regex(@"\bSHUTDOWN\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "SHUTDOWN (stops the SQL Server instance) is not permitted on an unattended surface"),

            // KILL terminates another session's work and rolls it back. Shaped so it only fires on the
            // statement (a spid, a variable, a unit-of-work GUID literal, or KILL STATS JOB) and not on
            // the word appearing as an identifier part.
            (new Regex(@"\bKILL\s+(?:\d|@|N?'|STATS\s+JOB\b|UOW\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "KILL (terminates another session) is not permitted on an unattended surface"),

            // BULK INSERT / OPENROWSET(BULK …) reads an arbitrary host or UNC file into the database.
            // Blocked for ANY path, not just a UNC one: the file is chosen by whoever authored the task
            // and there is no operator at the tick to confirm what is being loaded. This is the same
            // filesystem-reach class as xp_dirtree, plus an uncontrolled write into a table.
            (new Regex(@"\bBULK\s+INSERT\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "BULK INSERT (loads a host or network file into a table) is not permitted on an unattended surface"),
            (new Regex(@"\bOPENROWSET\s*\(\s*BULK\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "OPENROWSET(BULK ...) (reads a host or network file) is not permitted on an unattended surface"),

            // sp_rename makes an object vanish under the name every dependent uses, with no error at
            // rename time. On an unattended timer the breakage surfaces somewhere else, later.
            (new Regex(@"\bsp_rename\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "sp_rename (renames an object out from under its dependents) is not permitted on an unattended surface"),

            // DISABLE TRIGGER turns off audit and integrity triggers. ENABLE TRIGGER is deliberately
            // NOT blocked — restoring a control is not a destructive act.
            (new Regex(@"\bDISABLE\s+TRIGGER\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "DISABLE TRIGGER (defeats audit and integrity triggers) is not permitted on an unattended surface"),

            // The one DBCC option that destroys user data. Plain DBCC CHECKDB, CHECKTABLE, SHRINKFILE
            // and SHRINKDATABASE all still pass — see the disclosure list in the fix report.
            (new Regex(@"\bREPAIR_ALLOW_DATA_LOSS\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "DBCC ... REPAIR_ALLOW_DATA_LOSS (repairs by discarding data) is not permitted on an unattended surface"),

            // RESTORE DATABASE/LOG overwrites a live database from a file. The read-only members of the
            // RESTORE family — VERIFYONLY, HEADERONLY, FILELISTONLY, LABELONLY, REWINDONLY — are the ones
            // the product's own restore-verification uses and they are not matched here.
            (new Regex(@"\bRESTORE\s+(?:DATABASE|LOG)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "RESTORE DATABASE/LOG (overwrites a live database) is not permitted on an unattended surface"),

            // ALTER INDEX / TABLE / PROCEDURE / VIEW / FUNCTION are deliberately ABSENT: those are
            // the maintenance operations these surfaces exist to run.
            (new Regex(@"\bALTER\s+DATABASE\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "ALTER DATABASE (database state change) is not permitted on an unattended surface"),

            (new Regex(@"\bALTER\s+(?:SERVER\s+(?:ROLE|AUDIT|CONFIGURATION)|ROLE|LOGIN|CREDENTIAL)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "ALTER of a login, role, credential or server audit (privilege change) is not permitted on an unattended surface"),

            // GRANT / DENY / REVOKE. NOT anchored — anchoring at `^[\s;]*` was the hole a cold gate
            // proved on 2026-09-07: T-SQL does not require a semicolon, so `SELECT 1\nGRANT CONTROL
            // SERVER TO [x]` is one chunk and the anchor never reached the GRANT. What the anchor was
            // protecting against — the bare word GRANT in a permissions READ — is now handled by a
            // LOOKBEHIND instead, which is position-independent. GRANT, DENY and REVOKE are RESERVED
            // words in T-SQL, so outside a string literal (blanked before matching) and outside a
            // comment (removed by the splitter) they can only be a bracketed/quoted identifier or the
            // statement keyword itself. The lookbehind rules out the identifier forms — `[Grant]`,
            // "Grant", schema.Grant, @Grant, #Grant — and everything left is the statement.
            (new Regex(@"(?<![\[""\w.@#])(?:GRANT|DENY|REVOKE)\s", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "GRANT/DENY/REVOKE (permission change) is not permitted on an unattended surface"),

            (new Regex(@"\bsp_addsrvrolemember\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "sp_addsrvrolemember (server role membership change) is not permitted on an unattended surface"),
            (new Regex(@"\bsp_addrolemember\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "sp_addrolemember (database role membership change) is not permitted on an unattended surface"),
            (new Regex(@"\bsp_dropsrvrolemember\b|\bsp_droprolemember\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "sp_droprolemember/sp_dropsrvrolemember (role membership change) is not permitted on an unattended surface"),
        };

        // Destructive patterns whose PAYLOAD LIVES IN A STRING LITERAL, so they must be matched
        // against the RAW statement (the same reason ConfigTogglePatterns are). BACKUP … TO DISK =
        // '\\host\share\x.bak' writes a full copy of the database to a network location; blanking
        // literals first would erase the path and the pattern could never see it. A backup to a
        // LOCAL path is ordinary maintenance and passes.
        private static readonly List<(Regex Pattern, string Reason)> DestructiveRawPatterns = new()
        {
            (new Regex(@"\bBACKUP\s+(?:DATABASE|LOG)\b[\s\S]*?\bDISK\s*=\s*N?'\s*\\\\", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "BACKUP to a UNC network path (data egress) is not permitted on an unattended surface"),

            // BACKUP … TO URL writes the database to Azure Blob Storage — the SAME egress as the UNC
            // rule above with a wider blast radius, because the destination is off-premises and needs
            // only a credential the task itself can name. Measured ALLOWED before fix round 2 while
            // the UNC form was refused, which made the rule's stated purpose ("data egress") untrue.
            // TO URL is matched on the keyword, not on the URL's contents, so no host list is implied.
            (new Regex(@"\bBACKUP\s+(?:DATABASE|LOG)\b[\s\S]*?\bURL\s*=\s*N?'", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "BACKUP to a URL (off-premises data egress) is not permitted on an unattended surface"),
        };

        // ── DELETE WITH NO WHERE CLAUSE ─────────────────────────────────────────────────────────
        // WHAT THIS RULE CLAIMS, EXACTLY: a DELETE that carries no WHERE clause of its own is refused.
        // It does NOT claim to refuse every statement that deletes every row. `DELETE FROM t WHERE 1=1`
        // and `DELETE FROM t WHERE id IN (SELECT id FROM t)` both empty the table and both PASS, and
        // that is disclosed here, in the reason string, and in the release note. Deciding whether an
        // arbitrary predicate restricts any row means evaluating SQL, which a static classifier cannot
        // do; the alternative — refusing every DELETE — would stop the ordinary nightly purge task
        // (`DELETE TOP (1000) … WHERE Created < DATEADD(day,-90,…)`) that these surfaces exist to run.
        // Narrow and true was chosen over broad and false (ruled fix round 2, 2026-09-07).
        //
        // NOT ANCHORED, for the reason given on the GRANT rule. DELETE is a T-SQL reserved word, so the
        // lookbehind only has to rule out the places the word legitimately appears inside ANOTHER
        // statement: `ON DELETE CASCADE`, `INSTEAD OF DELETE`, `FOR DELETE`, `AFTER DELETE`,
        // `WHEN MATCHED THEN DELETE`, and the permission name in `GRANT SELECT, DELETE ON …`. .NET
        // allows a variable-length lookbehind, which is what makes that list expressible.
        private static readonly Regex DeleteStatement =
            new(@"(?<![\[""\w.@#])(?<!\b(?:ON|THEN|OF|FOR|AFTER|INSERT|UPDATE|SELECT|REFERENCES|EXECUTE|ALTER|CONTROL|GRANT|DENY|REVOKE|,)\s{1,20})DELETE\b(?:\s+TOP\s*\([^)]*\)(?:\s+PERCENT)?)?\s+(?:FROM\s+)?(?<target>[^\s;(,]+)",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex WhereClause = new(@"\bWHERE\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // The keywords that can only begin a NEW statement. Used to bound one DELETE's extent so that
        // neither a preceding nor a following statement's WHERE can shield it — the old code tested
        // `WHERE` against the whole chunk. Only occurrences at parenthesis depth 0 end the extent, so a
        // subquery inside the DELETE's own WHERE (`WHERE id IN (SELECT …)`) does not truncate it.
        private static readonly Regex StatementStartKeyword =
            new(@"\b(?:SELECT|INSERT|UPDATE|DELETE|MERGE|EXEC|EXECUTE|DECLARE|CREATE|ALTER|DROP|TRUNCATE|BACKUP|RESTORE|GRANT|DENY|REVOKE|PRINT|WAITFOR|USE|BEGIN|COMMIT|ROLLBACK)\b",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // ── DYNAMIC-EXEC class (added 2026-09-07, same lane) ────────────────────────────────────
        // Applied ONLY when the caller passes ExecSurfacePolicy.DynamicExec. Run-time-built SQL is
        // the general defeat of every pattern above it: `EXEC('DR' + 'OP TABLE x')` carries no DROP
        // token for any static matcher to find. This class does not try to see through the
        // construction — it refuses the construction itself, which is the only honest answer a
        // static classifier can give. Matched against the literals-blanked copy, so
        // `EXEC('DR'+'OP TABLE x')` reduces to `EXEC(''+'')` and STILL matches on the EXEC( shape.
        private static readonly List<(Regex Pattern, string Reason)> DynamicExecPatterns = new()
        {
            (new Regex(@"\bsp_executesql\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "sp_executesql (SQL built at run time) is not permitted on an unattended surface"),
            (new Regex(@"\bEXEC(?:UTE)?\s*\(", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "EXEC of a constructed string (SQL built at run time) is not permitted on an unattended surface"),
            // `EXEC @var` — the executed MODULE is a variable, so the text is built at run time.
            // The `(?!\s*=)` tail is a FALSE-POSITIVE fix from round 2: `EXEC @rc = dbo.usp_Nightly`
            // is ordinary return-code capture, not dynamic SQL — the thing executed there is the NAME
            // after the `=`. Measured: the old pattern blocked it and told the customer their nightly
            // task was "SQL built at run time", which was untrue. The optional `@\w+\s*=\s*` prefix
            // keeps `EXEC @rc = @sql` blocked, because THERE the executed module really is a variable.
            (new Regex(@"\bEXEC(?:UTE)?\s+(?:@\w+\s*=\s*)?@\w+\b(?!\s*=)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "EXEC @variable (SQL built at run time) is not permitted on an unattended surface"),

            // sp_MSforeachtable / sp_MSforeachdb take a command STRING and run it against every table
            // or database, substituting the object for `?`. That is exactly "SQL built at run time",
            // and it defeated this whole class before fix round 2: the payload is a string argument, so
            // BlankStringLiterals empties it and none of the three patterns above can see it. One
            // statement — `EXEC sp_MSforeachtable 'DELETE FROM ?'` — is arbitrary DML against the
            // entire database. Matched on the PROC NAME, which survives literal blanking.
            (new Regex(@"\bsp_MSforeach(?:table|db)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "sp_MSforeachtable/sp_MSforeachdb (runs a command string against every object) is not permitted on an unattended surface"),
        };

        // A line that is a batch separator: GO alone (optionally 'GO <count>').
        private static readonly Regex GoSeparator = new(@"^\s*GO(\s+\d+)?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Inspects a SQL batch and returns a verdict. A batch is blocked if ANY of its statements
        /// contains a dangerous OS/system-exec pattern. Empty/whitespace input is allowed (nothing
        /// to run). Never throws.
        /// </summary>
        public static DangerousExecVerdict Inspect(string? sql) => Inspect(sql, ExecSurfacePolicy.OsExecOnly);

        /// <summary>
        /// Inspects a SQL batch under an explicit surface policy. <see cref="ExecSurfacePolicy.OsExecOnly"/>
        /// is byte-for-byte the behaviour of the single-argument overload; the other flags ADD classes and
        /// never remove one. Empty/whitespace input is allowed (nothing to run). Never throws.
        /// </summary>
        public static DangerousExecVerdict Inspect(string? sql, ExecSurfacePolicy policy)
        {
            if (string.IsNullOrWhiteSpace(sql))
                return DangerousExecVerdict.Allowed();

            var destructive = (policy & ExecSurfacePolicy.Destructive) != 0;
            var dynamicExec = (policy & ExecSurfacePolicy.DynamicExec) != 0;

            foreach (var statement in SplitStatements(sql))
            {
                if (string.IsNullOrWhiteSpace(statement))
                    continue;

                // Config-toggle: the dangerous option name lives in a string literal, so match the
                // statement with its literals intact.
                foreach (var (pattern, reason) in ConfigTogglePatterns)
                {
                    if (pattern.IsMatch(statement))
                        return DangerousExecVerdict.Blocked(reason, pattern.ToString());
                }

                // Destructive patterns whose payload is inside a literal (BACKUP … TO DISK = UNC) —
                // same reason as the config toggles: match with literals intact.
                if (destructive)
                {
                    foreach (var (pattern, reason) in DestructiveRawPatterns)
                    {
                        if (pattern.IsMatch(statement))
                            return DangerousExecVerdict.Blocked(reason, pattern.ToString());
                    }
                }

                // Identifier/exec: match against a copy with string-literal contents blanked, so a
                // bare textual mention inside a string (a read) does not trip the guard.
                var literalsBlanked = BlankStringLiterals(statement);
                foreach (var (pattern, reason) in ExecPatterns)
                {
                    if (pattern.IsMatch(literalsBlanked))
                        return DangerousExecVerdict.Blocked(reason, pattern.ToString());
                }

                if (destructive)
                {
                    foreach (var (pattern, reason) in DestructivePatterns)
                    {
                        if (pattern.IsMatch(literalsBlanked))
                            return DangerousExecVerdict.Blocked(reason, pattern.ToString());
                    }

                    if (IsUnqualifiedDelete(literalsBlanked))
                    {
                        return DangerousExecVerdict.Blocked(
                            "DELETE with no WHERE clause is not permitted on an unattended surface; " +
                            "add a WHERE clause that restricts the rows (the clause itself is not evaluated)",
                            DeleteStatement.ToString());
                    }
                }

                if (dynamicExec)
                {
                    foreach (var (pattern, reason) in DynamicExecPatterns)
                    {
                        if (pattern.IsMatch(literalsBlanked))
                            return DangerousExecVerdict.Blocked(reason, pattern.ToString());
                    }
                }
            }

            return DangerousExecVerdict.Allowed();
        }

        /// <summary>
        /// True when the chunk contains a DELETE against a persistent table that carries no WHERE
        /// clause OF ITS OWN. A #temp table or @table variable target is scratch and is exempt.
        ///
        /// <para>EVERY DELETE in the chunk is examined, not just the first, because a chunk can hold
        /// several statements (T-SQL does not require semicolons). Each one's WHERE is looked for only
        /// within that DELETE's own extent, so a neighbouring statement's WHERE cannot shield it —
        /// the round-1 code tested <c>WHERE</c> against the whole chunk and both
        /// <c>SELECT … WHERE x=1\nDELETE FROM t</c> and <c>DELETE FROM t\nSELECT … WHERE x=1</c>
        /// would have passed.</para>
        /// </summary>
        private static bool IsUnqualifiedDelete(string literalsBlanked)
        {
            for (var m = DeleteStatement.Match(literalsBlanked); m.Success; m = m.NextMatch())
            {
                var target = m.Groups["target"].Value.TrimStart('[', '"');
                if (target.Length > 0 && (target[0] == '#' || target[0] == '@'))
                    continue;

                var from = m.Index + m.Length;
                var to = StatementExtentEnd(literalsBlanked, from);
                var where = WhereClause.Match(literalsBlanked, from, to - from);
                if (!where.Success)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Returns the index at which the statement that starts at <paramref name="from"/> ends: the
        /// first statement-leading keyword at parenthesis depth 0, or the end of the text. Depth is
        /// tracked so a subquery in the statement's own WHERE (<c>WHERE id IN (SELECT …)</c>) does not
        /// cut the extent short.
        /// </summary>
        private static int StatementExtentEnd(string text, int from)
        {
            int depth = 0;
            for (int i = from; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '(') { depth++; continue; }
                if (c == ')') { if (depth > 0) depth--; continue; }
                if (depth != 0) continue;

                if (!char.IsLetter(c)) continue;
                if (i > from && (char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '_' || text[i - 1] == '@' || text[i - 1] == '#')) continue;

                var m = StatementStartKeyword.Match(text, i);
                if (m.Success && m.Index == i)
                    return i;

                // Skip the rest of this word so the scan does not retry inside it.
                while (i + 1 < text.Length && (char.IsLetterOrDigit(text[i + 1]) || text[i + 1] == '_')) i++;
            }

            return text.Length;
        }

        /// <summary>
        /// Splits a batch into individual statements, respecting string/comment/identifier context
        /// so a <c>;</c> inside a literal, comment, or bracketed name does not split. Comments are
        /// removed (replaced by a space); string literals are PRESERVED (needed by the config-toggle
        /// patterns). Statements are then further split on standalone <c>GO</c> batch-separator lines.
        /// Conservative by design: a mis-split can only ever create additional statements, each of
        /// which is still scanned in full — it can never hide a dangerous token, because a token is
        /// wholly contained on one side of any split.
        /// </summary>
        private static List<string> SplitStatements(string sql)
        {
            var chunks = new List<string>();
            var current = new StringBuilder();
            int i = 0, n = sql.Length;

            while (i < n)
            {
                char c = sql[i];
                char next = i + 1 < n ? sql[i + 1] : '\0';

                // Line comment: -- … to end of line.
                if (c == '-' && next == '-')
                {
                    i += 2;
                    while (i < n && sql[i] != '\n') i++;
                    current.Append(' ');
                    continue;
                }

                // Block comment: /* … */ (non-nested; conservative).
                if (c == '/' && next == '*')
                {
                    i += 2;
                    while (i < n && !(sql[i] == '*' && i + 1 < n && sql[i + 1] == '/')) i++;
                    i += 2; // skip the closing */ (harmless if unterminated: i overruns, loop ends)
                    current.Append(' ');
                    continue;
                }

                // Single-quoted string literal: copy verbatim, honouring the '' escape.
                if (c == '\'')
                {
                    current.Append(c);
                    i++;
                    while (i < n)
                    {
                        current.Append(sql[i]);
                        if (sql[i] == '\'')
                        {
                            if (i + 1 < n && sql[i + 1] == '\'') { current.Append(sql[i + 1]); i += 2; continue; }
                            i++;
                            break;
                        }
                        i++;
                    }
                    continue;
                }

                // Bracket identifier [ … ], honouring the ]] escape.
                if (c == '[')
                {
                    current.Append(c);
                    i++;
                    while (i < n)
                    {
                        current.Append(sql[i]);
                        if (sql[i] == ']')
                        {
                            if (i + 1 < n && sql[i + 1] == ']') { current.Append(sql[i + 1]); i += 2; continue; }
                            i++;
                            break;
                        }
                        i++;
                    }
                    continue;
                }

                // Double-quoted identifier " … ", honouring the "" escape.
                if (c == '"')
                {
                    current.Append(c);
                    i++;
                    while (i < n)
                    {
                        current.Append(sql[i]);
                        if (sql[i] == '"')
                        {
                            if (i + 1 < n && sql[i + 1] == '"') { current.Append(sql[i + 1]); i += 2; continue; }
                            i++;
                            break;
                        }
                        i++;
                    }
                    continue;
                }

                // Top-level statement terminator.
                if (c == ';')
                {
                    chunks.Add(current.ToString());
                    current.Clear();
                    i++;
                    continue;
                }

                current.Append(c);
                i++;
            }

            if (current.Length > 0)
                chunks.Add(current.ToString());

            // GO is a client-side batch separator, not a statement terminator, so it survives the
            // scan above. Split each chunk on standalone GO lines.
            var statements = new List<string>();
            foreach (var chunk in chunks)
                statements.AddRange(SplitOnGo(chunk));
            return statements;
        }

        private static IEnumerable<string> SplitOnGo(string chunk)
        {
            var buffer = new StringBuilder();
            foreach (var line in chunk.Split('\n'))
            {
                if (GoSeparator.IsMatch(line.TrimEnd('\r')))
                {
                    yield return buffer.ToString();
                    buffer.Clear();
                }
                else
                {
                    buffer.Append(line);
                    buffer.Append('\n');
                }
            }
            if (buffer.Length > 0)
                yield return buffer.ToString();
        }

        /// <summary>
        /// Returns a copy of the statement with the CONTENTS of every single-quoted string literal
        /// removed (the surrounding quotes are kept). Used so the identifier/exec patterns match an
        /// actual proc invocation but not a proc NAME that appears only as string data.
        /// </summary>
        private static string BlankStringLiterals(string statement)
        {
            var sb = new StringBuilder(statement.Length);
            int i = 0, n = statement.Length;
            while (i < n)
            {
                char c = statement[i];
                if (c == '\'')
                {
                    sb.Append('\'');
                    i++;
                    while (i < n)
                    {
                        if (statement[i] == '\'')
                        {
                            if (i + 1 < n && statement[i + 1] == '\'') { i += 2; continue; } // '' escape: drop both
                            sb.Append('\'');
                            i++;
                            break;
                        }
                        i++; // drop literal content
                    }
                    continue;
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Result of a <see cref="DangerousExecGuard.Inspect"/> call. Mirrors
    /// <see cref="SqlValidationResult"/>'s shape.
    /// </summary>
    public class DangerousExecVerdict
    {
        /// <summary>True when the batch carries no blocked OS/system-exec pattern.</summary>
        public bool IsAllowed { get; private set; }

        /// <summary>Human-readable reason the batch was blocked; empty when allowed.</summary>
        public string Reason { get; private set; } = string.Empty;

        /// <summary>The regex that matched the blocked statement; empty when allowed.</summary>
        public string MatchedPattern { get; private set; } = string.Empty;

        public static DangerousExecVerdict Allowed() => new() { IsAllowed = true };

        public static DangerousExecVerdict Blocked(string reason, string matchedPattern) => new()
        {
            IsAllowed = false,
            Reason = reason,
            MatchedPattern = matchedPattern
        };
    }
}
