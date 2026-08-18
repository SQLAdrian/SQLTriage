/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Services.Licensing;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// Runtime SQL loader. Loads query metadata from Config/queries.json (via the active bundle)
    /// and SQL text from Data/Sql/*.sql files on disk. Bundle-state changes invalidate the metadata
    /// cache; SQL files on disk are always re-read on demand.
    /// </summary>
    public interface ISqlQueryRepository
    {
        SqlQueryDefinition? Get(string id);
        IReadOnlyDictionary<string, SqlQueryDefinition> GetAll();
        IReadOnlyList<SqlQueryDefinition> GetByTag(string tag);
        IReadOnlyList<SqlQueryDefinition> GetQuickChecks();
        Task ReloadAsync();
    }

    /// <summary>
    /// Metadata for a single query loaded from queries.json.
    /// </summary>
    public sealed class SqlQueryDefinition
    {
        public required string Id { get; init; }
        public required string Sql { get; init; }
        public required string FilePath { get; init; }
        public string Description { get; init; } = "";
        public string Category { get; init; } = "";
        public string Severity { get; init; } = "MEDIUM";
        public string Status { get; init; } = "working";
        public bool Quick { get; init; } = false;
        public IReadOnlyList<string> Audience { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Controls { get; init; } = Array.Empty<string>();
        public int TimeoutSec { get; init; } = 30;
    }

    public sealed class SqlQueryRepository : ISqlQueryRepository
    {
        private readonly ILogger<SqlQueryRepository> _logger;
        private readonly IBundleAccessor _bundle;
        private readonly string _sqlDirectory;

        // Metadata cache — invalidated by BundleStateChanged
        private readonly object _lock = new();
        private Dictionary<string, QueryMetadata>? _metadataCache;
        private bool _metadataLoaded;

        // Query + tag indexes (rebuilt on ReloadAsync)
        private readonly ConcurrentDictionary<string, SqlQueryDefinition> _queries = new();
        private readonly ConcurrentDictionary<string, List<string>> _tagIndex = new();

        public SqlQueryRepository(
            ILogger<SqlQueryRepository> logger,
            IBundleAccessor bundle)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _bundle = bundle ?? throw new ArgumentNullException(nameof(bundle));

            _sqlDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "Sql");

            // Invalidate metadata cache whenever the bundle state changes.
            _bundle.BundleStateChanged += (_, _) =>
            {
                lock (_lock) { _metadataLoaded = false; _metadataCache = null; }
                _ = Task.Run(async () =>
                {
                    try { await ReloadAsync().ConfigureAwait(false); }
                    catch (Exception ex) { _logger.LogError(ex, "Background reload after BundleStateChanged failed"); }
                });
            };

            // Initial load
            _ = Task.Run(async () =>
            {
                try { await LoadAsync().ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogError(ex, "Initial SQL query repository load failed"); }
            });
        }

        public SqlQueryDefinition? Get(string id)
        {
            return _queries.TryGetValue(id, out var query) ? query : null;
        }

        public IReadOnlyDictionary<string, SqlQueryDefinition> GetAll()
        {
            return _queries.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }

        public IReadOnlyList<SqlQueryDefinition> GetByTag(string tag)
        {
            if (_tagIndex.TryGetValue(tag, out var ids))
            {
                return ids.Select(id => _queries[id]).Where(q => q != null).ToList();
            }
            return Array.Empty<SqlQueryDefinition>();
        }

        public IReadOnlyList<SqlQueryDefinition> GetQuickChecks()
        {
            return _queries.Values.Where(q => q.Quick).ToList();
        }

        public async Task ReloadAsync()
        {
            await LoadAsync().ConfigureAwait(false);
            _logger.LogInformation("SQL query repository reloaded");
        }

        private async Task LoadAsync()
        {
            try
            {
                var metadata = GetMetadata();
                await LoadSqlFilesAsync(metadata).ConfigureAwait(false);
                BuildTagIndex();
                _logger.LogInformation("Loaded {Count} SQL queries from {SqlDir}",
                    _queries.Count, _sqlDirectory);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load SQL query repository");
            }
        }

        /// <summary>
        /// Returns the lazily-parsed query metadata from the bundle.
        /// Thread-safe; invalidated by BundleStateChanged.
        /// Returns empty dict when queries.json is absent from the current bundle.
        /// </summary>
        private Dictionary<string, QueryMetadata> GetMetadata()
        {
            lock (_lock)
            {
                if (_metadataLoaded)
                    return _metadataCache!;

                _metadataLoaded = true;
                var text = _bundle.GetText("Config/queries.json");
                if (text is null)
                {
                    _logger.LogWarning(
                        "queries.json not in current bundle (tier={Tier}); SqlQueryRepository returning empty metadata.",
                        _bundle.Tier);
                    _metadataCache = new Dictionary<string, QueryMetadata>(StringComparer.OrdinalIgnoreCase);
                    return _metadataCache;
                }

                try
                {
                    var config = JsonSerializer.Deserialize<QueriesConfig>(text,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    _metadataCache = config?.Queries
                        ?? new Dictionary<string, QueryMetadata>(StringComparer.OrdinalIgnoreCase);
                    _logger.LogInformation(
                        "SqlQueryRepository loaded {Count} metadata entries from bundle (tier={Tier})",
                        _metadataCache.Count, _bundle.Tier);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to parse queries.json from bundle; using empty metadata.");
                    _metadataCache = new Dictionary<string, QueryMetadata>(StringComparer.OrdinalIgnoreCase);
                }

                return _metadataCache;
            }
        }

        private async Task LoadSqlFilesAsync(Dictionary<string, QueryMetadata> metadata)
        {
            var newQueries = new ConcurrentDictionary<string, SqlQueryDefinition>();

            if (!Directory.Exists(_sqlDirectory))
            {
                _logger.LogWarning("SQL directory not found: {Path}", _sqlDirectory);
                Directory.CreateDirectory(_sqlDirectory);
                return;
            }

            var sqlFiles = Directory.GetFiles(_sqlDirectory, "*.sql", SearchOption.AllDirectories);

            foreach (var sqlFile in sqlFiles)
            {
                try
                {
                    var id = Path.GetFileNameWithoutExtension(sqlFile);
                    var sql = await File.ReadAllTextAsync(sqlFile).ConfigureAwait(false);

                    var queryMetadata = metadata.GetValueOrDefault(id, new QueryMetadata());

                    var definition = new SqlQueryDefinition
                    {
                        Id = id,
                        Sql = sql,
                        FilePath = sqlFile,
                        Description = queryMetadata.Description ?? "",
                        Category = queryMetadata.Category ?? "",
                        Severity = queryMetadata.Severity ?? "MEDIUM",
                        Status = queryMetadata.Status ?? "working",
                        Quick = queryMetadata.Quick,
                        Audience = queryMetadata.Audience ?? Array.Empty<string>(),
                        Controls = queryMetadata.Controls ?? Array.Empty<string>(),
                        TimeoutSec = queryMetadata.TimeoutSec > 0 ? queryMetadata.TimeoutSec : 30
                    };

                    newQueries[id] = definition;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to load SQL file {Path}", sqlFile);
                }
            }

            _queries.Clear();
            foreach (var kvp in newQueries)
                _queries[kvp.Key] = kvp.Value;
        }

        private void BuildTagIndex()
        {
            var newIndex = new ConcurrentDictionary<string, List<string>>();

            foreach (var query in _queries.Values)
            {
                if (!string.IsNullOrEmpty(query.Category))
                    AddToIndex(newIndex, query.Category.ToLower(), query.Id);

                foreach (var audience in query.Audience)
                    AddToIndex(newIndex, audience.ToLower(), query.Id);

                if (!string.IsNullOrEmpty(query.Severity))
                    AddToIndex(newIndex, query.Severity.ToLower(), query.Id);

                if (query.Quick)
                    AddToIndex(newIndex, "quick", query.Id);

                if (query.Status == "working")
                    AddToIndex(newIndex, "working", query.Id);
            }

            _tagIndex.Clear();
            foreach (var kvp in newIndex)
                _tagIndex[kvp.Key] = kvp.Value;
        }

        private static void AddToIndex(ConcurrentDictionary<string, List<string>> index, string tag, string id)
        {
            index.AddOrUpdate(tag,
                _ => new List<string> { id },
                (_, list) => { list.Add(id); return list; });
        }

        // Internal classes for deserialization
        private class QueriesConfig
        {
            public string? Comment { get; set; }
            public Dictionary<string, object>? Schema { get; set; }
            public int SchemaVersion { get; set; }
            public Dictionary<string, QueryMetadata>? Queries { get; set; }
        }

        private class QueryMetadata
        {
            public string? File { get; set; }
            public string? Description { get; set; }
            public string? Category { get; set; }
            public string? Severity { get; set; }
            public string? Status { get; set; }
            public bool Quick { get; set; }
            public string[]? Audience { get; set; }
            public string[]? Controls { get; set; }
            public int TimeoutSec { get; set; }
        }
    }
}
