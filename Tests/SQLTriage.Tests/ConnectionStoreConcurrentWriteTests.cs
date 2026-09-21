/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// <c>server-connections.json</c> is rewritten WHOLESALE from a process-lifetime cache, so
    /// anything written to it after this process loaded it is deleted by the next save — including
    /// SQL logins and their protected passwords, which are the only copies on the machine.
    ///
    /// <para><b>Driven by the 2026-08-05 cold gate, not argued.</b> It loaded a healthy
    /// 1-connection store, added a second connection carrying a credential to the file on disk, then
    /// called the UNATTENDED <c>UpdateSuccessfulServers</c>. Hash <c>508E…</c>→<c>93CD…</c>;
    /// survivors were the first connection only. The credential was silently gone.</para>
    ///
    /// <para>The startup-outcome guard could not see it and was never going to: the file had loaded
    /// perfectly, so there was no damage for a probe to find. The fix is a fingerprint taken at load
    /// and re-taken immediately before every write. These tests hold that, and hold the two
    /// properties that make it safe to have: a normal edit still saves, and the refusal carries
    /// advice about reloading rather than about damage or disk space.</para>
    /// </summary>
    public class ConnectionStoreConcurrentWriteTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _path;

        public ConnectionStoreConcurrentWriteTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "conn-race-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _path = Path.Combine(_dir, "server-connections.json");
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
        }

        private ServerConnectionManager Build() =>
            new(NullLogger<ServerConnectionManager>.Instance, seats: null, connectionsFilePath: _path);

        private void WriteStore(params (string Id, string Name)[] rows)
        {
            var list = rows.Select(r => new Dictionary<string, object?>
            {
                ["Id"] = r.Id,
                ["ServerNames"] = r.Name,
                ["Username"] = "sa-" + r.Name,
                ["Password"] = "protected-secret-for-" + r.Name,
            }).ToList();
            File.WriteAllText(_path, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        }

        private static string Hash(string p) =>
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)));

        /// <summary>
        /// THE REGRESSION, in the gate's exact shape. A second writer's connection must survive.
        /// </summary>
        [Fact]
        public void AConnectionAddedByAnotherWriterAfterLoad_IsNotDeletedByOurNextSave()
        {
            WriteStore(("prod", "PROD-SQL01"));
            var mgr = Build();                                  // loads 1
            Assert.Single(mgr.GetConnections());

            // Another writer adds a DR server WITH a credential, straight to disk.
            WriteStore(("prod", "PROD-SQL01"), ("dr", "DR-SQL01"));
            var before = Hash(_path);

            // The unattended status write — the path that did the damage.
            mgr.UpdateSuccessfulServers("prod", new List<string> { "PROD-SQL01" });

            Assert.Equal(before, Hash(_path));                  // file untouched

            var onDisk = File.ReadAllText(_path);
            Assert.Contains("DR-SQL01", onDisk, StringComparison.Ordinal);
            Assert.Contains("protected-secret-for-DR-SQL01", onDisk, StringComparison.Ordinal);
        }

        /// <summary>
        /// Same race through the operator-facing mutator: refused, and the reason talks about
        /// RELOADING — not about damage (there is none) and not about disk space (it is fine).
        /// A distinct state inheriting another's advice is this wave's most repeated defect.
        /// </summary>
        [Fact]
        public void AddConnectionAfterAnExternalEdit_IsRefused_WithReloadAdviceNotDamageAdvice()
        {
            WriteStore(("prod", "PROD-SQL01"));
            var mgr = Build();

            WriteStore(("prod", "PROD-SQL01"), ("dr", "DR-SQL01"));
            var before = Hash(_path);

            var result = mgr.AddConnection(new ServerConnection { ServerNames = "NEW-SQL" });

            Assert.False(result.Succeeded);
            Assert.Equal(before, Hash(_path));
            Assert.Contains("changed by something else", result.Reason ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(".rejected-", result.Reason ?? "", StringComparison.Ordinal);
            Assert.DoesNotContain("disk space", result.Reason ?? "", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// THE OTHER HALF, and the reason a guard like this gets removed if it is wrong: an ordinary
        /// edit with nobody else writing must still save. A guard that refuses normal work is a
        /// guard an operator disables, taking the credential protection with it.
        /// </summary>
        [Fact]
        public void AnOrdinaryEdit_WithNoOtherWriter_StillSaves()
        {
            WriteStore(("prod", "PROD-SQL01"));
            var mgr = Build();

            var first = mgr.AddConnection(new ServerConnection { ServerNames = "NEW-SQL" });
            Assert.True(first.Succeeded, first.Reason);

            // And again - the fingerprint must have been re-taken after our own write, or the
            // second save would refuse itself.
            var second = mgr.AddConnection(new ServerConnection { ServerNames = "NEW-SQL-2" });
            Assert.True(second.Succeeded, second.Reason);

            Assert.Equal(3, mgr.GetConnections().Count);
        }

        /// <summary>
        /// THE SECOND-ATTEMPT PROPERTY, and the reason this guard is usable at all.
        ///
        /// <para>Found by the 2026-08-05 delta gate: this class is a DI SINGLETON and
        /// <c>LoadConnections</c> ran only in its constructor, so one external edit refused EVERY
        /// subsequent save until the process restarted — while the operator-facing sentence told
        /// them to reload and try again. Refusing once is the guard; refusing forever is a bug, and
        /// advice that cannot be followed is how a guard gets disabled.</para>
        ///
        /// <para>So the refusal adopts the newer file. The first attempt still refuses — nothing is
        /// silently overwritten — and the retry works against what is actually on disk, with the
        /// other writer's connection intact.</para>
        /// </summary>
        [Fact]
        public void AfterARefusal_TheRetrySucceeds_AndTheOtherWritersConnectionSurvives()
        {
            WriteStore(("prod", "PROD-SQL01"));
            var mgr = Build();

            WriteStore(("prod", "PROD-SQL01"), ("dr", "DR-SQL01"));

            var first = mgr.AddConnection(new ServerConnection { ServerNames = "NEW-SQL" });
            Assert.False(first.Succeeded);                      // refused, as it must be

            var second = mgr.AddConnection(new ServerConnection { ServerNames = "NEW-SQL" });
            Assert.True(second.Succeeded, second.Reason);       // and now it works

            var onDisk = File.ReadAllText(_path);
            Assert.Contains("DR-SQL01", onDisk, StringComparison.Ordinal);
            Assert.Contains("NEW-SQL", onDisk, StringComparison.Ordinal);
            Assert.Equal(3, mgr.GetConnections().Count);

            // The DR row's CREDENTIAL survives, checked through the model rather than by string:
            // unlike the refusal tests above, this path actually saves, and a save encrypts a
            // plaintext password once (the legacy-migration path). Asserting the literal here would
            // fail for the right reason and read as data loss, which is the opposite of the truth.
            var dr = mgr.GetConnections().Single(c => c.ServerNames == "DR-SQL01");
            Assert.False(string.IsNullOrWhiteSpace(dr.Password));
            Assert.Equal("sa-DR-SQL01", dr.Username);
        }

        /// <summary>
        /// A fresh install has no file, and the first save must work — <c>Missing</c> is not damage
        /// and is not a "change". Pinned because the fingerprint of an absent file is null, which is
        /// exactly the value an uninitialised field would also hold.
        /// </summary>
        [Fact]
        public void AFirstSaveOnAFreshInstall_Works()
        {
            Assert.False(File.Exists(_path));
            var mgr = Build();

            var result = mgr.AddConnection(new ServerConnection { ServerNames = "FIRST" });

            Assert.True(result.Succeeded, result.Reason);
            Assert.True(File.Exists(_path));
        }

        /// <summary>
        /// A store REPAIRED on disk while this process holds an empty default must not be flattened
        /// by that default — the damaged-load path takes a fingerprint too.
        /// </summary>
        [Fact]
        public void AStoreRepairedOnDisk_IsNotOverwrittenByOurEmptyDefault()
        {
            File.WriteAllText(_path, "{ truncated");
            var mgr = Build();
            Assert.True(mgr.IsStoreDamaged);

            WriteStore(("prod", "PROD-SQL01"));                 // operator restores it
            var before = Hash(_path);

            var result = mgr.AddConnection(new ServerConnection { ServerNames = "NEW-SQL" });

            Assert.False(result.Succeeded);
            Assert.Equal(before, Hash(_path));
            Assert.Contains("PROD-SQL01", File.ReadAllText(_path), StringComparison.Ordinal);
        }
    }
}
