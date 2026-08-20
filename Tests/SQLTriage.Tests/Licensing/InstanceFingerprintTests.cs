/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.Globalization;
using SQLTriage.Data.Models;
using Xunit;

namespace SQLTriage.Tests.Licensing;

/// <summary>
/// The SEAT IDENTITY contract (ruled 2026-07-17). These tests pin the hash inputs, because a change
/// to the canonical form silently re-fingerprints every seated instance in every client estate and
/// burns every swap. If one of these fails, the fix is almost certainly to revert the production
/// change — not to update the test.
/// </summary>
public sealed class InstanceFingerprintTests
{
    private static readonly DateTime Created =
        DateTime.Parse("2019-03-04T11:22:33.4560000Z", CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

    private static InstanceFingerprint Fp(
        string machine = "SQLPROD01",
        string? instance = null,
        string? serverName = "SQLPROD01",
        DateTime? created = null) =>
        new()
        {
            MachineName = machine,
            InstanceName = instance,
            ServerName = serverName,
            MasterCreateDate = created ?? Created,
        };

    // ── Stability ────────────────────────────────────────────────────────────

    [Fact]
    public void SameInput_ProducesSameHash()
    {
        Assert.Equal(Fp().Hash, Fp().Hash);
    }

    [Fact]
    public void Hash_IsLowercaseHexSha256()
    {
        var hash = Fp().Hash;
        Assert.Equal(64, hash.Length);
        Assert.All(hash, c => Assert.True((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'),
            $"unexpected character '{c}' in hash"));
    }

    [Fact]
    public void MachineName_IsCaseInsensitive()
    {
        // The same box reported with different casing must not fork into two seats.
        Assert.Equal(Fp(machine: "SQLPROD01").Hash, Fp(machine: "sqlprod01").Hash);
    }

    // ── The RENAME rule: ServerName is display-only, NOT hashed ───────────────

    [Fact]
    public void ServerNameRename_ProducesSameFingerprint()
    {
        // THE RULING: a server rename must NOT burn a swap. ServerName is display metadata only.
        var before = Fp(serverName: "OLD-NAME");
        var after = Fp(serverName: "BRAND-NEW-NAME");
        Assert.Equal(before.Hash, after.Hash);
    }

    [Fact]
    public void ServerNameNull_ProducesSameFingerprint()
    {
        Assert.Equal(Fp(serverName: "ANY").Hash, Fp(serverName: null).Hash);
    }

    // ── The REPOINT rule: a different install is a different seat ─────────────

    [Fact]
    public void DifferentMasterCreateDate_ProducesDifferentFingerprint()
    {
        // THE RULING: master's create_date is what makes a repoint cost a seat. Same host, same
        // instance name, different install => different seat.
        var a = Fp();
        var b = Fp(created: Created.AddSeconds(1));
        Assert.NotEqual(a.Hash, b.Hash);
    }

    [Fact]
    public void DifferentMachine_ProducesDifferentFingerprint()
    {
        Assert.NotEqual(Fp(machine: "SQLPROD01").Hash, Fp(machine: "SQLPROD02").Hash);
    }

    [Fact]
    public void NamedInstances_OnSameHost_AreDistinctSeats()
    {
        // Two named instances on one host share MachineName but have distinct master create_dates
        // in reality; even with the SAME date they must differ by instance name alone.
        Assert.NotEqual(Fp(instance: "ALPHA").Hash, Fp(instance: "BETA").Hash);
    }

    // ── Default-instance canonicalisation ────────────────────────────────────

    [Fact]
    public void NullInstanceName_CanonicalisesToMssqlserver()
    {
        // SERVERPROPERTY('InstanceName') is NULL on a default instance; every default instance must
        // canonicalise to the same token or the same box would fingerprint differently depending on
        // how the probe happened to read it.
        Assert.Equal(Fp(instance: null).Hash, Fp(instance: "MSSQLSERVER").Hash);
        Assert.Equal(Fp(instance: null).Hash, Fp(instance: "mssqlserver").Hash);
        Assert.Contains("|mssqlserver|", Fp(instance: null).Canonical);
    }

    [Fact]
    public void WhitespaceInstanceName_CanonicalisesToDefault()
    {
        Assert.Equal(Fp(instance: null).Hash, Fp(instance: "   ").Hash);
    }

    // ── Canonical form ───────────────────────────────────────────────────────

    [Fact]
    public void Canonical_IsLowerMachine_Pipe_LowerInstance_Pipe_RoundTripDate()
    {
        var fp = Fp(machine: "SQLProd01", instance: "Alpha");
        Assert.Equal(
            "sqlprod01|alpha|" + Created.ToString("O", CultureInfo.InvariantCulture),
            fp.Canonical);
    }

    [Fact]
    public void Canonical_DateFormat_IsCultureInvariant()
    {
        // A host in a culture with a non-Gregorian calendar or a different separator must produce
        // byte-identical canonical text, or the same instance forks per-machine.
        var prior = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = new CultureInfo("ar-SA");
            var underArabic = Fp().Canonical;
            System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            Assert.Equal(Fp().Canonical, underArabic);
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = prior;
        }
    }

    [Fact]
    public void Canonical_LowercasingIsInvariant_NotTurkish()
    {
        // ToLowerInvariant, never ToLower: under tr-TR, 'I'.ToLower() is the dotless 'ı', which
        // would silently fork the fingerprint of a machine whose name contains an I.
        var prior = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = new CultureInfo("tr-TR");
            var underTurkish = Fp(machine: "SQLPRODI").Hash;
            System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            Assert.Equal(Fp(machine: "SQLPRODI").Hash, underTurkish);
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = prior;
        }
    }

    // ── Display metadata ─────────────────────────────────────────────────────

    [Fact]
    public void DisplayName_PrefersServerName_ThenMachineInstance()
    {
        Assert.Equal("REPORTED", Fp(serverName: "REPORTED").DisplayName);
        Assert.Equal("SQLPROD01", Fp(serverName: null, instance: null).DisplayName);
        Assert.Equal("SQLPROD01\\ALPHA", Fp(serverName: null, instance: "ALPHA").DisplayName);
    }

    [Fact]
    public void ShortHash_IsFirst12OfHash()
    {
        var fp = Fp();
        Assert.Equal(fp.Hash[..12], fp.ShortHash);
    }

    // ── The accepted loophole, pinned as a FACT (not an aspiration) ───────────

    [Fact]
    public void ClonedVm_SharesASeat_KnownAcceptedLoophole()
    {
        // DOCUMENTED, ACCEPTED loophole: a cloned/restored VM carries the same MachineName and the
        // same master create_date, so it shares a seat. This test exists to pin that as a KNOWN
        // property of the design — if it ever fails, the fingerprint inputs changed and the
        // documentation in InstanceFingerprint + any UI copy must be revisited to match.
        var original = Fp(machine: "SQLPROD01", serverName: "SQLPROD01");
        var clone = Fp(machine: "SQLPROD01", serverName: "SQLPROD01-CLONE");
        Assert.Equal(original.Hash, clone.Hash);
    }
}
