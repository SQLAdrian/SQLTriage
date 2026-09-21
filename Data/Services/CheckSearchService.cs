/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services.Licensing;

namespace SQLTriage.Data.Services
{
    public record CheckSearchResult(string CheckId, string? MatchedColumn, string Snippet, double Rank);

    /// <summary>
    /// In-memory FTS5 full-text search over the SQL check corpus (#13 Phase 1).
    /// Index is rebuilt lazily on first search and invalidated when the bundle changes.
    /// </summary>
    public class CheckSearchService : IDisposable
    {
        private readonly CheckRepositoryService _repo;
        private readonly ILogger<CheckSearchService> _logger;
        private SqliteConnection? _connection;
        private bool _indexBuilt;
        private readonly object _lock = new();

        public CheckSearchService(
            CheckRepositoryService repo,
            ILogger<CheckSearchService> logger,
            IBundleAccessor? bundle = null)
        {
            _repo = repo;
            _logger = logger;

            if (bundle != null)
            {
                bundle.BundleStateChanged += (_, _) =>
                {
                    _indexBuilt = false;
                    _logger.LogInformation("CheckSearch FTS5 index invalidated on bundle state change");
                };
            }
        }

        private void BuildIndex()
        {
            lock (_lock)
            {
                if (_indexBuilt) return;

                _connection?.Dispose();
                _connection = new SqliteConnection("Data Source=:memory:");
                _connection.Open();

                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    CREATE VIRTUAL TABLE checks USING fts5(
                        check_id, name, description, category, sql_body, framework_mappings,
                        tokenize='unicode61'
                    );
                ";
                cmd.ExecuteNonQuery();

                var checks = _repo.GetAllChecks();
                var fwMappings = _repo.FrameworkMappings;

                using var txn = _connection.BeginTransaction();
                using var insertCmd = _connection.CreateCommand();
                insertCmd.CommandText = @"
                    INSERT INTO checks(check_id, name, description, category, sql_body, framework_mappings)
                    VALUES (@id, @name, @desc, @cat, @sql, @fw);
                ";
                var pId = insertCmd.Parameters.Add("@id", SqliteType.Text);
                var pName = insertCmd.Parameters.Add("@name", SqliteType.Text);
                var pDesc = insertCmd.Parameters.Add("@desc", SqliteType.Text);
                var pCat = insertCmd.Parameters.Add("@cat", SqliteType.Text);
                var pSql = insertCmd.Parameters.Add("@sql", SqliteType.Text);
                var pFw = insertCmd.Parameters.Add("@fw", SqliteType.Text);

                foreach (var c in checks)
                {
                    pId.Value = c.Id ?? "";
                    pName.Value = c.Name ?? "";
                    pDesc.Value = c.Description ?? "";
                    pCat.Value = c.Category ?? "";
                    pSql.Value = c.SqlQuery ?? "";

                    var fwText = BuildFrameworkText(c.Id!, fwMappings);
                    pFw.Value = fwText;

                    insertCmd.ExecuteNonQuery();
                }

                txn.Commit();
                _indexBuilt = true;
                _logger.LogInformation("CheckSearch FTS5 index built: {N} checks", checks.Count);
            }
        }

        private static string BuildFrameworkText(
            string checkId,
            IReadOnlyDictionary<string, IReadOnlyList<SQLTriage.Data.Parser.FrameworkMapping>> fwMappings)
        {
            if (!fwMappings.TryGetValue(checkId, out var mappings))
                return "";

            return string.Join(" ", mappings.Select(m =>
                string.Join(" ", new[] { m.Framework, m.ControlId, m.ControlName, m.MappingType }
                    .Where(s => !string.IsNullOrWhiteSpace(s)))));
        }

        public List<CheckSearchResult> Search(string query, int topK = 20)
        {
            if (!_indexBuilt) BuildIndex();
            if (_connection == null) return new List<CheckSearchResult>();

            var sanitized = SanitizeFts5Query(query);
            if (string.IsNullOrWhiteSpace(sanitized)) return new List<CheckSearchResult>();

            var results = new List<CheckSearchResult>();
            lock (_lock)
            {
                using var cmd = _connection.CreateCommand();
                // Highlight matched terms with STX/ETX sentinels (char(2)/char(3)), NOT literal
                // '<b>'/'</b>'. The snippet text is server-sourced check content, so it is
                // HTML-encoded in EncodeSnippet() before the sentinels are swapped for the only
                // markup this sink may emit — <b>...</b>. Emitting raw '<b>' here would let a
                // '<img onerror=...>' in a check description reach the CheckValidatorTable
                // MarkupString sink verbatim (fresh-eyes security-9 / lane/xss-markdown-sinks).
                cmd.CommandText = @"
                    SELECT check_id,
                           highlight(checks, 1, char(2), char(3)) AS name_hl,
                           highlight(checks, 2, char(2), char(3)) AS desc_hl,
                           highlight(checks, 3, char(2), char(3)) AS cat_hl,
                           highlight(checks, 4, char(2), char(3)) AS sql_hl,
                           highlight(checks, 5, char(2), char(3)) AS fw_hl,
                           rank
                    FROM checks
                    WHERE checks MATCH @q
                    ORDER BY rank DESC
                    LIMIT @topK;
                ";
                cmd.Parameters.AddWithValue("@q", sanitized);
                cmd.Parameters.AddWithValue("@topK", topK);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var checkId = reader.GetString(0);
                    var nameHl = GetStringOrNull(reader, 1);
                    var descHl = GetStringOrNull(reader, 2);
                    var catHl = GetStringOrNull(reader, 3);
                    var sqlHl = GetStringOrNull(reader, 4);
                    var fwHl = GetStringOrNull(reader, 5);
                    var rank = reader.IsDBNull(6) ? 0.0 : reader.GetDouble(6);

                    var (matchedCol, snippet) = PickSnippet(nameHl, descHl, catHl, sqlHl, fwHl);

                    results.Add(new CheckSearchResult(checkId, matchedCol, snippet, rank));
                }
            }

            return results;
        }

        private static string? GetStringOrNull(SqliteDataReader reader, int ordinal)
            => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

        private static (string? column, string snippet) PickSnippet(
            string? nameHl, string? descHl, string? catHl, string? sqlHl, string? fwHl)
        {
            var candidates = new (string col, string? text)[]
            {
                ("name", nameHl),
                ("description", descHl),
                ("category", catHl),
                ("framework_mappings", fwHl),
                ("sql_body", sqlHl),
            };

            foreach (var (col, text) in candidates)
            {
                // The open sentinel is present only when this column actually matched.
                if (text?.Contains("\u0002") == true)
                    return (col, EncodeSnippet(TruncateSnippet(text)));
            }

            var fallback = nameHl ?? descHl ?? catHl ?? fwHl ?? sqlHl ?? "";
            return (null, EncodeSnippet(TruncateSnippet(fallback)));
        }

        /// <summary>
        /// Neutralise server-sourced snippet text, then re-emit ONLY the highlight markup. The
        /// snippet is HTML-encoded first (so any '&lt;'/'&gt;'/'&amp;' in a check's name, description
        /// or SQL body becomes an entity and cannot inject markup), then the STX/ETX sentinels that
        /// highlight() placed around matched terms are swapped for &lt;b&gt;...&lt;/b&gt;. Those two
        /// control chars pass through <see cref="System.Net.WebUtility.HtmlEncode"/> unchanged, so
        /// the swap is exact. This is the sanitiser allow-list for the CheckValidatorTable
        /// MarkupString sink (fresh-eyes security-9 / lane/xss-markdown-sinks): the search highlight
        /// survives; nothing else HTML can.
        /// </summary>
        private static string EncodeSnippet(string text)
            => System.Net.WebUtility.HtmlEncode(text)
                .Replace("\u0002", "<b>")
                .Replace("\u0003", "</b>");

        private static string TruncateSnippet(string text, int maxChars = 200)
        {
            if (text.Length <= maxChars) return text;
            var trunc = text[..maxChars];
            var lastSpace = trunc.LastIndexOf(' ');
            if (lastSpace > maxChars / 2) trunc = trunc[..lastSpace];
            return trunc + "…";
        }

        internal static string SanitizeFts5Query(string query)
        {
            var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (terms.Length == 0) return string.Empty;

            var escaped = terms.Select(t => $"\"{t.Replace("\"", "\"\"")}\"");
            return string.Join(" OR ", escaped);
        }

        public void Dispose()
        {
            _connection?.Dispose();
            _connection = null;
        }
    }
}
