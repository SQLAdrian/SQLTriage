/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SQLTriage.Data.Services.Capacity;

/// <summary>
/// Deserialisation shape for <c>Config/consolidation-model.json</c> — the PREMIUM IP that
/// drives the Capacity / Consolidation engine. The numbers, rules, thresholds and voice live
/// in the encrypted Full-tier bundle (sourced from the corpus repo), NOT in this public app
/// repo. This type is only the *shape*; without the bundle file the engine is inert and the
/// page renders a locked teaser. Pricing comes separately from sql-licensing-pricing.json.
/// </summary>
public sealed class ConsolidationModel
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("lastUpdated")] public string LastUpdated { get; set; } = "";
    [JsonPropertyName("modelName")] public string ModelName { get; set; } = "";

    [JsonPropertyName("lifecycle")] public LifecycleModel Lifecycle { get; set; } = new();
    [JsonPropertyName("objective")] public ObjectiveModel Objective { get; set; } = new();
    [JsonPropertyName("sizing")] public SizingModel Sizing { get; set; } = new();
    [JsonPropertyName("packing")] public PackingModel Packing { get; set; } = new();
    [JsonPropertyName("ghzNormalisation")] public GhzNormalisationModel GhzNormalisation { get; set; } = new();
    [JsonPropertyName("growth")] public GrowthModel Growth { get; set; } = new();
    [JsonPropertyName("edition")] public EditionModel Edition { get; set; } = new();
    [JsonPropertyName("disposition")] public DispositionModel Disposition { get; set; } = new();
    [JsonPropertyName("confidence")] public ConfidenceModel Confidence { get; set; } = new();
    [JsonPropertyName("blastRadius")] public BlastRadiusModel BlastRadius { get; set; } = new();
    [JsonPropertyName("voice")] public VoiceModel Voice { get; set; } = new();
    [JsonPropertyName("telemetry")] public TelemetryModel Telemetry { get; set; } = new();
}

public sealed class LifecycleModel
{
    [JsonPropertyName("years")] public int Years { get; set; } = 7;
}

public sealed class ObjectiveModel
{
    [JsonPropertyName("minimise")] public string Minimise { get; set; } = "licensedEnterpriseCores";
    [JsonPropertyName("ramIsFreeLever")] public bool RamIsFreeLever { get; set; } = true;
    [JsonPropertyName("ghzIsFreeLever")] public bool GhzIsFreeLever { get; set; } = true;
}

public sealed class SizingModel
{
    [JsonPropertyName("targetUtilization")] public double TargetUtilization { get; set; } = 0.75;
    [JsonPropertyName("minCores")] public int MinCores { get; set; } = 4;
    [JsonPropertyName("corePackSize")] public int CorePackSize { get; set; } = 2;
}

public sealed class PackingModel
{
    [JsonPropertyName("method")] public string Method { get; set; } = "time-sync-then-analytic";
    [JsonPropertyName("safetyFactor")] public double SafetyFactor { get; set; } = 0.75;
    [JsonPropertyName("z")] public ZScores Z { get; set; } = new();
    [JsonPropertyName("tailQuantile")] public string TailQuantile { get; set; } = "p95";
    [JsonPropertyName("assumeIndependence")] public bool AssumeIndependence { get; set; } = true;
    [JsonPropertyName("covarianceFallback")] public double CovarianceFallback { get; set; } = 0.3;
}

public sealed class ZScores
{
    [JsonPropertyName("p95")] public double P95 { get; set; } = 1.65;
    [JsonPropertyName("p99")] public double P99 { get; set; } = 2.33;
}

public sealed class GhzNormalisationModel
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("baselineGhzPerCore")] public double BaselineGhzPerCore { get; set; } = 2.7;
}

public sealed class GrowthModel
{
    [JsonPropertyName("useObservedTrend")] public bool UseObservedTrend { get; set; } = true;
    [JsonPropertyName("defaultAnnualGrowthPct")] public double DefaultAnnualGrowthPct { get; set; } = 0.10;
    [JsonPropertyName("planningHorizonMonths")] public int PlanningHorizonMonths { get; set; } = 36;
}

public sealed class EditionModel
{
    [JsonPropertyName("enterpriseIsDefault")] public bool EnterpriseIsDefault { get; set; } = true;
    [JsonPropertyName("allowStandardDowngrade")] public bool AllowStandardDowngrade { get; set; }
    [JsonPropertyName("standardCoreCap")] public int StandardCoreCap { get; set; } = 24;
    [JsonPropertyName("standardMemGbCap")] public int StandardMemGbCap { get; set; } = 128;
    [JsonPropertyName("standardAgType")] public string StandardAgType { get; set; } = "Basic";
    [JsonPropertyName("downgradeBlockerSkuFeatures")] public List<string> DowngradeBlockerSkuFeatures { get; set; } = new();
    [JsonPropertyName("biRequiresEnterpriseCaveat")] public string BiRequiresEnterpriseCaveat { get; set; } = "";
    [JsonPropertyName("standardTopology")] public StandardTopologyModel StandardTopology { get; set; } = new();
}

public sealed class StandardTopologyModel
{
    [JsonPropertyName("preferCrossAgPair")] public bool PreferCrossAgPair { get; set; } = true;
}

public sealed class DispositionModel
{
    [JsonPropertyName("lowCpuPct")] public double LowCpuPct { get; set; } = 25;
    [JsonPropertyName("highCpuPct")] public double HighCpuPct { get; set; } = 70;
    [JsonPropertyName("lowIopsPerCore")] public double LowIopsPerCore { get; set; } = 50;
    [JsonPropertyName("highIopsPerCore")] public double HighIopsPerCore { get; set; } = 200;
    [JsonPropertyName("rules")] public List<DispositionRule> Rules { get; set; } = new();
    [JsonPropertyName("tuning")] public TuningModel Tuning { get; set; } = new();
}

public sealed class DispositionRule
{
    [JsonPropertyName("signature")] public string Signature { get; set; } = "";
    [JsonPropertyName("disposition")] public string Disposition { get; set; } = "";
    [JsonPropertyName("note")] public string Note { get; set; } = "";
}

public sealed class TuningModel
{
    [JsonPropertyName("reductionPctLow")] public double ReductionPctLow { get; set; } = 0.30;
    [JsonPropertyName("reductionPctHigh")] public double ReductionPctHigh { get; set; } = 0.50;
    [JsonPropertyName("topNworkloads")] public int TopNWorkloads { get; set; } = 10;
}

public sealed class ConfidenceModel
{
    [JsonPropertyName("estateEqualsWeakestServer")] public bool EstateEqualsWeakestServer { get; set; } = true;
    [JsonPropertyName("ladder")] public List<ConfidenceRung> Ladder { get; set; } = new();
}

public sealed class ConfidenceRung
{
    [JsonPropertyName("window")] public string Window { get; set; } = "";
    [JsonPropertyName("cyclesCaptured")] public string CyclesCaptured { get; set; } = "";
    [JsonPropertyName("cl")] public double Cl { get; set; }
    [JsonPropertyName("caveat")] public string Caveat { get; set; } = "";
}

public sealed class BlastRadiusModel
{
    [JsonPropertyName("warnInstancesPerHost")] public int WarnInstancesPerHost { get; set; } = 5;
    [JsonPropertyName("maxInstancesPerHost")] public int MaxInstancesPerHost { get; set; } = 8;
}

public sealed class VoiceModel
{
    [JsonPropertyName("savingsHedge")] public string SavingsHedge { get; set; } = "It looks like there could be";
    [JsonPropertyName("neverPromise")] public bool NeverPromise { get; set; } = true;
    [JsonPropertyName("thatsRightAnchor")] public string ThatsRightAnchor { get; set; } = "";
}

public sealed class TelemetryModel
{
    [JsonPropertyName("queryStore")] public QueryStoreTelemetry QueryStore { get; set; } = new();
    [JsonPropertyName("ringBuffer")] public RingBufferTelemetry RingBuffer { get; set; } = new();
    [JsonPropertyName("workerTimeBridge")] public WorkerTimeTelemetry WorkerTimeBridge { get; set; } = new();
}

public sealed class QueryStoreTelemetry
{
    [JsonPropertyName("metadataOnly")] public bool MetadataOnly { get; set; } = true;
    [JsonPropertyName("forbiddenObjects")] public List<string> ForbiddenObjects { get; set; } = new();
    [JsonPropertyName("allowedObjects")] public List<string> AllowedObjects { get; set; } = new();
    [JsonPropertyName("consentRequiredToEnable")] public bool ConsentRequiredToEnable { get; set; } = true;
}

public sealed class RingBufferTelemetry
{
    [JsonPropertyName("cadenceHours")] public int CadenceHours { get; set; } = 4;
    [JsonPropertyName("captureProcessCpu")] public bool CaptureProcessCpu { get; set; } = true;
    [JsonPropertyName("captureTotalHostCpu")] public bool CaptureTotalHostCpu { get; set; } = true;
}

public sealed class WorkerTimeTelemetry
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
}
