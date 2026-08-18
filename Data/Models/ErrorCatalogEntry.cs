/* In the name of God, the Merciful, the Compassionate */

namespace SQLTriage.Data.Models
{
    /// <summary>
    /// A single entry in the ErrorCatalog. Provides human-friendly error guidance
    /// across three audiences (DBA, IT Manager, Executive) and links to related
    /// SQL health checks from sql-checks.json for contextual remediation.
    /// </summary>
    public sealed class ErrorCatalogEntry
    {
        /// <summary>Stable error code, e.g. CONN_TIMEOUT, QUERY_DEADLOCK.</summary>
        public required string ErrorCode { get; set; }

        /// <summary>High-level category: Connection, Query, Permission, Credential, DataShape, Resource, Cache, Config, Runtime, Edge.</summary>
        public required string Category { get; set; }

        /// <summary>Short user-facing message (one sentence).</summary>
        public required string UserMessage { get; set; }

        /// <summary>Governance score impact when this error occurs, e.g. "Security -5".</summary>
        public string GovernanceImpact { get; set; } = "";

        /// <summary>Technical remediation steps for the DBA audience.</summary>
        public string Remediation { get; set; } = "";

        /// <summary>Audience-tailored messages. Keys: dba, it, exec.</summary>
        public Dictionary<string, string> AudienceMessages { get; set; } = new();

        /// <summary>Check IDs from sql-checks.json that relate to this error scenario.</summary>
        public List<string> RelatedCheckIds { get; set; } = new();

        /// <summary>External documentation link.</summary>
        public string? DocUrl { get; set; }
    }

    /// <summary>
    /// Strongly-typed error categories used by the catalog.
    /// </summary>
    public static class ErrorCategories
    {
        public const string Connection = "Connection";
        public const string Query = "Query";
        public const string Permission = "Permission";
        public const string Credential = "Credential";
        public const string DataShape = "DataShape";
        public const string Resource = "Resource";
        public const string Cache = "Cache";
        public const string Config = "Config";
        public const string Runtime = "Runtime";
        public const string Edge = "Edge";

        public static readonly IReadOnlyList<string> All = new[]
        {
            Connection, Query, Permission, Credential, DataShape,
            Resource, Cache, Config, Runtime, Edge
        };
    }

    /// <summary>
    /// Audience keys for ErrorCatalogEntry.AudienceMessages.
    /// </summary>
    public static class ErrorAudiences
    {
        public const string Dba = "dba";
        public const string ItManager = "it";
        public const string Executive = "exec";
    }

    /// <summary>
    /// Root JSON structure for Config/error-catalog.json.
    /// </summary>
    public sealed class ErrorCatalogFile
    {
        public int SchemaVersion { get; set; }
        public List<ErrorCatalogEntry> Entries { get; set; } = new();
    }
}
