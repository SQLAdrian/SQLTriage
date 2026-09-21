/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Round 2 of lane hygiene-tail (residuals R2/R3, DECISIONS 2026-09-08 06:33; fixed per
    /// Adrian's 06:38 ruling). Pins <see cref="QuickCheckResultStore.GetServersWithRuns"/>
    /// end-to-end against the three "IsBad" shapes <see cref="SQLTriage.Data.Models.LegacyBoolConverter"/>
    /// does and does not accept, and pins that the shape it still (by design) rejects now LOGS
    /// instead of silently dropping the server from the listing — previously a bare
    /// <c>catch { /* skip corrupt file */ }</c> with no trace at all (R2).
    ///
    /// The store's directory is not injectable (fixed at AppContext.BaseDirectory/output/quickcheck)
    /// and other test classes write into the same directory under a parallel run, so every server
    /// name here is GUID-suffixed and assertions use Contains/DoesNotContain, never an exact list —
    /// same discipline as QuickCheckResultStorePathTests.
    /// </summary>
    public class QuickCheckResultStoreListingTests : IDisposable
    {
        private readonly CapturingLogger _logger = new();
        private readonly QuickCheckResultStore _store;
        private readonly List<string> _serverNamesToClean = new();

        public QuickCheckResultStoreListingTests()
        {
            _store = new QuickCheckResultStore(_logger);
        }

        public void Dispose()
        {
            foreach (var server in _serverNamesToClean)
            {
                try
                {
                    foreach (var f in Directory.GetFiles(_store.RootDir, $"{server}-*.json"))
                        File.Delete(f);
                }
                catch { /* best-effort cleanup */ }
            }
        }

        private string Track(string name)
        {
            _serverNamesToClean.Add(name);
            return name;
        }

        private static string NewServerName(string tag) =>
            "R2PROBE-" + tag + "-" + Guid.NewGuid().ToString("N")[..8];

        /// <summary>
        /// Writes a run file directly to disk (not via WriteRun) so "IsBad" can carry a shape
        /// WriteRun itself never produces — exactly how a legacy or external producer's file lands
        /// on disk (the same threat model LegacyBoolConverter's own doc comment names).
        /// </summary>
        private string WriteRawRunFile(string serverName, string isBadJsonFragment)
        {
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            var path = Path.Combine(_store.RootDir, $"{serverName}-{stamp}.json");
            var json = "{\"ServerName\":\"" + serverName + "\",\"WrittenAtUtc\":\"2026-09-08T06:38:00Z\"," +
                       "\"SchemaVersion\":1,\"Results\":[{\"CheckId\":\"SQLT-R2PROBE\",\"CheckName\":\"R2 probe\"," +
                       "\"Category\":\"Configuration\",\"Severity\":\"Low\",\"Passed\":true,\"IsBad\":" +
                       isBadJsonFragment + "}]}";
            File.WriteAllText(path, json);
            return path;
        }

        [Fact]
        public void NumericLegacyIsBad_IsListed()
        {
            // Fact (a): closes R3 end to end — the numeric "IsBad" shape LegacyBoolConverter now
            // accepts no longer drops this server from the listing.
            var server = Track(NewServerName("NUM"));
            WriteRawRunFile(server, "1");

            var names = _store.GetServersWithRuns();

            Assert.Contains(server, names);
        }

        [Fact]
        public void UnparseableIsBad_IsNotListed_AndLogsOneContentFreeWarningNamingTheFile()
        {
            // Fact (b): a shape LegacyBoolConverter still (by design) rejects. R2's fix: the
            // per-file catch now logs the file name and the JsonException instead of swallowing it.
            const string junkValue = "yes";
            var server = Track(NewServerName("BAD"));
            var path = WriteRawRunFile(server, "\"" + junkValue + "\"");
            var fileName = Path.GetFileName(path);

            var names = _store.GetServersWithRuns();

            Assert.DoesNotContain(server, names);

            var matching = _logger.Entries
                .Where(e => e.Level == LogLevel.Warning && e.Message.Contains(fileName, StringComparison.Ordinal))
                .ToList();

            Assert.True(matching.Count == 1,
                $"Expected exactly one Warning naming {fileName}, got {matching.Count}. Every entry captured: " +
                string.Join(" || ", _logger.Entries.Select(e => e.Level + ": " + e.Message)));
            Assert.IsType<JsonException>(matching[0].Exception);

            // A junk run file is UNTRUSTED content, so the warning may carry the file's NAME and
            // the failure SHAPE and nothing else. LegacyBoolConverter used to interpolate the raw
            // string value into its JsonException ("Cannot convert string \"yes\" ..."), and
            // LogWarning(ex, ...) hands that exception straight to an operator-readable log — a
            // run file carrying a 10 MB junk value would have written 10 MB of it into the log.
            var loggedText = matching[0].Message + "\n" + AllMessages(matching[0].Exception);
            Assert.DoesNotContain(junkValue, loggedText, StringComparison.Ordinal);
            Assert.Contains(fileName, loggedText, StringComparison.Ordinal);
        }

        /// <summary>
        /// Every message text a logger emits for <paramref name="ex"/>: its own plus its whole
        /// inner chain — i.e. what <see cref="Exception.ToString"/> writes, minus the stack frames.
        /// System.Text.Json surfaces a converter's JsonException either by mutating it in place or
        /// by wrapping it with the original as InnerException, so both shapes must be inspected.
        /// </summary>
        private static string AllMessages(Exception? ex)
        {
            var sb = new StringBuilder();
            for (var e = ex; e != null; e = e.InnerException) sb.AppendLine(e.Message);
            return sb.ToString();
        }

        [Fact]
        public void BoolLegacyIsBad_IsListed_AndLogsNothing()
        {
            // Fact (c): the shape WriteRun itself has always produced (and the pre-rename shape
            // QuickCheckResultStorePathTests.RunFilesWrittenBeforeTheRename_StillReadTheirFlag
            // already pins for TryParseRunPayload) stays clean through the listing path too — no
            // warning logged for a file that parsed fine.
            var server = Track(NewServerName("BOOL"));
            var path = WriteRawRunFile(server, "true");
            var fileName = Path.GetFileName(path);

            var names = _store.GetServersWithRuns();

            Assert.Contains(server, names);
            Assert.DoesNotContain(_logger.Entries, e => e.Message.Contains(fileName, StringComparison.Ordinal));
        }

        /// <summary>
        /// Minimal capturing logger that keeps level/message/exception as separate fields (unlike
        /// Tests/Portal/E2TestDoubles.cs's CapturingLogger&lt;T&gt;, which concatenates them into one
        /// grep line for a SAS-leak assertion and is Compile-Removed from the community test build —
        /// this class must run on both axes).
        /// </summary>
        private sealed class CapturingLogger : ILogger<QuickCheckResultStore>
        {
            public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (Entries) Entries.Add((logLevel, formatter(state, exception), exception));
            }

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new();
                public void Dispose() { }
            }
        }
    }
}
