/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Remediation;
using SQLTriage.Tests.Licensing;
using Xunit;

namespace SQLTriage.Tests;

/// <summary>
/// R2 and R3 (gate residuals, 2026-08-25): offline, CI-resident pins for the two per-instance
/// wirings the remediation-safety lane fixed.
///
/// <para><b>What was missing.</b> The lane fixed two real defects. The multi-instance APPLY lane
/// discarded the script's rollback-script result set, so an apply that changed a client's server
/// wrote no undo file (r1-04). The PREVIEW lane had no BatchErrors field at all, so a run where
/// every batch threw returned Refused=False, zero rows, and a clean-looking export, and the
/// /remediation hardening panel rendered a green tick over it (r1-03 / r2-02). Both fixes were
/// proved live against <c>.\new2022</c>. Neither shipped an OFFLINE test of the wiring: the tests
/// that came with them construct <c>InstanceChangeControlResult</c> and <c>InstanceApplyResult</c>
/// BY HAND and then assert on their own construction. That is the r2-08 test-side shape the whole
/// honesty-hunt wave is about, re-opened inside the lane that was fixing it.</para>
///
/// <para><b>What these do instead.</b> They drive the REAL producer. A <see cref="DbConnection"/>
/// the service cannot tell apart from a live one is handed to it through
/// <c>ConnectionFactoryOverride</c>; the service then reads the REAL shipped .sql, splits its REAL
/// batches, runs its REAL reader loop and its REAL per-batch catch, and builds the result record
/// from the accumulators that loop filled. Nothing in this file constructs a result record. The
/// assertions read what the lane's own code produced.</para>
///
/// <para>Skipped when the shipped .sql is absent (a community test build Content-Removes it), the
/// same convention <c>ServerConfigScriptServiceRollbackTests</c> uses for its script-side tests.</para>
/// </summary>
public sealed class ServerConfigInstanceWiringTests : IDisposable
{
    private readonly string _outDir;

    public ServerConfigInstanceWiringTests()
    {
        _outDir = Path.Combine(Path.GetTempPath(), "sqlt-instance-wiring-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_outDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_outDir)) Directory.Delete(_outDir, recursive: true); }
        catch { /* test cleanup */ }
    }

    private static ServerConfigScriptService NewService()
    {
        // Licensed, because the lanes under test are licence-gated first: the Server Configuration
        // set needs a Full-tier bundle carrying the remediation claim. Same fail-closed gate
        // production uses, satisfied the same way.
        var accessor = new FakeBundleAccessor
        {
            Tier = SQLTriage.Data.Services.Licensing.Tier.Full,
            Features = new SQLTriage.Data.Services.Licensing.BundleFeatures(
                false, true, true, Array.Empty<int>(), Remediation: true, RemediationCreditsPerServer: 10),
        };
        return new ServerConfigScriptService(
            null!,
            NullLogger<ServerConfigScriptService>.Instance,
            new BundleBackedRemediationCapability(accessor),
            accessor);
    }

    // ── R3: the preview lane's batch errors reach the record it returns ──────────────────────

    [Fact]
    public async Task APreviewWhereEveryBatchThrows_ProducesARecordThatSaysSo()
    {
        var svc = NewService();
        if (!svc.ScriptExists) return; // community test build: the .sql is Content-Removed

        // Every batch raises a real DbException. Nothing else about the run is faked: the script,
        // the batch split, the mode rewrite and the loop are all the shipped ones.
        svc.ConnectionFactoryOverride = _ => new FakeConnection(FakeBehaviour.ThrowOnEveryBatch);

        var result = await svc.RunPreviewForInstanceAsync("SQL01", "Server=fake", _outDir);

        // The exact live-proved shape, produced by the real lane rather than typed into a
        // constructor: not refused, no refusal reason, zero rows, and NOT a success.
        Assert.False(result.Refused);
        Assert.Null(result.RefusalReason);
        Assert.Empty(result.Rows);
        Assert.NotNull(result.BatchErrors);
        Assert.NotEmpty(result.BatchErrors!);
        Assert.False(result.Succeeded);

        // The error text came out of the real catch, so it carries the batch number and error code.
        Assert.All(result.BatchErrors!, e => Assert.Contains("ERROR", e, StringComparison.Ordinal));
        Assert.Contains(result.BatchErrors!, e => e.Contains("[batch", StringComparison.Ordinal));

        // And the export the real lane wrote does not read as a clean server.
        Assert.NotNull(result.ExportedPath);
        var export = File.ReadAllText(result.ExportedPath!);
        Assert.DoesNotContain("No change-control rows were captured.", export, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APreviewThatReadsTheServerCleanly_ProducesASucceededRecord()
    {
        var svc = NewService();
        if (!svc.ScriptExists) return;

        // The control arm. Same real path, one #ChangeControlReport-shaped result set, no throws.
        svc.ConnectionFactoryOverride = _ => new FakeConnection(FakeBehaviour.ReturnOneChangeControlRow);

        var result = await svc.RunPreviewForInstanceAsync("SQL01", "Server=fake", _outDir);

        Assert.False(result.Refused);
        Assert.True(result.BatchErrors is null || result.BatchErrors.Count == 0);
        Assert.NotEmpty(result.Rows);
        Assert.True(result.Succeeded);

        // The row was MAPPED by the real reader loop, not handed in.
        var row = result.Rows[0];
        Assert.Equal("PLANNED", row.Mode);
        Assert.Equal("Agent Alerts", row.Section);
        Assert.Equal("Operator", row.Setting);
    }

    [Fact]
    public async Task APreviewThatCapturedRowsAndAlsoFailed_IsNotASuccess()
    {
        var svc = NewService();
        if (!svc.ScriptExists) return;

        // Partial capture, produced by the real loop: the first batch returns a row, the rest throw.
        svc.ConnectionFactoryOverride = _ => new FakeConnection(FakeBehaviour.OneRowThenThrow);

        var result = await svc.RunPreviewForInstanceAsync("SQL01", "Server=fake", _outDir);

        Assert.NotEmpty(result.Rows);
        Assert.NotEmpty(result.BatchErrors!);
        Assert.False(result.Succeeded);
        Assert.Contains("did not finish",
            ServerConfigScriptService.DescribeUnreadablePreview(result.Rows, result.BatchErrors, "SQL01"),
            StringComparison.Ordinal);
    }

    // ── R2: the apply lane's rollback-script capture reaches the file it names ───────────────

    [Fact]
    public async Task AnApplyThatReturnsARollbackScript_WritesItAndNamesIt()
    {
        var svc = NewService();
        if (!svc.ScriptExists) return;

        // The (ServerName, RollbackScript) result set the shipped script emits on an apply that
        // changed something. r1-04: the multi-instance lane used to DISCARD it, so a real client
        // apply produced no undo file at all.
        svc.ConnectionFactoryOverride = _ => new FakeConnection(FakeBehaviour.ReturnRollbackScript);

        var result = await svc.RunApplyForInstanceAsync("SQL01", "Server=fake", _outDir);

        Assert.False(result.Refused);
        Assert.NotNull(result.RollbackScriptPath);
        Assert.True(File.Exists(result.RollbackScriptPath!));

        // The file holds the script the READER captured, not something recomposed afterwards.
        var written = File.ReadAllText(result.RollbackScriptPath!);
        Assert.Contains(FakeConnection.RollbackScriptText, written, StringComparison.Ordinal);

        // And the operator sentence names the file rather than saying none was produced.
        Assert.Contains("Rollback script saved", result.RollbackScriptNote, StringComparison.Ordinal);
        Assert.Contains(Path.GetFileName(result.RollbackScriptPath!), result.RollbackScriptNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnApplyThatReturnsNoRollbackScript_SaysThatPlainly()
    {
        var svc = NewService();
        if (!svc.ScriptExists) return;

        svc.ConnectionFactoryOverride = _ => new FakeConnection(FakeBehaviour.ReturnOneChangeControlRow);

        var result = await svc.RunApplyForInstanceAsync("SQL01", "Server=fake", _outDir);

        Assert.Null(result.RollbackScriptPath);
        Assert.Contains("No rollback script was returned", result.RollbackScriptNote, StringComparison.Ordinal);
        // The absent undo file does not silently downgrade the run: Succeeded means "did this
        // read/change the server", and the note is a separate fact.
        Assert.True(result.Succeeded);
    }

    // ── The fake connection: a DbConnection, not a mock of the service ───────────────────────

    private enum FakeBehaviour
    {
        ThrowOnEveryBatch,
        ReturnOneChangeControlRow,
        OneRowThenThrow,
        ReturnRollbackScript,
    }

    private sealed class FakeDbException : DbException
    {
        public FakeDbException(string message, int code) : base(message) => HResult = code;
        public override int ErrorCode => 208;
    }

    private sealed class FakeConnection : DbConnection
    {
        public const string RollbackScriptText = "EXEC sp_configure 'max degree of parallelism', 0; RECONFIGURE;";

        private readonly FakeBehaviour _behaviour;
        private ConnectionState _state = ConnectionState.Closed;
        internal int BatchesSeen;

        public FakeConnection(FakeBehaviour behaviour) => _behaviour = behaviour;

        public override string ConnectionString { get; set; } = "Server=fake";
        public override string Database => "master";
        public override string DataSource => "SQL01";
        public override string ServerVersion => "16.0.0";
        public override ConnectionState State => _state;

        public override void ChangeDatabase(string databaseName) { }
        public override void Close() => _state = ConnectionState.Closed;
        public override void Open() => _state = ConnectionState.Open;
        public override Task OpenAsync(CancellationToken ct) { Open(); return Task.CompletedTask; }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            throw new NotSupportedException();

        protected override DbCommand CreateDbCommand() => new FakeCommand(this, _behaviour);

        private sealed class FakeCommand : DbCommand
        {
            private readonly FakeConnection _conn;
            private readonly FakeBehaviour _behaviour;

            public FakeCommand(FakeConnection conn, FakeBehaviour behaviour)
            {
                _conn = conn; _behaviour = behaviour; DbConnection = conn;
            }

            public override string CommandText { get; set; } = string.Empty;
            public override int CommandTimeout { get; set; }
            public override CommandType CommandType { get; set; }
            public override bool DesignTimeVisible { get; set; }
            public override UpdateRowSource UpdatedRowSource { get; set; }
            protected override DbConnection? DbConnection { get; set; }
            protected override DbParameterCollection DbParameterCollection { get; } = new FakeParameters();
            protected override DbTransaction? DbTransaction { get; set; }

            public override void Cancel() { }
            public override int ExecuteNonQuery() => 0;
            public override object? ExecuteScalar() => null;
            public override void Prepare() { }
            protected override DbParameter CreateDbParameter() => throw new NotSupportedException();

            protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
            {
                var batch = ++_conn.BatchesSeen;
                switch (_behaviour)
                {
                    case FakeBehaviour.ThrowOnEveryBatch:
                        throw new FakeDbException("Invalid object name 'x'.", 208);
                    case FakeBehaviour.OneRowThenThrow when batch > 1:
                        throw new FakeDbException("permission denied.", 229);
                    case FakeBehaviour.OneRowThenThrow:
                    case FakeBehaviour.ReturnOneChangeControlRow when batch == 1:
                        return FakeReader.ChangeControlReport();
                    case FakeBehaviour.ReturnRollbackScript when batch == 1:
                        return FakeReader.RollbackScript();
                    default:
                        return FakeReader.Empty();
                }
            }
        }

        private sealed class FakeParameters : DbParameterCollection
        {
            private readonly List<object> _items = new();
            public override int Count => _items.Count;
            public override object SyncRoot => _items;
            public override int Add(object value) { _items.Add(value); return _items.Count - 1; }
            public override void AddRange(Array values) { foreach (var v in values) _items.Add(v!); }
            public override void Clear() => _items.Clear();
            public override bool Contains(object value) => _items.Contains(value);
            public override bool Contains(string value) => false;
            public override void CopyTo(Array array, int index) => _items.ToArray().CopyTo(array, index);
            public override System.Collections.IEnumerator GetEnumerator() => _items.GetEnumerator();
            public override int IndexOf(object value) => _items.IndexOf(value);
            public override int IndexOf(string parameterName) => -1;
            public override void Insert(int index, object value) => _items.Insert(index, value);
            public override void Remove(object value) => _items.Remove(value);
            public override void RemoveAt(int index) => _items.RemoveAt(index);
            public override void RemoveAt(string parameterName) { }
            protected override DbParameter GetParameter(int index) => throw new NotSupportedException();
            protected override DbParameter GetParameter(string parameterName) => throw new NotSupportedException();
            protected override void SetParameter(int index, DbParameter value) { }
            protected override void SetParameter(string parameterName, DbParameter value) { }
        }

        /// <summary>
        /// A result set with the EXACT column names the service's shape detectors look for. The
        /// detectors are the shipped ones; nothing here reaches around them.
        /// </summary>
        private sealed class FakeReader : DbDataReader
        {
            private readonly string[] _names;
            private readonly object?[][] _rows;
            private int _index = -1;

            private FakeReader(string[] names, object?[][] rows) { _names = names; _rows = rows; }

            public static FakeReader Empty() => new(Array.Empty<string>(), Array.Empty<object?[]>());

            public static FakeReader ChangeControlReport() => new(
                new[] { "ID", "Captured", "Mode", "Section", "Setting", "CurrentValue", "TargetValue", "Detail" },
                new[]
                {
                    new object?[] { 1, new DateTime(2026, 8, 25, 9, 0, 0), "PLANNED", "Agent Alerts", "Operator",
                                    "(none)", "SQLDBA", "would create the operator" },
                });

            public static FakeReader RollbackScript() => new(
                new[] { "ServerName", "RollbackScript" },
                new[] { new object?[] { "SQL01", RollbackScriptText } });

            public override int FieldCount => _names.Length;
            public override string GetName(int ordinal) => _names[ordinal];
            public override bool HasRows => _rows.Length > 0;
            public override bool IsClosed => false;
            public override int Depth => 0;
            public override int RecordsAffected => 0;

            public override bool Read() => ++_index < _rows.Length;
            public override Task<bool> ReadAsync(CancellationToken ct) => Task.FromResult(Read());
            public override bool NextResult() => false;
            public override Task<bool> NextResultAsync(CancellationToken ct) => Task.FromResult(false);

            public override object GetValue(int ordinal) => _rows[_index][ordinal]!;
            public override bool IsDBNull(int ordinal) => _rows[_index][ordinal] is null;
            public override string GetString(int ordinal) => (string)_rows[_index][ordinal]!;
            public override int GetInt32(int ordinal) => (int)_rows[_index][ordinal]!;
            public override DateTime GetDateTime(int ordinal) => (DateTime)_rows[_index][ordinal]!;

            public override int GetOrdinal(string name) => Array.FindIndex(_names,
                n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            public override string GetDataTypeName(int ordinal) => "nvarchar";
            public override Type GetFieldType(int ordinal) => typeof(string);
            public override int GetValues(object[] values) => 0;
            public override bool GetBoolean(int ordinal) => throw new NotSupportedException();
            public override byte GetByte(int ordinal) => throw new NotSupportedException();
            public override long GetBytes(int o, long fo, byte[]? b, int bo, int len) => throw new NotSupportedException();
            public override char GetChar(int ordinal) => throw new NotSupportedException();
            public override long GetChars(int o, long fo, char[]? b, int bo, int len) => throw new NotSupportedException();
            public override decimal GetDecimal(int ordinal) => throw new NotSupportedException();
            public override double GetDouble(int ordinal) => throw new NotSupportedException();
            public override float GetFloat(int ordinal) => throw new NotSupportedException();
            public override Guid GetGuid(int ordinal) => throw new NotSupportedException();
            public override short GetInt16(int ordinal) => throw new NotSupportedException();
            public override long GetInt64(int ordinal) => throw new NotSupportedException();
            public override object this[int ordinal] => GetValue(ordinal);
            public override object this[string name] => GetValue(GetOrdinal(name));
            public override System.Collections.IEnumerator GetEnumerator() => _rows.GetEnumerator();
        }
    }
}
