/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Concurrent;

namespace SQLTriage.Data
{
    /// <summary>
    /// Replaces real server/instance names with stable opaque aliases (SRV-001, SRV-002 …) in log output.
    /// Aliases are consistent within a process lifetime but not persisted across restarts.
    /// <para>
    /// Call <see cref="S"/> at log call sites instead of emitting the raw server name.
    /// Enable via <see cref="Enabled"/> (default <c>false</c> — off unless the user opts in).
    /// </para>
    /// </summary>
    public static class LogAnon
    {
        private static readonly ConcurrentDictionary<string, string> _map =
            new(StringComparer.OrdinalIgnoreCase);

        private static int _counter;

        /// <summary>
        /// When <c>true</c>, <see cref="S"/> returns an alias instead of the real name.
        /// Toggle from <c>UserSettingsService</c> or Settings UI.
        /// </summary>
        public static bool Enabled { get; set; } = false;

        /// <summary>
        /// Returns the stable alias for <paramref name="serverName"/> (e.g. <c>SRV-001</c>)
        /// when <see cref="Enabled"/> is <c>true</c>; otherwise returns the name unchanged.
        /// </summary>
        public static string S(string? serverName)
        {
            if (string.IsNullOrEmpty(serverName)) return serverName ?? "";
            if (!Enabled) return serverName;

            return _map.GetOrAdd(serverName, _ =>
            {
                var n = System.Threading.Interlocked.Increment(ref _counter);
                return $"SRV-{n:D3}";
            });
        }

        /// <summary>How many distinct server names have been aliased this session.</summary>
        public static int Count => _map.Count;

        /// <summary>Clears all aliases (e.g. on settings reset). Aliases will be re-assigned from SRV-001.</summary>
        public static void Reset()
        {
            _map.Clear();
            System.Threading.Interlocked.Exchange(ref _counter, 0);
        }
    }
}
