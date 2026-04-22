/* In the name of God, the Merciful, the Compassionate */

using System.Security.Cryptography;
using System.Text;

namespace SQLTriage.Data.Scheduling
{
    /// <summary>
    /// Computes stable SHA256 hash of SQL text for grouping identical queries.
    /// Normalizes whitespace and removes inline comments to improve hash stability.
    /// </summary>
    public static class SqlHasher
    {
        /// <summary>
        /// Computes SHA256 hash of the SQL string, returns as hex string.
        /// </summary>
        public static string ComputeHash(string sql)
        {
            if (string.IsNullOrWhiteSpace(sql))
                return "empty";

            // Normalize: trim, collapse whitespace, remove inline -- comments
            var normalized = NormalizeSql(sql);
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
            return Convert.ToHexString(bytes).ToLowerInvariant(); // hex string like "a1b2c3..."
        }

        /// <summary>
        /// Minimal normalization to improve hash stability across minor formatting changes.
        /// </summary>
        private static string NormalizeSql(string sql)
        {
            var lines = sql.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var cleaned = new List<string>();
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                // Strip inline comments
                var commentIndex = trimmed.IndexOf("--");
                if (commentIndex >= 0)
                    trimmed = trimmed[..commentIndex].Trim();
                if (!string.IsNullOrWhiteSpace(trimmed))
                    cleaned.Add(trimmed);
            }
            // Collapse multiple spaces to single space, but preserve case
            var collapsed = string.Join(" ", cleaned.SelectMany(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
            return collapsed.ToUpperInvariant(); // case-insensitive grouping
        }
    }
}
