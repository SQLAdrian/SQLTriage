/* In the name of God, the Merciful, the Compassionate */
/*
 * Host-probe lane — build step 1 (one probe, prove it, then breadth).
 *
 * Some validated checks (SPN/Kerberos audit, Power Plan, disk-offset alignment,
 * NTFS allocation unit) are NOT T-SQL: they need a host/OS/AD probe, which the
 * app runs AGENTLESS via dbatools (Test-Dba*) using the OPERATOR'S Windows creds
 * (decided 2026-06-19; see .handoff/BRIEF_HOST_PROBE_PATH_2026-06-19.md).
 *
 * REALITY (live-verified 2026-06-19): the WMI probes (PowerPlan/disk/NTFS) require
 * the operator to run ELEVATED on the host. So this lane is FAIL-CLOSED on
 * elevation: a non-elevated session returns NeedsElevation, NEVER a false
 * "compliant". A probe that could not run must never report as passed — same
 * discipline as the remediation gates.
 *
 * This is an EXTENSION of the existing read-only dbatools path (PowerShellService
 * .ExecuteAsDataTableAsync(importDbatools:true)), not a new subsystem.
 */

using System;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data.Services.HostProbe
{
    /// <summary>
    /// Capability gate for the host-probe lane (mirrors IRemediationCapability).
    /// Host probes cross the SQL->OS trust boundary, so they are gated like
    /// dev-tools: off unless the build/licence + operator grant it.
    /// </summary>
    public interface IHostProbeCapability
    {
        bool IsGranted { get; }
    }

    /// <summary>Fail-closed default: host probing denied. Production stays here
    /// until a build/licence explicitly grants it.</summary>
    public sealed class DeniedHostProbeCapability : IHostProbeCapability
    {
        public bool IsGranted => false;
    }

    /// <summary>Granted impl (dev harness / managed tier).</summary>
    public sealed class GrantedHostProbeCapability : IHostProbeCapability
    {
        public bool IsGranted => true;
    }

    /// <summary>Distinct terminal states — never a bare bool. A probe that did not
    /// run (capability denied, not elevated, host unreachable) must be visibly
    /// distinct from a real Compliant/NotCompliant verdict.</summary>
    public enum HostProbeOutcome
    {
        Compliant,        // probe ran, host matches best practice
        NotCompliant,     // probe ran, host deviates (a real finding)
        NeedsElevation,   // operator not running elevated — cannot probe (NOT a pass)
        CapabilityDenied, // lane not granted in this build/licence
        CouldNotProbe,    // host unreachable / WMI denied / dbatools error
    }

    public sealed record HostProbeResult(
        string ProbeKey,
        string Target,
        HostProbeOutcome Outcome,
        string Message,
        string? Detail = null)
    {
        /// <summary>True only for a verdict that actually ran. Callers must not
        /// treat NeedsElevation/CapabilityDenied/CouldNotProbe as a pass.</summary>
        public bool DidProbe => Outcome is HostProbeOutcome.Compliant or HostProbeOutcome.NotCompliant;
    }

    /// <summary>
    /// Runs read-only host probes via dbatools (operator creds, agentless).
    /// Step 1 implements ONE probe (Power Plan); the others (SPN, disk, NTFS)
    /// follow the same shape once this is proven end-to-end.
    /// </summary>
    public sealed class HostProbeService
    {
        private readonly PowerShellService _ps;
        private readonly IHostProbeCapability _capability;
        private readonly ILogger<HostProbeService> _logger;

        public HostProbeService(
            PowerShellService ps,
            IHostProbeCapability capability,
            ILogger<HostProbeService> logger)
        {
            _ps = ps;
            _capability = capability;
            _logger = logger;
        }

        /// <summary>Whether the current process is running elevated (admin). The
        /// WMI host probes need this; checked before every probe so we fail closed.</summary>
        public static bool IsElevated()
        {
            try
            {
                if (!OperatingSystem.IsWindows()) return false;
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false; // can't tell => treat as not-elevated (fail closed)
            }
        }

        // ── Probes ──────────────────────────────────────────────────────────
        // Each probe supplies a dbatools command + a row->verdict mapper. The two
        // gates (capability, elevation) and the execute/null handling live ONCE in
        // RunGatedProbeAsync so the fail-closed posture can't drift per-probe.
        // All host probes need admin-on-host (verified 2026-06-19: PowerPlan AND
        // Test-DbaSpn both report "elevation required"), so they share one gate.

        /// <summary>Power Plan (Test-DbaPowerPlan). SQL hosts should run High
        /// Performance; "Balanced" throttles CPU. Read-only WMI.</summary>
        public Task<HostProbeResult> ProbePowerPlanAsync(string computerName, CancellationToken ct = default) =>
            RunGatedProbeAsync("host.powerplan", computerName,
                $"Test-DbaPowerPlan -ComputerName '{Escape(computerName)}' -EnableException | Select-Object ActivePowerPlan, RecommendedPowerPlan, IsBest",
                row =>
                {
                    var active = Col(row, "ActivePowerPlan") ?? "(unknown)";
                    var rec = Col(row, "RecommendedPowerPlan") ?? "High Performance";
                    return Bool(row, "IsBest")
                        ? (HostProbeOutcome.Compliant, $"Power Plan '{active}' is the recommended setting.")
                        : (HostProbeOutcome.NotCompliant, $"Power Plan is '{active}' (recommended '{rec}'); non-High-Performance plans throttle CPU and degrade SQL Server.");
                }, ct);

        /// <summary>Disk allocation-unit / partition alignment (Test-DbaDiskAllocation).
        /// NTFS volumes hosting SQL data should use 64KB allocation units. Read-only WMI.</summary>
        public Task<HostProbeResult> ProbeDiskAllocationAsync(string computerName, CancellationToken ct = default) =>
            RunGatedProbeAsync("host.diskalloc", computerName,
                $"Test-DbaDiskAllocation -ComputerName '{Escape(computerName)}' -EnableException | Select-Object Name, BlockSize, IsBestPractice",
                row =>
                {
                    var name = Col(row, "Name") ?? "(volume)";
                    var block = Col(row, "BlockSize") ?? "?";
                    return Bool(row, "IsBestPractice")
                        ? (HostProbeOutcome.Compliant, $"Volume {name} allocation unit {block} matches best practice (64KB).")
                        : (HostProbeOutcome.NotCompliant, $"Volume {name} allocation unit is {block} (best practice: 64KB for SQL data/log volumes).");
                }, ct);

        /// <summary>SPN / Kerberos registration (Test-DbaSpn). Missing/incorrect SPNs
        /// break Kerberos auth and force NTLM fallback. AD probe; needs elevation.</summary>
        public Task<HostProbeResult> ProbeSpnAsync(string computerName, CancellationToken ct = default) =>
            RunGatedProbeAsync("host.spn", computerName,
                // Aggregate: any required SPN not set => NotCompliant. -EnableException so a
                // non-domain / no-rows case surfaces as CouldNotProbe, never a false pass.
                $"$s = Test-DbaSpn -ComputerName '{Escape(computerName)}' -EnableException; " +
                "[pscustomobject]@{ Missing = (@($s | Where-Object {{ -not $_.IsSet }}).Count); Total = (@($s).Count) }",
                row =>
                {
                    var missing = Col(row, "Missing") ?? "0";
                    var total = Col(row, "Total") ?? "0";
                    return missing == "0"
                        ? (HostProbeOutcome.Compliant, $"All {total} required SPNs are registered (Kerberos auth intact).")
                        : (HostProbeOutcome.NotCompliant, $"{missing} of {total} required SPNs are NOT registered — Kerberos will fail / fall back to NTLM. Register via setspn or Set-DbaSpn.");
                }, ct);

        /// <summary>Windows-login / AD validation (Test-DbaWindowsLogin). Flags SQL
        /// Windows logins/groups that are disabled, locked out, or no longer exist in
        /// AD (orphaned access). AD query; needs elevation. Note: the SqlInstance is
        /// the target here (this one connects to SQL, then validates its logins vs AD).</summary>
        public Task<HostProbeResult> ProbeWindowsLoginsAsync(string sqlInstance, CancellationToken ct = default) =>
            RunGatedProbeAsync("host.adlogin", sqlInstance,
                $"$w = Test-DbaWindowsLogin -SqlInstance '{Escape(sqlInstance)}' -EnableException; " +
                "[pscustomobject]@{ Problem = (@($w | Where-Object {{ $_.Disabled -or $_.LockedOut -or (-not $_.Found) }}).Count); Total = (@($w).Count) }",
                row =>
                {
                    var problem = Col(row, "Problem") ?? "0";
                    var total = Col(row, "Total") ?? "0";
                    return problem == "0"
                        ? (HostProbeOutcome.Compliant, $"All {total} Windows logins/groups validate against AD (none disabled/locked/orphaned).")
                        : (HostProbeOutcome.NotCompliant, $"{problem} of {total} Windows logins/groups are disabled, locked out, or missing from AD (orphaned access).");
                }, ct);

        // ── Shared gated-probe runner (fail-closed in ONE place) ────────────
        private async Task<HostProbeResult> RunGatedProbeAsync(
            string key, string computerName, string command,
            Func<System.Data.DataRow, (HostProbeOutcome, string)> map,
            CancellationToken ct)
        {
            // Gate 1: capability.
            if (!_capability.IsGranted)
                return new HostProbeResult(key, computerName, HostProbeOutcome.CapabilityDenied,
                    "Host-probe capability is not granted in this build/licence.");

            // Gate 2: elevation. All host probes need admin-on-host; never fake a pass.
            if (!IsElevated())
                return new HostProbeResult(key, computerName, HostProbeOutcome.NeedsElevation,
                    "Run SQLTriage elevated (administrator) to run host probes. Not probing - this is NOT a pass.");

            var res = await _ps.ExecuteAsDataTableAsync(command, importDbatools: true, timeoutSeconds: 60, cancellationToken: ct)
                               .ConfigureAwait(false);

            if (!res.Success || res.Data == null || res.Data.Rows.Count == 0)
            {
                _logger.LogWarning("Host probe {Key} could not run on {Host}: {Err}", key, computerName, res.Error ?? res.ParseError);
                return new HostProbeResult(key, computerName, HostProbeOutcome.CouldNotProbe,
                    "Could not run the host probe (host unreachable, not domain-joined, or WMI/AD access denied).",
                    res.Error ?? res.ParseError);
            }

            var (outcome, message) = map(res.Data.Rows[0]);
            return new HostProbeResult(key, computerName, outcome, message);
        }

        private static string? Col(System.Data.DataRow row, string c) =>
            row.Table.Columns.Contains(c) ? row[c]?.ToString() : null;

        private static bool Bool(System.Data.DataRow row, string c) =>
            row.Table.Columns.Contains(c) && bool.TryParse(row[c]?.ToString(), out var b) && b;

        private static string Escape(string s) => (s ?? string.Empty).Replace("'", "''");
    }
}
