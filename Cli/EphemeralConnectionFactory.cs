/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using SQLTriage.Data.Models;

namespace SQLTriage.Cli;

/// <summary>
/// Builds in-memory-only <see cref="ServerConnection"/> objects for --audit runs.
///
/// Never touches <see cref="SQLTriage.Data.ServerConnectionManager"/> or
/// Config/server-connections.json — the connection this factory builds lives only for the
/// duration of the CLI process and is never added/updated/removed via the manager (those are
/// the only methods that persist to disk), so the persisted file is byte-identical before and
/// after any --audit run.
///
/// Credentials are MEMORY-ONLY and bypass <see cref="SQLTriage.Data.CredentialProtector"/>
/// entirely: <see cref="ServerConnection.Password"/> is set directly to the plaintext value
/// (never via <c>SetPassword</c>, which calls <c>CredentialProtector.Encrypt</c>). At read
/// time, <c>ServerConnection.GetConnectionString</c> calls
/// <c>CredentialProtector.Decrypt(Password)</c> — but Decrypt only invokes DPAPI/AES when the
/// value is prefixed "aes:"/"enc:"; for an unprefixed plaintext value it falls through to the
/// "legacy plaintext" branch and returns the string unchanged with NO cryptographic call
/// (CredentialProtector.cs, Decrypt method, the final fallback branch). That matters because
/// DPAPI-CurrentUser is unavailable under a no-profile Task Scheduler run — this path never
/// reaches it.
/// </summary>
public static class EphemeralConnectionFactory
{
    /// <summary>
    /// Builds a single ephemeral <see cref="ServerConnection"/> carrying this run's auth
    /// settings. The same connection object is reused across every server named in
    /// <see cref="AuditCliArgs.Servers"/> — <c>CheckExecutionService.ExecuteChecksAsync</c>
    /// takes the target server name as a separate parameter, so one connection can serve many
    /// servers as long as they share the same auth (which --audit's single --auth/--user/
    /// --password-env flag set implies).
    /// </summary>
    public static ServerConnection Build(AuditCliArgs args)
    {
        var conn = new ServerConnection
        {
            Id = "cli-ephemeral-" + Guid.NewGuid().ToString("N"),
            ServerNames = string.Join(",", args.Servers),
            Database = "master",
            HasSqlWatch = false,
            // Local/enterprise SQL instances commonly present a self-signed certificate.
            // Trust it for this operator-initiated, ephemeral audit connection — matches the
            // TrustServerCertificate=true convention already used for this machine's own
            // local test connections (Config/server-connections.json: ".", ".\old2017",
            // ".\new2022" all carry true). A CLI --trust-cert opt-out is not implemented —
            // flagged as a known scope limitation, not exercised by this session's verify plan.
            TrustServerCertificate = true,
            ConnectionTimeout = 10,
            IsEnabled = true,
        };

        if (string.Equals(args.Auth, "sql", StringComparison.OrdinalIgnoreCase))
        {
            conn.UseWindowsAuthentication = false;
            conn.AuthenticationType = AuthenticationTypes.SqlServer;
            conn.Username = args.User;
            conn.Password = args.Password; // plaintext, memory-only — see class doc
        }
        else
        {
            conn.UseWindowsAuthentication = true;
            conn.AuthenticationType = AuthenticationTypes.Windows;
        }

        return conn;
    }

    /// <summary>
    /// Opens (and immediately closes) a real connection to confirm the server is reachable and
    /// auth succeeds, BEFORE any check runs against it. A failure here maps to exit code 2
    /// (server unreachable / auth failed) — distinct from a mid-run check error (exit code 1).
    /// </summary>
    public static async Task<(bool Ok, string? Error)> PreflightAsync(
        ServerConnection connection, string serverName, CancellationToken ct)
    {
        try
        {
            var connectionString = connection.GetConnectionString(serverName, "master");
            await using var sql = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
            await sql.OpenAsync(ct).ConfigureAwait(false);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Same reachability check as <see cref="PreflightAsync"/>, plus the resolved server
    /// identity (<c>@@SERVERNAME</c> — the same value <c>SqlAssessmentService</c> stamps onto
    /// every <c>AssessmentResult.ThisServer</c>), captured on the SAME connection preflight
    /// already opens rather than a second round trip. Used only by
    /// <c>CliAuditHost.RunAuditEvidenceReportAsync</c> so two requested names that resolve to one
    /// physical server (e.g. <c>.\new2022</c> and <c>MSI\NEW2022</c>) can be told apart from two
    /// genuinely distinct servers before a compliance artifact is built from them — see
    /// <see cref="SQLTriage.Cli.AuditEvidenceIdentityGrouping"/>.
    ///
    /// A failure querying the identity AFTER the connection already opened successfully does not
    /// fail the preflight: reachability was already proved, and this must under-detect (no
    /// resolved identity to group on) rather than turn a reachable server into a refused one.
    /// </summary>
    public static async Task<(bool Ok, string? Error, string? ResolvedIdentity)> PreflightWithIdentityAsync(
        ServerConnection connection, string serverName, CancellationToken ct)
    {
        try
        {
            var connectionString = connection.GetConnectionString(serverName, "master");
            await using var sql = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
            await sql.OpenAsync(ct).ConfigureAwait(false);

            string? identity = null;
            try
            {
                await using var cmd = sql.CreateCommand();
                cmd.CommandText = "SELECT @@SERVERNAME";
                var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                identity = result?.ToString();
            }
            catch
            {
                // See class doc: reachability already stands, only identity-grouping degrades.
            }

            return (true, null, identity);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }
}
