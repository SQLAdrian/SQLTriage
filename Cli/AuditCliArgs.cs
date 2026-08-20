/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SQLTriage.Data.Services;

namespace SQLTriage.Cli;

/// <summary>
/// Parsed, validated arguments for <c>SQLTriage.exe --audit</c>. <see cref="Parse"/> is the
/// single entry point — it both tokenizes argv and runs every pre-run validation (arg shape,
/// bundle presence, --out writability, --password-env presence) so a caller either gets a
/// fully-usable <see cref="AuditCliArgs"/> or a human-readable error with no side effects
/// beyond the checks themselves (no server touched, no output written).
///
/// Exit-code contract (see CliAuditHost): any <see cref="AuditCliArgsResult.Success"/> == false
/// here maps to process exit code 3, always BEFORE any server connection is attempted.
/// </summary>
public sealed class AuditCliArgs
{
    public IReadOnlyList<string> Servers { get; init; } = Array.Empty<string>();

    /// <summary>"integrated" or "sql" (lower-case, already validated).</summary>
    public string Auth { get; init; } = "integrated";

    public string? User { get; init; }

    /// <summary>
    /// Resolved plaintext password (read from the --password-env environment variable).
    /// MEMORY-ONLY — never written to disk, never logged, never accepted as a literal
    /// command-line argument (--password is explicitly rejected in <see cref="Parse"/>).
    /// </summary>
    public string? Password { get; init; }

    public IReadOnlyList<string> Formats { get; init; } = new[] { "json" };

    public string OutDir { get; init; } = "output";

    public int Parallel { get; init; } = 4;

    /// <summary>
    /// Per-run override for concurrent queries against ONE instance (--concurrency), or null to
    /// use the persisted Settings value. A DIFFERENT axis from <see cref="Parallel"/>, which is
    /// how many SERVERS run at once. Null is threaded through to
    /// CheckExecutionService.ExecuteChecksAsync's own optional override, so "not supplied" keeps
    /// the existing precedence (setting → shipped default) rather than pinning a value here.
    /// </summary>
    public int? Concurrency { get; init; }

    /// <summary>Resolved, existence-checked path to the corpus bundle (default: the shipped
    /// free bundle next to the exe, Config/free-bundle.dat).</summary>
    public string BundlePath { get; init; } = "";

    public bool Quiet { get; init; }

    public static readonly string[] ValidFormats = { "json", "csv", "pdf" };
    public static readonly string[] ValidAuth = { "integrated", "sql" };

    /// <summary>--concurrency bounds. Taken from UserSettingsService rather than restated, so the
    /// CLI cannot drift from the range the Settings page and CheckExecutionService already clamp
    /// to.</summary>
    public const int ConcurrencyMin = SQLTriage.Data.UserSettingsService.AuditConcurrencyMin;
    public const int ConcurrencyMax = SQLTriage.Data.UserSettingsService.AuditConcurrencyMax;

    /// <summary>
    /// The --help text. Every option and exit code below is one this parser and
    /// <see cref="CliAuditHost"/> actually implement — check both before editing a line here.
    /// </summary>
    public static readonly string Usage = $$"""
SQLTriage --audit — run the corpus audit headlessly and write its artifacts.

Usage:
  SQLTriage.exe --audit --servers <name[,name...]|@file> [options]

Options:
  --servers <v>         REQUIRED. Comma-separated instance names, or @file (one name
                        per line; blank lines and #-comment lines are skipped).
  --auth <mode>         integrated (default) | sql
  --user <name>         SQL login name. Only valid with --auth sql.
  --password-env <VAR>  NAME of an environment variable holding the password. Only
                        valid with --auth sql. A plaintext --password is never accepted.
  --format <list>       Comma list of json,csv,pdf. Default: json
  --out <dir>           Output directory, created if missing. Default: output
  --parallel <n>        How many SERVERS are audited at once. Default: 4
  --concurrency <n>     How many queries run at once against ONE instance,
                        {{ConcurrencyMin}}-{{ConcurrencyMax}}. Default: the Settings > Performance value.
  --bundle <path>       Corpus bundle. Default: Config/free-bundle.dat beside the exe.
  --quiet               Console shows warnings and errors only.
  --help, -h, -?        Print this help and exit 0.
  --version             Print the product version and exit 0.

Exit codes:
  0  clean run
  1  a check errored, or a requested output artifact failed to write
  2  a requested server was not assessed — unreachable / auth failed at preflight,
     or not covered by the licence's seat count
  3  bad arguments, a missing or invalid bundle, or an unwritable --out
  4  refused by the corpus allocation (instances per 24h). No server was contacted
     and no artifact was written; stderr says which allowance applied and why.
     On the free build that cap is a safety limit, not a paywall, and it is raised
     at no charge — ask at sqldba.org.
""";

    public static AuditCliArgsResult Parse(string[] args)
    {
        var servers = new List<string>();
        var auth = "integrated";
        string? user = null;
        string? passwordEnvVar = null;
        var formats = new List<string> { "json" };
        var outDir = "output";
        var parallel = 4;
        int? concurrency = null;
        string? bundlePath = null;
        var quiet = false;

        for (var i = 0; i < args.Length; i++)
        {
            var raw = args[i];
            switch (raw.ToLowerInvariant())
            {
                case "--audit":
                    // Consumed by Program.cs's mode dispatch; harmless to see it again here.
                    break;

                // Answered immediately and unconditionally: --help must work even when the rest
                // of the command line is nonsense (that is usually WHY it is being asked for),
                // so neither of these waits for the validation block below.
                case "--help":
                case "-h":
                case "-?":
                case "/?":
                    return AuditCliArgsResult.Help();

                case "--version":
                    return AuditCliArgsResult.Version();

                case "--servers":
                    if (++i >= args.Length) return AuditCliArgsResult.Fail("--servers requires a value (name[,name...] or @file)");
                    var parsedServers = ParseServers(args[i], out var serversError);
                    if (parsedServers is null) return AuditCliArgsResult.Fail(serversError!);
                    servers = parsedServers;
                    break;

                case "--auth":
                    if (++i >= args.Length) return AuditCliArgsResult.Fail("--auth requires a value (integrated|sql)");
                    auth = args[i].ToLowerInvariant();
                    break;

                case "--user":
                    if (++i >= args.Length) return AuditCliArgsResult.Fail("--user requires a value");
                    user = args[i];
                    break;

                case "--password-env":
                    if (++i >= args.Length) return AuditCliArgsResult.Fail("--password-env requires a value (an environment variable NAME)");
                    passwordEnvVar = args[i];
                    break;

                case "--password":
                    // Security posture (non-negotiable): a plaintext password is NEVER accepted
                    // on the command line — reject loudly rather than silently ignoring it.
                    return AuditCliArgsResult.Fail(
                        "--password is not accepted. Plaintext passwords are never read from the command " +
                        "line or a file — use --auth sql --user <name> --password-env <VAR> instead.");

                case "--format":
                    if (++i >= args.Length) return AuditCliArgsResult.Fail("--format requires a value (comma list of json,csv,pdf)");
                    formats = args[i].Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim().ToLowerInvariant())
                        .Where(s => s.Length > 0)
                        .ToList();
                    break;

                case "--out":
                    if (++i >= args.Length) return AuditCliArgsResult.Fail("--out requires a value (a directory path)");
                    outDir = args[i];
                    break;

                case "--parallel":
                    if (++i >= args.Length) return AuditCliArgsResult.Fail("--parallel requires a value (a positive integer)");
                    if (!int.TryParse(args[i], out parallel) || parallel < 1)
                        return AuditCliArgsResult.Fail($"--parallel must be a positive integer, got '{args[i]}'");
                    break;

                case "--concurrency":
                    if (++i >= args.Length) return AuditCliArgsResult.Fail($"--concurrency requires a value ({ConcurrencyMin}-{ConcurrencyMax})");
                    if (!int.TryParse(args[i], out var parsedConcurrency)
                        || parsedConcurrency < ConcurrencyMin || parsedConcurrency > ConcurrencyMax)
                    {
                        // Rejected, never clamped: silently accepting --concurrency 64 and running
                        // 16 is the "absent/invalid value resolves to something else" shape this
                        // codebase has been bitten by. The operator gets told, before anything runs.
                        return AuditCliArgsResult.Fail(
                            $"--concurrency must be an integer between {ConcurrencyMin} and {ConcurrencyMax}, got '{args[i]}'");
                    }
                    concurrency = parsedConcurrency;
                    break;

                case "--bundle":
                    if (++i >= args.Length) return AuditCliArgsResult.Fail("--bundle requires a value (a path to the corpus bundle)");
                    bundlePath = args[i];
                    break;

                case "--quiet":
                    quiet = true;
                    break;

                default:
                    return AuditCliArgsResult.Fail($"Unknown argument: {raw}");
            }
        }

        // ── Validation (all pre-run; no server touched, nothing written) ──

        if (servers.Count == 0)
            return AuditCliArgsResult.Fail("--servers is required (name[,name...] or @file)");

        if (!ValidAuth.Contains(auth))
            return AuditCliArgsResult.Fail($"--auth must be 'integrated' or 'sql', got '{auth}'");

        string? password = null;
        if (auth == "sql")
        {
            if (string.IsNullOrWhiteSpace(user))
                return AuditCliArgsResult.Fail("--auth sql requires --user <name>");
            if (string.IsNullOrWhiteSpace(passwordEnvVar))
                return AuditCliArgsResult.Fail("--auth sql requires --password-env <VAR>");

            password = Environment.GetEnvironmentVariable(passwordEnvVar);
            if (string.IsNullOrEmpty(password))
                return AuditCliArgsResult.Fail($"--password-env '{passwordEnvVar}' is not set (or empty) in the environment");
        }
        else if (user != null || passwordEnvVar != null)
        {
            return AuditCliArgsResult.Fail("--user / --password-env are only valid together with --auth sql");
        }

        if (formats.Count == 0)
            return AuditCliArgsResult.Fail("--format must include at least one of json,csv,pdf");
        foreach (var f in formats)
            if (!ValidFormats.Contains(f))
                return AuditCliArgsResult.Fail($"--format contains unrecognized value '{f}' (valid: json,csv,pdf)");

        // Bundle: default = the shipped free bundle next to the exe (matches
        // LicenseService.TryUnlockFree's own resolution — Config/free-bundle.dat, falling
        // back to a bare free-bundle.dat directly in the install dir).
        var resolvedBundle = bundlePath ?? Path.Combine(AppContext.BaseDirectory, "Config", "free-bundle.dat");
        if (!File.Exists(resolvedBundle))
        {
            if (bundlePath is null)
            {
                var alt = Path.Combine(AppContext.BaseDirectory, Path.GetFileName(resolvedBundle));
                if (File.Exists(alt)) resolvedBundle = alt;
                else return AuditCliArgsResult.Fail($"No corpus bundle found (looked for '{resolvedBundle}' and '{alt}'). Reinstall or pass --bundle <path>.");
            }
            else
            {
                return AuditCliArgsResult.Fail($"--bundle path not found: {resolvedBundle}");
            }
        }

        // --out must be creatable and writable BEFORE any run.
        try
        {
            Directory.CreateDirectory(outDir);
            var probe = Path.Combine(outDir, $".sqltriage-cli-write-probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            return AuditCliArgsResult.Fail($"--out directory '{outDir}' is not writable: {ex.Message}");
        }

        return AuditCliArgsResult.Ok(new AuditCliArgs
        {
            Servers = servers,
            Auth = auth,
            User = user,
            Password = password,
            Formats = formats,
            OutDir = outDir,
            Parallel = parallel,
            Concurrency = concurrency,
            BundlePath = resolvedBundle,
            Quiet = quiet,
        });
    }

    /// <summary>
    /// "name[,name...]" or "@file" (one server per line; blank lines and "#"-comments skipped).
    ///
    /// Splits with <see cref="ServerAddress.SplitList"/>, not <c>Split(',')</c>. A comma is SQL
    /// Server's port separator, so <c>SQL01\INST,56510</c> is ONE server; the raw split read it as
    /// two, and this lane was the one the 2026-07-21 sweep missed (the desktop lane went through
    /// ServerConnection.GetServerList and was fixed then).
    ///
    /// Then every address is syntax-checked BEFORE the list leaves the parser. That ordering is
    /// the point of the change, not a tidiness preference: the next thing to look at this list is
    /// the corpus-demo allocation, which counts entries. A malformed argument that survived
    /// parsing as an extra entry came back as a COMMERCIAL refusal — "You asked for 2. Run fewer
    /// at a time" — for a single mistyped server. A parse problem now answers as a parse problem,
    /// naming the address and what was wrong with it, and the run stops at exit 3 (bad arguments)
    /// rather than exit 4 (allocation refused).
    ///
    /// AMENDED 2026-08-06: splitting with <see cref="ServerAddress.SplitList"/> and then checking
    /// each result was still one step too late. SplitList RESOLVES a comma it cannot make sense of
    /// by treating it as a list separator, so <c>SQL01\INST,5651Z</c> became two addresses, each
    /// perfectly valid on its own — and the allocation counted two. The comma is where the mistake
    /// is, so the judgement now happens AT the comma:
    /// <see cref="ServerAddress.TrySplitList"/> splits and judges in one pass and refuses what it
    /// cannot read as either a port or a second server.
    /// </summary>
    internal static List<string>? ParseServers(string value, out string? error)
    {
        error = null;

        if (value.StartsWith("@", StringComparison.Ordinal))
        {
            var path = value.Substring(1);
            if (!File.Exists(path))
            {
                error = $"--servers file not found: {path}";
                return null;
            }

            var source = $"--servers file '{path}'";
            var lines = new List<string>();
            foreach (var line in File.ReadAllLines(path)
                         .Select(l => l.Trim())
                         .Where(l => l.Length > 0 && !l.StartsWith("#", StringComparison.Ordinal)))
            {
                if (!ServerAddress.TrySplitList(line, out var parsed, out var parseReason))
                {
                    error = $"{source}: {parseReason}";
                    return null;
                }
                lines.AddRange(parsed);
            }

            if (lines.Count == 0)
            {
                error = $"--servers file '{path}' contains no server names";
                return null;
            }

            return ValidateAll(lines, source, out error);
        }

        if (!ServerAddress.TrySplitList(value, out var names, out var reason))
        {
            error = $"--servers: {reason}";
            return null;
        }

        if (names.Count == 0)
        {
            error = "--servers value is empty";
            return null;
        }

        return ValidateAll(names, "--servers", out error);
    }

    /// <summary>
    /// Returns the list unchanged, or null with an error naming the FIRST malformed address and
    /// the reason it is malformed. Refuses the whole invocation rather than dropping the bad entry:
    /// running an audit against a silently shortened list is how a server goes unassessed while
    /// the report reads clean.
    /// </summary>
    private static List<string>? ValidateAll(List<string> addresses, string source, out string? error)
    {
        foreach (var address in addresses)
        {
            if (ServerAddress.TryValidate(address, out var reason)) continue;

            error = $"{source}: '{address}' is not a valid server address - {reason}";
            return null;
        }

        error = null;
        return addresses;
    }
}

/// <summary>Result of <see cref="AuditCliArgs.Parse"/> — a usable <see cref="Args"/>, a
/// human-readable <see cref="Error"/>, or a request to print help/version and stop.</summary>
public sealed class AuditCliArgsResult
{
    public bool Success { get; private init; }
    public AuditCliArgs? Args { get; private init; }
    public string? Error { get; private init; }

    /// <summary>--help / -h / -? / /?. Not an error: the caller prints
    /// <see cref="AuditCliArgs.Usage"/> on STDOUT and exits 0.</summary>
    public bool HelpRequested { get; private init; }

    /// <summary>--version. Not an error: the caller prints the version on STDOUT and exits 0.</summary>
    public bool VersionRequested { get; private init; }

    public static AuditCliArgsResult Ok(AuditCliArgs args) => new() { Success = true, Args = args };
    public static AuditCliArgsResult Fail(string error) => new() { Success = false, Error = error };
    public static AuditCliArgsResult Help() => new() { Success = false, HelpRequested = true };
    public static AuditCliArgsResult Version() => new() { Success = false, VersionRequested = true };
}
