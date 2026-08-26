/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
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

        /// <summary>
        /// Completes when the load started at construction has FINISHED — which is not the same as
        /// published: a finished load publishes nothing when the SQL directory is missing, or when
        /// it faulted. Construction does not block on disk I/O, so until some load publishes the
        /// repository reports EMPTY, and after this task completes it is empty only in those two
        /// no-publish cases. See <see cref="SqlQueryRepository.InitializationComplete"/> for the
        /// full window.
        /// </summary>
        Task InitializationComplete { get; }
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

        /// <summary>
        /// The published queries and the tag index that was built FROM those exact queries.
        /// A load builds a whole new instance and swaps the single reference, so a reader that
        /// takes the reference once sees one internally consistent state for the whole operation.
        ///
        /// Never mutated after publication, and as of 2026-08-10 that is HELD BY THE COMPILER
        /// rather than by the call sites. It used to be a property of the two call sites only:
        /// <see cref="IReadOnlyDictionary{TKey,TValue}"/> is a read-only VIEW over a caller-owned
        /// <see cref="Dictionary{TKey,TValue}"/>, so a builder that retained its reference could
        /// still mutate a published snapshot, and nothing — no compiler error, no test — would
        /// have said so. The comment that stood here said exactly that, and asked a future
        /// author to switch to immutable collections. This is that switch.
        ///
        /// <para><b>Why <see cref="FrozenDictionary{TKey,TValue}"/> and not
        /// <c>ImmutableDictionary</c>.</b> Both are genuinely immutable, so both close the hole.
        /// They differ in what they optimise. ImmutableDictionary is a persistent tree built for
        /// cheap incremental Add/Remove that return a new instance, and it pays for that with
        /// O(log n) lookups and node allocations per read path. This snapshot is never edited
        /// incrementally: it is built ONCE per load and then read on every check execution
        /// (<see cref="Get"/>, <see cref="GetByTag"/>, <see cref="GetQuickChecks"/>).
        /// FrozenDictionary is the collection for exactly that shape — a one-off analysis cost at
        /// construction in exchange for O(1) reads faster than <c>Dictionary</c>'s own, and no
        /// mutating API at all. A read-hot, write-once structure wants the frozen one; the
        /// persistent tree would be paying construction-flexibility we never use.</para>
        ///
        /// <para>The tag index's VALUES are frozen too (<see cref="ImmutableArray{T}"/>). Leaving
        /// them as <c>IReadOnlyList&lt;string&gt;</c> over a <c>List&lt;string&gt;</c> would have
        /// moved the same hole one level down: an immutable dictionary of mutable lists is not an
        /// immutable index.</para>
        ///
        /// The two live together rather than in two fields because the tag index stores query
        /// ids: if the index could be swapped independently of the dictionary, a reader could
        /// hold an index naming ids the dictionary no longer carries.
        /// </summary>
        private sealed class Snapshot
        {
            // EqualityComparer<string>.Default (ordinal) throughout — the same comparer the
            // parameterless ConcurrentDictionary fields these replace were built with, and the
            // one the source dictionaries carry, so freezing changes no lookup result.
            internal static readonly Snapshot Empty = new(
                new Dictionary<string, SqlQueryDefinition>(),
                new Dictionary<string, ImmutableArray<string>>());

            internal Snapshot(
                Dictionary<string, SqlQueryDefinition> queries,
                Dictionary<string, ImmutableArray<string>> tagIndex)
            {
                Queries = queries.ToFrozenDictionary(queries.Comparer);
                TagIndex = tagIndex.ToFrozenDictionary(tagIndex.Comparer);
            }

            internal FrozenDictionary<string, SqlQueryDefinition> Queries { get; }
            internal FrozenDictionary<string, ImmutableArray<string>> TagIndex { get; }
        }

        // Read with Volatile.Read, written with Volatile.Write. The release/acquire pair is what
        // publishes the new Snapshot's contents to reader threads along with the reference.
        private Snapshot _snapshot = Snapshot.Empty;

        // Serialises loads so two of them cannot interleave. Loads arrive from three places:
        // the constructor, ReloadAsync callers, and the BundleStateChanged handler.
        private readonly SemaphoreSlim _loadGate = new(1, 1);

        /// <summary>
        /// Completes when the load started by the constructor has finished. It does not fault:
        /// <see cref="LoadAsync"/> catches and logs load failures, so a failed load completes this
        /// task with the repository left at whatever snapshot it already had.
        ///
        /// Construction deliberately does not block on disk I/O, so there IS a startup window —
        /// from the constructor returning until the first load publishes — in which every reader
        /// sees an EMPTY repository: <c>_snapshot</c> is initialised to <c>Snapshot.Empty</c> and
        /// no load has replaced it yet. Awaiting this task closes that window; so does awaiting a
        /// <see cref="ReloadAsync"/> call, which publishes a snapshot of its own. What does NOT
        /// close it is merely holding a constructed instance.
        /// </summary>
        public Task InitializationComplete { get; }

        public SqlQueryRepository(
            ILogger<SqlQueryRepository> logger,
            IBundleAccessor bundle)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _bundle = bundle ?? throw new ArgumentNullException(nameof(bundle));

            _sqlDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "Sql");

            // Invalidate metadata cache whenever the bundle state changes.
            // This reload is NOT tracked by InitializationComplete and nothing awaits it: after
            // raising BundleStateChanged a caller can still read the pre-reload snapshot.
            _bundle.BundleStateChanged += (_, _) =>
            {
                lock (_lock) { _metadataLoaded = false; _metadataCache = null; }
                _ = Task.Run(async () =>
                {
                    try { await ReloadAsync().ConfigureAwait(false); }
                    catch (Exception ex) { _logger.LogError(ex, "Background reload after BundleStateChanged failed"); }
                });
            };

            // Initial load. Still off the constructor's thread (DI construction stays synchronous
            // and non-blocking), but the task is kept rather than discarded so callers and tests
            // can await it instead of guessing at the timing.
            InitializationComplete = Task.Run(async () =>
            {
                try { await LoadAsync().ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogError(ex, "Initial SQL query repository load failed"); }
            });
        }

        public SqlQueryDefinition? Get(string id)
        {
            var snapshot = Volatile.Read(ref _snapshot);
            return snapshot.Queries.TryGetValue(id, out var query) ? query : null;
        }

        public IReadOnlyDictionary<string, SqlQueryDefinition> GetAll()
        {
            var snapshot = Volatile.Read(ref _snapshot);
            return snapshot.Queries.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }

        public IReadOnlyList<SqlQueryDefinition> GetByTag(string tag)
        {
            var snapshot = Volatile.Read(ref _snapshot);
            if (snapshot.TagIndex.TryGetValue(tag, out var ids))
            {
                // Same snapshot for the index and the lookups, so every id here resolves.
                // TryGetValue rather than the indexer keeps this total regardless.
                // Length, not Count: ImmutableArray<T> exposes Count only through ICollection, so
                // `ids.Count` would bind to the LINQ extension and enumerate.
                var matches = new List<SqlQueryDefinition>(ids.Length);
                foreach (var id in ids)
                {
                    if (snapshot.Queries.TryGetValue(id, out var query))
                        matches.Add(query);
                }
                return matches;
            }
            return Array.Empty<SqlQueryDefinition>();
        }

        public IReadOnlyList<SqlQueryDefinition> GetQuickChecks()
        {
            var snapshot = Volatile.Read(ref _snapshot);
            return snapshot.Queries.Values.Where(q => q.Quick).ToList();
        }

        public async Task ReloadAsync()
        {
            await LoadAsync().ConfigureAwait(false);
            _logger.LogInformation("SQL query repository reloaded");
        }

        private async Task LoadAsync()
        {
            await _loadGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var metadata = GetMetadata();
                var queries = await LoadSqlFilesAsync(metadata).ConfigureAwait(false);
                if (queries is null)
                {
                    // SQL directory absent — nothing was read, so the current snapshot stands
                    // rather than being replaced with an empty one. Same as the pre-existing
                    // behaviour, where the early return skipped the repopulate entirely.
                    return;
                }

                var tagIndex = BuildTagIndex(queries);
                Volatile.Write(ref _snapshot, new Snapshot(queries, tagIndex));

                _logger.LogInformation("Loaded {Count} SQL queries from {SqlDir}",
                    queries.Count, _sqlDirectory);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load SQL query repository");
            }
            finally
            {
                _loadGate.Release();
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

        /// <summary>
        /// Reads every SQL file into a fresh dictionary and returns it. Publishing is the caller's
        /// job, so this method never mutates what readers can see.
        /// Returns <c>null</c> when the SQL directory does not exist — "nothing to publish",
        /// which the caller distinguishes from "read the directory and found no files".
        /// </summary>
        private async Task<Dictionary<string, SqlQueryDefinition>?> LoadSqlFilesAsync(
            Dictionary<string, QueryMetadata> metadata)
        {
            if (!Directory.Exists(_sqlDirectory))
            {
                _logger.LogWarning("SQL directory not found: {Path}", _sqlDirectory);
                Directory.CreateDirectory(_sqlDirectory);
                return null;
            }

            var newQueries = new Dictionary<string, SqlQueryDefinition>();

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

            return newQueries;
        }

        /// <summary>
        /// Builds the tag index for one specific query dictionary. Takes the dictionary as an
        /// argument rather than reading a field so the index it returns can only ever describe
        /// the queries it is published alongside.
        /// </summary>
        private static Dictionary<string, ImmutableArray<string>> BuildTagIndex(
            Dictionary<string, SqlQueryDefinition> queries)
        {
            var newIndex = new Dictionary<string, List<string>>();

            foreach (var query in queries.Values)
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

            // ToImmutableArray, not the List behind an IReadOnlyList: an immutable dictionary
            // whose values are mutable lists is not an immutable index. AddToIndex's working lists
            // are dropped here and never reach a published snapshot.
            var published = new Dictionary<string, ImmutableArray<string>>(newIndex.Count);
            foreach (var kvp in newIndex)
                published[kvp.Key] = kvp.Value.ToImmutableArray();
            return published;
        }

        private static void AddToIndex(Dictionary<string, List<string>> index, string tag, string id)
        {
            if (index.TryGetValue(tag, out var list))
                list.Add(id);
            else
                index[tag] = new List<string> { id };
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
