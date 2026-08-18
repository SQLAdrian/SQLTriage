<!-- In the name of God, the Merciful, the Compassionate -->

# Caching

Encrypted SQLite + in-memory cache primitives. WAL-mode SQLite, AES-256 via `SqliteCipherHelper`, 2-week default retention, delta-fetch friendly.

| File | Purpose |
|------|---------|
| SqliteCacheStore | Encrypted SQLite store with WAL, retention, and bulk-row insert |
| SqliteMaintenanceService | Periodic VACUUM / retention sweep |
| CacheEvictionService | Background eviction loop with TTL + size bounds |
| CacheHotTier | In-memory hot cache layered above SQLite |
| CacheStateTracker | Tracks per-key staleness and last-fetch timestamps |
| CachingQueryExecutor | DMV-query executor that pulls from cache if fresh, else queries + writes |
| SpBlitzCache | sp_BLITZ-specific cache (output-folder scanner + run results) |

Connection string is built via `SqliteCipherHelper.GetConnectionString(path, key)`. **Never** open these DBs with a plain-SQLite client — they're encrypted.
