/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SQLTriage.Cli;
using SQLTriage.Data.Models;
using Xunit;

namespace SQLTriage.Tests.Cli
{
    /// <summary>#33 headless CLI — the generic CSV writer that fills the "there is no existing
    /// generic CheckResult CSV export" gap for --format csv.</summary>
    public class CsvResultWriterTests
    {
        private static string TempDir() =>
            Path.Combine(Path.GetTempPath(), "sqlt-csv-test-" + Guid.NewGuid().ToString("N"));

        [Fact]
        public void Write_ProducesDeterministicTimestampedFileName()
        {
            var dir = TempDir();
            var stamp = new DateTime(2026, 7, 13, 9, 30, 15, DateTimeKind.Utc);
            try
            {
                var path = CsvResultWriter.Write(new List<CheckResult>(), dir, stamp);

                Assert.Equal(Path.Combine(dir, "audit_20260713_093015Z.csv"), path);
                Assert.True(File.Exists(path));
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        [Fact]
        public void Write_HeaderRow_MatchesExpectedColumns()
        {
            var dir = TempDir();
            try
            {
                var path = CsvResultWriter.Write(new List<CheckResult>(), dir, DateTime.UtcNow);
                var lines = File.ReadAllLines(path);

                Assert.Equal(
                    "Server,CheckId,CheckName,Category,Severity,Status,Passed,IsAccepted,IsCorrupted,ErrorMessage,Message,ActualValue,ExpectedValue,DurationMs,ExecutedAtUtc",
                    lines[0]);
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        [Fact]
        public void Write_EmitsUtf8Bom()
        {
            // Excel decodes a BOM-less UTF-8 CSV as ANSI on an English-Windows default, mangling
            // the em-dash the corpus SKIP messages carry.
            var dir = TempDir();
            try
            {
                var path = CsvResultWriter.Write(new List<CheckResult>(), dir, DateTime.UtcNow);
                var head = new byte[3];
                using (var fs = File.OpenRead(path)) fs.ReadExactly(head);

                Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, head);
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        [Fact]
        public void Write_VerdictSkipRow_StatusIsSkip_EvenThoughPassedIsTrue()
        {
            // APP-12: CheckExecutionService sets Passed=true for a SKIP, so a not-applicable
            // Critical check exported as Passed=True with nothing to say otherwise. Passed keeps
            // its raw value; Status carries the real verdict.
            var dir = TempDir();
            try
            {
                var results = new List<CheckResult>
                {
                    new()
                    {
                        InstanceName = ".\\new2022", CheckId = "SQLT-CORE-00580",
                        CheckName = "Implement Appropriate AG Synchronization Mode",
                        Category = "Reliability", Severity = "High", Passed = true,
                        Verdict = "SKIP", Message = "No AGs configured.",
                    },
                };

                var path = CsvResultWriter.Write(results, dir, DateTime.UtcNow);
                var fields = File.ReadAllLines(path)[1].Split(',');

                Assert.Equal("SKIP", fields[5]);   // Status
                Assert.Equal("True", fields[6]);   // Passed, deliberately unchanged
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        [Fact]
        public void Write_VerdictInfoRow_StatusIsInfo()
        {
            var dir = TempDir();
            try
            {
                var results = new List<CheckResult>
                {
                    new()
                    {
                        InstanceName = ".\\new2022", CheckId = "SQLT-CUSTOM-MAXDOP",
                        CheckName = "MAXDOP recommendation", Category = "Performance",
                        Severity = "Medium", Passed = true, Verdict = "INFO",
                        Message = "Recommended MAXDOP is 8.",
                    },
                };

                var path = CsvResultWriter.Write(results, dir, DateTime.UtcNow);
                var fields = File.ReadAllLines(path)[1].Split(',');

                Assert.Equal("INFO", fields[5]);
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        [Fact]
        public void Write_PlainPassAndFail_StatusDiscriminates()
        {
            // The CONTROL for the two tests above: the same Status column must produce PASS and
            // FAIL for ordinary rows, so "SKIP"/"INFO" above are real classifications rather than
            // a column that is constant.
            var dir = TempDir();
            try
            {
                var results = new List<CheckResult>
                {
                    new() { InstanceName = "S", CheckId = "P", Category = "Security", Severity = "High", Passed = true },
                    new() { InstanceName = "S", CheckId = "F", Category = "Security", Severity = "High", Passed = false },
                };

                var path = CsvResultWriter.Write(results, dir, DateTime.UtcNow);
                var lines = File.ReadAllLines(path);

                Assert.Equal("PASS", lines[1].Split(',')[5]);
                Assert.Equal("FAIL", lines[2].Split(',')[5]);
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        [Fact]
        public void Write_UnassessedServers_AreEmittedAsDataRowsNotACommentLine()
        {
            // Was a "# Servers not assessed: ..." line ABOVE the header. Measured 2026-07-19,
            // Python csv.DictReader read that line AS the header and reported a single field,
            // with Server resolving to None on every data row.
            var dir = TempDir();
            try
            {
                var path = CsvResultWriter.Write(
                    new List<CheckResult>(), dir, DateTime.UtcNow,
                    new[] { "SQL02", "SQL03" });
                var lines = File.ReadAllLines(path);

                Assert.StartsWith("Server,CheckId", lines[0]);       // header on line 1, always
                Assert.DoesNotContain(lines, l => l.StartsWith("#"));

                Assert.Equal(3, lines.Length);                        // header + one row each
                Assert.Equal("SQL02", lines[1].Split(',')[0]);
                Assert.Equal("NOT_ASSESSED", lines[1].Split(',')[5]); // Status
                Assert.Equal("SQL03", lines[2].Split(',')[0]);
                Assert.Equal("NOT_ASSESSED", lines[2].Split(',')[5]);
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        [Fact]
        public void Write_NoUnassessedServers_EmitsNoNotAssessedRows()
        {
            // Control for the test above: NOT_ASSESSED must be produced by the unassessed list
            // rather than being a constant the writer always emits.
            var dir = TempDir();
            try
            {
                var path = CsvResultWriter.Write(new List<CheckResult>(), dir, DateTime.UtcNow);
                var lines = File.ReadAllLines(path);

                Assert.StartsWith("Server,CheckId", lines[0]);
                Assert.Single(lines);
                Assert.DoesNotContain(lines, l => l.Contains("NOT_ASSESSED"));
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        [Fact]
        public void Write_UnassessedRows_AreFullWidth_AndParseWithTheSameColumnsAsRealRows()
        {
            // The actual regression: every line must carry the header's field count, so a reader
            // that maps header->row by position (Import-Csv, csv.DictReader, Excel) sees one
            // rectangular table. A short synthetic row silently shifts every later column.
            var dir = TempDir();
            try
            {
                var results = new List<CheckResult>
                {
                    new()
                    {
                        InstanceName = "SQL01", CheckId = "SQLT-TEST-003", CheckName = "Real Check",
                        Category = "Security", Severity = "High", Passed = false, Message = "detail",
                    },
                };

                var path = CsvResultWriter.Write(results, dir, DateTime.UtcNow, new[] { "SQL02" });
                var lines = File.ReadAllLines(path);
                var expected = lines[0].Split(',').Length;

                Assert.Equal(15, expected);
                Assert.All(lines, l => Assert.Equal(expected, SplitCsv(l).Count));

                // ...and the unassessed row must be discoverable by a Status filter, which is the
                // whole point of moving it below the header.
                var notAssessed = lines.Where(l => SplitCsv(l)[5] == "NOT_ASSESSED").ToList();
                Assert.Single(notAssessed);
                Assert.Equal("SQL02", SplitCsv(notAssessed[0])[0]);
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        [Fact]
        public void Write_UnassessedServerNameContainingAComma_StaysOneField()
        {
            // The old comment line did not quote the names it interpolated, so "SRV,1" widened it.
            var dir = TempDir();
            try
            {
                var path = CsvResultWriter.Write(
                    new List<CheckResult>(), dir, DateTime.UtcNow, new[] { "SRV,1" });
                var lines = File.ReadAllLines(path);

                Assert.Equal(15, SplitCsv(lines[1]).Count);
                Assert.Equal("SRV,1", SplitCsv(lines[1])[0]);
                Assert.Contains("\"SRV,1\"", lines[1]);
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        /// <summary>Minimal RFC-4180 field splitter — honours quoted fields containing commas, so
        /// the column-count assertions above measure real fields rather than raw commas.</summary>
        private static List<string> SplitCsv(string line)
        {
            var fields = new List<string>();
            var sb = new System.Text.StringBuilder();
            var inQuotes = false;
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (inQuotes)
                {
                    if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else if (c == '"') inQuotes = false;
                    else sb.Append(c);
                }
                else if (c == '"') inQuotes = true;
                else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
            fields.Add(sb.ToString());
            return fields;
        }

        [Fact]
        public void Write_OneRowPerResult_FieldsRoundTrip()
        {
            var dir = TempDir();
            try
            {
                var results = new List<CheckResult>
                {
                    new()
                    {
                        InstanceName = ".\\old2017", CheckId = "SQLT-TEST-001", CheckName = "Test Check",
                        Category = "Security", Severity = "Critical", Passed = false, IsAccepted = false,
                        IsCorrupted = false, Message = "finding detail", ActualValue = 3, ExpectedValue = 0,
                        DurationMs = 42,
                    },
                };

                var path = CsvResultWriter.Write(results, dir, DateTime.UtcNow);
                var lines = File.ReadAllLines(path);

                Assert.Equal(2, lines.Length);
                Assert.Contains(".\\old2017", lines[1]);
                Assert.Contains("SQLT-TEST-001", lines[1]);
                Assert.Contains("False", lines[1]); // Passed
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        [Fact]
        public void Write_FieldsContainingCommaOrQuote_AreQuotedAndEscaped()
        {
            var dir = TempDir();
            try
            {
                var results = new List<CheckResult>
                {
                    new()
                    {
                        InstanceName = "SRV,1", CheckId = "SQLT-TEST-002", CheckName = "Has \"quotes\", and a comma",
                        Category = "Perf", Severity = "Warning", Passed = true, Message = "",
                    },
                };

                var path = CsvResultWriter.Write(results, dir, DateTime.UtcNow);
                var lines = File.ReadAllLines(path);

                Assert.Contains("\"SRV,1\"", lines[1]);
                Assert.Contains("\"Has \"\"quotes\"\", and a comma\"", lines[1]);
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        [Fact]
        public void Write_CreatesOutDirIfMissing()
        {
            var dir = TempDir();
            Assert.False(Directory.Exists(dir));
            try
            {
                CsvResultWriter.Write(new List<CheckResult>(), dir, DateTime.UtcNow);
                Assert.True(Directory.Exists(dir));
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }
    }
}
