/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Concurrent;
using System.Text.Json.Serialization;

namespace SQLTriage.Data.Scheduling
{
    /// <summary>
    /// Mutable metadata for a registered query.
    /// </summary>
    public class QueryMetadata
    {
        [JsonPropertyName("queryId")]
        public string QueryId { get; set; } = "";

        [JsonPropertyName("sqlHash")]
        public string SqlHash { get; set; } = "";

        [JsonPropertyName("batchGroupId")]
        public string BatchGroupId { get; set; } = "default";

        [JsonPropertyName("roundtripMs")]
        public double RoundtripMs { get; set; }

        [JsonPropertyName("estimatedMs")]
        public double EstimatedMs { get; set; }

        [JsonPropertyName("targetPeriodSec")]
        public double TargetPeriodSec { get; set; } = 15.0;

        [JsonPropertyName("lastExecuted")]
        public DateTime LastExecuted { get; set; }

        [JsonPropertyName("concurrency")]
        public int Concurrency { get; set; } = 1;

        [JsonPropertyName("serverName")]
        public string? ServerName { get; set; }

        [JsonPropertyName("priority")]
        public int Priority { get; set; } = 0;

        [JsonPropertyName("createdAt")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [JsonPropertyName("sampleCount")]
        public int SampleCount { get; set; } = 1;
    }

    /// <summary>
    /// Runtime state for a batch group — tracks health and scheduling.
    /// </summary>
    public class BatchGroupState
    {
        public string GroupId { get; }
        public ConcurrentDictionary<string, QueryMetadata> Queries { get; } = new();
        public double TargetPeriodSec { get; set; } = 15.0;
        public DateTime NextReleaseTime { get; set; } = DateTime.UtcNow;
        public int InFlightCount; // field for Interlocked
        public long TotalExecutions { get; set; }
        public double AvgRoundtripMs
        {
            get
            {
                if (Queries.Count == 0) return 0;
                var sum = Queries.Values.Sum(q => q.RoundtripMs * q.SampleCount);
                var totalSamples = Queries.Values.Sum(q => q.SampleCount);
                return totalSamples > 0 ? sum / totalSamples : 0;
            }
        }
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
        public bool IsHealthy => InFlightCount <= Queries.Count * 2;

        public BatchGroupState(string groupId) => GroupId = groupId;
    }

    /// <summary>
    /// Snapshot of scheduler state for persistence/UI.
    /// </summary>
    public record SchedulerState
    {
        [JsonPropertyName("lastUpdated")]
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

        [JsonPropertyName("globalMultiplier")]
        public double GlobalMultiplier { get; set; } = 1.0;

        [JsonPropertyName("queryCount")]
        public int QueryCount { get; set; }

        [JsonPropertyName("groupCount")]
        public int GroupCount { get; set; }

        [JsonPropertyName("queries")]
        public Dictionary<string, QueryMetadata> Queries { get; set; } = new();

        [JsonPropertyName("groups")]
        public Dictionary<string, GroupSnapshot> Groups { get; set; } = new();
    }

    public record GroupSnapshot
    {
        [JsonPropertyName("periodSec")]
        public double PeriodSec { get; init; }

        [JsonPropertyName("queryIds")]
        public List<string> QueryIds { get; init; } = new();

        [JsonPropertyName("avgRoundtripMs")]
        public double AvgRoundtripMs { get; init; }

        [JsonPropertyName("inFlight")]
        public int InFlight { get; init; }

        [JsonPropertyName("isHealthy")]
        public bool IsHealthy { get; init; }
    }
}
