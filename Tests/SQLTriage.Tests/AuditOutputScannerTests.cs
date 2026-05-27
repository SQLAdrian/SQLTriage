/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using System.Linq;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests;

/// <summary>
/// AuditOutputScanner finds the newest sp_Blitz CSV per server in the output
/// folder and ignores non-sp_Blitz files. Uses a temp dir per test.
/// </summary>
public class AuditOutputScannerTests : IDisposable
{
    private readonly string _dir;

    public AuditOutputScannerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sqltriage-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* test cleanup */ }
    }

    private void Write(string name, string content)
        => File.WriteAllText(Path.Combine(_dir, name), content);

    private const string BlitzCsv =
        "Priority,FindingsGroup,Finding,DatabaseName,Details,ServerName\n" +
        "1,Reliability,Backups Not Taken,master,No full backups,PROD\\SQL01\n";

    [Fact]
    public void MissingDirectory_ReturnsEmpty()
    {
        var scanner = new AuditOutputScanner(Path.Combine(_dir, "does-not-exist"));
        Assert.Empty(scanner.ScanSpBlitzFiles());
    }

    [Fact]
    public void EmptyDirectory_ReturnsEmpty()
        => Assert.Empty(new AuditOutputScanner(_dir).ScanSpBlitzFiles());

    [Fact]
    public void DetectsSpBlitzFile_AndExtractsServerName()
    {
        Write("sp_Blitz_PROD_20260526.csv", BlitzCsv);

        var found = new AuditOutputScanner(_dir).ScanSpBlitzFiles();

        var f = Assert.Single(found);
        Assert.Equal("PROD\\SQL01", f.ServerName);
        Assert.EndsWith("sp_Blitz_PROD_20260526.csv", f.FilePath);
    }

    [Fact]
    public void IgnoresTriageAndSqlmagicFiles()
    {
        Write("sp_triage_PROD.csv", "SQLInstance,evaldate\nPROD\\SQL01,2026-05-26\n");
        Write("sqlmagic_export.csv", "Server,evaldate\nPROD\\SQL01,2026-05-26\n");
        // a blitz-named file that also contains "triage" must NOT count as sp_Blitz
        Write("blitz_triage_combo.csv", BlitzCsv);

        Assert.Empty(new AuditOutputScanner(_dir).ScanSpBlitzFiles());
    }

    [Fact]
    public void SameServerTwoFiles_KeepsNewestOnly()
    {
        Write("sp_Blitz_old.csv", BlitzCsv);
        var older = Path.Combine(_dir, "sp_Blitz_old.csv");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddHours(-2));

        Write("sp_Blitz_new.csv", BlitzCsv);
        File.SetLastWriteTimeUtc(Path.Combine(_dir, "sp_Blitz_new.csv"), DateTime.UtcNow);

        var found = new AuditOutputScanner(_dir).ScanSpBlitzFiles();

        var f = Assert.Single(found);   // same ServerName → only the newest kept
        Assert.EndsWith("sp_Blitz_new.csv", f.FilePath);
    }

    [Fact]
    public void TwoServers_BothReturned()
    {
        Write("sp_Blitz_a.csv", BlitzCsv);
        Write("sp_Blitz_b.csv",
            "Priority,FindingsGroup,Finding,DatabaseName,Details,ServerName\n" +
            "1,Reliability,X,master,Y,DEV\\SQL02\n");

        var found = new AuditOutputScanner(_dir).ScanSpBlitzFiles();

        Assert.Equal(2, found.Count);
        Assert.Contains(found, f => f.ServerName == "PROD\\SQL01");
        Assert.Contains(found, f => f.ServerName == "DEV\\SQL02");
    }

    [Fact]
    public void FileWithoutServerNameColumn_IsSkipped()
    {
        Write("sp_Blitz_noserver.csv", "Priority,Finding,Details\n1,X,Y\n");
        Assert.Empty(new AuditOutputScanner(_dir).ScanSpBlitzFiles());
    }
}
