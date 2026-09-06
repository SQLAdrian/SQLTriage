/* In the name of God, the Merciful, the Compassionate */

/*
============================================================================================
  TEST-FIXTURE-ONLY. Never run against a client or production instance.
============================================================================================

  Stands up every scenario the env vars named in the INVOCATION doc blocks of
    Tests/SQLTriage.Tests/PrincipalTraceLiveSmokeTests.cs   (PTRACE_LIVE_*)
    Tests/SQLTriage.Tests/AccessProseLiveSmokeTests.cs      (APROSE_LIVE_*)
  need on a local test instance. Those two class-level comments ARE the fixture contract;
  this script is what makes the contract runnable rather than merely documented. Written
  2026-08-20 (task 4j) because neither file named an existing setup script — both say "this
  file plants nothing" — and no such script existed anywhere in the tree.

  IDEMPOTENT: every fixture is torn down (in dependency order) and rebuilt from scratch on
  every run, so re-running this script reproduces the exact documented end-state regardless
  of what state the instance was already in. That matters here specifically because two of
  the fixtures (the ORPHAN and the GHOST logins) are not "create if missing" states at all —
  the end-state is "the login is ABSENT while a dependent object it left behind still
  exists", which only a scripted teardown-then-rebuild can reproduce reliably on a re-run.

  THE TWO-INSTANCE CONTRACT. PrincipalTraceLiveSmokeTests needs TWO instances (A and B) and
  most of its fixtures are asymmetric between them (present on both, but built differently,
  or dropped on one only). This ONE script drives both sides via the Role variable:

    sqlcmd -S <instance-A> -v Role=A -v IncludeAccessProse=0 -v Mode=provision -i principal-trace-and-access-prose-live-fixtures.sql
    sqlcmd -S <instance-B> -v Role=B -v IncludeAccessProse=0 -v Mode=provision -i principal-trace-and-access-prose-live-fixtures.sql

  AccessProseLiveSmokeTests needs only ONE instance (its own APROSE_LIVE_TARGET). Its
  section is independent of Role and runs only when explicitly asked for — the doc
  examples for both files use "lpc:." for PTRACE's instance A AND for APROSE's target, so
  the common case is a THIRD invocation (or folded into the Role=A run):

    sqlcmd -S <instance-A> -v Role=A -v IncludeAccessProse=1 -v Mode=provision -i ...same-file...

  ⚠⚠ Role, IncludeAccessProse and Mode are REQUIRED -v arguments — every invocation above
  spells out all three. There is no :setvar default for any of them, on purpose (2026-08-20
  fix round, defect B): a :setvar line in this file WINS over a command-line -v of the same
  name, on every read, so a script that defaulted Role/IncludeAccessProse and only DOCUMENTED
  the -v override made every documented override inert — proven live: "-v Role=B -v
  IncludeAccessProse=1" ran Role=A/IncludeAccessProse=0 regardless, which for the Role=B case
  meant creating databases, an Agent job, server roles and dropping that instance's own
  NT AUTHORITY\NETWORK SERVICE login against the WRONG instance. Forgetting a -v now fails
  loudly (sqlcmd: "must declare the scalar variable") instead of silently running the wrong
  branch. The rest of the variables below (LoginVar, OwnedDbVar, …) keep their :setvar
  defaults — they are fixture-object NAMES that also have to match the C# tests' env vars, not
  a runtime-behaviour switch, so the same footgun does not apply to them.

  TEARDOWN MODE (defect G, added 2026-08-20). -v Mode=teardown runs ONLY the drops, in
  dependency order (server-role members removed before the roles themselves, to avoid Msg
  15144 "role has members" — the naive drop order this fix round hit), and skips every
  creation step. -v Mode=provision is the normal path described above. Use teardown after any
  failed or abandoned run, or to leave the instance clean: it never strands
  $(ProxyCredentialVar) even if the proxy section itself already failed once, because
  provisioning now creates that credential and its proxy inside one guarded block that cleans
  up after itself on failure (see the WindowsGhostVar section below).

  ⚠⚠ WHAT THIS SCRIPT DOES NOT DO, BY DESIGN — the "wedge" fixtures (PTRACE_LIVE_WEDGE_DB,
  APROSE_LIVE_WEDGE_DB). Both class comments already carve this step out as a manual,
  per-invocation action, not a permanent fixture state, and this script follows that split
  rather than inventing a different one: it creates the wedge database (ONLINE, MULTI_USER,
  safe to leave that way indefinitely), but setting it SINGLE_USER with an occupied session
  is something you do IMMEDIATELY BEFORE running the wedge-specific tests and undo right
  after — a permanently single-user database would make every OTHER live test that walks
  every database (the estate sweep, the general renders) see a spurious refusal. See "WEDGE
  COMPANION STEPS" near the bottom of this file for the exact commands.

  ⚠ A REAL GAP THIS SCRIPT CLOSES, FOUND WHILE WRITING IT (2026-08-20). The PTRACE class
  comment's SrvRoleVar/SrvParentVar bullet only describes the two ROLE-held permission
  grants; it never says the leaver login also needs a DIRECT server permission grant. But
  the test itself (A_server_permission_held_only_through_a_server_role_is_traced_and_names_
  the_role) asserts perms.Should().Contain(h => h.Via == "Direct", ...) — a fixture built
  strictly from the documented bullet list would leave that assertion with nothing to find.
  This script grants all three (direct + two role-held), each a DIFFERENT permission so the
  "Via" trace is unambiguous. Report this doc/test mismatch upstream if the class comment is
  ever revised.

  PRECONDITIONS THIS SCRIPT DOES NOT CHECK, BECAUSE IT CANNOT: SQL Server Agent must be
  running for the Agent job / operator / proxy sections (msdb objects), and the caller must
  be sysadmin on the target instance, per both class comments.

  ⚠ PRECONDITION THIS SCRIPT DOES CHECK (defect F, added 2026-08-20): mixed-mode
  authentication. AProseLowPrivVar is the one fixture here that is actually authenticated
  against with SQL auth — AccessProseLiveSmokeTests connects as it for real — so IncludeAccessProse=1
  against a Windows-authentication-only instance (SERVERPROPERTY('IsIntegratedSecurityOnly') = 1,
  true of .\new2022 on this box) creates a login that can never log in. The script PRINTs a
  named warning for that case; it does not refuse to run, because every OTHER fixture in this
  script still applies fine to a Windows-auth-only instance.
============================================================================================
*/

-- ── Scripting variables ───────────────────────────────────────────────────────────────────
-- Role, IncludeAccessProse and Mode carry NO :setvar default — pass all three via -v on every
-- invocation. See "Role, IncludeAccessProse and Mode are REQUIRED" near the top of this file
-- for why (defect B, 2026-08-20): a :setvar default here would silently win over -v.
--   Role              A or B; which side of PTRACE's two-instance contract this run builds
--   IncludeAccessProse  1 to also build the AccessProseLiveSmokeTests fixtures on this instance
--   Mode              provision (build fixtures) or teardown (drop everything, build nothing)

:setvar LoginVar "_sqlt_ptrace_leaver"
:setvar WindowsVar "NT AUTHORITY\SYSTEM"
:setvar OrphanVar "_sqlt_ptrace_orphan"
:setvar OwnedDbVar "_sqlt_ptrace_owned"
:setvar OwnedJobVar "_sqlt_ptrace_job"
:setvar SrvRoleVar "_sqlt_ptrace_srvrole"
:setvar SrvParentVar "_sqlt_ptrace_srvparent"
:setvar GhostVar "_sqlt_ptrace_ghost"
:setvar WindowsGhostVar "NT AUTHORITY\NETWORK SERVICE"
:setvar ProxyVar "_sqlt_ptrace_proxy"
:setvar WedgeDbVar "_sqlt_ptrace_f_wedge"
:setvar ProxyCredentialVar "_sqlt_ptrace_proxy_cred"

:setvar AProseWedgeVar "_sqlt_aprose_wedge"
:setvar AProseLowPrivVar "_sqlt_aprose_lowpriv"
:setvar AProseLowPrivPwd "Sqlt_Fixture_LowPriv_2026!"   -- MUST match $env:APROSE_LIVE_LOWPRIV_PWD

:setvar FixturePwd "Sqlt_Fixture_2026!"                 -- catalog-only logins; nothing ever authenticates as them except LowPriv above

SET NOCOUNT ON;
-- EFFECTIVE VALUES — true by construction: Role/IncludeAccessProse/Mode carry no :setvar
-- default above, so whatever prints here is exactly, and only, what -v supplied.
PRINT '=== principal-trace-and-access-prose-live-fixtures.sql — EFFECTIVE Role=$(Role), IncludeAccessProse=$(IncludeAccessProse), Mode=$(Mode), server=' + CONVERT(nvarchar(128), SERVERPROPERTY('ServerName'));
IF ('$(Mode)' NOT IN ('provision', 'teardown'))
    RAISERROR('Mode must be ''provision'' or ''teardown'' — got ''$(Mode)''.', 16, 1);
GO

-- ── Mixed-mode-auth precondition check (defect F) — AProseLowPrivVar is the ONE fixture in
-- this script that is actually authenticated against with SQL auth (AccessProseLiveSmokeTests
-- connects as it for real). On a Windows-auth-only instance (like .\new2022 on this box) the
-- login is created successfully but can never log in, and the armed test then fails for a
-- fixture reason, not a product reason.
IF ('$(Mode)' = 'provision') AND ('$(IncludeAccessProse)' = '1') AND (CONVERT(int, SERVERPROPERTY('IsIntegratedSecurityOnly')) = 1)
    PRINT 'WARNING: this instance is Windows-authentication-only (IsIntegratedSecurityOnly=1). '
        + '$(AProseLowPrivVar) will be created but CANNOT log in with SQL auth here — '
        + 'AccessProseLiveSmokeTests.A_genuinely_low_privilege_sweep_still_makes_the_reduced_privilege_claim '
        + 'will fail to connect for this reason, not a product reason. Point IncludeAccessProse=1 at a '
        + 'mixed-mode instance instead.';
GO

-- ════════════════════════════════════════════════════════════════════════════════════════
-- TEARDOWN (defect G) — Mode=teardown runs ONLY this block, in dependency order (server-role
-- members removed before the roles, to avoid Msg 15144 "role has members"), and none of the
-- creation sections below. Idempotent: every step is EXISTS-guarded.
--
-- Every "dropped X" PRINT below is VERIFY-BEFORE-PRINT (2026-08-20, defect G-1). A DROP that
-- fails with a non-fatal, statement-terminating error (e.g. Msg 3702 "Cannot drop database
-- ... currently in use") does NOT abort the batch — sqlcmd without -b carries straight on to
-- the next statement, which used to be an unconditional success PRINT. Proven live: a wedge
-- database held by another session printed "Teardown: dropped ..." and "teardown complete"
-- with sqlcmd exit 0 while the database still existed. Every drop below now re-checks the
-- catalog AFTER the DROP and prints success only when the object is actually gone; the block
-- ends with a RESIDUE RE-CHECK that fails loudly if anything survives.
-- ════════════════════════════════════════════════════════════════════════════════════════
IF ('$(Mode)' = 'teardown')
BEGIN
    USE master;

    DECLARE @sqlt_teardown_killSql nvarchar(max);
    DECLARE @sqlt_teardown_wait int;

    -- WedgeDbVar / AProseWedgeVar: real eviction (defect G-1b). The old remedy — ALTER
    -- DATABASE ... SET MULTI_USER WITH ROLLBACK IMMEDIATE — cannot get exclusive access to a
    -- SINGLE_USER database another session already occupies (Msg 5064): that occupied
    -- SINGLE_USER state is exactly what the WEDGE COMPANION STEPS at the bottom of this file
    -- put the database in on purpose. KILL the holding session(s) directly instead, scoped
    -- ONLY to this database's own database_id via DB_ID() BY NAME, so this can never reach a
    -- session on any other database, real or fixture.
    IF EXISTS (SELECT 1 FROM sys.databases WHERE name = N'$(WedgeDbVar)')
    BEGIN
        SET @sqlt_teardown_killSql = N'';
        SET @sqlt_teardown_wait = 0;
        SELECT @sqlt_teardown_killSql = @sqlt_teardown_killSql + N'KILL ' + CONVERT(nvarchar(10), session_id) + N'; '
        FROM sys.dm_exec_sessions
        WHERE database_id = DB_ID(N'$(WedgeDbVar)') AND session_id <> @@SPID;
        IF (@sqlt_teardown_killSql <> N'')
        BEGIN
            PRINT 'Teardown: evicting session(s) holding $(WedgeDbVar): ' + @sqlt_teardown_killSql;
            EXEC (@sqlt_teardown_killSql);
            WHILE EXISTS (SELECT 1 FROM sys.dm_exec_sessions WHERE database_id = DB_ID(N'$(WedgeDbVar)') AND session_id <> @@SPID)
                AND @sqlt_teardown_wait < 20
            BEGIN
                WAITFOR DELAY '00:00:00.5';
                SET @sqlt_teardown_wait += 1;
            END
        END
        IF NOT EXISTS (SELECT 1 FROM sys.dm_exec_sessions WHERE database_id = DB_ID(N'$(WedgeDbVar)') AND session_id <> @@SPID)
            DROP DATABASE [$(WedgeDbVar)];
        ELSE
            PRINT 'Teardown: WARNING — could not evict every session holding $(WedgeDbVar) within the wait window; DROP DATABASE skipped this run.';
    END
    IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE name = N'$(WedgeDbVar)')
        PRINT 'Teardown: dropped $(WedgeDbVar)';
    ELSE
        PRINT 'Teardown: WARNING — $(WedgeDbVar) still exists after teardown.';

    IF EXISTS (SELECT 1 FROM sys.databases WHERE name = N'$(AProseWedgeVar)')
    BEGIN
        SET @sqlt_teardown_killSql = N'';
        SET @sqlt_teardown_wait = 0;
        SELECT @sqlt_teardown_killSql = @sqlt_teardown_killSql + N'KILL ' + CONVERT(nvarchar(10), session_id) + N'; '
        FROM sys.dm_exec_sessions
        WHERE database_id = DB_ID(N'$(AProseWedgeVar)') AND session_id <> @@SPID;
        IF (@sqlt_teardown_killSql <> N'')
        BEGIN
            PRINT 'Teardown: evicting session(s) holding $(AProseWedgeVar): ' + @sqlt_teardown_killSql;
            EXEC (@sqlt_teardown_killSql);
            WHILE EXISTS (SELECT 1 FROM sys.dm_exec_sessions WHERE database_id = DB_ID(N'$(AProseWedgeVar)') AND session_id <> @@SPID)
                AND @sqlt_teardown_wait < 20
            BEGIN
                WAITFOR DELAY '00:00:00.5';
                SET @sqlt_teardown_wait += 1;
            END
        END
        IF NOT EXISTS (SELECT 1 FROM sys.dm_exec_sessions WHERE database_id = DB_ID(N'$(AProseWedgeVar)') AND session_id <> @@SPID)
            DROP DATABASE [$(AProseWedgeVar)];
        ELSE
            PRINT 'Teardown: WARNING — could not evict every session holding $(AProseWedgeVar) within the wait window; DROP DATABASE skipped this run.';
    END
    IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE name = N'$(AProseWedgeVar)')
        PRINT 'Teardown: dropped $(AProseWedgeVar)';
    ELSE
        PRINT 'Teardown: WARNING — $(AProseWedgeVar) still exists after teardown.';

    IF EXISTS (SELECT 1 FROM sys.databases WHERE name = N'$(OwnedDbVar)')
    BEGIN
        ALTER DATABASE [$(OwnedDbVar)] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
        DROP DATABASE [$(OwnedDbVar)];
    END
    IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE name = N'$(OwnedDbVar)')
        PRINT 'Teardown: dropped $(OwnedDbVar)';
    ELSE
        PRINT 'Teardown: WARNING — $(OwnedDbVar) still exists after teardown.';

    IF EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = N'$(OwnedJobVar)')
        EXEC msdb.dbo.sp_delete_job @job_name = N'$(OwnedJobVar)';
    IF NOT EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = N'$(OwnedJobVar)')
        PRINT 'Teardown: dropped Agent job $(OwnedJobVar)';
    ELSE
        PRINT 'Teardown: WARNING — Agent job $(OwnedJobVar) still exists after teardown.';

    IF EXISTS (SELECT 1 FROM msdb.dbo.sysproxies WHERE name = N'$(ProxyVar)')
        EXEC msdb.dbo.sp_delete_proxy @proxy_name = N'$(ProxyVar)';
    IF NOT EXISTS (SELECT 1 FROM msdb.dbo.sysproxies WHERE name = N'$(ProxyVar)')
        PRINT 'Teardown: dropped Agent proxy $(ProxyVar)';
    ELSE
        PRINT 'Teardown: WARNING — Agent proxy $(ProxyVar) still exists after teardown.';

    IF EXISTS (SELECT 1 FROM msdb.dbo.sysoperators WHERE name = N'$(GhostVar)')
        EXEC msdb.dbo.sp_delete_operator @name = N'$(GhostVar)';
    IF NOT EXISTS (SELECT 1 FROM msdb.dbo.sysoperators WHERE name = N'$(GhostVar)')
        PRINT 'Teardown: dropped Agent operator $(GhostVar)';
    ELSE
        PRINT 'Teardown: WARNING — Agent operator $(GhostVar) still exists after teardown.';

    IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$(OrphanVar)')
        DROP USER [$(OrphanVar)];
    IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$(OrphanVar)')
        PRINT 'Teardown: dropped master user $(OrphanVar)';
    ELSE
        PRINT 'Teardown: WARNING — master user $(OrphanVar) still exists after teardown.';

    -- Server-role membership BEFORE the roles themselves — the naive order (drop role first)
    -- hit Msg 15144 "role has members" live during this fix round.
    IF EXISTS (SELECT 1 FROM sys.server_role_members rm
               JOIN sys.server_principals child ON child.principal_id = rm.member_principal_id
               JOIN sys.server_principals parent ON parent.principal_id = rm.role_principal_id
               WHERE parent.name = N'$(SrvParentVar)' AND child.name = N'$(SrvRoleVar)')
        ALTER SERVER ROLE [$(SrvParentVar)] DROP MEMBER [$(SrvRoleVar)];
    IF EXISTS (SELECT 1 FROM sys.server_role_members rm
               JOIN sys.server_principals mem ON mem.principal_id = rm.member_principal_id
               JOIN sys.server_principals role ON role.principal_id = rm.role_principal_id
               WHERE role.name = N'$(SrvRoleVar)' AND mem.name = N'$(LoginVar)')
        ALTER SERVER ROLE [$(SrvRoleVar)] DROP MEMBER [$(LoginVar)];
    IF EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'$(SrvParentVar)' AND type = 'R')
        DROP SERVER ROLE [$(SrvParentVar)];
    IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'$(SrvParentVar)' AND type = 'R')
        PRINT 'Teardown: dropped server role $(SrvParentVar)';
    ELSE
        PRINT 'Teardown: WARNING — server role $(SrvParentVar) still exists after teardown.';
    IF EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'$(SrvRoleVar)' AND type = 'R')
        DROP SERVER ROLE [$(SrvRoleVar)];
    IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'$(SrvRoleVar)' AND type = 'R')
        PRINT 'Teardown: dropped server role $(SrvRoleVar)';
    ELSE
        PRINT 'Teardown: WARNING — server role $(SrvRoleVar) still exists after teardown.';

    IF EXISTS (SELECT 1 FROM sys.credentials WHERE name = N'$(ProxyCredentialVar)')
        DROP CREDENTIAL [$(ProxyCredentialVar)];
    IF NOT EXISTS (SELECT 1 FROM sys.credentials WHERE name = N'$(ProxyCredentialVar)')
        PRINT 'Teardown: dropped credential $(ProxyCredentialVar)';
    ELSE
        PRINT 'Teardown: WARNING — credential $(ProxyCredentialVar) still exists after teardown.';

    IF EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'$(LoginVar)')
        DROP LOGIN [$(LoginVar)];
    IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'$(LoginVar)')
        PRINT 'Teardown: dropped login $(LoginVar)';
    ELSE
        PRINT 'Teardown: WARNING — login $(LoginVar) still exists after teardown.';

    IF EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'$(OrphanVar)')
        DROP LOGIN [$(OrphanVar)];
    IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'$(OrphanVar)')
        PRINT 'Teardown: dropped login $(OrphanVar)';
    ELSE
        PRINT 'Teardown: WARNING — login $(OrphanVar) still exists after teardown.';

    IF EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'$(GhostVar)')
        DROP LOGIN [$(GhostVar)];
    IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'$(GhostVar)')
        PRINT 'Teardown: dropped login $(GhostVar)';
    ELSE
        PRINT 'Teardown: WARNING — login $(GhostVar) still exists after teardown.';

    IF EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'$(AProseLowPrivVar)')
        DROP LOGIN [$(AProseLowPrivVar)];
    IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'$(AProseLowPrivVar)')
        PRINT 'Teardown: dropped login $(AProseLowPrivVar)';
    ELSE
        PRINT 'Teardown: WARNING — login $(AProseLowPrivVar) still exists after teardown.';

    -- WindowsVar / WindowsGhostVar are never dropped here. Not because both are always
    -- "shared identities this script does not own" — for WindowsGhostVar that claim is FALSE
    -- on a Role=B provision run, which creates it from nothing (see the PRINT at its creation
    -- site, which says so at the moment it happens — defect G-2). The real reason: a teardown
    -- invocation runs independently, possibly by someone else, possibly a different day — it
    -- cannot tell whether a given Windows login here was created by some prior provision run
    -- or already existed on the box before this script ever touched it. Provenance across runs
    -- is unknowable, so shared Windows identities are never dropped automatically here,
    -- regardless of who created them.

    -- ── RESIDUE RE-CHECK (defect G-1c) — count every remaining _sqlt_ptrace_*/_sqlt_aprose_*
    -- object across every catalog this script writes to (server principals incl. server roles,
    -- databases, the master orphan user, msdb operators/proxies/jobs, credentials).
    -- '=== teardown complete ===' prints ONLY when this comes back zero; otherwise the
    -- surviving names print and a severity-16 RAISERROR fires, so even the documented no--b
    -- invocation shows an unmissable error and a -b invocation exits non-zero.
    DECLARE @sqlt_teardown_residue TABLE (Category nvarchar(30), ObjectName sysname);
    INSERT INTO @sqlt_teardown_residue (Category, ObjectName)
    SELECT 'server principal', name FROM sys.server_principals
        WHERE name LIKE N'\_sqlt\_ptrace\_%' ESCAPE '\' OR name LIKE N'\_sqlt\_aprose\_%' ESCAPE '\'
    UNION ALL
    SELECT 'database', name FROM sys.databases
        WHERE name LIKE N'\_sqlt\_ptrace\_%' ESCAPE '\' OR name LIKE N'\_sqlt\_aprose\_%' ESCAPE '\'
    UNION ALL
    SELECT 'master user', name FROM sys.database_principals
        WHERE name LIKE N'\_sqlt\_ptrace\_%' ESCAPE '\' OR name LIKE N'\_sqlt\_aprose\_%' ESCAPE '\'
    UNION ALL
    SELECT 'msdb operator', name FROM msdb.dbo.sysoperators
        WHERE name LIKE N'\_sqlt\_ptrace\_%' ESCAPE '\' OR name LIKE N'\_sqlt\_aprose\_%' ESCAPE '\'
    UNION ALL
    SELECT 'msdb proxy', name FROM msdb.dbo.sysproxies
        WHERE name LIKE N'\_sqlt\_ptrace\_%' ESCAPE '\' OR name LIKE N'\_sqlt\_aprose\_%' ESCAPE '\'
    UNION ALL
    SELECT 'msdb job', name FROM msdb.dbo.sysjobs
        WHERE name LIKE N'\_sqlt\_ptrace\_%' ESCAPE '\' OR name LIKE N'\_sqlt\_aprose\_%' ESCAPE '\'
    UNION ALL
    SELECT 'credential', name FROM sys.credentials
        WHERE name LIKE N'\_sqlt\_ptrace\_%' ESCAPE '\' OR name LIKE N'\_sqlt\_aprose\_%' ESCAPE '\';

    IF EXISTS (SELECT 1 FROM @sqlt_teardown_residue)
    BEGIN
        DECLARE @sqlt_teardown_residueList nvarchar(max);
        DECLARE @sqlt_teardown_residueCount int;
        SELECT @sqlt_teardown_residueList = STRING_AGG(CONVERT(nvarchar(max), Category + N': ' + ObjectName), N', ')
        FROM @sqlt_teardown_residue;
        SELECT @sqlt_teardown_residueCount = COUNT(*) FROM @sqlt_teardown_residue;
        PRINT 'Teardown: RESIDUE — the following fixture object(s) survive teardown: ' + @sqlt_teardown_residueList;
        RAISERROR('Teardown incomplete: %d fixture object(s) still exist after teardown — see the RESIDUE line above.', 16, 1, @sqlt_teardown_residueCount);
    END
    ELSE
        PRINT '=== teardown complete ===';
END
GO

-- ════════════════════════════════════════════════════════════════════════════════════════
-- EVERYTHING BELOW THIS LINE IS THE PROVISION PATH — each section is guarded by
-- '$(Mode)' = 'provision' (added alongside the existing Role guard where one already existed)
-- so a teardown invocation runs the block above and nothing else.
-- ════════════════════════════════════════════════════════════════════════════════════════

-- ── Helper: ensure a Windows login exists (never drop these — they are shared identities,
--    not fixtures this script owns end to end; PTRACE_LIVE_WINDOWS is used read-only, and
--    PTRACE_LIVE_WGHOST is torn down/rebuilt in its own dedicated section below instead) ──
IF ('$(Mode)' = 'provision')
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'$(WindowsVar)')
    BEGIN
        PRINT 'Creating Windows login $(WindowsVar) (SID pivot fixture; needs no other setup)';
        CREATE LOGIN [$(WindowsVar)] FROM WINDOWS;
    END
    ELSE PRINT 'Windows login $(WindowsVar) already present — leaving it alone (read-only fixture)';
END
GO

-- ════════════════════════════════════════════════════════════════════════════════════════
-- SECTION: LoginVar — the leaver, present on BOTH sides, created independently so the SIDs
-- differ (⚠ never add WITH SID = the same value on both — see the class comment).
-- ════════════════════════════════════════════════════════════════════════════════════════
IF ('$(Mode)' = 'provision')
BEGIN
    IF EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'$(LoginVar)')
    BEGIN
        PRINT 'Dropping existing login $(LoginVar) to rebuild it fresh (independent SID)';
        DROP LOGIN [$(LoginVar)];
    END
    CREATE LOGIN [$(LoginVar)] WITH PASSWORD = N'$(FixturePwd)', CHECK_POLICY = OFF, CHECK_EXPIRATION = OFF;
    PRINT 'Created login $(LoginVar) with a fresh SID';
END
GO

-- ════════════════════════════════════════════════════════════════════════════════════════
-- SECTION: OrphanVar — login on BOTH sides; a database user from it in master; then, on
-- Role=A only, DROP the login (the user in master survives, orphaned).
-- ════════════════════════════════════════════════════════════════════════════════════════
-- The doc bullet builds the master user on instance A ONLY ("creating a database user from
-- it on instance A, then DROPPING the login on A only") — B carries the login and nothing
-- else, so the picker can resolve the identity but has no orphaned row to find there.
USE master;
GO
IF ('$(Mode)' = 'provision' AND '$(Role)' = 'A')
BEGIN
    IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$(OrphanVar)')
    BEGIN
        PRINT 'Dropping existing user $(OrphanVar) in master to rebuild the orphan fixture fresh';
        DROP USER [$(OrphanVar)];
    END
END
GO
IF ('$(Mode)' = 'provision')
BEGIN
    IF EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'$(OrphanVar)')
    BEGIN
        PRINT 'Dropping existing login $(OrphanVar) to rebuild it fresh';
        DROP LOGIN [$(OrphanVar)];
    END
    CREATE LOGIN [$(OrphanVar)] WITH PASSWORD = N'$(FixturePwd)', CHECK_POLICY = OFF, CHECK_EXPIRATION = OFF;
    PRINT 'Created login $(OrphanVar)';
END
GO
IF ('$(Mode)' = 'provision')
BEGIN
    IF ('$(Role)' = 'A')
    BEGIN
        CREATE USER [$(OrphanVar)] FOR LOGIN [$(OrphanVar)];
        DROP LOGIN [$(OrphanVar)];
        PRINT 'Role A: created master user $(OrphanVar), then dropped the login — the user is now orphaned, by design';
    END
    ELSE
        PRINT 'Role B: leaving login $(OrphanVar) in place, no database user — the picker needs it to resolve the identity at all';
END
GO

-- ════════════════════════════════════════════════════════════════════════════════════════
-- SECTION: GhostVar — login on BOTH sides, an Agent OPERATOR of the same name on BOTH
-- sides; then, on Role=A only, DROP the login (the operator survives — it carries no SID).
-- ════════════════════════════════════════════════════════════════════════════════════════
IF ('$(Mode)' = 'provision')
BEGIN
    IF EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'$(GhostVar)')
    BEGIN
        PRINT 'Dropping existing login $(GhostVar) to rebuild it fresh';
        DROP LOGIN [$(GhostVar)];
    END
    CREATE LOGIN [$(GhostVar)] WITH PASSWORD = N'$(FixturePwd)', CHECK_POLICY = OFF, CHECK_EXPIRATION = OFF;
END
GO
IF ('$(Mode)' = 'provision')
BEGIN
    IF EXISTS (SELECT 1 FROM msdb.dbo.sysoperators WHERE name = N'$(GhostVar)')
    BEGIN
        PRINT 'Dropping existing Agent operator $(GhostVar) to rebuild it fresh';
        EXEC msdb.dbo.sp_delete_operator @name = N'$(GhostVar)';
    END
    EXEC msdb.dbo.sp_add_operator @name = N'$(GhostVar)', @enabled = 1, @email_address = N'sqlt-fixture@invalid';
    PRINT 'Created login + Agent operator for $(GhostVar)';
END
GO
IF ('$(Mode)' = 'provision')
BEGIN
    IF ('$(Role)' = 'A')
    BEGIN
        DROP LOGIN [$(GhostVar)];
        PRINT 'Role A: dropped login $(GhostVar) — the Agent operator row survives, by design (no SID to lose)';
    END
    ELSE
        PRINT 'Role B: leaving login $(GhostVar) in place';
END
GO

-- ════════════════════════════════════════════════════════════════════════════════════════
-- SECTION: WindowsGhostVar — a Windows login on BOTH sides (created if this box does not
-- already carry it); on Role=A only, an Agent PROXY granted to it, then the Windows login
-- DROPPED (the msdb.dbo.sysproxylogin row survives, keyed by the Windows SID).
-- ════════════════════════════════════════════════════════════════════════════════════════
IF ('$(Mode)' = 'provision')
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'$(WindowsGhostVar)')
    BEGIN
        PRINT 'Creating Windows login $(WindowsGhostVar)';
        CREATE LOGIN [$(WindowsGhostVar)] FROM WINDOWS;
        IF ('$(Role)' = 'B')
            -- Role=A drops this same login later in THIS run (the Agent-proxy section below),
            -- unconditionally, whether or not the proxy itself succeeds — so it never lingers
            -- from a Role=A run. Role=B keeps it (the SID-pivot fixture needs it present),
            -- which means this run just created a shared Windows identity from nothing, and
            -- Mode=teardown will NOT remove it (provenance across runs is unknowable — see the
            -- teardown-block comment near the WindowsVar/WindowsGhostVar note).
            PRINT 'Role B: $(WindowsGhostVar) is now a shared Windows identity this run created '
                + 'from nothing. Mode=teardown will NOT remove it. Manual drop if you want it '
                + 'gone: DROP LOGIN [$(WindowsGhostVar)];';
    END
    ELSE PRINT 'Windows login $(WindowsGhostVar) already present';
END
GO

-- sp_add_proxy REJECTS a credential whose IDENTITY is not a real Windows account ("not a valid
-- Windows user", Msg 14529) — the earlier version of this section used a fabricated identity
-- ('sqlt_fixture_identity') that sp_add_proxy has never accepted, PRINTed success anyway, and
-- under -b the whole script aborted there, leaving five later sections unbuilt (defect C, found
-- live 2026-08-20). Fixed: the credential identity is the CALLER's own Windows principal
-- (SUSER_SNAME() — the "the caller must be sysadmin" precondition already guarantees this is a
-- real, connectable account on this instance); the secret is still a dummy, since the proxy job
-- step never runs. The whole block runs in TRY/CATCH so a failure here (e.g. a non-Windows,
-- SQL-auth caller, where SUSER_SNAME() is not a Windows account either) PRINTs a named warning
-- and cleans up any partial credential rather than aborting the rest of the script (defect G:
-- the credential is created and verified inside this ONE guarded block, so a failure here can
-- never strand it — Mode=teardown also drops it unconditionally as a second line of defence).
IF ('$(Mode)' = 'provision')
BEGIN
    IF ('$(Role)' = 'A')
    BEGIN
        BEGIN TRY
            DECLARE @sqlt_ptrace_callerPrincipal sysname = SUSER_SNAME();
            DECLARE @sqlt_ptrace_createCredSql nvarchar(max);

            IF EXISTS (SELECT 1 FROM msdb.dbo.sysproxies WHERE name = N'$(ProxyVar)')
            BEGIN
                PRINT 'Dropping existing Agent proxy $(ProxyVar) to rebuild it fresh';
                EXEC msdb.dbo.sp_delete_proxy @proxy_name = N'$(ProxyVar)';
            END
            IF EXISTS (SELECT 1 FROM sys.credentials WHERE name = N'$(ProxyCredentialVar)')
                DROP CREDENTIAL [$(ProxyCredentialVar)];

            SET @sqlt_ptrace_createCredSql = N'CREATE CREDENTIAL [$(ProxyCredentialVar)] WITH IDENTITY = '
                + QUOTENAME(@sqlt_ptrace_callerPrincipal, '''')
                + N', SECRET = N''unused, never authenticated'';';
            EXEC (@sqlt_ptrace_createCredSql);

            EXEC msdb.dbo.sp_add_proxy @proxy_name = N'$(ProxyVar)', @credential_name = N'$(ProxyCredentialVar)', @enabled = 1;
            EXEC msdb.dbo.sp_grant_login_to_proxy @proxy_name = N'$(ProxyVar)', @login_name = N'$(WindowsGhostVar)';

            -- Verify before claiming success — the old PRINT ran unconditionally, even the run
            -- that actually failed inside msdb's own procs (Msg 14529 / 14262) and left no proxy.
            IF EXISTS (SELECT 1 FROM msdb.dbo.sysproxies WHERE name = N'$(ProxyVar)')
                PRINT 'Role A: created Agent proxy $(ProxyVar) (credential identity ' + @sqlt_ptrace_callerPrincipal
                    + ') and granted it to $(WindowsGhostVar)';
            ELSE
                PRINT 'Role A: WARNING — Agent proxy $(ProxyVar) was not created (no error raised, but the '
                    + 'catalog row is absent). The sysproxylogin fixture is ABSENT; the '
                    + '"dropped Windows login still shows the proxy row" test will fail for a fixture '
                    + 'reason, not a product reason.';
        END TRY
        BEGIN CATCH
            PRINT 'Role A: WARNING — Agent proxy section failed: ' + ERROR_MESSAGE()
                + '. The sysproxylogin fixture is ABSENT; cleaning up any partial credential so '
                + '$(ProxyCredentialVar) is never left stranded.';
            IF EXISTS (SELECT 1 FROM sys.credentials WHERE name = N'$(ProxyCredentialVar)')
                DROP CREDENTIAL [$(ProxyCredentialVar)];
        END CATCH

        -- Dropped unconditionally, matching the documented contract, whether or not the proxy
        -- section above succeeded — the Windows-login asymmetry between A and B is a separate
        -- fixture in its own right (the SID pivot), not contingent on the proxy.
        DROP LOGIN [$(WindowsGhostVar)];
        PRINT 'Role A: dropped Windows login $(WindowsGhostVar) — the sysproxylogin row survives '
            + '(if the proxy section above succeeded), keyed by SID';
    END
    ELSE
        PRINT 'Role B: leaving Windows login $(WindowsGhostVar) in place';
END
GO

-- ════════════════════════════════════════════════════════════════════════════════════════
-- SECTION (Role=A only): OwnedDbVar + OwnedJobVar — database and Agent job owned by the
-- leaver. See the note below on why a separate database USER is not created here.
-- ════════════════════════════════════════════════════════════════════════════════════════
IF ('$(Mode)' = 'provision' AND '$(Role)' = 'A')
BEGIN
    IF EXISTS (SELECT 1 FROM sys.databases WHERE name = N'$(OwnedDbVar)')
    BEGIN
        PRINT 'Dropping existing database $(OwnedDbVar) to rebuild it fresh';
        ALTER DATABASE [$(OwnedDbVar)] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
        DROP DATABASE [$(OwnedDbVar)];
    END
    CREATE DATABASE [$(OwnedDbVar)];
    ALTER AUTHORIZATION ON DATABASE::[$(OwnedDbVar)] TO [$(LoginVar)];
    PRINT 'Role A: created database $(OwnedDbVar), owned by $(LoginVar)';

    -- ⚠ Defects D + E (found live 2026-08-20). The class-comment bullet asks for "a database
    -- user for the login inside PTRACE_LIVE_DB holding at least one database role" — but
    -- ALTER AUTHORIZATION above already maps $(LoginVar) onto the dbo user of this database,
    -- and SQL Server refuses to ALSO CREATE USER the same login under its own name (Msg 15063
    -- "the login already has an account under a different user name" / Msg 15151 "cannot add
    -- the principal … does not exist or you do not have permission" once the first error
    -- aborts the batch). The earlier version of this section attempted it anyway inside
    -- USE [$(OwnedDbVar)] — a statement sqlcmd resolves at COMPILE time regardless of any IF
    -- around it, so on a Role=B run (where $(OwnedDbVar) never exists) it failed the whole
    -- batch with Msg 911 even though the CREATE USER it guarded never would have run either.
    -- Neither statement is needed: grep-checked 2026-08-20, no test in
    -- PrincipalTraceLiveSmokeTests asserts a NAMED database-role row for this login — only
    -- Owned_objects_match_the_catalog_exactly_and_are_flagged_as_ownership checks this database
    -- at all, and it asserts ownership only. dbo carries every permission in the database
    -- unconditionally, a STRONGER claim than membership in any one role, so ownership alone
    -- already covers the doc bullet's intent; no separate USE/CREATE USER/ALTER ROLE step runs.
    PRINT 'Role A: $(LoginVar) owns $(OwnedDbVar) and maps to dbo there — that already covers '
        + '"holds a database role" (dbo has every permission, which is why no separate '
        + 'CREATE USER is attempted; see the comment above this line)';
END
GO

USE master;
GO
IF ('$(Mode)' = 'provision' AND '$(Role)' = 'A')
BEGIN
    IF EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = N'$(OwnedJobVar)')
    BEGIN
        PRINT 'Dropping existing Agent job $(OwnedJobVar) to rebuild it fresh';
        EXEC msdb.dbo.sp_delete_job @job_name = N'$(OwnedJobVar)';
    END
    EXEC msdb.dbo.sp_add_job @job_name = N'$(OwnedJobVar)', @owner_login_name = N'$(LoginVar)', @enabled = 0;
    EXEC msdb.dbo.sp_add_jobstep @job_name = N'$(OwnedJobVar)', @step_name = N'noop',
        @subsystem = N'TSQL', @command = N'SELECT 1;';
    EXEC msdb.dbo.sp_add_jobserver @job_name = N'$(OwnedJobVar)', @server_name = @@SERVERNAME;
    PRINT 'Role A: created Agent job $(OwnedJobVar), owned by $(LoginVar), disabled (never runs)';
END
GO

-- ════════════════════════════════════════════════════════════════════════════════════════
-- SECTION (Role=A only): SrvRoleVar / SrvParentVar — nested server roles, the leaver a
-- member of the CHILD role, three DISTINCT server permissions (child-role, parent-role,
-- and one granted DIRECTLY to the leaver — see the "real gap" note at the top of this file).
-- ════════════════════════════════════════════════════════════════════════════════════════
IF ('$(Mode)' = 'provision' AND '$(Role)' = 'A')
BEGIN
    -- Teardown in dependency order: members before roles.
    IF EXISTS (SELECT 1 FROM sys.server_role_members rm
               JOIN sys.server_principals child ON child.principal_id = rm.member_principal_id
               JOIN sys.server_principals parent ON parent.principal_id = rm.role_principal_id
               WHERE parent.name = N'$(SrvParentVar)' AND child.name = N'$(SrvRoleVar)')
        ALTER SERVER ROLE [$(SrvParentVar)] DROP MEMBER [$(SrvRoleVar)];

    IF EXISTS (SELECT 1 FROM sys.server_role_members rm
               JOIN sys.server_principals mem ON mem.principal_id = rm.member_principal_id
               JOIN sys.server_principals role ON role.principal_id = rm.role_principal_id
               WHERE role.name = N'$(SrvRoleVar)' AND mem.name = N'$(LoginVar)')
        ALTER SERVER ROLE [$(SrvRoleVar)] DROP MEMBER [$(LoginVar)];

    IF EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'$(SrvParentVar)' AND type = 'R')
        DROP SERVER ROLE [$(SrvParentVar)];
    IF EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'$(SrvRoleVar)' AND type = 'R')
        DROP SERVER ROLE [$(SrvRoleVar)];

    CREATE SERVER ROLE [$(SrvRoleVar)];
    CREATE SERVER ROLE [$(SrvParentVar)];
    ALTER SERVER ROLE [$(SrvParentVar)] ADD MEMBER [$(SrvRoleVar)];
    ALTER SERVER ROLE [$(SrvRoleVar)] ADD MEMBER [$(LoginVar)];

    GRANT VIEW SERVER STATE TO [$(SrvRoleVar)];
    GRANT ALTER ANY CREDENTIAL TO [$(SrvParentVar)];
    GRANT ALTER ANY EVENT NOTIFICATION TO [$(LoginVar)];  -- the DIRECT grant the test asserts and the class comment omits

    PRINT 'Role A: rebuilt $(SrvRoleVar) (member of $(SrvParentVar)) with $(LoginVar) nested inside, '
        + 'plus a direct grant on $(LoginVar) itself — three distinct permissions total';
END
GO

-- ════════════════════════════════════════════════════════════════════════════════════════
-- SECTION (Role=A only): WedgeDbVar — the database itself, ONLINE and MULTI_USER. Setting it
-- SINGLE_USER with an occupied session is a per-invocation companion step — see below.
-- ════════════════════════════════════════════════════════════════════════════════════════
IF ('$(Mode)' = 'provision' AND '$(Role)' = 'A')
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE name = N'$(WedgeDbVar)')
    BEGIN
        CREATE DATABASE [$(WedgeDbVar)];
        PRINT 'Role A: created database $(WedgeDbVar) (ONLINE, MULTI_USER — see WEDGE COMPANION STEPS below)';
    END
    ELSE
    BEGIN
        -- If a previous test run left it SINGLE_USER (companion step not reverted), fix that
        -- rather than leaving every OTHER live test seeing a spurious refusal here.
        IF EXISTS (SELECT 1 FROM sys.databases WHERE name = N'$(WedgeDbVar)' AND user_access_desc <> 'MULTI_USER')
        BEGIN
            ALTER DATABASE [$(WedgeDbVar)] SET MULTI_USER WITH ROLLBACK IMMEDIATE;
            PRINT 'Role A: $(WedgeDbVar) was left SINGLE_USER by a prior run — reverted to MULTI_USER';
        END
        ELSE PRINT 'Role A: database $(WedgeDbVar) already present and MULTI_USER';
    END
END
GO

-- ════════════════════════════════════════════════════════════════════════════════════════
-- SECTION (IncludeAccessProse=1 only): AProseWedgeVar (same MULTI_USER-by-default pattern as
-- above) and AProseLowPrivVar (a plain SQL login holding nothing but public).
-- ════════════════════════════════════════════════════════════════════════════════════════
IF ('$(Mode)' = 'provision' AND '$(IncludeAccessProse)' = '1')
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE name = N'$(AProseWedgeVar)')
    BEGIN
        CREATE DATABASE [$(AProseWedgeVar)];
        PRINT 'AccessProse: created database $(AProseWedgeVar) (ONLINE, MULTI_USER — see WEDGE COMPANION STEPS below)';
    END
    ELSE
    BEGIN
        IF EXISTS (SELECT 1 FROM sys.databases WHERE name = N'$(AProseWedgeVar)' AND user_access_desc <> 'MULTI_USER')
        BEGIN
            ALTER DATABASE [$(AProseWedgeVar)] SET MULTI_USER WITH ROLLBACK IMMEDIATE;
            PRINT 'AccessProse: $(AProseWedgeVar) was left SINGLE_USER by a prior run — reverted to MULTI_USER';
        END
        ELSE PRINT 'AccessProse: database $(AProseWedgeVar) already present and MULTI_USER';
    END

    IF EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'$(AProseLowPrivVar)')
    BEGIN
        PRINT 'AccessProse: dropping existing login $(AProseLowPrivVar) to rebuild it fresh';
        DROP LOGIN [$(AProseLowPrivVar)];
    END
    -- Deliberately holds nothing beyond the implicit "public" server role — no GRANT, no
    -- fixed-role membership. This login IS authenticated against for real by the test (unlike
    -- every SQL login above), so its password must match $env:APROSE_LIVE_LOWPRIV_PWD exactly.
    CREATE LOGIN [$(AProseLowPrivVar)] WITH PASSWORD = N'$(AProseLowPrivPwd)', CHECK_POLICY = OFF, CHECK_EXPIRATION = OFF;
    PRINT 'AccessProse: created low-privilege login $(AProseLowPrivVar) — set $env:APROSE_LIVE_LOWPRIV_PWD to $(AProseLowPrivPwd) or re-run with -v AProseLowPrivPwd=<yours>';
END
GO

IF ('$(Mode)' = 'provision')
    PRINT '=== fixture provisioning complete for Role=$(Role), IncludeAccessProse=$(IncludeAccessProse) ===';
GO

/*
============================================================================================
  WEDGE COMPANION STEPS (run manually, once, immediately before the wedge-specific tests;
  this script never runs these — see the note near the top of this file for why).

  1. Set the database SINGLE_USER (fails/rolls back any other session in it):
       ALTER DATABASE [<wedge db name>] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;

  2. In a SEPARATE terminal, open and HOLD a session inside it — this occupies the one
     SINGLE_USER slot so the sweep's own connection attempt is refused:
       sqlcmd -S <instance> -d <wedge db name> -Q "WAITFOR DELAY '01:00:00';"
     Leave that window running for the duration of the live test run.

  3. Run the wedge-specific tests (PrincipalTraceLiveSmokeTests.
     A_sysadmin_sweep_that_loses_one_database_never_claims_it_lacked_rights, and/or
     AccessProseLiveSmokeTests' three wedge tests).

  4. Afterwards, Ctrl+C the WAITFOR window, then:
       ALTER DATABASE [<wedge db name>] SET MULTI_USER WITH ROLLBACK IMMEDIATE;
     (Step 2 of THIS script also self-heals a forgotten revert on its next run.)
============================================================================================
*/
