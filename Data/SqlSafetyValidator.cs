/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SQLTriage.Data
{
    /// <summary>
    /// Validates SQL scripts and queries before execution to prevent dangerous operations.
    /// Blocks modifications to security settings, SQL Server configuration, and tables
    /// outside the master and SQLWATCH databases.
    /// </summary>
    public static class SqlSafetyValidator
    {
        /// <summary>
        /// Databases that diagnostic scripts are allowed to target.
        /// Scripts operating outside these databases will be blocked.
        /// </summary>
        private static readonly HashSet<string> AllowedDatabases = new(StringComparer.OrdinalIgnoreCase)
        {
            "master",
            "MASTER",
            "sqlwatch",
            "SQLWATCH",
            "tempdb",
            "TEMPDB",
            "msdb",
            "MSDB"
        };

        /// <summary>
        /// Regex patterns for dangerous SQL statements that should NEVER be executed
        /// by diagnostic/monitoring scripts. Each pattern is case-insensitive.
        /// </summary>
        private static readonly List<(Regex Pattern, string Reason)> BlockedPatterns = new()
        {
            // Security modifications
            (new Regex(@"\bCREATE\s+LOGIN\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "Creating SQL logins is not permitted"),
            (new Regex(@"\bALTER\s+LOGIN\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "Altering SQL logins is not permitted"),
            (new Regex(@"\bDROP\s+LOGIN\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "Dropping SQL logins is not permitted"),
            (new Regex(@"\bCREATE\s+USER\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "Creating database users is not permitted"),
            (new Regex(@"\bALTER\s+USER\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "Altering database users is not permitted"),
            (new Regex(@"\bDROP\s+USER\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "Dropping database users is not permitted"),
            (new Regex(@"\bALTER\s+ROLE\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "Altering server/database roles is not permitted"),
            (new Regex(@"\bALTER\s+SERVER\s+ROLE\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "Altering server roles is not permitted"),
            (new Regex(@"\bGRANT\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "GRANT permissions is not permitted"),
            (new Regex(@"\bDENY\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "DENY permissions is not permitted"),
            (new Regex(@"\bREVOKE\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "REVOKE permissions is not permitted"),

            // Server configuration changes
            (new Regex(@"\bsp_configure\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "Changing SQL Server configuration (sp_configure) is not permitted"),
            (new Regex(@"\bRECONFIGURE\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "RECONFIGURE is not permitted"),
            (new Regex(@"\bALTER\s+DATABASE\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "ALTER DATABASE is not permitted from diagnostic scripts"),
            (new Regex(@"\bALTER\s+SERVER\s+CONFIGURATION\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "ALTER SERVER CONFIGURATION is not permitted"),

            // Destructive operations
            (new Regex(@"\bDROP\s+DATABASE\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "DROP DATABASE is not permitted"),
            (new Regex(@"\bSHUTDOWN\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "SHUTDOWN command is not permitted"),
            (new Regex(@"\bRESTORE\s+DATABASE\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "RESTORE DATABASE is not permitted from diagnostic scripts"),
            (new Regex(@"\bBACKUP\s+DATABASE\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "BACKUP DATABASE is not permitted from diagnostic scripts"),

            // Dangerous system procedures
            (new Regex(@"\bxp_cmdshell\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "xp_cmdshell (OS command execution) is not permitted"),
            (new Regex(@"\bxp_regwrite\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "xp_regwrite (registry modification) is not permitted"),
            (new Regex(@"\bxp_regdeletekey\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "xp_regdeletekey is not permitted"),
            (new Regex(@"\bxp_regdeletevalue\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "xp_regdeletevalue is not permitted"),
            (new Regex(@"\bsp_OACreate\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "OLE Automation (sp_OACreate) is not permitted"),
            (new Regex(@"\bOPENROWSET\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "OPENROWSET is not permitted (potential data exfiltration)"),
            (new Regex(@"\bOPENDATASOURCE\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "OPENDATASOURCE is not permitted (potential data exfiltration)"),
            (new Regex(@"\bBULK\s+INSERT\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "BULK INSERT is not permitted from diagnostic scripts"),

            // Table modifications outside allowed scope (DDL on user tables)
            (new Regex(@"\bDROP\s+TABLE\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "DROP TABLE is not permitted from diagnostic scripts"),
            (new Regex(@"\bTRUNCATE\s+TABLE\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "TRUNCATE TABLE is not permitted from diagnostic scripts"),

            // Index DDL — a WRITE. Free-form CREATE/DROP INDEX is blocked; it reaches the server
            // ONLY when promoted to Remediation by the registered ADDMISSINGINDEX template key
            // (the add-missing-index remediation). Shipped diagnostics that create temp-table
            // indexes are batch-exempt via their SELECT ... FROM sys.* (AllowedExceptions), so this
            // does not block them — only the read-path-free, op-rendered DDL is gated by it.
            (new Regex(@"\bCREATE\s+(UNIQUE\s+)?(CLUSTERED\s+|NONCLUSTERED\s+)?(COLUMNSTORE\s+|SPATIAL\s+|(PRIMARY\s+)?XML\s+)?INDEX\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "CREATE INDEX is a write — permitted only via the gated remediation lane"),
            (new Regex(@"\bDROP\s+INDEX\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "DROP INDEX is a write — permitted only via the gated remediation lane"),

            // Credential/encryption manipulation
            (new Regex(@"\bCREATE\s+CREDENTIAL\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "CREATE CREDENTIAL is not permitted"),
            (new Regex(@"\bALTER\s+CREDENTIAL\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "ALTER CREDENTIAL is not permitted"),
            (new Regex(@"\bCREATE\s+CERTIFICATE\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "CREATE CERTIFICATE is not permitted"),
            (new Regex(@"\bCREATE\s+MASTER\s+KEY\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "CREATE MASTER KEY is not permitted"),

            // Linked server manipulation
            (new Regex(@"\bsp_addlinkedserver\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "Adding linked servers is not permitted"),
            (new Regex(@"\bsp_addlinkedsrvlogin\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "Adding linked server logins is not permitted"),
        };

        /// <summary>
        /// Patterns that are ALLOWED in certain contexts (e.g., reading from system views).
        /// These are checked to prevent false positives from the blocked patterns.
        /// </summary>
        private static readonly List<Regex> AllowedExceptions = new()
        {
            // SELECT from sys.* views is always allowed
            new Regex(@"\bSELECT\b.*\bFROM\b\s+sys\.", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline),
            // GRANT/DENY/REVOKE inside comments
            new Regex(@"--.*\b(GRANT|DENY|REVOKE)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        };

        /// <summary>
        /// Template keys that are recognised as registered remediations for the
        /// purposes of classification. A write statement can ONLY be classified as
        /// <see cref="SqlClassification.Remediation"/> when the caller supplies a
        /// context whose template key appears here. This is the structural wall: a
        /// write is never promoted out of <see cref="SqlClassification.Blocked"/> by
        /// its text — only by an authorising, registered context.
        ///
        /// Build step 1 ships exactly one bounded template (MAXDOP). The runner,
        /// template store, and the remaining ~12 templates are later build steps;
        /// this set is the seam they will populate.
        /// </summary>
        // The RemediationRunner passes the template store's keys to Classify (the single
        // authority); this built-in set is only the fallback for direct Classify(sql, ctx)
        // callers. Keep it in sync with the shipped template keys.
        private static readonly HashSet<string> RegisteredRemediationKeys = new(StringComparer.Ordinal)
        {
            "MAXDOP",
            "CTFP",
            "OPTIMIZEFORADHOC",
            "MAXSERVERMEMORY",
            "BACKUPCOMPRESSION",
            "DEFAULTTRACE",
            "CROSSDBOWNERSHIP",
            "ADHOCDISTRIBUTEDQUERIES",
            "ADDMISSINGINDEX",
        };

        /// <summary>
        /// Three-way safety classification used by the gated remediation lane.
        /// Strictly additive to <see cref="Validate"/>, which remains the binary
        /// read-only gate for the existing read path.
        /// <list type="bullet">
        /// <item><see cref="SqlClassification.Safe"/> — read-only; may run on the read path.</item>
        /// <item><see cref="SqlClassification.Remediation"/> — a write authorised ONLY by a
        /// registered remediation context; may run ONLY through the remediation runner's gates.</item>
        /// <item><see cref="SqlClassification.Blocked"/> — never runs. Free-form writes land here,
        /// including write text that arrives without a registered-template context.</item>
        /// </list>
        ///
        /// Anti-bypass invariant: with a null/empty/unregistered context, this method
        /// returns exactly what <see cref="Validate"/> decides — Safe for reads, Blocked
        /// for writes. Remediation is unreachable from SQL text alone.
        /// </summary>
        /// <param name="sql">The SQL text to classify.</param>
        /// <param name="context">
        /// The remediation context proving a registered template authorised this SQL,
        /// or <c>null</c> for an unauthorised (free-form) classification.
        /// </param>
        /// <param name="registeredKeys">
        /// The authoritative set of registered template keys. When supplied (the
        /// RemediationRunner passes the template store's keys), this is the single
        /// source of truth for "is this key registered?" — so the store and the
        /// validator can never drift. When null, falls back to the built-in
        /// <see cref="RegisteredRemediationKeys"/> set (the read-path / direct-call default).
        /// </param>
        public static SqlClassification Classify(
            string sql, RemediationContext? context = null, IReadOnlySet<string>? registeredKeys = null)
        {
            // Reads are safe regardless of context.
            var baseResult = Validate(sql);
            if (baseResult.IsSafe)
                return SqlClassification.Safe;

            // From here the text is a write that Validate() blocks. It may be promoted
            // to Remediation ONLY by an authorising, registered context — never by text.
            if (context is null)
                return SqlClassification.Blocked;

            if (string.IsNullOrWhiteSpace(context.RegisteredTemplateKey))
                return SqlClassification.Blocked;

            var authority = registeredKeys ?? RegisteredRemediationKeys;
            if (!authority.Contains(context.RegisteredTemplateKey))
                return SqlClassification.Blocked;

            return SqlClassification.Remediation;
        }

        /// <summary>
        /// Validates a SQL batch for safety. Returns a validation result indicating
        /// whether the SQL is safe to execute.
        /// </summary>
        /// <param name="sql">The SQL text to validate.</param>
        /// <returns>A validation result with success/failure and reason.</returns>
        public static SqlValidationResult Validate(string sql)
        {
            if (string.IsNullOrWhiteSpace(sql))
                return SqlValidationResult.Safe();

            // Strip single-line comments to avoid false positives
            var sqlWithoutComments = StripComments(sql);

            // NOTE (batch-level BY DESIGN — verified necessary 2026-06-23): the allowed
            // exceptions are evaluated over the WHOLE batch, not per-statement. This is
            // intentional and LOAD-BEARING. A statement-aware (per-statement) tightening was
            // built and verified against every shipped script, and it BLOCKS the core trusted
            // diagnostics: sp_Blitz and sp_triage genuinely EXECUTE xp_cmdshell (the service-
            // account-local-admin check, e.g. `EXEC xp_cmdshell 'net start'`), and usp_bpcheck
            // (Check_BP_Servers) toggles `sp_configure 'xp_cmdshell'` + RECONFIGURE. These are
            // REAL blocked statements in TRUSTED scripts — there is no syntactic way to tell them
            // from a malicious one, so the only viable model is trust-by-source, which the
            // batch-level exemption approximates. Per-statement strictness would gut the app for
            // ZERO real-world gain: Validate only ever sees SHIPPED/trusted SQL (diagnostic
            // scripts + op-rendered remediation queries), never attacker input. If an untrusted-
            // input path is ever added, gate IT specifically — do not tighten this shared wall.
            // (EveryShippedScript_StillPassesValidate guards this contract.)
            foreach (var (pattern, reason) in BlockedPatterns)
            {
                if (pattern.IsMatch(sqlWithoutComments))
                {
                    bool isAllowedException = AllowedExceptions.Any(ex => ex.IsMatch(sqlWithoutComments));
                    if (!isAllowedException)
                    {
                        return SqlValidationResult.Blocked(reason, pattern.ToString());
                    }
                }
            }

            return SqlValidationResult.Safe();
        }

        /// <summary>
        /// Validates a SQL batch and throws if unsafe.
        /// </summary>
        /// <param name="sql">The SQL text to validate.</param>
        /// <param name="scriptName">Name of the script for error reporting.</param>
        /// <exception cref="SqlSafetyException">Thrown when the SQL contains blocked patterns.</exception>
        public static void ValidateOrThrow(string sql, string scriptName = "unknown")
        {
            var result = Validate(sql);
            if (!result.IsSafe)
            {
                throw new SqlSafetyException(
                    $"Script '{scriptName}' blocked: {result.Reason}",
                    scriptName,
                    result.Reason);
            }
        }

        /// <summary>
        /// Validates that a USE statement (if present) only targets allowed databases.
        /// </summary>
        /// <param name="sql">The SQL batch to check.</param>
        /// <returns>Validation result.</returns>
        public static SqlValidationResult ValidateDatabaseScope(string sql)
        {
            if (string.IsNullOrWhiteSpace(sql))
                return SqlValidationResult.Safe();

            var usePattern = new Regex(@"\bUSE\s+\[(\w+)\]?", RegexOptions.IgnoreCase);
            var matches = usePattern.Matches(sql);

            foreach (Match match in matches)
            {
                var dbName = match.Groups[1].Value;
                if (!AllowedDatabases.Contains(dbName))
                {
                    return SqlValidationResult.Blocked(
                        $"USE [{dbName}] targets a database outside the allowed list ({string.Join(", ", AllowedDatabases)})",
                        match.Value);
                }
            }

            return SqlValidationResult.Safe();
        }

        /// <summary>
        /// Strips single-line (--) and block (/* */) comments from SQL to prevent
        /// hiding malicious code in comments that might bypass pattern matching.
        /// </summary>
        private static string StripComments(string sql)
        {
            // Remove block comments
            var result = Regex.Replace(sql, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            // Remove single-line comments
            result = Regex.Replace(result, @"--[^\r\n]*", " ");
            return result;
        }
    }

    /// <summary>
    /// Result of SQL safety validation.
    /// </summary>
    public class SqlValidationResult
    {
        public bool IsSafe { get; private set; }
        public string Reason { get; private set; } = string.Empty;
        public string MatchedPattern { get; private set; } = string.Empty;

        public static SqlValidationResult Safe() => new() { IsSafe = true };

        public static SqlValidationResult Blocked(string reason, string matchedPattern) => new()
        {
            IsSafe = false,
            Reason = reason,
            MatchedPattern = matchedPattern
        };
    }

    /// <summary>
    /// Three-way classification produced by <see cref="SqlSafetyValidator.Classify"/>.
    /// </summary>
    public enum SqlClassification
    {
        /// <summary>Read-only SQL; safe to run on the read path.</summary>
        Safe,

        /// <summary>
        /// A write authorised by a registered remediation context. Runnable ONLY
        /// through the remediation runner's gates — never on the read path.
        /// </summary>
        Remediation,

        /// <summary>Never runs. Free-form writes and unauthorised write text.</summary>
        Blocked
    }

    /// <summary>
    /// Proof that a registered remediation template authorised a given write.
    /// Supplying this context is the ONLY way <see cref="SqlSafetyValidator.Classify"/>
    /// will return <see cref="SqlClassification.Remediation"/>; an unregistered or
    /// empty key leaves the SQL Blocked. The context is constructed by the
    /// remediation runner from the template store (later build steps), never from
    /// untrusted UI input.
    /// </summary>
    public class RemediationContext
    {
        /// <summary>
        /// The registered template key authorising the write (e.g. <c>"MAXDOP"</c>).
        /// Must match a key the validator recognises as registered, or the SQL stays Blocked.
        /// </summary>
        public string RegisteredTemplateKey { get; }

        public RemediationContext(string registeredTemplateKey)
        {
            RegisteredTemplateKey = registeredTemplateKey ?? string.Empty;
        }
    }

    /// <summary>
    /// Exception thrown when a SQL script fails safety validation.
    /// </summary>
    public class SqlSafetyException : Exception
    {
        public string ScriptName { get; }
        public string BlockedReason { get; }

        public SqlSafetyException(string message, string scriptName, string blockedReason)
            : base(message)
        {
            ScriptName = scriptName;
            BlockedReason = blockedReason;
        }
    }
}
