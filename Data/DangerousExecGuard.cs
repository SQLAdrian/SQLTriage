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
    /// <para><b>Per-statement.</b> Each statement is tested independently, so no leading benign
    /// statement (a <c>sys.*</c> read included) can shield a trailing dangerous one. This is the
    /// property the ruling requires and the batch-waiver bypass the wall structurally cannot
    /// provide.</para>
    /// </summary>
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

        // A line that is a batch separator: GO alone (optionally 'GO <count>').
        private static readonly Regex GoSeparator = new(@"^\s*GO(\s+\d+)?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Inspects a SQL batch and returns a verdict. A batch is blocked if ANY of its statements
        /// contains a dangerous OS/system-exec pattern. Empty/whitespace input is allowed (nothing
        /// to run). Never throws.
        /// </summary>
        public static DangerousExecVerdict Inspect(string? sql)
        {
            if (string.IsNullOrWhiteSpace(sql))
                return DangerousExecVerdict.Allowed();

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

                // Identifier/exec: match against a copy with string-literal contents blanked, so a
                // bare textual mention inside a string (a read) does not trip the guard.
                var literalsBlanked = BlankStringLiterals(statement);
                foreach (var (pattern, reason) in ExecPatterns)
                {
                    if (pattern.IsMatch(literalsBlanked))
                        return DangerousExecVerdict.Blocked(reason, pattern.ToString());
                }
            }

            return DangerousExecVerdict.Allowed();
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
