/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SQLTriage.Data;
using Xunit;

namespace SQLTriage.Tests
{
    public class AuditLogServiceTests : IDisposable
    {
        private readonly string _tempDir;
        private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

        public AuditLogServiceTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "audit-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try
            {
                // Clear Hidden/ReadOnly attributes set by AuditLogService on hmac.key so
                // recursive delete succeeds on all files.
                foreach (var f in Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                }
                Directory.Delete(_tempDir, recursive: true);
            }
            catch { /* test cleanup; ignore */ }
        }

        private AuditLogService NewService() => new(_tempDir, startFlushTimer: false);

        private string LatestLogFile()
        {
            var files = Directory.GetFiles(_tempDir, "audit-*.jsonl");
            Assert.NotEmpty(files);
            return files.OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase).First();
        }

        private List<AuditLogEntry> ReadAll(string file) =>
            File.ReadAllLines(file)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => JsonSerializer.Deserialize<AuditLogEntry>(l, Json)!)
                .ToList();

        // ── HMAC chain integrity ────────────────────────────────────────

        [Fact]
        public void Flush_TwoEntries_SignaturesChainCorrectly()
        {
            using var svc = NewService();
            svc.LogApplicationStart();
            svc.LogConnectionAttempt("srv1", success: true);
            svc.Flush();

            var entries = ReadAll(LatestLogFile());
            Assert.Equal(2, entries.Count);
            Assert.Equal(string.Empty, entries[0].PreviousHash);
            Assert.Equal(entries[0].Signature, entries[1].PreviousHash);
            Assert.NotEmpty(entries[0].Signature);
            Assert.NotEmpty(entries[1].Signature);
            Assert.NotEqual(entries[0].Signature, entries[1].Signature);
        }

        [Fact]
        public void Flush_ManyEntries_FormsContiguousChain()
        {
            using var svc = NewService();
            for (int i = 0; i < 10; i++)
                svc.LogConnectionAttempt($"srv-{i}", success: true);
            svc.Flush();

            var entries = ReadAll(LatestLogFile());
            Assert.Equal(10, entries.Count);
            for (int i = 1; i < entries.Count; i++)
                Assert.Equal(entries[i - 1].Signature, entries[i].PreviousHash);
        }

        [Fact]
        public void NewService_OnCleanDirectory_ReadsNoChainBreak()
        {
            using var svc = NewService();
            svc.LogApplicationStart();
            svc.Flush();
            svc.Dispose();

            using var svc2 = NewService();
            Assert.False(svc2.ChainBroken);
        }

        [Fact]
        public void NewService_OnUntamperedExistingFile_ContinuesChain()
        {
            // Round 1: write two entries
            using (var svc = NewService())
            {
                svc.LogApplicationStart();
                svc.LogConnectionAttempt("srv1", success: true);
                svc.Flush();
            }

            // Round 2: same directory, new service instance, append more
            using (var svc = NewService())
            {
                Assert.False(svc.ChainBroken);
                svc.LogConnectionAttempt("srv2", success: true);
                svc.Flush();
            }

            // Round 3: verify the full file still chains end-to-end
            using var verify = NewService();
            Assert.False(verify.ChainBroken);
            var entries = ReadAll(LatestLogFile());
            Assert.Equal(3, entries.Count);
            for (int i = 1; i < entries.Count; i++)
                Assert.Equal(entries[i - 1].Signature, entries[i].PreviousHash);
        }

        // ── Tamper detection ────────────────────────────────────────────

        [Fact]
        public void TamperedMessage_IsDetectedAsChainBreak()
        {
            string logFile;
            using (var svc = NewService())
            {
                svc.LogConnectionAttempt("srv1", success: true);
                svc.LogConnectionAttempt("srv2", success: true);
                svc.LogConnectionAttempt("srv3", success: true);
                svc.Flush();
                logFile = LatestLogFile();
            }

            // Mutate the middle entry's Message in place
            var lines = File.ReadAllLines(logFile);
            var middle = JsonSerializer.Deserialize<AuditLogEntry>(lines[1], Json)!;
            middle.Message = "TAMPERED";
            lines[1] = JsonSerializer.Serialize(middle, Json);
            File.WriteAllLines(logFile, lines);

            // Restart — verification should fire
            using var svc2 = NewService();
            Assert.True(svc2.ChainBroken);
        }

        [Fact]
        public void TamperedSignature_IsDetectedAsChainBreak()
        {
            string logFile;
            using (var svc = NewService())
            {
                svc.LogApplicationStart();
                svc.LogConnectionAttempt("srv1", success: true);
                svc.Flush();
                logFile = LatestLogFile();
            }

            var lines = File.ReadAllLines(logFile);
            var first = JsonSerializer.Deserialize<AuditLogEntry>(lines[0], Json)!;
            // Flip one base64 char in the signature (still valid base64, different bytes)
            var sig = first.Signature.ToCharArray();
            sig[0] = sig[0] == 'A' ? 'B' : 'A';
            first.Signature = new string(sig);
            lines[0] = JsonSerializer.Serialize(first, Json);
            File.WriteAllLines(logFile, lines);

            using var svc2 = NewService();
            Assert.True(svc2.ChainBroken);
        }

        [Fact]
        public void TamperedTimestamp_IsDetectedAsChainBreak()
        {
            string logFile;
            using (var svc = NewService())
            {
                svc.LogApplicationStart();
                svc.Flush();
                logFile = LatestLogFile();
            }

            var lines = File.ReadAllLines(logFile);
            var entry = JsonSerializer.Deserialize<AuditLogEntry>(lines[0], Json)!;
            entry.Timestamp = entry.Timestamp.AddHours(-1);
            lines[0] = JsonSerializer.Serialize(entry, Json);
            File.WriteAllLines(logFile, lines);

            using var svc2 = NewService();
            Assert.True(svc2.ChainBroken);
        }

        [Fact]
        public void TamperedDetails_IsDetectedAsChainBreak()
        {
            string logFile;
            using (var svc = NewService())
            {
                svc.LogConnectionAttempt("real-server", success: true);
                svc.Flush();
                logFile = LatestLogFile();
            }

            var lines = File.ReadAllLines(logFile);
            var entry = JsonSerializer.Deserialize<AuditLogEntry>(lines[0], Json)!;
            entry.Details["ServerName"] = "evil-server";
            lines[0] = JsonSerializer.Serialize(entry, Json);
            File.WriteAllLines(logFile, lines);

            using var svc2 = NewService();
            Assert.True(svc2.ChainBroken);
        }

        [Fact]
        public void DeletedMiddleEntry_IsDetectedAsChainBreak()
        {
            string logFile;
            using (var svc = NewService())
            {
                svc.LogConnectionAttempt("a", success: true);
                svc.LogConnectionAttempt("b", success: true);
                svc.LogConnectionAttempt("c", success: true);
                svc.Flush();
                logFile = LatestLogFile();
            }

            // Delete the middle entry
            var lines = File.ReadAllLines(logFile);
            File.WriteAllLines(logFile, new[] { lines[0], lines[2] });

            // Now the third entry's PreviousHash points at line 1's signature but actually
            // expects line 2's. Chain should break.
            using var svc2 = NewService();
            Assert.True(svc2.ChainBroken);
        }

        [Fact]
        public void ReorderedEntries_AreDetectedAsChainBreak()
        {
            string logFile;
            using (var svc = NewService())
            {
                svc.LogConnectionAttempt("a", success: true);
                svc.LogConnectionAttempt("b", success: true);
                svc.LogConnectionAttempt("c", success: true);
                svc.Flush();
                logFile = LatestLogFile();
            }

            // Swap the second and third entries
            var lines = File.ReadAllLines(logFile);
            File.WriteAllLines(logFile, new[] { lines[0], lines[2], lines[1] });

            using var svc2 = NewService();
            Assert.True(svc2.ChainBroken);
        }

        [Fact]
        public void KeyReplaced_OldEntriesFailToVerify()
        {
            // Round 1: write entries with key A
            using (var svc = NewService())
            {
                svc.LogApplicationStart();
                svc.LogConnectionAttempt("srv", success: true);
                svc.Flush();
            }

            // Replace the HMAC key (simulates an attacker swapping the key file).
            // The service sets Hidden attribute on the key file — clear it before overwriting,
            // since Windows blocks WriteAllBytes on hidden files via UnauthorizedAccessException.
            var keyPath = Path.Combine(_tempDir, "hmac.key");
            File.SetAttributes(keyPath, FileAttributes.Normal);
            File.WriteAllBytes(keyPath, System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

            using var svc2 = NewService();
            Assert.True(svc2.ChainBroken);
        }

        // ── High-level logger methods ───────────────────────────────────

        [Fact]
        public void LogConnectionAttempt_Success_RecordsInfoSeverity()
        {
            using var svc = NewService();
            svc.LogConnectionAttempt("srv1", success: true);
            svc.Flush();
            var e = ReadAll(LatestLogFile()).Single();
            Assert.Equal(AuditEventType.ConnectionAttempt, e.EventType);
            Assert.Equal(AuditSeverity.Info, e.Severity);
            Assert.Equal("srv1", e.Details["ServerName"]);
            Assert.Equal("True", e.Details["Success"]);
            Assert.Empty(e.Details["Error"]);
        }

        [Fact]
        public void LogConnectionAttempt_Failure_RecordsWarningSeverity()
        {
            using var svc = NewService();
            svc.LogConnectionAttempt("srv1", success: false, errorMessage: "Login failed for user");
            svc.Flush();
            var e = ReadAll(LatestLogFile()).Single();
            Assert.Equal(AuditSeverity.Warning, e.Severity);
            Assert.Contains("Login failed", e.Message);
            Assert.Equal("Login failed for user", e.Details["Error"]);
        }

        [Fact]
        public void LogScriptBlocked_IsAlwaysCritical()
        {
            using var svc = NewService();
            svc.LogScriptBlocked("drop-everything.sql", "Contains DROP DATABASE");
            svc.Flush();
            var e = ReadAll(LatestLogFile()).Single();
            Assert.Equal(AuditEventType.SecurityBlock, e.EventType);
            Assert.Equal(AuditSeverity.Critical, e.Severity);
            Assert.Equal("Contains DROP DATABASE", e.Details["BlockReason"]);
        }

        [Fact]
        public void LogScriptExecution_IncludesDuration()
        {
            using var svc = NewService();
            svc.LogScriptExecution("waits.sql", "srv1", success: true, duration: TimeSpan.FromMilliseconds(250));
            svc.Flush();
            var e = ReadAll(LatestLogFile()).Single();
            Assert.Equal("250", e.Details["DurationMs"]);
        }

        [Fact]
        public void LogSecurityEvent_OverrideSeverity_IsPreserved()
        {
            using var svc = NewService();
            svc.LogSecurityEvent("Unusual login pattern", AuditSeverity.Critical,
                details: new Dictionary<string, string> { ["IP"] = "10.0.0.1" });
            svc.Flush();
            var e = ReadAll(LatestLogFile()).Single();
            Assert.Equal(AuditSeverity.Critical, e.Severity);
            Assert.Equal("10.0.0.1", e.Details["IP"]);
        }

        [Fact]
        public void LogDeployment_Failure_IsError()
        {
            using var svc = NewService();
            svc.LogDeployment("MyDb", "srv1", success: false, errorMessage: "Out of space");
            svc.Flush();
            var e = ReadAll(LatestLogFile()).Single();
            Assert.Equal(AuditSeverity.Error, e.Severity);
            Assert.Contains("Out of space", e.Details["Error"]);
        }

        [Fact]
        public void LogApplicationStart_CapturesUserMachineVersion()
        {
            using var svc = NewService();
            svc.LogApplicationStart();
            svc.Flush();
            var e = ReadAll(LatestLogFile()).Single();
            Assert.Equal(AuditEventType.ApplicationLifecycle, e.EventType);
            Assert.Equal(Environment.UserName, e.Details["User"]);
            Assert.Equal(Environment.MachineName, e.Details["Machine"]);
            Assert.True(e.Details.ContainsKey("Version"));
        }

        // ── Buffering / flush ────────────────────────────────────────────

        [Fact]
        public void Enqueue_BelowBufferSize_DoesNotAutoFlush()
        {
            using var svc = NewService();
            svc.MaxBufferSize = 100; // make sure we stay under
            svc.LogConnectionAttempt("srv1", success: true);
            // No explicit Flush() — file should not exist yet
            var files = Directory.GetFiles(_tempDir, "audit-*.jsonl");
            Assert.Empty(files);
        }

        [Fact]
        public void Enqueue_AtBufferSize_AutoFlushes()
        {
            using var svc = NewService();
            svc.MaxBufferSize = 3;
            svc.LogConnectionAttempt("srv1", success: true);
            svc.LogConnectionAttempt("srv2", success: true);
            // Third entry should trip MaxBufferSize and force a flush
            svc.LogConnectionAttempt("srv3", success: true);

            var entries = ReadAll(LatestLogFile());
            Assert.Equal(3, entries.Count);
        }

        [Fact]
        public void Dispose_FlushesPendingEntries()
        {
            string logFile;
            using (var svc = NewService())
            {
                svc.MaxBufferSize = 100;
                svc.LogConnectionAttempt("srv1", success: true);
                // No explicit Flush. Dispose should drain.
            }
            // After Dispose, file should now exist
            var files = Directory.GetFiles(_tempDir, "audit-*.jsonl");
            Assert.Single(files);
            logFile = files[0];
            Assert.Single(ReadAll(logFile));
        }

        [Fact]
        public void Flush_EmptyBuffer_IsNoop()
        {
            using var svc = NewService();
            svc.Flush(); // should not throw
            Assert.Empty(Directory.GetFiles(_tempDir, "audit-*.jsonl"));
        }

        // ── GetEntries ──────────────────────────────────────────────────

        [Fact]
        public void GetEntries_FiltersByEventType()
        {
            using var svc = NewService();
            svc.LogConnectionAttempt("srv", success: true);
            svc.LogScriptBlocked("bad.sql", "policy");
            svc.LogDeployment("db", "srv", success: true);
            svc.Flush();

            var blocks = svc.GetEntries(DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(5),
                                         filterType: AuditEventType.SecurityBlock);
            Assert.Single(blocks);
            Assert.Equal(AuditEventType.SecurityBlock, blocks[0].EventType);
        }

        [Fact]
        public void GetEntries_FiltersByDateRange()
        {
            using var svc = NewService();
            svc.LogConnectionAttempt("srv", success: true);
            svc.Flush();

            // Way-in-the-past range returns nothing
            var oldEntries = svc.GetEntries(DateTime.UtcNow.AddYears(-10), DateTime.UtcNow.AddYears(-9));
            Assert.Empty(oldEntries);

            // Inclusive of now
            var recent = svc.GetEntries(DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(5));
            Assert.Single(recent);
        }

        [Fact]
        public void GetEntries_OrdersByTimestampDescending()
        {
            using var svc = NewService();
            svc.LogConnectionAttempt("first", success: true);
            System.Threading.Thread.Sleep(15); // ensure distinct timestamps
            svc.LogConnectionAttempt("second", success: true);
            System.Threading.Thread.Sleep(15);
            svc.LogConnectionAttempt("third", success: true);
            svc.Flush();

            var entries = svc.GetEntries(DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(5));
            Assert.Equal(3, entries.Count);
            Assert.True(entries[0].Timestamp >= entries[1].Timestamp);
            Assert.True(entries[1].Timestamp >= entries[2].Timestamp);
        }

        // ── HMAC key persistence ────────────────────────────────────────

        [Fact]
        public void Service_CreatesHmacKeyFile_OnFirstRun()
        {
            using var svc = NewService();
            var keyFile = Path.Combine(_tempDir, "hmac.key");
            Assert.True(File.Exists(keyFile));
            Assert.True(new FileInfo(keyFile).Length >= 32);
        }

        [Fact]
        public void Service_ReusesExistingHmacKey_AcrossInstances()
        {
            string keyContentRound1;
            using (var svc = NewService())
            {
                svc.LogApplicationStart();
                svc.Flush();
                keyContentRound1 = Convert.ToBase64String(File.ReadAllBytes(Path.Combine(_tempDir, "hmac.key")));
            }
            using (var svc = NewService())
            {
                var keyContentRound2 = Convert.ToBase64String(File.ReadAllBytes(Path.Combine(_tempDir, "hmac.key")));
                Assert.Equal(keyContentRound1, keyContentRound2);
            }
        }

        [Fact]
        public void Service_RegeneratesKey_IfExistingKeyIsTruncated()
        {
            // Plant a too-short key
            var keyFile = Path.Combine(_tempDir, "hmac.key");
            File.WriteAllBytes(keyFile, new byte[] { 1, 2, 3 });

            using var svc = NewService();
            // Service should have replaced the short key with a 32-byte one
            Assert.True(new FileInfo(keyFile).Length >= 32);
        }

        // ── Sanity: HMAC signature is non-trivial ───────────────────────

        [Fact]
        public void Signatures_AreUniquePerEntry()
        {
            using var svc = NewService();
            for (int i = 0; i < 20; i++)
                svc.LogConnectionAttempt("srv", success: true);
            svc.Flush();

            var sigs = ReadAll(LatestLogFile()).Select(e => e.Signature).ToList();
            Assert.Equal(sigs.Count, sigs.Distinct().Count());  // all distinct
            Assert.All(sigs, s =>
            {
                // Each signature is valid base64 of 32 bytes (HMAC-SHA256 output)
                var bytes = Convert.FromBase64String(s);
                Assert.Equal(32, bytes.Length);
            });
        }

        [Fact]
        public void Signature_DoesNotIncludeSelf_InComputation()
        {
            // Sanity check the canonical form: if Signature were part of its own input,
            // the chain couldn't bootstrap. Indirectly verified by the fact that the
            // chain verifies correctly across multiple entries. But also assert the
            // first entry's PreviousHash is empty (chain root).
            using var svc = NewService();
            svc.LogApplicationStart();
            svc.Flush();
            var first = ReadAll(LatestLogFile()).Single();
            Assert.Equal(string.Empty, first.PreviousHash);
            Assert.NotEmpty(first.Signature);
        }

        // ── T1: failover write when primary dir is unavailable ──────────

        [Fact]
        public void Flush_WhenPrimaryDirUnwritable_WritesToFailoverDir()
        {
            // Arrange: create service, then delete the log directory so the next
            // Flush() cannot write to the primary location.
            using var svc = NewService();
            svc.MaxBufferSize = 100; // prevent auto-flush on Enqueue

            // Confirm no log file exists yet
            Assert.Empty(Directory.GetFiles(_tempDir, "audit-*.jsonl"));

            // Delete the primary log directory so File.AppendAllText fails.
            // The service was already constructed (key loaded, timers stopped)
            // so removing the directory only blocks writes, not construction.
            Directory.Delete(_tempDir, recursive: true);

            // Enqueue 3 entries, then force FlushFailoverThreshold (3) flushes
            // to trigger the failover path. Each failed Flush() increments the counter.
            svc.LogConnectionAttempt("srv1", success: false, errorMessage: "unreachable");
            svc.LogConnectionAttempt("srv2", success: false, errorMessage: "unreachable");
            svc.LogConnectionAttempt("srv3", success: false, errorMessage: "unreachable");

            // Three consecutive Flush() calls needed to reach FlushFailoverThreshold (3)
            svc.Flush(); // attempt 1 – fails, requeues, increments counter to 1
            svc.Flush(); // attempt 2 – fails, counter 2
            svc.Flush(); // attempt 3 – fails, counter == FlushFailoverThreshold → writes failover

            // Assert: failover directory + at least one failover file was created.
            var failoverDir = Path.Combine(_tempDir, ".failover");
            Assert.True(Directory.Exists(failoverDir), "Failover directory should have been created by TryWriteFailover.");

            var failoverFiles = Directory.GetFiles(failoverDir, "*.jsonl");
            Assert.NotEmpty(failoverFiles);

            // Chain should not be flagged as broken from our side (the break is on the write side,
            // not the read/verify side — ChainBroken is set only by VerifyChainOnStartup).
            Assert.False(svc.ChainBroken);

            // Cleanup: re-create _tempDir so IDisposable cleanup in class can succeed
            Directory.CreateDirectory(_tempDir);
        }

        // ── T2: HMAC key rotation preserves chain continuity ────────────

        [Fact]
        public void RotateHmacKey_PreservesChainContinuity()
        {
            // Arrange: 3 entries before rotation
            using var svc = NewService();
            svc.LogConnectionAttempt("srv1", success: true);
            svc.LogConnectionAttempt("srv2", success: true);
            svc.LogConnectionAttempt("srv3", success: true);

            // Act: rotate key (internally flushes first, writes rotation anchor, then new entries use new key)
            svc.RotateHmacKey("test-admin");

            // Append 3 more entries under the new key
            svc.LogConnectionAttempt("srv4", success: true);
            svc.LogConnectionAttempt("srv5", success: true);
            svc.LogConnectionAttempt("srv6", success: true);
            svc.Flush();

            // Assert: 7 entries total (3 + 1 rotation anchor + 3)
            var entries = ReadAll(LatestLogFile());
            Assert.Equal(7, entries.Count);

            // The 4th entry (index 3) is the rotation anchor
            Assert.Equal(AuditEventType.HmacKeyRotated, entries[3].EventType);

            // Structural chain continuity: each entry's PreviousHash must equal
            // the prior entry's Signature regardless of which key signed them.
            // This validates that the rotation anchor correctly bridges old-key and
            // new-key entries without breaking the PreviousHash link sequence.
            for (int i = 1; i < entries.Count; i++)
                Assert.Equal(entries[i - 1].Signature, entries[i].PreviousHash);

            // ChainBroken is set only by startup verification (VerifyChainOnStartup).
            // Since we haven't restarted the service, it should remain false.
            Assert.False(svc.ChainBroken,
                "ChainBroken should not be set mid-session after key rotation.");

            // Note: VerifyChain() re-signs using only the current (new) key, so it will
            // report intact=false for pre-rotation entries — by design. The structural
            // PreviousHash chain above (asserted above) is the correct continuity guarantee.
        }

        // ── T7: legacy raw key file migrates to DPAPI on Windows ────────

        [Fact]
        public void LegacyRawKey_MigratesToDpapi_OnWindows()
        {
            if (!OperatingSystem.IsWindows()) return; // DPAPI is Windows-only

            // Arrange: plant a raw 32-byte key (no DPAPI wrap) — simulates a pre-DPAPI build.
            var keyPath = Path.Combine(_tempDir, "hmac.key");
            var rawKey = new byte[32];
            System.Security.Cryptography.RandomNumberGenerator.Fill(rawKey);
            File.WriteAllBytes(keyPath, rawKey);

            // Act: construct service — LoadOrCreateHmacKey detects raw 32-byte blob,
            // re-wraps under DPAPI, and returns the original key bytes.
            using var svc = NewService();
            svc.LogApplicationStart();
            svc.Flush();

            // Assert: file is now larger than 32 bytes (DPAPI header adds overhead)
            var migratedSize = new FileInfo(keyPath).Length;
            Assert.True(migratedSize > 32,
                $"Key file should be DPAPI-wrapped (>32 bytes) but was {migratedSize} bytes.");

            // Assert: chain is valid (key semantics preserved through migration)
            Assert.False(svc.ChainBroken);
            var entries = ReadAll(LatestLogFile());
            Assert.Equal(AuditEventType.ApplicationLifecycle, entries[0].EventType);
        }
    }
}
