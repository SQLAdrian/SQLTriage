/* In the name of God, the Merciful, the Compassionate */

using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    public class WaitCategoryClassifierTests
    {
        // ── Null / empty input ──────────────────────────────────────────

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void Classify_NullOrEmpty_ReturnsOther(string? waitType)
        {
            Assert.Equal(WaitCategory.Other, WaitCategoryClassifier.Classify(waitType!));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void IsIdleBenign_NullOrEmpty_ReturnsFalse(string? waitType)
        {
            Assert.False(WaitCategoryClassifier.IsIdleBenign(waitType!));
        }

        // ── Idle / benign waits ─────────────────────────────────────────

        [Theory]
        [InlineData("CHECKPOINT_QUEUE")]
        [InlineData("LAZYWRITER_SLEEP")]
        [InlineData("XE_DISPATCHER_WAIT")]
        [InlineData("REQUEST_FOR_DEADLOCK_SEARCH")]
        [InlineData("SQLTRACE_WAIT_ENTRIES")]
        [InlineData("WAITFOR")]
        public void Classify_KnownBenign_ReturnsIdleBenign(string waitType)
        {
            Assert.Equal(WaitCategory.IdleBenign, WaitCategoryClassifier.Classify(waitType));
        }

        [Theory]
        [InlineData("CHECKPOINT_QUEUE")]
        [InlineData("LAZYWRITER_SLEEP")]
        [InlineData("WAITFOR")]
        public void IsIdleBenign_KnownBenign_ReturnsTrue(string waitType)
        {
            Assert.True(WaitCategoryClassifier.IsIdleBenign(waitType));
        }

        [Fact]
        public void IsIdleBenign_IsCaseInsensitive()
        {
            Assert.True(WaitCategoryClassifier.IsIdleBenign("checkpoint_queue"));
            Assert.True(WaitCategoryClassifier.IsIdleBenign("Checkpoint_Queue"));
        }

        // ── Prefix-matched categories ───────────────────────────────────

        [Theory]
        [InlineData("PAGEIOLATCH_SH")]
        [InlineData("PAGEIOLATCH_EX")]
        [InlineData("PAGEIOLATCH_UP")]
        public void Classify_PageIoLatch_ReturnsIo(string waitType)
        {
            Assert.Equal(WaitCategory.Io, WaitCategoryClassifier.Classify(waitType));
        }

        [Theory]
        [InlineData("PAGELATCH_SH")]
        [InlineData("PAGELATCH_EX")]
        [InlineData("PAGELATCH_UP")]
        public void Classify_PageLatch_ReturnsBuffer(string waitType)
        {
            // PAGELATCH_* (no IO) is in-memory buffer contention, distinct from PAGEIOLATCH_*
            Assert.Equal(WaitCategory.Buffer, WaitCategoryClassifier.Classify(waitType));
        }

        [Theory]
        [InlineData("LATCH_SH")]
        [InlineData("LATCH_EX")]
        public void Classify_NonBufferLatch_ReturnsLatch(string waitType)
        {
            Assert.Equal(WaitCategory.Latch, WaitCategoryClassifier.Classify(waitType));
        }

        [Theory]
        [InlineData("LCK_M_S")]
        [InlineData("LCK_M_X")]
        [InlineData("LCK_M_IX")]
        public void Classify_LockWait_ReturnsLock(string waitType)
        {
            Assert.Equal(WaitCategory.Lock, WaitCategoryClassifier.Classify(waitType));
        }

        [Theory]
        [InlineData("WRITELOG")]
        [InlineData("BACKUPIO")]
        [InlineData("BACKUPBUFFER")]
        public void Classify_LogAndBackupIo_ReturnsIo(string waitType)
        {
            Assert.Equal(WaitCategory.Io, WaitCategoryClassifier.Classify(waitType));
        }

        [Theory]
        [InlineData("HADR_SYNC_COMMIT")]
        [InlineData("PREEMPTIVE_HADR_LEASE_MECHANISM")]
        [InlineData("DBMIRROR_SEND")]
        public void Classify_ReplicationFamilies_ReturnsReplication(string waitType)
        {
            Assert.Equal(WaitCategory.Replication, WaitCategoryClassifier.Classify(waitType));
        }

        // ── Exact-match categories ──────────────────────────────────────

        [Theory]
        [InlineData("SOS_SCHEDULER_YIELD")]
        [InlineData("CXPACKET")]
        [InlineData("CXCONSUMER")]
        [InlineData("THREADPOOL")]
        public void Classify_CpuFamily_ReturnsCpu(string waitType)
        {
            Assert.Equal(WaitCategory.Cpu, WaitCategoryClassifier.Classify(waitType));
        }

        [Theory]
        [InlineData("ASYNC_IO_COMPLETION")]
        [InlineData("IO_COMPLETION")]
        [InlineData("IO_QUEUE_LIMIT")]
        public void Classify_IoFamily_ReturnsIo(string waitType)
        {
            Assert.Equal(WaitCategory.Io, WaitCategoryClassifier.Classify(waitType));
        }

        [Theory]
        [InlineData("RESOURCE_SEMAPHORE")]
        [InlineData("RESOURCE_SEMAPHORE_QUERY_COMPILE")]
        [InlineData("CMEMTHREAD")]
        public void Classify_MemoryFamily_ReturnsMemory(string waitType)
        {
            Assert.Equal(WaitCategory.Memory, WaitCategoryClassifier.Classify(waitType));
        }

        [Theory]
        [InlineData("ASYNC_NETWORK_IO")]
        [InlineData("NET_WAITFOR_PACKET")]
        public void Classify_NetworkFamily_ReturnsNetwork(string waitType)
        {
            Assert.Equal(WaitCategory.Network, WaitCategoryClassifier.Classify(waitType));
        }

        // ── Unknown / catch-all ─────────────────────────────────────────

        [Theory]
        [InlineData("SOMETHING_UNKNOWN")]
        [InlineData("MADE_UP_WAIT_TYPE")]
        public void Classify_Unknown_ReturnsOther(string waitType)
        {
            Assert.Equal(WaitCategory.Other, WaitCategoryClassifier.Classify(waitType));
        }

        // ── Cascade / precedence guarantees ─────────────────────────────

        [Fact]
        public void Classify_BenignBeatsExactMatch()
        {
            // EXECSYNC appears in BOTH the benign set AND the CPU-family switch.
            // Benign must win because IsIdleBenign is checked first.
            Assert.Equal(WaitCategory.IdleBenign, WaitCategoryClassifier.Classify("EXECSYNC"));
        }

        [Fact]
        public void Classify_PageIoLatchPrefix_BeatsPageLatchPrefix()
        {
            // Guard against accidentally reordering the prefix checks so PAGELATCH_
            // matches the start of PAGEIOLATCH_. PAGEIOLATCH_ must classify as Io.
            Assert.Equal(WaitCategory.Io, WaitCategoryClassifier.Classify("PAGEIOLATCH_SH"));
            Assert.Equal(WaitCategory.Buffer, WaitCategoryClassifier.Classify("PAGELATCH_SH"));
        }

        // ── Case sensitivity of Classify ────────────────────────────────

        [Fact]
        public void Classify_PrefixesAreCaseSensitive()
        {
            // The prefix branches use StringComparison.Ordinal — lowercase input
            // does NOT match. Documented behaviour; locking in via test so a future
            // change to case-insensitive matching is a conscious decision.
            Assert.Equal(WaitCategory.Other, WaitCategoryClassifier.Classify("pageiolatch_sh"));
        }

        [Fact]
        public void Classify_BenignSetIsCaseInsensitive()
        {
            // BenignWaitTypes uses StringComparer.OrdinalIgnoreCase, so this path
            // tolerates lowercase. Pinning so the set comparer isn't changed by mistake.
            Assert.Equal(WaitCategory.IdleBenign, WaitCategoryClassifier.Classify("checkpoint_queue"));
        }

        // ── BenignWaitTypes set sanity ──────────────────────────────────

        [Fact]
        public void BenignWaitTypes_ContainsExpectedAnchorEntries()
        {
            // Sanity check: the set should contain a handful of well-known idle waits.
            // Catches accidental wipe-outs of the static initialiser.
            Assert.Contains("CHECKPOINT_QUEUE", WaitCategoryClassifier.BenignWaitTypes);
            Assert.Contains("LAZYWRITER_SLEEP", WaitCategoryClassifier.BenignWaitTypes);
            Assert.Contains("WAITFOR", WaitCategoryClassifier.BenignWaitTypes);
            Assert.Contains("XE_DISPATCHER_WAIT", WaitCategoryClassifier.BenignWaitTypes);
        }
    }
}
