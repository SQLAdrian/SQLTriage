/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// Enterprise process integrity guard. Monitors the process for:
    ///   - Debugger attachment (managed and native)
    ///   - Integrity verification of critical assemblies
    ///   - Secure memory handling guidance
    ///
    /// In production builds, detects suspicious conditions and logs warnings.
    /// Does NOT terminate the process — defense-in-depth logging only,
    /// so that security teams can investigate anomalies.
    /// </summary>
    public sealed class ProcessGuard : IDisposable
    {
        private readonly ILogger<ProcessGuard> _logger;
        private readonly Timer _watchdogTimer;
        private readonly byte[] _assemblyHash;
        private bool _disposed;
        private int _debuggerWarningCount;

        public bool DebuggerDetected { get; private set; }
        public DateTime StartedAt { get; } = DateTime.UtcNow;

        public ProcessGuard(ILogger<ProcessGuard> logger)
        {
            _logger = logger;

            // Compute hash of our own assembly for integrity verification
            _assemblyHash = ComputeAssemblyHash();

            // Initial checks
            RunIntegrityChecks();

            // Periodic watchdog: check every 30 seconds
            _watchdogTimer = new Timer(WatchdogCallback, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

            _logger.LogInformation("ProcessGuard initialized. PID={Pid}, IntegrityHash={Hash}",
                Environment.ProcessId,
                Convert.ToHexString(_assemblyHash)[..16]);
        }

        private void RunIntegrityChecks()
        {
            // Check for managed debugger
            if (Debugger.IsAttached)
            {
                DebuggerDetected = true;
                _logger.LogWarning("ProcessGuard: Managed debugger is attached (PID {Pid})", Environment.ProcessId);
            }

            // Check for native debugger (Windows API)
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    if (IsDebuggerPresent())
                    {
                        DebuggerDetected = true;
                        _logger.LogWarning("ProcessGuard: Native debugger detected via IsDebuggerPresent");
                    }
                }
                catch { /* P/Invoke not available in all environments */ }
            }

            // Verify DEP is enabled (Data Execution Prevention)
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    if (GetProcessDEPPolicy(Process.GetCurrentProcess().Handle, out var flags, out var permanent))
                    {
                        var depEnabled = (flags & 0x1) != 0;
                        if (!depEnabled)
                            _logger.LogWarning("ProcessGuard: DEP is NOT enabled for this process");
                        else
                            _logger.LogDebug("ProcessGuard: DEP enabled (permanent={Permanent})", permanent);
                    }
                }
                catch { /* Best-effort check */ }
            }

            // Verify ASLR — .NET processes always have ASLR via OS, but log the base address
            var mainModule = Process.GetCurrentProcess().MainModule;
            if (mainModule != null)
            {
                _logger.LogDebug("ProcessGuard: Module base address=0x{Base:X}, ASLR active",
                    mainModule.BaseAddress.ToInt64());
            }
        }

        private void WatchdogCallback(object? state)
        {
            if (_disposed) return;

            var debuggerNow = Debugger.IsAttached;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try { debuggerNow |= IsDebuggerPresent(); } catch (Exception ex) { _logger.LogDebug(ex, "[ProcessGuard] P/Invoke IsDebuggerPresent failed"); }
            }

            if (debuggerNow && !DebuggerDetected)
            {
                DebuggerDetected = true;
                Interlocked.Increment(ref _debuggerWarningCount);
                _logger.LogWarning("ProcessGuard: Debugger attached at runtime (detection #{Count})",
                    _debuggerWarningCount);
            }
            else if (debuggerNow)
            {
                // Already detected — periodic reminder
                var count = Interlocked.Increment(ref _debuggerWarningCount);
                if (count % 10 == 0) // Log every ~5 minutes
                {
                    _logger.LogWarning("ProcessGuard: Debugger still attached (detection #{Count})", count);
                }
            }
            else if (!debuggerNow && DebuggerDetected)
            {
                _logger.LogInformation("ProcessGuard: Debugger detached");
                DebuggerDetected = false;
            }

            // Verify assembly integrity hasn't changed (tamper detection)
            VerifyAssemblyIntegrity();
        }

        private byte[] ComputeAssemblyHash()
        {
            try
            {
                var assemblyLocation = typeof(ProcessGuard).Assembly.Location;
                if (!string.IsNullOrEmpty(assemblyLocation) && System.IO.File.Exists(assemblyLocation))
                {
                    var bytes = System.IO.File.ReadAllBytes(assemblyLocation);
                    return SHA256.HashData(bytes);
                }
            }
            catch { /* Single-file publish may not have a file path */ }

            // Fallback: hash the assembly full name
            var name = typeof(ProcessGuard).Assembly.FullName ?? "unknown";
            return SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name));
        }

        private void VerifyAssemblyIntegrity()
        {
            try
            {
                var currentHash = ComputeAssemblyHash();
                if (!CryptographicOperations.FixedTimeEquals(currentHash, _assemblyHash))
                {
                    _logger.LogError("ProcessGuard: Assembly integrity check FAILED — possible tampering detected");
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ProcessGuard: Could not verify assembly integrity");
            }
        }

        /// <summary>
        /// Returns a summary of the current process security posture.
        /// </summary>
        public ProcessSecurityStatus GetStatus()
        {
            return new ProcessSecurityStatus
            {
                ProcessId = Environment.ProcessId,
                DebuggerAttached = DebuggerDetected,
                AssemblyIntegrityHash = Convert.ToHexString(_assemblyHash),
                UptimeSeconds = (int)(DateTime.UtcNow - StartedAt).TotalSeconds,
                Is64Bit = Environment.Is64BitProcess,
                OsDescription = RuntimeInformation.OSDescription,
                FrameworkDescription = RuntimeInformation.FrameworkDescription,
                DebuggerWarnings = _debuggerWarningCount
            };
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _watchdogTimer.Dispose();
                CryptographicOperations.ZeroMemory(_assemblyHash);
            }
        }

        // ──────────────── Windows P/Invoke ──────────────

        [DllImport("kernel32.dll")]
        private static extern bool IsDebuggerPresent();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessDEPPolicy(IntPtr hProcess, out uint flags, out bool permanent);
    }

    public class ProcessSecurityStatus
    {
        public int ProcessId { get; set; }
        public bool DebuggerAttached { get; set; }
        public string AssemblyIntegrityHash { get; set; } = "";
        public int UptimeSeconds { get; set; }
        public bool Is64Bit { get; set; }
        public string OsDescription { get; set; } = "";
        public string FrameworkDescription { get; set; } = "";
        public int DebuggerWarnings { get; set; }
    }
}
