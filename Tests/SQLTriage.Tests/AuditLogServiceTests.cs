/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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

        /// <summary>
        /// Key loss means the old entries can no longer be verified — that intent is unchanged.
        /// What changed (2026-08-01) is the VERDICT it produces. This test used to assert
        /// <c>ChainBroken</c>, i.e. that losing a key is indistinguishable from tampering. That
        /// conflation is the defect: it fired for real on the installed service when its Windows
        /// account changed, and it means a genuine tamper cannot be told apart from an ops
        /// mishap. The correct verdict is UNVERIFIABLE — cannot be confirmed OR denied.
        /// </summary>
        [Fact]
        public void KeyReplaced_OldEntriesBecomeUnverifiable_NotBroken()
        {
            // Round 1: write entries with key A
            using (var svc = NewService())
            {
                svc.LogApplicationStart();
                svc.LogConnectionAttempt("srv", success: true);
                svc.Flush();
            }

            // H4 (2026-07-07): entries are now tagged with their signing key's id and that key is
            // archived beside the main key file. Genuine, unrecoverable key LOSS means both the main
            // key AND the archives are gone — simulate that here. (Replacing only the main key while
            // the archive survives is exactly the rotation case that must NOT break old entries; that
            // is covered by RotateHmacKey_PreservesChainContinuity.)
            var keyPath = Path.Combine(_tempDir, "hmac.key");
            File.SetAttributes(keyPath, FileAttributes.Normal);
            File.WriteAllBytes(keyPath, System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            foreach (var archive in Directory.GetFiles(_tempDir, "hmac.key.*"))
            {
                if (archive.EndsWith(".meta")) continue; // keep the age sidecar
                File.SetAttributes(archive, FileAttributes.Normal);
                File.Delete(archive);
            }

            using var svc2 = NewService();

            // Not clean — the gap is surfaced...
            Assert.True(svc2.ChainUnverifiable,
                "Entries signed by the lost key must be flagged as unverifiable, not passed over.");
            // ...but not slandered as tampering either.
            Assert.False(svc2.ChainBroken,
                "Losing a key is not evidence that anyone altered a record.");

            var result = svc2.VerifyChain("test");
            Assert.Equal(AuditLogService.ChainVerificationStatus.Unverifiable, result.Status);
            Assert.Equal(2, result.UnverifiableCount);
            Assert.Equal(0, result.BrokenCount);
            Assert.False(result.Intact);
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

            // H4 (2026-07-07): VerifyChain now resolves the per-entry key from its KeyId (the
            // retiring key is archived on rotation), so pre-rotation entries verify cryptographically,
            // not just structurally. This is the fix — previously it reported intact=false here.
            Assert.True(svc.VerifyChain().Intact,
                "After rotation, all entries must verify under their archived signing keys.");
        }

        // ── H4: rotation survives a restart (key archival) ──────────────

        [Fact]
        public void RotateThenRestart_OldEntriesStillVerify_ChainNotBroken()
        {
            using (var svc = NewService())
            {
                svc.LogConnectionAttempt("srv1", success: true);
                svc.LogConnectionAttempt("srv2", success: true);
                svc.Flush();
                svc.RotateHmacKey("test-admin");
                svc.LogConnectionAttempt("srv3", success: true);
                svc.Flush();
            }

            // Restart: startup verification must NOT flag a break — the pre-rotation entries
            // resolve to the archived old key. This is the H4 regression that bricked history.
            using var svc2 = NewService();
            Assert.False(svc2.ChainBroken,
                "Pre-rotation entries must still verify after restart via the archived key.");
            Assert.True(svc2.VerifyChain().Intact);
        }

        // ── H5: trailing truncation is caught by the out-of-band anchor ──

        [Fact]
        public void Truncation_TrailingEntryRemoved_IsDetectedOnRestart()
        {
            using (var svc = NewService())
            {
                svc.LogConnectionAttempt("srv1", success: true);
                svc.LogConnectionAttempt("srv2", success: true);
                svc.LogConnectionAttempt("srv3", success: true);
                svc.Flush();
            }

            // Remove the LAST line of the segment. The remaining prefix is still a valid chain,
            // so in-band verification alone would report it intact — only the out-of-band anchor
            // (recorded the removed tail's signature) can detect the truncation.
            var seg = LatestLogFile();
            var lines = File.ReadAllLines(seg).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            Assert.True(lines.Count >= 3);
            File.WriteAllLines(seg, lines.Take(lines.Count - 1));

            using var svc2 = NewService();
            Assert.True(svc2.ChainBroken,
                "Removing the anchored tail entry must be detected as truncation.");
        }

        // ── H5 (adversarial-review fix): deleting the anchor itself is not a silent bypass ──

        [Fact]
        public void Truncation_AnchorDeleted_WithActiveRegime_IsDetected()
        {
            using (var svc = NewService())
            {
                svc.LogConnectionAttempt("s1", success: true);
                svc.LogConnectionAttempt("s2", success: true);
                svc.Flush(); // writes the anchor AND the regime marker
            }

            // An attacker who truncates the log would also delete the anchor to hide it. With the
            // regime marker present and the log non-empty, a missing anchor must NOT be treated as
            // a benign first-run — it is evidence of tampering.
            var anchor = Path.Combine(_tempDir, ".chain-anchor");
            Assert.True(File.Exists(anchor));
            File.SetAttributes(anchor, FileAttributes.Normal);
            File.Delete(anchor);

            using var svc2 = NewService();
            Assert.True(svc2.ChainBroken,
                "A deleted anchor with an active regime and existing entries must be flagged.");
        }

        // ── H6: failover must not poison the recovered main chain ────────

        [Fact]
        public void Failover_DoesNotPoisonMainChain_RecoveryVerifiesClean()
        {
            var svc = NewService();
            svc.MaxBufferSize = 100; // no auto-flush
            svc.LogConnectionAttempt("main1", success: true);
            svc.Flush();
            var mainTailSig = ReadAll(LatestLogFile()).Last().Signature;

            // Force failover: primary dir unavailable, three flushes to cross the threshold.
            Directory.Delete(_tempDir, recursive: true);
            svc.LogConnectionAttempt("f1", success: false, errorMessage: "x");
            svc.LogConnectionAttempt("f2", success: false, errorMessage: "x");
            svc.Flush(); svc.Flush(); svc.Flush(); // -> writes signed failover side-chain

            // Recover: primary dir back, flush the still-requeued originals into the MAIN chain.
            Directory.CreateDirectory(_tempDir);
            svc.Flush();

            var mainEntries = ReadAll(LatestLogFile());
            // The recovered entries must chain off the last MAIN signature, NOT a failover signature
            // (the H6 bug advanced _lastSignature into the failover side-chain, permanently breaking
            // the main chain). And the originals must keep their real EventType (not AuditFailoverEntry).
            Assert.Equal(mainTailSig, mainEntries.First().PreviousHash);
            Assert.All(mainEntries, e => Assert.NotEqual(AuditEventType.AuditFailoverEntry, e.EventType));

            // Verify in-session (the signing key is still in memory). We deliberately do NOT restart
            // here: the failover simulation deletes the whole temp dir, which also removes hmac.key
            // and its archives — a real transiently-unavailable primary directory does not destroy
            // the key, so a key-less restart would be an artifact of the test, not a chain defect.
            Assert.True(svc.VerifyChain().Intact, "Recovered main chain must verify intact after failover.");
            svc.Dispose();
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

            // Assert: the legitimate migration path must NOT be mistaken for the incident path.
            // Same key material, so nothing is preserved aside and no key-replacement event fires.
            Assert.Empty(Directory.GetFiles(_tempDir, "hmac.key.unreadable-*"));
            Assert.DoesNotContain(ReadAll(LatestLogFile()),
                e => e.EventType == AuditEventType.HmacKeyReplacedUnreadable);

            // Assert: chain is valid (key semantics preserved through migration)
            Assert.False(svc.ChainBroken);
            var entries = ReadAll(LatestLogFile());
            Assert.Equal(AuditEventType.ApplicationLifecycle, entries[0].EventType);
        }

        // ================================================================
        // 2026-08-01: audit-chain honesty — UNVERIFIABLE is not BROKEN
        //
        // Live incident: the installed service's Windows account changed between two
        // launches. The DPAPI(CurrentUser)-wrapped hmac.key could no longer be unwrapped,
        // a fresh key was minted SILENTLY, and every subsequent verification reported
        // "BROKEN after 1 entries" on a 307-entry chain that nobody had touched.
        // Two defects: (1) the silent regeneration, (2) a verifier that cannot tell a
        // key-management accident from a tamper.
        // ================================================================

        /// <summary>
        /// An entry whose signing key is not available on this machine must be reported as
        /// UNVERIFIABLE ("cannot be confirmed or denied"), NOT as BROKEN ("signature is wrong").
        /// Before the fix, ResolveKeyForEntry silently fell back to the CURRENT key, the
        /// recomputed signature necessarily mismatched, and the run reported tampering.
        /// </summary>
        [Fact]
        public void UnresolvableSigningKey_IsClassifiedUnverifiable_NotBroken()
        {
            // Arrange: three entries signed under key K1, then make K1 unavailable — exactly
            // the state the service was left in when its identity changed (the archived copy
            // is DPAPI-wrapped under the same lost identity, so it goes too).
            using (var svc = NewService())
            {
                svc.LogConnectionAttempt("srv1", success: true);
                svc.LogConnectionAttempt("srv2", success: true);
                svc.LogConnectionAttempt("srv3", success: true);
                svc.Flush();
            }

            var signedEntries = ReadAll(LatestLogFile());
            Assert.Equal(3, signedEntries.Count);
            var lostKeyId = signedEntries[0].KeyId;
            Assert.False(string.IsNullOrEmpty(lostKeyId), "Entries must carry the KeyId that signed them.");

            foreach (var keyFile in Directory.GetFiles(_tempDir, "hmac.key*"))
            {
                File.SetAttributes(keyFile, FileAttributes.Normal);
                File.Delete(keyFile);
            }

            // Act: restart. A new key is minted; the three existing entries are now orphaned
            // from any key this machine holds.
            using var svc2 = NewService();

            // Assert — startup verification: a gap, NOT a tamper verdict.
            Assert.False(svc2.ChainBroken,
                "A missing signing key is not evidence of tampering — ChainBroken must stay false.");
            Assert.True(svc2.ChainUnverifiable,
                "Entries signed with an unavailable key must be flagged UNVERIFIABLE, not ignored.");
            Assert.Equal(lostKeyId, svc2.ChainUnverifiableKeyId);

            // Assert — on-demand verification: same split, with honest counts.
            var result = svc2.VerifyChain("test");
            Assert.Equal(AuditLogService.ChainVerificationStatus.Unverifiable, result.Status);
            Assert.Equal("UNVERIFIABLE", result.StatusLabel);
            Assert.Equal(0, result.BrokenCount);
            Assert.Equal(3, result.UnverifiableCount);
            Assert.Equal(lostKeyId, result.FirstUnresolvableKeyId);
            Assert.False(result.Intact, "Intact stays strict: an unverifiable entry is not a clean bill of health.");

            // Assert — the whole chain was examined. The old code broke out of the loop on the
            // first mismatch, so a 307-entry chain reported "1 entries" and 306 records were
            // never looked at: a real tamper deeper in the chain would have been invisible.
            Assert.True(result.EntryCount >= 4,
                $"Verification must not stop at the first unverifiable entry (examined {result.EntryCount}).");

            // Assert — entries signed under the CURRENT key still verify normally alongside the gap.
            Assert.Equal(result.EntryCount - 3, result.VerifiedCount);

            // Assert — the gap is on the chain, not only in the log file.
            svc2.Flush();
            Assert.Contains(ReadAll(LatestLogFile()),
                e => e.EventType == AuditEventType.AuditChainUnverifiable);
        }

        /// <summary>
        /// A genuinely altered entry — whose key IS available — must still be reported as
        /// BROKEN. This is the other half of the split: making the verifier forgiving about
        /// missing keys must not make it forgiving about wrong signatures.
        /// </summary>
        [Fact]
        public void TamperedEntry_WithKeyAvailable_IsStillClassifiedBroken()
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

            var lines = File.ReadAllLines(logFile);
            var middle = JsonSerializer.Deserialize<AuditLogEntry>(lines[1], Json)!;
            middle.Message = "TAMPERED";
            lines[1] = JsonSerializer.Serialize(middle, Json);
            File.WriteAllLines(logFile, lines);

            using var svc2 = NewService();
            Assert.True(svc2.ChainBroken, "An altered entry with an available key is a tamper signal.");
            Assert.False(svc2.ChainUnverifiable, "Nothing here is unverifiable — the key is present.");

            var result = svc2.VerifyChain("test");
            Assert.Equal(AuditLogService.ChainVerificationStatus.Broken, result.Status);
            Assert.Equal("BROKEN", result.StatusLabel);
            Assert.True(result.BrokenCount >= 1);
            Assert.Equal(0, result.UnverifiableCount);
            Assert.False(result.Intact);
            Assert.NotNull(result.FirstBrokenRecord);
        }

        // ================================================================
        // 2026-08-01 ROUND 2: the UNVERIFIABLE split made the verdict depend on KeyId, and
        // KeyId is NOT in the v1 signed canonical form. Tamper an entry, then point its KeyId
        // at a key nobody holds, and a Critical tamper verdict became a Warning "integrity
        // gap" — with the product printing "No entry was found to be altered" about the entry
        // that had just been altered. Cost to the attacker: one unsigned JSON field.
        //
        // Every test below is written against RAW JSON rather than typed properties so that it
        // also COMPILES at b0c9603 — that is what makes the red half demonstrable.
        // ================================================================

        /// <summary>
        /// THE ATTACK, in its corrected direction. Alter an entry's Message and rewrite that same
        /// entry's KeyId to a key id this installation never held. Verdict must be BROKEN.
        /// <para>RED at b0c9603: Unverifiable / BrokenCount 0 / Warning.</para>
        /// </summary>
        [Fact]
        public void TamperedEntry_WithKeyIdRewrittenToAnUnknownKey_IsClassifiedBroken()
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

            var lines = File.ReadAllLines(logFile);
            lines[1] = RewriteJson(lines[1], "Message", "\"TAMPERED\"");
            lines[1] = RewriteJson(lines[1], "KeyId", "\"DEADBEEFDEADBEEF\"");
            File.WriteAllLines(logFile, lines);

            using var svc2 = NewService();
            Assert.True(svc2.ChainBroken,
                "Rewriting the (unsigned) KeyId of an altered entry must not buy the benign verdict.");
            Assert.False(svc2.ChainUnverifiable,
                "A lone dead key sandwiched between live ones is a rewrite, not key loss.");

            var result = svc2.VerifyChain("test");
            Assert.Equal(AuditLogService.ChainVerificationStatus.Broken, result.Status);
            Assert.Equal("BROKEN", result.StatusLabel);
            Assert.True(result.BrokenCount >= 1);
            Assert.Equal(0, result.UnverifiableCount);
            Assert.False(result.Intact);

            // ...and the client-facing sentence must not exonerate the record that was altered.
            Assert.DoesNotContain("No entry was found to be altered", result.StatusDetail);
        }

        /// <summary>
        /// The contiguous-run heuristic's OWN weakness, closed. Rewriting a whole run at the head of
        /// the chain produces exactly the shape genuine key loss produces, so shape alone would let
        /// it through. The key-provenance ledger in the (DPAPI-wrapped, out-of-band) chain anchor
        /// refutes it: this installation never made that key id current, and no archived key file
        /// bears it. Genuine loss leaves the archive file on disk — merely unreadable.
        /// <para>RED at b0c9603: Unverifiable, BrokenCount 0.</para>
        /// </summary>
        [Fact]
        public void TamperedHeadRun_WithMatchingKeyIdRewrite_IsClassifiedBroken()
        {
            string logFile;
            using (var svc = NewService())
            {
                svc.LogConnectionAttempt("srv1", success: true);
                svc.LogConnectionAttempt("srv2", success: true);
                svc.LogConnectionAttempt("srv3", success: true);
                svc.LogConnectionAttempt("srv4", success: true);
                svc.Flush();
                logFile = LatestLogFile();
            }

            var lines = File.ReadAllLines(logFile);
            for (int i = 0; i < 2; i++)
            {
                lines[i] = RewriteJson(lines[i], "Message", "\"TAMPERED\"");
                lines[i] = RewriteJson(lines[i], "KeyId", "\"DEADBEEFDEADBEEF\"");
            }
            File.WriteAllLines(logFile, lines);

            using var svc2 = NewService();
            var result = svc2.VerifyChain("test");

            Assert.True(svc2.ChainBroken,
                "A head-anchored run mimics key loss; provenance must still refute an invented key id.");
            Assert.Equal(AuditLogService.ChainVerificationStatus.Broken, result.Status);
            Assert.Equal(2, result.BrokenCount);
            Assert.Equal(0, result.UnverifiableCount);
        }

        /// <summary>
        /// Entries written from now on bind KeyId (and the version discriminator itself) into the
        /// signed canonical form, so the field stops being free to rewrite going forward. This test
        /// pins the v2 wire format by rebuilding it independently rather than calling the service.
        /// <para>RED at b0c9603: no SigVersion is emitted and the v1 recomputation still matches.</para>
        /// </summary>
        [Fact]
        public void NewEntries_AreSignedUnderCanonicalFormV2_WhichBindsKeyId()
        {
            byte[] key;
            string logFile;
            using (var svc = NewService())
            {
                svc.LogConnectionAttempt("srv1", success: true);
                svc.Flush();
                logFile = LatestLogFile();
                key = HmacKeyOf(svc);
            }

            var raw = File.ReadAllLines(logFile)[0];
            Assert.Contains("\"SigVersion\":2", raw);

            var entry = JsonSerializer.Deserialize<AuditLogEntry>(raw, Json)!;
            Assert.False(string.IsNullOrEmpty(entry.KeyId));

            // Every signing path sets PreviousHash to the same value it signs against.
            Assert.Equal(V2Signature(entry, entry.PreviousHash, key), entry.Signature);
            Assert.NotEqual(V1Signature(entry, entry.PreviousHash, key), entry.Signature);
        }

        /// <summary>
        /// THE COMPATIBILITY GUARD, and the one that matters most: getting the versioning wrong
        /// invalidates every entry every installation has ever written. A legacy entry (no
        /// SigVersion, no KeyId) must still verify under the ORIGINAL canonical form, byte for byte.
        /// The v1 form is rebuilt here by hand precisely so that changing it in the service is
        /// caught by this test instead of by a client.
        /// <para>Green at b0c9603 as well — it pins a frozen format, it is not a fix demo.</para>
        /// </summary>
        [Fact]
        public void LegacyEntriesWithoutSigVersion_StillVerifyUnderTheOriginalCanonicalForm()
        {
            byte[] key;
            string logFile;
            using (var svc = NewService())
            {
                svc.LogApplicationStart();
                svc.Flush();
                logFile = LatestLogFile();
                key = HmacKeyOf(svc);
            }

            // Hand-build a pre-2026-08-01 chain: KeyId null, no SigVersion, v1 signatures.
            var baseTs = new DateTime(2026, 7, 15, 9, 0, 0, DateTimeKind.Utc);
            var legacy = new List<AuditLogEntry>();
            var prevSig = string.Empty;
            for (int i = 0; i < 3; i++)
            {
                var e = new AuditLogEntry
                {
                    Timestamp = baseTs.AddMinutes(i),
                    EventType = AuditEventType.ConnectionAttempt,
                    Severity = AuditSeverity.Info,
                    Message = "legacy entry " + i,
                    Details = new Dictionary<string, string> { ["ServerName"] = "srv" + i },
                    PreviousHash = prevSig,
                    KeyId = null
                };
                e.Signature = V1Signature(e, prevSig, key);
                prevSig = e.Signature;
                legacy.Add(e);
            }
            File.WriteAllLines(logFile, legacy.Select(e => JsonSerializer.Serialize(e, Json)));
            // A legacy chain predates the H5 truncation-anchor regime. Leaving the anchor in place
            // would pin a tail signature that this hand-built chain does not contain, and the
            // resulting (correct) truncation finding would mask what this test is actually about.
            foreach (var f in Directory.GetFiles(_tempDir, ".chain-anchor*"))
            {
                File.SetAttributes(f, FileAttributes.Normal);
                File.Delete(f);
            }

            using var svc2 = NewService();
            Assert.False(svc2.ChainBroken, "Legacy entries must keep verifying under the v1 canonical form.");
            Assert.False(svc2.ChainUnverifiable);

            var result = svc2.VerifyChain("test");
            Assert.Equal(AuditLogService.ChainVerificationStatus.Intact, result.Status);
            Assert.Equal(3, result.EntryCount);
            Assert.Equal(0, result.BrokenCount);
            Assert.Equal(0, result.UnverifiableCount);
            Assert.True(result.Intact);
        }

        /// <summary>
        /// The version discriminator is self-protecting: stripping it to force a v1 recomputation
        /// (which would drop KeyId back out of the signed form) is itself a signature mismatch.
        /// Without this, versioning would just hand the attacker a downgrade switch.
        /// <para>RED at b0c9603: there is no SigVersion to strip, so the chain stays INTACT.</para>
        /// </summary>
        [Fact]
        public void SignatureVersion_DowngradeOnANewEntry_IsClassifiedBroken()
        {
            string logFile;
            using (var svc = NewService())
            {
                svc.LogConnectionAttempt("srv1", success: true);
                svc.LogConnectionAttempt("srv2", success: true);
                svc.Flush();
                logFile = LatestLogFile();
            }

            var lines = File.ReadAllLines(logFile);
            lines[1] = lines[1].Replace(",\"SigVersion\":2", "");
            File.WriteAllLines(logFile, lines);

            using var svc2 = NewService();
            Assert.True(svc2.ChainBroken,
                "Downgrading an entry to the v1 canonical form must not verify.");
            Assert.Equal(AuditLogService.ChainVerificationStatus.Broken, svc2.VerifyChain("test").Status);
        }

        /// <summary>
        /// hmac.key.meta records when the CURRENT key was born. Replacing an unreadable key left the
        /// DEAD key's birth date in place, so from then on the age check measured a key that no
        /// longer existed. Reset it on the replacement path.
        /// <para>RED at b0c9603: the backdated sidecar survives the replacement untouched.</para>
        /// </summary>
        [Fact]
        public void KeyReplacement_ResetsTheAgeSidecar()
        {
            using (var svc = NewService())
            {
                svc.LogApplicationStart();
                svc.Flush();
            }

            var metaPath = Path.Combine(_tempDir, "hmac.key.meta");
            var stale = DateTime.UtcNow.AddDays(-400);
            File.WriteAllText(metaPath,
                "{\"CreatedAt\":\"" + stale.ToString("o") + "\",\"RotationDueAt\":\"" +
                stale.AddDays(365).ToString("o") + "\"}");

            // Make the key unreadable: neither a valid DPAPI blob nor a raw 32-byte legacy key.
            var keyPath = Path.Combine(_tempDir, "hmac.key");
            File.SetAttributes(keyPath, FileAttributes.Normal);
            File.WriteAllBytes(keyPath, System.Security.Cryptography.RandomNumberGenerator.GetBytes(50));

            using (var svc2 = NewService()) { }

            using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
            var created = DateTime.Parse(doc.RootElement.GetProperty("CreatedAt").GetString()!,
                null, System.Globalization.DateTimeStyles.RoundtripKind);
            Assert.True((DateTime.UtcNow - created.ToUniversalTime()).TotalMinutes < 10,
                $"The age sidecar must track the REPLACEMENT key, not the dead one (got {created:o}).");
        }

        /// <summary>
        /// Fix 3 (Adrian's ruling): the audit HMAC key is DPAPI-wrapped LocalMachine, and a key file
        /// still wrapped under the legacy CurrentUser scope is read transparently and re-wrapped in
        /// place. Same key material, unchanged chain.
        /// <para>
        /// This PREVENTS RECURRENCE ONLY. It does not and cannot recover the key orphaned in the
        /// live incident — that plaintext existed only inside the previous identity's blob. The
        /// fallback fires only while the identity that wrapped the blob is still the one running.
        /// </para>
        /// <para>RED at b0c9603: that build wraps CurrentUser, so the blob's scope flag stays clear.
        /// Note the assertion CANNOT be "LocalMachine unprotect succeeds" — DPAPI stores the scope in
        /// the blob and ignores the scope argument on unprotect, so that passes either way. Measured
        /// on this box before writing the test; the scope has to be read out of the blob.</para>
        /// </summary>
        [Fact]
        public void LegacyCurrentUserWrappedKey_IsReadAndSilentlyRewrappedLocalMachine()
        {
            if (!OperatingSystem.IsWindows()) return; // DPAPI is Windows-only

            byte[] rawKey;
            using (var svc = NewService())
            {
                svc.LogConnectionAttempt("srv1", success: true);
                svc.LogConnectionAttempt("srv2", success: true);
                svc.Flush();
                rawKey = HmacKeyOf(svc);
            }

            var entropy = HmacEntropy();
            var legacyBlob = System.Security.Cryptography.ProtectedData.Protect(
                rawKey, entropy, System.Security.Cryptography.DataProtectionScope.CurrentUser);

            // Push BOTH the live key and its archive back to the legacy scope.
            foreach (var f in Directory.GetFiles(_tempDir, "hmac.key*"))
            {
                if (f.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                File.SetAttributes(f, FileAttributes.Normal);
                File.WriteAllBytes(f, legacyBlob);
            }

            using var svc2 = NewService();
            Assert.False(svc2.ChainBroken, "A legacy-scope key is the SAME key — nothing may look tampered.");
            Assert.False(svc2.ChainUnverifiable, "...and nothing may go unverifiable either.");
            Assert.Equal(AuditLogService.ChainVerificationStatus.Intact, svc2.VerifyChain("test").Status);

            // And the file has been silently upgraded, so the next identity change cannot orphan it.
            var onDisk = File.ReadAllBytes(Path.Combine(_tempDir, "hmac.key"));
            Assert.True(IsLocalMachineBlob(onDisk),
                "The legacy CurrentUser-wrapped key must be re-wrapped LocalMachine in place.");
            var reread = System.Security.Cryptography.ProtectedData.Unprotect(
                onDisk, entropy, System.Security.Cryptography.DataProtectionScope.LocalMachine);
            Assert.Equal(rawKey, reread);
        }

        /// <summary>
        /// Pins the (undocumented) DPAPI blob layout the production scope probe relies on, against
        /// blobs generated by ProtectedData itself. If Windows ever moves the flags field, this fails
        /// loudly instead of the product silently skipping every re-wrap.
        /// </summary>
        [Fact]
        public void DpapiScopeProbe_SeparatesCurrentUserFromLocalMachineBlobs()
        {
            if (!OperatingSystem.IsWindows()) return;

            var entropy = HmacEntropy();
            var raw = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            var cu = System.Security.Cryptography.ProtectedData.Protect(
                raw, entropy, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            var lm = System.Security.Cryptography.ProtectedData.Protect(
                raw, entropy, System.Security.Cryptography.DataProtectionScope.LocalMachine);

            Assert.False(IsLocalMachineBlob(cu));
            Assert.True(IsLocalMachineBlob(lm));

            // ...and the production probe must agree with the one this test uses.
            var probe = typeof(AuditLogService).GetMethod(
                "IsLocalMachineWrapped", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(probe);
            Assert.False((bool)probe!.Invoke(null, new object[] { cu })!);
            Assert.True((bool)probe.Invoke(null, new object[] { lm })!);
        }

        // ================================================================
        // 2026-08-01 ROUND 3: the LEDGER-LESS WINDOW.
        //
        // Round 2's classifier refutes a forged KeyId using a key-provenance ledger carried in
        // the chain anchor — and FAILS OPEN when that ledger is unavailable. It is unavailable
        // on EVERY existing installation's first launch after the upgrade (pre-fix anchors carry
        // no ledger), and reachable at will by deleting the anchor and its regime marker
        // together. In that window a single altered entry at the head of the chain, with its
        // unsigned KeyId rewritten to sixteen arbitrary hex digits, bought the benign
        // UNVERIFIABLE verdict AND a printed exculpation. It was also PERMANENT: the forged id
        // was learned into SeenKeyIds during the fail-open, so later launches with a rebuilt
        // ledger corroborated the forgery against itself.
        //
        // The honest verdict when there is no provenance evidence is neither "benign gap" nor
        // "tampering" — it is INDETERMINATE: we cannot tell, and we must say so.
        //
        // As with the round-2 tests, everything below is written against strings and existing
        // API only, so the whole file still COMPILES at e2eb6b9 and the red half is demonstrable.
        // ================================================================

        /// <summary>
        /// THE BLOCKING HOLE. One entry at the head of the chain, altered, with its unsigned
        /// KeyId rewritten to sixteen hex digits picked out of thin air — no prior key incident
        /// on the box, no real dead key id, no file deleted, nothing read from the machine. The
        /// anchor is downgraded to its pre-upgrade shape first, which is not a contrivance: that
        /// is what every existing installation's anchor looks like on its first launch after this
        /// upgrade.
        /// <para>RED at e2eb6b9: UNVERIFIABLE, UnverifiableCount 1, severity Warning, and the
        /// StatusDetail prints the confident exculpation over the record that was just altered.</para>
        /// </summary>
        [Fact]
        public void LedgerlessUpgradeWindow_ForgedKeyIdAtChainHead_IsNotExonerated()
        {
            if (!OperatingSystem.IsWindows()) return; // the anchor is DPAPI-wrapped

            string logFile;
            using (var svc = NewService())
            {
                svc.LogConnectionAttempt("srv1", success: true);
                svc.LogConnectionAttempt("srv2", success: true);
                svc.LogConnectionAttempt("srv3", success: true);
                svc.Flush();
                logFile = LatestLogFile();
            }

            DowngradeChainAnchorToPreUpgradeShape();

            var lines = File.ReadAllLines(logFile);
            lines[0] = RewriteJson(lines[0], "Message", "\"TAMPERED\"");
            lines[0] = RewriteJson(lines[0], "KeyId", "\"CAFEBABECAFEBABE\"");
            File.WriteAllLines(logFile, lines);

            using var svc2 = NewService();
            var result = svc2.VerifyChain("test");

            Assert.Equal("INDETERMINATE", result.StatusLabel);
            Assert.False(result.Intact);
            Assert.Equal(0, result.UnverifiableCount);   // NOT the benign bucket
            Assert.Equal(0, result.BrokenCount);         // and not an accusation either

            // The wording must assert nothing about whether entries were altered.
            Assert.DoesNotContain("was found to have been altered", result.StatusDetail);
            Assert.Contains("no claim", result.StatusDetail, StringComparison.OrdinalIgnoreCase);

            // The startup surface (which is what the viewer banner and the compliance bundle read)
            // must not report the benign gap either.
            Assert.False(svc2.ChainUnverifiable,
                "With no provenance evidence this is not a known-benign key gap.");
            Assert.True(StartupProvenanceIndeterminate(svc2),
                "Startup verification must expose the indeterminate state to the viewer and the bundle.");
        }

        /// <summary>
        /// The same hole, reached at will rather than waiting for an upgrade. The ledger-less state
        /// is restored by deleting BOTH anchor files: deleting `.chain-anchor` alone is caught as
        /// truncation, and deleting `.chain-anchor.regime` with it suppresses that check. Both sit
        /// in the audit-log directory under the same ACL as the `.jsonl` the attacker is already
        /// editing, so this costs nothing extra. It must not buy the benign verdict either.
        /// <para>RED at e2eb6b9: UNVERIFIABLE.</para>
        /// </summary>
        [Fact]
        public void DeletingBothAnchorFiles_DoesNotRestoreTheBenignVerdict()
        {
            if (!OperatingSystem.IsWindows()) return;

            string logFile;
            using (var svc = NewService())
            {
                svc.LogConnectionAttempt("srv1", success: true);
                svc.LogConnectionAttempt("srv2", success: true);
                svc.Flush();
                logFile = LatestLogFile();
            }

            foreach (var f in Directory.GetFiles(_tempDir, ".chain-anchor*"))
            {
                File.SetAttributes(f, FileAttributes.Normal);
                File.Delete(f);
            }

            var lines = File.ReadAllLines(logFile);
            lines[0] = RewriteJson(lines[0], "Message", "\"TAMPERED\"");
            lines[0] = RewriteJson(lines[0], "KeyId", "\"CAFEBABECAFEBABE\"");
            File.WriteAllLines(logFile, lines);

            using var svc2 = NewService();
            var result = svc2.VerifyChain("test");

            Assert.Equal("INDETERMINATE", result.StatusLabel);
            Assert.Equal(0, result.UnverifiableCount);
            Assert.False(svc2.ChainUnverifiable);
            Assert.DoesNotContain("was found to have been altered", result.StatusDetail);
        }

        /// <summary>
        /// PERMANENCE. During the fail-open the forged key id was learned into the anchor's
        /// SeenKeyIds, so launches 2 and 3 — with a ledger rebuilt and available — still returned
        /// UNVERIFIABLE: the forgery corroborated itself. Learning must not happen while the
        /// ledger is unavailable, so the next launch refutes the id via R1.
        /// <para>RED at e2eb6b9: launch 2 reports UNVERIFIABLE, permanently.</para>
        /// </summary>
        [Fact]
        public void LedgerlessForgery_IsNotLearned_SoTheNextLaunchRefutesIt()
        {
            if (!OperatingSystem.IsWindows()) return;

            string logFile;
            using (var svc = NewService())
            {
                svc.LogConnectionAttempt("srv1", success: true);
                svc.LogConnectionAttempt("srv2", success: true);
                svc.LogConnectionAttempt("srv3", success: true);
                svc.Flush();
                logFile = LatestLogFile();
            }

            DowngradeChainAnchorToPreUpgradeShape();

            var lines = File.ReadAllLines(logFile);
            lines[0] = RewriteJson(lines[0], "Message", "\"TAMPERED\"");
            lines[0] = RewriteJson(lines[0], "KeyId", "\"CAFEBABECAFEBABE\"");
            File.WriteAllLines(logFile, lines);

            // Launch 1: the ledger-less window. Flushing rebuilds the anchor WITH a ledger.
            using (var svc2 = NewService())
            {
                svc2.VerifyChain("ledger-less launch");
                svc2.Flush();
            }

            // Launch 2: the ledger is available now. The forged id has no trace anywhere —
            // unless launch 1 laundered it into SeenKeyIds.
            using var svc3 = NewService();
            var result = svc3.VerifyChain("test");

            Assert.Equal("BROKEN", result.StatusLabel);
            Assert.True(result.BrokenCount >= 1);
            Assert.True(svc3.ChainBroken);
            Assert.DoesNotContain("was found to have been altered", result.StatusDetail);

            // And the laundering itself: the forged id must never reach the persisted ledger.
            Assert.DoesNotContain("CAFEBABECAFEBABE", ReadChainAnchorJson());
        }

        /// <summary>
        /// THE OVER-CORRECTION GUARD, and the reason this whole branch exists. A GENUINE key loss
        /// — key and archives gone, exactly the live 2026-08-01 incident, which orphaned the
        /// anchor along with the key — must still never be reported as tampering. It is
        /// INDETERMINATE (no evidence either way), never BROKEN.
        /// <para>Half red at e2eb6b9: the label is UNVERIFIABLE there. The "never BROKEN"
        /// assertions are green on both sides by design — they are the regression guard against
        /// reinstating the false-tamper incident, not a fix demo.</para>
        /// </summary>
        [Fact]
        public void GenuineKeyLoss_WithNoLedger_IsIndeterminate_NeverBroken()
        {
            if (!OperatingSystem.IsWindows()) return;

            using (var svc = NewService())
            {
                svc.LogConnectionAttempt("srv1", success: true);
                svc.LogConnectionAttempt("srv2", success: true);
                svc.Flush();
            }

            DowngradeChainAnchorToPreUpgradeShape();

            // Real loss: the key AND every archive are unreadable/gone.
            foreach (var keyFile in Directory.GetFiles(_tempDir, "hmac.key*"))
            {
                File.SetAttributes(keyFile, FileAttributes.Normal);
                File.Delete(keyFile);
            }

            using var svc2 = NewService();
            var result = svc2.VerifyChain("test");

            Assert.False(svc2.ChainBroken,
                "Losing a key is not evidence that anyone altered a record — this must never be BROKEN.");
            Assert.Equal(0, result.BrokenCount);
            Assert.Equal("INDETERMINATE", result.StatusLabel);
            Assert.DoesNotContain("Treat as tampering", result.StatusDetail);
        }

        /// <summary>
        /// B3: on a legacy store the CURRENT key's archive stayed CurrentUser-wrapped forever —
        /// ArchiveKeyIfAbsent skips files that exist, and current-key resolution never reads the
        /// archive, so nothing ever touched it. The claim that "key + archives + anchor all wrap
        /// LocalMachine now" was therefore over-broad. We hold the current key's material, so the
        /// archive can simply be rewritten.
        /// <para>RED at e2eb6b9: the archive blob's LocalMachine flag stays clear.</para>
        /// </summary>
        [Fact]
        public void CurrentKeyArchive_IsRewrappedLocalMachine_OnStartup()
        {
            if (!OperatingSystem.IsWindows()) return;

            byte[] rawKey;
            using (var svc = NewService())
            {
                svc.LogConnectionAttempt("srv1", success: true);
                svc.Flush();
                rawKey = HmacKeyOf(svc);
            }

            var entropy = HmacEntropy();
            var legacyBlob = System.Security.Cryptography.ProtectedData.Protect(
                rawKey, entropy, System.Security.Cryptography.DataProtectionScope.CurrentUser);

            var archives = Directory.GetFiles(_tempDir, "hmac.key.*")
                .Where(f => !f.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            Assert.NotEmpty(archives);
            foreach (var f in archives)
            {
                File.SetAttributes(f, FileAttributes.Normal);
                File.WriteAllBytes(f, legacyBlob);
            }

            using var svc2 = NewService();
            Assert.False(svc2.ChainBroken, "A re-wrap is the same key material — nothing may look tampered.");
            Assert.False(svc2.ChainUnverifiable);

            foreach (var f in archives)
            {
                Assert.True(IsLocalMachineBlob(File.ReadAllBytes(f)),
                    $"The current key's archive ({Path.GetFileName(f)}) must be re-wrapped LocalMachine.");
                Assert.Equal(rawKey, System.Security.Cryptography.ProtectedData.Unprotect(
                    File.ReadAllBytes(f), entropy,
                    System.Security.Cryptography.DataProtectionScope.LocalMachine));
            }
        }

        // ── helpers for the 2026-08-01 round-3 tests ────────────────────

        /// <summary>
        /// Rewrites the chain anchor with its pre-2026-08-01 shape — tail signature and timestamp
        /// only, no key-provenance ledger. Sig/Ts are preserved so the truncation check stays
        /// quiet and the test is about the ledger and nothing else. This is not a contrived
        /// state: it is exactly what every installation's anchor looks like on its first launch
        /// after this upgrade.
        /// </summary>
        private void DowngradeChainAnchorToPreUpgradeShape()
        {
            var path = Path.Combine(_tempDir, ".chain-anchor");
            Assert.True(File.Exists(path), "The anchor must exist before it can be downgraded.");
            var entropy = HmacEntropy();
            var json = System.Text.Encoding.UTF8.GetString(
                System.Security.Cryptography.ProtectedData.Unprotect(
                    File.ReadAllBytes(path), entropy,
                    System.Security.Cryptography.DataProtectionScope.LocalMachine));
            using var doc = JsonDocument.Parse(json);
            var legacy =
                "{\"Sig\":" + JsonSerializer.Serialize(doc.RootElement.GetProperty("Sig").GetString()) +
                ",\"Ts\":" + JsonSerializer.Serialize(doc.RootElement.GetProperty("Ts").GetString()) + "}";
            File.WriteAllBytes(path, System.Security.Cryptography.ProtectedData.Protect(
                System.Text.Encoding.UTF8.GetBytes(legacy), entropy,
                System.Security.Cryptography.DataProtectionScope.LocalMachine));
        }

        /// <summary>The anchor's plaintext JSON, for asserting on what did (not) get persisted.</summary>
        private string ReadChainAnchorJson()
        {
            var path = Path.Combine(_tempDir, ".chain-anchor");
            Assert.True(File.Exists(path));
            return System.Text.Encoding.UTF8.GetString(
                System.Security.Cryptography.ProtectedData.Unprotect(
                    File.ReadAllBytes(path), HmacEntropy(),
                    System.Security.Cryptography.DataProtectionScope.LocalMachine));
        }

        /// <summary>
        /// Reads the startup indeterminate flag by reflection so this file still compiles at
        /// e2eb6b9, where the property does not exist yet (absent property = the old build =
        /// false, which is precisely the defect being asserted against).
        /// </summary>
        private static bool StartupProvenanceIndeterminate(AuditLogService svc) =>
            typeof(AuditLogService).GetProperty("ChainProvenanceIndeterminate") is { } p &&
            (bool)p.GetValue(svc)!;

        /// <summary>Flags DWORD at byte 40; bit 0x4 = CRYPTPROTECT_LOCAL_MACHINE.</summary>
        private static bool IsLocalMachineBlob(byte[] blob) =>
            blob.Length >= 44 && (BitConverter.ToUInt32(blob, 40) & 0x4) != 0;

        // ================================================================
        // 2026-08-01 ROUND 4: the ARCHIVE TRACE, and the STARTUP BANNER'S VERDICT.
        //
        // R1 corroborated a key id from three traces, one of which was a bare File.Exists on
        // hmac.key.<id>. Measured on a fully healthy install (launch 0 INTACT, ledger
        // established): a ONE-BYTE file at that path bought the benign UNVERIFIABLE verdict with
        // its printed exculpation, and — because the benign verdict is what licenses learning —
        // laundered the forged id into the anchor's SeenKeyIds, so deleting the planted file
        // afterwards left the verdict benign forever. Same laundering B1(a) killed on the
        // fail-open path, re-entering through R1. It was also incoherent: 32 bytes of junk at the
        // same path reported BROKEN, because 32 bytes resolve as key material.
        //
        // Separately, an anchor that is PRESENT BUT UNREADABLE — key + archive + anchor all
        // orphaned by a service-account change, nothing altered — set ChainBroken and handed the
        // operator the tampering playbook, while VerifyChain on the same bytes said INDETERMINATE
        // with broken=0. The runbook classifies that shape as INDETERMINATE.
        //
        // As with rounds 2 and 3, everything below is written against existing public API (plus
        // the reflection helpers) so the whole file still COMPILES at 0e3fad0 and the red half is
        // demonstrable there.
        // ================================================================

        /// <summary>
        /// THE ONE-BYTE FREE WIN. A fully healthy install — launch 0 INTACT, ledger established,
        /// nothing deleted, no prior key incident. Alter the head entry, rewrite its unsigned
        /// KeyId to sixteen invented hex digits, and drop a single byte in a file named after
        /// that id. R1's archive trace must not accept it.
        /// <para>RED at 0e3fad0: UNVERIFIABLE, UnverifiableCount 1, broken 0, the exculpatory
        /// sentence printed, and CAFEBABECAFEBABE persisted into the anchor's SeenKeyIds.</para>
        /// </summary>
        [Fact]
        public void ForgedKeyId_CorroboratedOnlyByAOneByteArchiveFile_DoesNotBuyTheBenignVerdict()
        {
            if (!OperatingSystem.IsWindows()) return; // the anchor (and so the ledger) is DPAPI-wrapped

            string logFile;
            using (var svc = NewService())
            {
                svc.LogConnectionAttempt("srv1", success: true);
                svc.LogConnectionAttempt("srv2", success: true);
                svc.LogConnectionAttempt("srv3", success: true);
                svc.Flush(); // establishes the anchor WITH a key-provenance ledger
                logFile = LatestLogFile();
            }

            // Sanity: this install is healthy before the tamper, so nothing below is inherited.
            using (var healthy = NewService())
            {
                Assert.Equal("INTACT", healthy.VerifyChain("baseline").StatusLabel);
            }

            var lines = File.ReadAllLines(logFile);
            lines[0] = RewriteJson(lines[0], "Message", "\"TAMPERED\"");
            lines[0] = RewriteJson(lines[0], "KeyId", "\"CAFEBABECAFEBABE\"");
            File.WriteAllLines(logFile, lines);

            // One byte. Not a key, not a DPAPI blob, not anything this service could have written.
            File.WriteAllBytes(Path.Combine(_tempDir, "hmac.key.CAFEBABECAFEBABE"), new byte[] { 0x7A });

            using var svc2 = NewService();
            var result = svc2.VerifyChain("test");

            Assert.NotEqual("UNVERIFIABLE", result.StatusLabel);
            Assert.Equal(0, result.UnverifiableCount);
            Assert.False(svc2.ChainUnverifiable,
                "A one-byte file is not evidence that this installation ever held that key.");
            Assert.DoesNotContain("was found to have been altered", result.StatusDetail);

            // And the laundering: the forged id must never reach the persisted ledger, or removing
            // the planted file later would leave the verdict permanently benign.
            svc2.Flush();
            Assert.DoesNotContain("CAFEBABECAFEBABE", ReadChainAnchorJson());
        }

        /// <summary>
        /// THE OVER-CORRECTION GUARD for the hardening above, and — read honestly — the measurement
        /// that shows the hardening does NOT close the hole. Same forged run, but the planted
        /// archive file is a real DPAPI blob this box cannot unwrap (protected under different
        /// entropy). That is byte-for-byte what genuine key loss leaves on disk after an identity
        /// change, so it MUST still reach the benign UNVERIFIABLE verdict — refusing it would
        /// reinstate the false-tamper incident this branch exists to remove.
        /// <para>
        /// The same fact read the other way: an attacker who can write this directory can COPY a
        /// real key blob to hmac.key.&lt;forged-id&gt; and satisfy R1 exactly as this test does. The
        /// hardening closed the ONE-BYTE route and nothing else. It did not remove the trace, and it
        /// did not remove the permanent laundering that follows from it — see
        /// <see cref="ForgedKeyId_CorroboratedByTheHeaderBytesAlone_StillLaundersPermanently"/>,
        /// which measures the same laundering at a price of twenty published constant bytes.
        /// </para>
        /// <para>Green at 0e3fad0 as well, by design: it is a regression guard against
        /// over-correcting, not a fix demo.</para>
        /// </summary>
        [Fact]
        public void PresentButUnreadableArchive_StillReachesUnverifiable_NeverBroken()
        {
            if (!OperatingSystem.IsWindows()) return;

            string logFile;
            using (var svc = NewService())
            {
                svc.LogConnectionAttempt("srv1", success: true);
                svc.LogConnectionAttempt("srv2", success: true);
                svc.LogConnectionAttempt("srv3", success: true);
                svc.Flush();
                logFile = LatestLogFile();
            }

            var lines = File.ReadAllLines(logFile);
            lines[0] = RewriteJson(lines[0], "KeyId", "\"CAFEBABECAFEBABE\"");
            File.WriteAllLines(logFile, lines);

            // A genuine, well-formed key blob that this identity cannot unwrap.
            var unreadable = System.Security.Cryptography.ProtectedData.Protect(
                new byte[32], System.Text.Encoding.UTF8.GetBytes("a-different-entropy"),
                System.Security.Cryptography.DataProtectionScope.LocalMachine);
            File.WriteAllBytes(Path.Combine(_tempDir, "hmac.key.CAFEBABECAFEBABE"), unreadable);

            using var svc2 = NewService();
            var result = svc2.VerifyChain("test");

            Assert.Equal("UNVERIFIABLE", result.StatusLabel);
            Assert.Equal(0, result.BrokenCount);
            Assert.False(svc2.ChainBroken,
                "Present-but-unreadable is what real key loss looks like — it must never be tampering.");
        }

        /// <summary>
        /// THE RESIDUAL, PINNED (2026-08-01 round 5). Four consecutive rounds have stated this
        /// residual smaller than measurement, so it is measured here instead of described.
        /// <para>
        /// The planted archive is TWENTY BYTES: the DPAPI blob header and nothing else. No key
        /// material, no ciphertext, nothing derived from this machine — the same twenty constants
        /// this product prints in its own source comment above <c>DpapiBlobProviderGuid</c> and
        /// asserts a second time in <see cref="DpapiBlobHeaderProbe_MatchesWhatProtectedDataActuallyWrites"/>.
        /// </para>
        /// <para>
        /// It must still buy the benign UNVERIFIABLE verdict (refusing it would need a rule that
        /// also refuses genuine key loss), it is still learned into the anchor's SeenKeyIds, and the
        /// verdict still stays benign after the plant is deleted — i.e. the permanent laundering
        /// round 4 said it had removed is fully intact, at a price of 20 published bytes instead of
        /// one arbitrary byte. GREEN at dc9d4dc by design: this is a measurement of the shipped
        /// residual, not a fix demo. If it ever goes red, the residual shrank and every string that
        /// describes it must be re-measured before this test is changed.
        /// </para>
        /// </summary>
        [Fact]
        public void ForgedKeyId_CorroboratedByTheHeaderBytesAlone_StillLaundersPermanently()
        {
            if (!OperatingSystem.IsWindows()) return;

            string logFile;
            using (var svc = NewService())
            {
                svc.LogConnectionAttempt("srv1", success: true);
                svc.LogConnectionAttempt("srv2", success: true);
                svc.LogConnectionAttempt("srv3", success: true);
                svc.Flush();
                logFile = LatestLogFile();
            }

            using (var healthy = NewService())
            {
                Assert.Equal("INTACT", healthy.VerifyChain("baseline").StatusLabel);
            }

            var lines = File.ReadAllLines(logFile);
            lines[0] = RewriteJson(lines[0], "Message", "\"TAMPERED\"");
            lines[0] = RewriteJson(lines[0], "KeyId", "\"CAFEBABECAFEBABE\"");
            File.WriteAllLines(logFile, lines);

            // dwVersion (LE 1) + the 16-byte DPAPI provider GUID. Twenty published constants.
            byte[] headerOnly =
            {
                0x01, 0x00, 0x00, 0x00,
                0xD0, 0x8C, 0x9D, 0xDF, 0x01, 0x15, 0xD1, 0x11,
                0x8C, 0x7A, 0x00, 0xC0, 0x4F, 0xC2, 0x97, 0xEB
            };
            Assert.Equal(20, headerOnly.Length);
            var plantPath = Path.Combine(_tempDir, "hmac.key.CAFEBABECAFEBABE");
            File.WriteAllBytes(plantPath, headerOnly);

            using (var svc2 = NewService())
            {
                var result = svc2.VerifyChain("test");
                Assert.Equal("UNVERIFIABLE", result.StatusLabel);
                Assert.Equal(0, result.BrokenCount);
                svc2.Flush(); // the benign verdict is what licenses learning
            }

            Assert.Contains("CAFEBABECAFEBABE", ReadChainAnchorJson());

            // And it survives removal of the only thing that ever corroborated it.
            File.SetAttributes(plantPath, FileAttributes.Normal);
            File.Delete(plantPath);

            using var svc3 = NewService();
            var afterDeletion = svc3.VerifyChain("test");
            Assert.Equal("UNVERIFIABLE", afterDeletion.StatusLabel);
            Assert.Equal(0, afterDeletion.BrokenCount);
        }

        // ================================================================
        // 2026-08-01 ROUND 5: THE SEALED EXPORT, AND THE TWO FOLD BRANCHES.
        //
        // Round 4 gave the Audit Evidence bundle a folded verdict (DescribeChainForCompliance) and
        // left the OTHER client-facing document — the SHA-256-sealed "Audit Chain Verification
        // Report" the viewer exports — printing a raw VerifyChain result. Measured, one launch, one
        // set of bytes:
        //   (a) records removed, anchor still in place : bundle "Broken"        | export "INTACT"
        //   (b) anchor unreadable, chain untouched     : bundle "Indeterminate" | export "INTACT"
        // The document that says INTACT is the one that gets sealed and handed over.
        //
        // Neither fold branch had ANY test. Both are pinned below, on both surfaces. Each test
        // asserts the raw VerifyChain label FIRST — that value is exactly what the pre-fix export
        // printed, so the red half is measured inside the test rather than described.
        // ================================================================

        /// <summary>
        /// Truncation with the anchor in place. The surviving entries all verify, so VerifyChain
        /// says INTACT and is right about what is still on disk; the anchor is the only witness that
        /// anything was removed. The sealed export must not print INTACT over that.
        /// </summary>
        [Fact]
        public void SealedExport_DoesNotPrintIntact_WhenTheAnchorShowsRecordsWereRemoved()
        {
            if (!OperatingSystem.IsWindows()) return; // the anchor is DPAPI-wrapped

            string logFile;
            using (var svc = NewService())
            {
                for (int i = 0; i < 4; i++) svc.LogConnectionAttempt($"srv{i}", success: true);
                svc.Flush(); // anchors the tail signature
                logFile = LatestLogFile();
            }

            // Remove the newest record. Everything left is a valid prefix and re-verifies clean.
            var lines = File.ReadAllLines(logFile);
            Assert.Equal(4, lines.Length);
            File.WriteAllLines(logFile, lines.Take(3).ToArray());

            using var svc2 = NewService();

            // The value the pre-fix export printed, measured on these exact bytes.
            Assert.Equal("INTACT", svc2.VerifyChain("precondition").StatusLabel);
            Assert.True(svc2.ChainBroken, "Precondition: the anchor is what notices the removal.");

            var statement = svc2.DescribeChainForCompliance("test");
            var report = svc2.ComposeChainVerificationReport(statement, "tester");

            Assert.Equal("BROKEN", statement.StatusWord);
            Assert.DoesNotContain("Chain status:     INTACT", report);
            Assert.Contains("Chain status:     BROKEN", report);
            Assert.Contains("REMOVED", report);
        }

        /// <summary>
        /// The other branch: the anchor is present but unreadable — a service-account change orphans
        /// it exactly as deliberate corruption would — over a chain nothing touched. Truncation can
        /// then be neither detected nor ruled out, so the sealed export must claim neither.
        /// </summary>
        [Fact]
        public void SealedExport_DoesNotPrintIntact_WhenTheAnchorCannotBeRead()
        {
            if (!OperatingSystem.IsWindows()) return;

            using (var svc = NewService())
            {
                svc.LogConnectionAttempt("srv1", success: true);
                svc.LogConnectionAttempt("srv2", success: true);
                svc.Flush();
            }

            var anchorPath = Path.Combine(_tempDir, ".chain-anchor");
            Assert.True(File.Exists(anchorPath));
            File.SetAttributes(anchorPath, FileAttributes.Normal);
            File.WriteAllBytes(anchorPath, System.Security.Cryptography.ProtectedData.Protect(
                System.Text.Encoding.UTF8.GetBytes("{\"Sig\":\"x\",\"Ts\":\"x\"}"),
                System.Text.Encoding.UTF8.GetBytes("a-different-entropy"),
                System.Security.Cryptography.DataProtectionScope.LocalMachine));

            using var svc2 = NewService();

            Assert.Equal("INTACT", svc2.VerifyChain("precondition").StatusLabel);
            Assert.True(StartupProvenanceIndeterminate(svc2),
                "Precondition: an unreadable anchor is the indeterminate shape, not the broken one.");

            var statement = svc2.DescribeChainForCompliance("test");
            var report = svc2.ComposeChainVerificationReport(statement, "tester");

            Assert.Equal("INDETERMINATE", statement.StatusWord);
            Assert.DoesNotContain("Chain status:     INTACT", report);
            Assert.Contains("Chain status:     INDETERMINATE", report);
            Assert.Contains("makes no claim that the chain is complete", report);
        }

        /// <summary>
        /// B3 (2026-08-01 round 5): the sealed export printed "HMAC key wrapped: DPAPI (LocalMachine
        /// scope)" as a CONSTANT, swapped in from "(CurrentUser scope)" by e2eb6b9 when the WRITE
        /// path changed. Measured on the installed service's own directory that same day, every one
        /// of hmac.key, both hmac.key.&lt;id&gt; archives and .chain-anchor carried flags 0x00000000 —
        /// CurrentUser. A false fact, on a sealed document, about the exact property (identity
        /// independence) whose absence caused the incident this branch exists to remove.
        /// <para>
        /// Two files, one report, opposite scopes: the live key as this build writes it
        /// (LocalMachine) and an older archive left as every pre-2026-08-01 install has it
        /// (CurrentUser — prior keys' archives are only re-wrapped when verification actually reads
        /// one, so an unread archive stays as it was). No constant can be right about both.
        /// </para>
        /// </summary>
        [Fact]
        public void SealedExport_ReportsTheScopeEachKeyFileActuallyCarries_NotAConstant()
        {
            if (!OperatingSystem.IsWindows()) return;

            using (var svc = NewService())
            {
                svc.LogConnectionAttempt("srv1", success: true);
                svc.Flush();
            }

            // An older key's archive, CurrentUser-wrapped. No entry names it, so nothing reads it
            // and nothing upgrades it — exactly the state a real prior key's archive is left in.
            var staleArchive = Path.Combine(_tempDir, "hmac.key.AAAABBBBCCCCDDDD");
            File.WriteAllBytes(staleArchive, System.Security.Cryptography.ProtectedData.Protect(
                new byte[32], HmacEntropy(),
                System.Security.Cryptography.DataProtectionScope.CurrentUser));

            using var svc2 = NewService();
            var scopes = svc2.DescribeKeyMaterialScopes();
            var report = svc2.ComposeChainVerificationReport(
                svc2.DescribeChainForCompliance("test"), "tester");

            Assert.Equal("DPAPI, LocalMachine scope",
                scopes.Single(r => r.FileName == "hmac.key").Scope);
            Assert.StartsWith("DPAPI, CurrentUser scope",
                scopes.Single(r => r.FileName == "hmac.key.AAAABBBBCCCCDDDD").Scope);

            // Both must reach the sealed document, and the old constant must be gone from it.
            Assert.Contains("hmac.key.AAAABBBBCCCCDDDD", report);
            Assert.Contains("DPAPI, LocalMachine scope", report);
            Assert.Contains("DPAPI, CurrentUser scope", report);
            Assert.DoesNotContain("HMAC key wrapped: DPAPI (LocalMachine scope)", report);
        }

        // ================================================================
        // 2026-08-01 ROUND 6: THE FOOTNOTE THAT EXCULPATED UNCONDITIONALLY,
        //                     AND THE CHAIN'S OWN RECORD OF ITS OWN VERDICT.
        //
        // Round 5's fix (8f04748) added three unconditional lines to the sealed report ending
        // "...That is the 2026-08-01 incident, and it is not tampering." Measured at 8f04748 they
        // printed under a BROKEN verdict, and on an install holding ZERO CurrentUser blobs.
        //
        // Separately, LogChainVerified is called from inside VerifyChain with the RAW result, so
        // the chain's own AuditChainVerified record contradicted every artifact the same call
        // produced — and that record is client-reachable through the viewer's CSV/JSON exports.
        //
        // Both halves below are written against existing public API so the file still COMPILES at
        // 8f04748 and each red half is measured there rather than described.
        // ================================================================

        /// <summary>The one gated sentence, quoted so a reword shows up here rather than silently
        /// re-enabling the unconditional print.</summary>

        private const string CurrentUserFootnote =
            "At least one blob above is wrapped CurrentUser";

        /// <summary>
        /// THE FRESH-INSTALL HALF. This build writes hmac.key and .chain-anchor LocalMachine, so a
        /// clean install holds NO CurrentUser blob at all. The footnote describes evidence that
        /// does not exist there, and a sealed document must not describe evidence it did not read.
        /// <para>RED at 8f04748: the three lines are unconditional, so the exculpation prints over
        /// an install whose measured scope table has no CurrentUser row anywhere in it.</para>
        /// </summary>
        [Fact]
        public void SealedExport_OmitsTheCurrentUserFootnote_WhenNoBlobCarriesThatScope()
        {
            if (!OperatingSystem.IsWindows()) return;

            using (var svc = NewService())
            {
                svc.LogConnectionAttempt("srv1", success: true);
                svc.Flush();
            }

            using var svc2 = NewService();

            // THE GATE, measured: not one row in the scope table this report prints is CurrentUser.
            var scopes = svc2.DescribeKeyMaterialScopes();
            Assert.DoesNotContain(scopes, r => r.Scope.StartsWith("DPAPI, CurrentUser scope", StringComparison.Ordinal));

            var report = svc2.ComposeChainVerificationReport(
                svc2.DescribeChainForCompliance("test"), "tester");

            Assert.Contains("Chain status:     INTACT", report);
            Assert.DoesNotContain(CurrentUserFootnote, report);
            Assert.DoesNotContain("service-account change can orphan it", report);
            Assert.DoesNotContain("it is not tampering", report);
        }

        /// <summary>
        /// THE BROKEN-VERDICT HALF. An ordinary tamper: one entry's message rewritten, its signature
        /// no longer recomputes. VerifyChain says BROKEN on its own, with no anchor finding needed.
        /// The sealed document must not carry the words "it is not tampering" anywhere on a page it
        /// headlines BROKEN — and eleven lines below, INDETERMINATE's own text tells the reader not
        /// to close the finding as a key-management event without evidence.
        /// <para>RED at 8f04748: "Chain status:     BROKEN" and "it is not tampering" both print, in
        /// that order, on the same sealed page.</para>
        /// </summary>
        [Fact]
        public void SealedExport_NeverPrintsAnExculpation_OnAChainItCallsBroken()
        {
            if (!OperatingSystem.IsWindows()) return;

            string logFile;
            using (var svc = NewService())
            {
                svc.LogConnectionAttempt("srv1", success: true);
                svc.LogConnectionAttempt("srv2", success: true);
                svc.Flush();
                logFile = LatestLogFile();
            }

            // Ordinary tamper: alter the payload and leave the KeyId alone, so the entry IS checked
            // against a key that is present and simply fails. Not an evidence gap — the tamper signal.
            var lines = File.ReadAllLines(logFile);
            lines[1] = RewriteJson(lines[1], "Message", "\"TAMPERED\"");
            File.WriteAllLines(logFile, lines);

            using var svc2 = NewService();
            var statement = svc2.DescribeChainForCompliance("test");
            var report = svc2.ComposeChainVerificationReport(statement, "tester");

            Assert.Equal("BROKEN", statement.StatusWord);
            Assert.Contains("Chain status:     BROKEN", report);
            Assert.DoesNotContain("it is not tampering", report);
            Assert.DoesNotContain(CurrentUserFootnote, report);
        }

        /// <summary>
        /// THE GREEN HALF, and the shape of what survives. Plant a CurrentUser-wrapped archive — the
        /// state every pre-2026-08-01 install's prior-key archives are actually in — and the footnote
        /// prints, because now the table above it has a row saying so. What it must NOT do is recover
        /// any of the three over-claims: no verdict named (the deployed shape measured INDETERMINATE
        /// where the old text said UNVERIFIABLE), no incident asserted, no judgement about tampering.
        /// </summary>
        [Fact]
        public void SealedExport_PrintsTheCurrentUserFootnote_OnlyAsAMeasuredFactWithNoVerdictAndNoExculpation()
        {
            if (!OperatingSystem.IsWindows()) return;

            using (var svc = NewService())
            {
                svc.LogConnectionAttempt("srv1", success: true);
                svc.Flush();
            }

            var staleArchive = Path.Combine(_tempDir, "hmac.key.AAAABBBBCCCCDDDD");
            File.WriteAllBytes(staleArchive, System.Security.Cryptography.ProtectedData.Protect(
                new byte[32], HmacEntropy(),
                System.Security.Cryptography.DataProtectionScope.CurrentUser));

            using var svc2 = NewService();
            var scopes = svc2.DescribeKeyMaterialScopes();
            Assert.StartsWith("DPAPI, CurrentUser scope",
                scopes.Single(r => r.FileName == "hmac.key.AAAABBBBCCCCDDDD").Scope);

            var report = svc2.ComposeChainVerificationReport(
                svc2.DescribeChainForCompliance("test"), "tester");

            Assert.Contains(CurrentUserFootnote, report);
            // The three over-claims, each pinned out.
            Assert.DoesNotContain("it is not tampering", report);
            Assert.DoesNotContain("2026-08-01 incident", report);
            Assert.DoesNotContain("turn its entries UNVERIFIABLE", report);
        }

        /// <summary>
        /// ITEM 2. LogChainVerified is called from inside VerifyChain with the RAW result, so on a
        /// truncated chain the chain's OWN record of the verification said the opposite of every
        /// artifact the same call produced. It is not cosmetic: that record leaves the building
        /// through the audit viewer's Export CSV / Export JSON buttons, and a truncation finding
        /// filed at Info is invisible to any triage that sorts by severity.
        /// <para>
        /// RED at 8f04748, measured on these exact bytes: Status=INTACT, Intact=True,
        /// Severity=Info, message "Audit chain verification: INTACT ...", while the sealed
        /// statement from the same launch says BROKEN.
        /// </para>
        /// </summary>
        [Fact]
        public void ChainVerifiedRecord_CarriesTheFoldedVerdict_WhenTheAnchorShowsRecordsWereRemoved()
        {
            if (!OperatingSystem.IsWindows()) return; // the anchor is DPAPI-wrapped

            string logFile;
            using (var svc = NewService())
            {
                for (int i = 0; i < 4; i++) svc.LogConnectionAttempt($"srv{i}", success: true);
                svc.Flush(); // anchors the tail signature
                logFile = LatestLogFile();
            }

            var lines = File.ReadAllLines(logFile);
            Assert.Equal(4, lines.Length);
            File.WriteAllLines(logFile, lines.Take(3).ToArray());

            using var svc2 = NewService();
            Assert.True(svc2.ChainBroken, "Precondition: the anchor is the only witness to the removal.");

            // The RAW verdict — exactly what the pre-fix record filed — measured here, not described.
            var raw = svc2.VerifyChain("test");
            Assert.Equal("INTACT", raw.StatusLabel);
            // And the verdict every artifact from that same call prints.
            Assert.Equal("BROKEN", svc2.DescribeChainForCompliance("test").StatusWord);

            svc2.Flush();

            var record = ReadAll(LatestLogFile())
                .Last(e => e.EventType == AuditEventType.AuditChainVerified);

            Assert.Equal("BROKEN", record.Details["Status"]);
            Assert.Equal("False", record.Details["Intact"]);
            Assert.Equal(AuditSeverity.Critical, record.Severity);
            Assert.Contains("BROKEN", record.Message);
            Assert.DoesNotContain("verification: INTACT", record.Message);
            // The raw result is kept beside the folded one rather than erased — the escalation
            // stays visible, symmetric with LogChainVerificationExported one method below.
            Assert.Equal("INTACT", record.Details["UnfoldedStatus"]);
        }

        /// <summary>
        /// The other fold branch on the same record: an unreadable anchor over an untouched chain.
        /// Nothing was altered, so this must NOT reach Critical — but it must not stay Info either,
        /// because whether records were removed can be neither detected nor ruled out.
        /// <para>RED at 8f04748: Status=INTACT, Intact=True, Severity=Info.</para>
        /// </summary>
        [Fact]
        public void ChainVerifiedRecord_CarriesTheFoldedVerdict_WhenTheAnchorCannotBeRead()
        {
            if (!OperatingSystem.IsWindows()) return;

            using (var svc = NewService())
            {
                svc.LogConnectionAttempt("srv1", success: true);
                svc.LogConnectionAttempt("srv2", success: true);
                svc.Flush();
            }

            var anchorPath = Path.Combine(_tempDir, ".chain-anchor");
            Assert.True(File.Exists(anchorPath));
            File.SetAttributes(anchorPath, FileAttributes.Normal);
            File.WriteAllBytes(anchorPath, System.Security.Cryptography.ProtectedData.Protect(
                System.Text.Encoding.UTF8.GetBytes("{\"Sig\":\"x\",\"Ts\":\"x\"}"),
                System.Text.Encoding.UTF8.GetBytes("a-different-entropy"),
                System.Security.Cryptography.DataProtectionScope.LocalMachine));

            using var svc2 = NewService();
            Assert.True(StartupProvenanceIndeterminate(svc2));
            Assert.Equal("INTACT", svc2.VerifyChain("test").StatusLabel);

            svc2.Flush();

            var record = ReadAll(LatestLogFile())
                .Last(e => e.EventType == AuditEventType.AuditChainVerified);

            Assert.Equal("INDETERMINATE", record.Details["Status"]);
            Assert.Equal("False", record.Details["Intact"]);
            Assert.Equal(AuditSeverity.Error, record.Severity);
            Assert.Equal("INTACT", record.Details["UnfoldedStatus"]);
        }

        /// <summary>
        /// ITEM 3. DescribeWrappingOf named "CurrentUser scope" for any DPAPI-SHAPED blob that
        /// IsLocalMachineWrapped could not read — including one too short to carry the flags DWORD
        /// at all. Measured with the twenty published header bytes: 20 &lt; 44, so the flags read is
        /// impossible, IsLocalMachineWrapped conservatively returns false (right for the re-wrap
        /// decision), and a sealed document then asserted a scope nobody measured.
        /// <para>RED at 8f04748: "DPAPI, CurrentUser scope - bound to the Windows identity that
        /// wrote it" for a 20-byte file.</para>
        /// </summary>
        [Fact]
        public void KeyMaterialScope_ClaimsNoScope_ForADpapiShapedBlobTooShortToCarryOne()
        {
            if (!OperatingSystem.IsWindows()) return;

            using (var svc = NewService())
            {
                svc.LogConnectionAttempt("srv1", success: true);
                svc.Flush();
            }

            // dwVersion (LE 1) + the 16-byte DPAPI provider GUID. DPAPI-shaped, 24 bytes short of
            // the flags DWORD at offset 40.
            byte[] headerOnly =
            {
                0x01, 0x00, 0x00, 0x00,
                0xD0, 0x8C, 0x9D, 0xDF, 0x01, 0x15, 0xD1, 0x11,
                0x8C, 0x7A, 0x00, 0xC0, 0x4F, 0xC2, 0x97, 0xEB
            };
            Assert.Equal(20, headerOnly.Length);
            Assert.False(IsLocalMachineBlob(headerOnly), "Precondition: the scope flags are unreadable.");
            File.WriteAllBytes(Path.Combine(_tempDir, "hmac.key.FEEDFACEFEEDFACE"), headerOnly);

            using var svc2 = NewService();
            var scope = svc2.DescribeKeyMaterialScopes()
                .Single(r => r.FileName == "hmac.key.FEEDFACEFEEDFACE").Scope;

            Assert.DoesNotContain("CurrentUser", scope);
            Assert.DoesNotContain("LocalMachine", scope);
            Assert.Contains("scope not readable", scope);
            Assert.Contains("20 bytes", scope);
        }

        /// <summary>
        /// THE CONVERSE OF THE HEADLINE DEFECT. The live 2026-08-01 incident's own shape: the
        /// out-of-band anchor is present but cannot be read, nothing on the chain was touched.
        /// A service-account change produces exactly this. Startup used to call it truncation and
        /// set ChainBroken, so the operator got the tampering playbook, while VerifyChain on the
        /// same bytes reported broken=0 and the runbook classified the shape as INDETERMINATE.
        /// <para>RED at 0e3fad0: ChainBroken true, ChainProvenanceIndeterminate false.</para>
        /// </summary>
        [Fact]
        public void UnreadableChainAnchor_IsIndeterminate_NotTampering()
        {
            if (!OperatingSystem.IsWindows()) return;

            using (var svc = NewService())
            {
                svc.LogConnectionAttempt("srv1", success: true);
                svc.LogConnectionAttempt("srv2", success: true);
                svc.Flush();
            }

            // Present, well-formed, and unreadable here — the DPAPI shape an orphaned anchor has.
            var anchorPath = Path.Combine(_tempDir, ".chain-anchor");
            Assert.True(File.Exists(anchorPath));
            File.SetAttributes(anchorPath, FileAttributes.Normal);
            File.WriteAllBytes(anchorPath, System.Security.Cryptography.ProtectedData.Protect(
                System.Text.Encoding.UTF8.GetBytes("{\"Sig\":\"x\",\"Ts\":\"x\"}"),
                System.Text.Encoding.UTF8.GetBytes("a-different-entropy"),
                System.Security.Cryptography.DataProtectionScope.LocalMachine));

            using var svc2 = NewService();

            Assert.False(svc2.ChainBroken,
                "An anchor this identity cannot read is an evidence failure, not evidence of an attack.");
            Assert.True(StartupProvenanceIndeterminate(svc2),
                "It is not benign either — the operator must be told the chain state is undetermined.");

            // Startup and the verifier must not contradict each other about the same bytes.
            var result = svc2.VerifyChain("test");
            Assert.Equal(0, result.BrokenCount);
        }

        /// <summary>
        /// Pins the DPAPI blob header that <c>ArchivedKeyFileIsKeyShaped</c> keys off, by generating
        /// blobs with ProtectedData itself. MEASURED on this platform 2026-08-01: identical under
        /// both scopes and with no entropy at all.
        /// <para>Green at 0e3fad0 — it pins a platform fact, it is not a fix demo. Its job is to
        /// fail loudly if the header layout ever moves under the shape check.</para>
        /// </summary>
        [Fact]
        public void DpapiBlobHeaderProbe_MatchesWhatProtectedDataActuallyWrites()
        {
            if (!OperatingSystem.IsWindows()) return;

            byte[] expected =
            {
                0x01, 0x00, 0x00, 0x00,
                0xD0, 0x8C, 0x9D, 0xDF, 0x01, 0x15, 0xD1, 0x11,
                0x8C, 0x7A, 0x00, 0xC0, 0x4F, 0xC2, 0x97, 0xEB
            };

            foreach (var scope in new[]
                     {
                         System.Security.Cryptography.DataProtectionScope.LocalMachine,
                         System.Security.Cryptography.DataProtectionScope.CurrentUser
                     })
            {
                var blob = System.Security.Cryptography.ProtectedData.Protect(
                    new byte[32], HmacEntropy(), scope);
                Assert.Equal(expected, blob.Take(20).ToArray());
            }
        }

        // ── helpers for the 2026-08-01 round-2 tests ────────────────────

        /// <summary>Replaces one top-level scalar field in an audit JSON line, textually.</summary>
        private static string RewriteJson(string line, string field, string jsonValue)
        {
            var needle = "\"" + field + "\":";
            var start = line.IndexOf(needle, StringComparison.Ordinal);
            Assert.True(start >= 0, $"Field {field} not present in the audit line.");
            var valueStart = start + needle.Length;
            // Values here are always a quoted string; find its closing quote.
            Assert.Equal('"', line[valueStart]);
            var valueEnd = valueStart + 1;
            while (line[valueEnd] != '"' || line[valueEnd - 1] == '\\') valueEnd++;
            return line[..valueStart] + jsonValue + line[(valueEnd + 1)..];
        }

        private static byte[] HmacKeyOf(AuditLogService svc) =>
            (byte[])typeof(AuditLogService)
                .GetField("_hmacKey", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(svc)!;

        private static byte[] HmacEntropy() =>
            (byte[])typeof(AuditLogService)
                .GetField("HmacKeyEntropy", BindingFlags.NonPublic | BindingFlags.Static)!
                .GetValue(null)!;

        /// <summary>
        /// The ORIGINAL (v1) canonical form, rebuilt independently of the service. Frozen: every
        /// entry written before 2026-08-01 on every installation verifies under exactly this.
        /// </summary>
        private static string V1Signature(AuditLogEntry e, string previousSig, byte[] key)
        {
            var canonical = new
            {
                e.Timestamp, e.EventType, e.Severity, e.Message,
                e.Details, e.User, e.Machine, e.PreviousHash
            };
            return Hmac(key, previousSig, JsonSerializer.Serialize(canonical, Json));
        }

        /// <summary>v2: the v1 fields plus KeyId and the version discriminator itself.</summary>
        private static string V2Signature(AuditLogEntry e, string previousSig, byte[] key)
        {
            var canonical = new
            {
                e.Timestamp, e.EventType, e.Severity, e.Message,
                e.Details, e.User, e.Machine, e.PreviousHash,
                e.KeyId, SigVersion = (int?)2
            };
            return Hmac(key, previousSig, JsonSerializer.Serialize(canonical, Json));
        }

        private static string Hmac(byte[] key, string previousSig, string payload) =>
            Convert.ToBase64String(System.Security.Cryptography.HMACSHA256.HashData(
                key, System.Text.Encoding.UTF8.GetBytes(previousSig + "|" + payload)));

        /// <summary>
        /// The reason verification no longer stops at the first bad entry: it used to leave every
        /// later record unexamined, so a second, deeper tamper was invisible behind the first one.
        /// (In the live incident a benign first-entry failure hid 306 unexamined records.)
        /// </summary>
        [Fact]
        public void VerifyChain_CountsEveryTamperedEntry_NotJustTheFirst()
        {
            string logFile;
            using (var svc = NewService())
            {
                for (int i = 0; i < 4; i++)
                    svc.LogConnectionAttempt($"srv{i}", success: true);
                svc.Flush();
                logFile = LatestLogFile();
            }

            var lines = File.ReadAllLines(logFile);
            Assert.Equal(4, lines.Length);
            foreach (var idx in new[] { 0, 2 })
            {
                var e = JsonSerializer.Deserialize<AuditLogEntry>(lines[idx], Json)!;
                e.Message = "TAMPERED-" + idx;
                lines[idx] = JsonSerializer.Serialize(e, Json);
            }
            File.WriteAllLines(logFile, lines);

            using var svc2 = NewService();
            var result = svc2.VerifyChain("test");

            Assert.Equal(AuditLogService.ChainVerificationStatus.Broken, result.Status);
            Assert.Equal(4, result.EntryCount);
            Assert.Equal(2, result.BrokenCount);   // the deeper tamper is seen, not swallowed
            Assert.Equal(2, result.VerifiedCount);
        }

        /// <summary>
        /// When an existing key file cannot be unwrapped, replacing it must be LOUD and must
        /// PRESERVE the old blob. Before the fix this path emitted no log line at all and
        /// overwrote hmac.key in place — losing the key AND destroying the evidence.
        /// </summary>
        [Fact]
        public void UnreadableKeyFile_IsPreservedAndAudited_NotSilentlyOverwritten()
        {
            if (!OperatingSystem.IsWindows()) return; // DPAPI is Windows-only

            using (var svc = NewService())
            {
                svc.LogConnectionAttempt("srv1", success: true);
                svc.Flush();
            }

            // Arrange: replace hmac.key with a blob that is neither unwrappable under this
            // identity nor a legacy raw 32-byte key — the shape a foreign-identity DPAPI blob
            // presents (the production one was 262 bytes).
            var keyPath = Path.Combine(_tempDir, "hmac.key");
            var foreignBlob = new byte[262];
            System.Security.Cryptography.RandomNumberGenerator.Fill(foreignBlob);
            File.SetAttributes(keyPath, FileAttributes.Normal);
            File.WriteAllBytes(keyPath, foreignBlob);

            var captured = new CapturingSink();
            var previousLogger = Serilog.Log.Logger;
            Serilog.Log.Logger = new Serilog.LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.Sink(captured)
                .CreateLogger();
            try
            {
                using var svc2 = NewService();
                svc2.Flush();

                // Assert — LOUD: an Error-level line naming the cause and the consequence.
                var errors = captured.Snapshot(Serilog.Events.LogEventLevel.Error);
                Assert.Contains(errors, m => m.Contains("HMAC KEY REPLACED"));
                Assert.Contains(errors, m => m.Contains("UNVERIFIABLE"));
                Assert.Contains(errors, m => m.Contains("262"));   // the blob length, for diagnosis

                // Assert — EVIDENCE PRESERVED: the unreadable blob survives, byte for byte.
                var preserved = Directory.GetFiles(_tempDir, "hmac.key.unreadable-*");
                Assert.Single(preserved);
                Assert.Equal(foreignBlob, File.ReadAllBytes(preserved[0]));

                // Assert — the key really was replaced (not left broken), and the new file is
                // not the blob we planted.
                var newBlob = File.ReadAllBytes(keyPath);
                Assert.NotEqual(foreignBlob, newBlob);

                // Assert — the replacement is ON the chain, so an operator reading the audit log
                // can see why history stopped verifying.
                var replacementEvents = ReadAll(LatestLogFile())
                    .Where(e => e.EventType == AuditEventType.HmacKeyReplacedUnreadable)
                    .ToList();
                Assert.Single(replacementEvents);
                Assert.Equal(AuditSeverity.Critical, replacementEvents[0].Severity);
                Assert.Equal(preserved[0], replacementEvents[0].Details["PreservedPath"]);
                Assert.Equal("262", replacementEvents[0].Details["UnreadableBlobLength"]);
            }
            finally
            {
                Serilog.Log.Logger = previousLogger;
            }
        }

        /// <summary>Minimal in-memory Serilog sink so the "is it actually loud?" claim is exercised.</summary>
        private sealed class CapturingSink : Serilog.Core.ILogEventSink
        {
            private readonly List<(Serilog.Events.LogEventLevel Level, string Message)> _events = new();

            public void Emit(Serilog.Events.LogEvent logEvent)
            {
                lock (_events)
                    _events.Add((logEvent.Level, logEvent.RenderMessage()));
            }

            public List<string> Snapshot(Serilog.Events.LogEventLevel minimumLevel)
            {
                lock (_events)
                    return _events.Where(e => e.Level >= minimumLevel).Select(e => e.Message).ToList();
            }
        }
    }
}
