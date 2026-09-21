/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sql;
using Microsoft.Win32;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// Discovers SQL Server instances installed AND running on the LOCAL
    /// machine. Used by Pages/Servers.razor to pre-populate the
    /// "Add Server" dialog when no servers are configured yet.
    ///
    /// Two layered sources, results union-deduped:
    /// 1. Registry: `HKLM\SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL`
    ///    — authoritative "what's installed". Each value name is a SQL
    ///    instance ("MSSQLSERVER" for default; named otherwise). Sub-millisecond.
    /// 2. <see cref="SqlDataSourceEnumerator"/> — best-effort UDP-1434
    ///    broadcast discovery (depends on SQL Browser service). Filtered
    ///    to local machine only. **30–45 s blocking call** with no internal
    ///    timeout; only attempted in the async overload behind a timeout race.
    ///
    /// Returns server-name strings in app convention:
    ///   default instance → "."
    ///   named instance   → ".\InstanceName"
    /// Errors are swallowed; method returns the best-effort list (possibly
    /// empty). This is UX defaulting, not a security check.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class LocalSqlInstanceDiscoverer
    {
        /// <summary>Default cap on the SqlDataSourceEnumerator broadcast.</summary>
        public static readonly TimeSpan DefaultEnumeratorTimeout = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Fast, synchronous, registry-only discovery. Sub-millisecond.
        /// Skips the enumerator entirely — use <see cref="DiscoverLocalAsync"/>
        /// when you can await a 2 s broadcast for network-Browser-published instances.
        /// </summary>
        public static IReadOnlyList<string> DiscoverLocal()
        {
            var found = new List<string>(4);
            TryAddFromRegistry(found);
            return Dedupe(found);
        }

        /// <summary>
        /// Registry instantly, then SqlDataSourceEnumerator on a background task
        /// capped by <paramref name="enumeratorTimeout"/>. On timeout, the enumerator
        /// task is abandoned (it eventually completes and warms its internal cache for
        /// any future call) and we return registry-only results.
        /// </summary>
        public static async Task<IReadOnlyList<string>> DiscoverLocalAsync(
            TimeSpan? enumeratorTimeout = null,
            CancellationToken cancellationToken = default)
        {
            var timeout = enumeratorTimeout ?? DefaultEnumeratorTimeout;
            var found = new List<string>(4);

            // Registry — fast, on the calling thread.
            TryAddFromRegistry(found);

            // Enumerator — slow, race against timeout. We never await the task itself
            // because GetDataSources() doesn't respect CancellationToken; the orphaned
            // task continues in the background and is reclaimed by the runtime.
            var enumeratorTask = Task.Run(() =>
            {
                var sink = new List<string>(4);
                TryAddFromEnumerator(sink);
                return (IReadOnlyList<string>)sink;
            }, cancellationToken);

            var winner = await Task.WhenAny(enumeratorTask, Task.Delay(timeout, cancellationToken))
                                   .ConfigureAwait(false);

            if (winner == enumeratorTask)
            {
                try
                {
                    var extra = await enumeratorTask.ConfigureAwait(false);
                    found.AddRange(extra);
                }
                catch
                {
                    // swallow — enumerator threw inside the background task.
                }
            }

            return Dedupe(found);
        }

        private static IReadOnlyList<string> Dedupe(List<string> source) =>
            source
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        private static void TryAddFromRegistry(List<string> sink)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL");
                if (key is null) return;
                foreach (var instance in key.GetValueNames())
                {
                    if (string.IsNullOrWhiteSpace(instance)) continue;
                    sink.Add(instance.Equals("MSSQLSERVER", StringComparison.OrdinalIgnoreCase)
                        ? "."
                        : $".\\{instance}");
                }
            }
            catch { /* swallow — best-effort */ }
        }

        private static void TryAddFromEnumerator(List<string> sink)
        {
            try
            {
                var table = SqlDataSourceEnumerator.Instance.GetDataSources();
                var machine = Environment.MachineName;
                foreach (System.Data.DataRow row in table.Rows)
                {
                    var server = row["ServerName"]?.ToString();
                    var instance = row["InstanceName"]?.ToString();
                    if (string.IsNullOrWhiteSpace(server)) continue;

                    // local-only filter — must match this machine
                    if (!server.Equals(machine, StringComparison.OrdinalIgnoreCase)) continue;

                    sink.Add(string.IsNullOrWhiteSpace(instance) ||
                             instance.Equals("MSSQLSERVER", StringComparison.OrdinalIgnoreCase)
                        ? "."
                        : $".\\{instance}");
                }
            }
            catch { /* swallow — Browser service may be off */ }
        }
    }
}
