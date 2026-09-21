/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Services.Licensing;

namespace SQLTriage.Data.Services.Capacity;

/// <summary>
/// Premium Capacity / Consolidation engine. ALL coefficients, rules, thresholds and pricing
/// come from the encrypted bundle (<see cref="ConsolidationModelProvider"/> +
/// <c>sql-licensing-pricing.json</c>). Without the model the engine returns
/// <see cref="ConsolidationAnalysis.Locked"/> — there is no fallback math in this public repo.
///
/// Method (eval doc §4a/§5/§6/§10): single objective = minimise licensed Enterprise cores.
///   1. Right-size each instance to growth-adjusted observed demand (min 4, even pack).
///   2. Consolidate within an edition class by pooling demand (P95 statistical multiplexing;
///      time-synchronised series when available, analytic Sigma(mu)+z*sqrt(Sigma(sigma^2)) otherwise).
///   3. Price both as OV+SA annual + lifecycle; report current vs right-sized vs consolidated.
///   4. Per-server disposition (Consolidate / TuneFirst / CpuBound / IoBound).
///   5. Confidence range from seasonal coverage; estate = weakest server. Never an exact claim.
/// </summary>
public sealed class PremiumConsolidationEngine
{
    private readonly ILogger<PremiumConsolidationEngine> _logger;
    private readonly ConsolidationAnalysisService _probe;
    private readonly IConsolidationModelProvider _modelProvider;
    private readonly IBundleAccessor _bundle;

    public PremiumConsolidationEngine(
        ILogger<PremiumConsolidationEngine> logger,
        ConsolidationAnalysisService probe,
        IConsolidationModelProvider modelProvider,
        IBundleAccessor bundle)
    {
        _logger = logger;
        _probe = probe;
        _modelProvider = modelProvider;
        _bundle = bundle;
    }

    public bool IsUnlocked => _modelProvider.IsUnlocked;

    public async Task<ConsolidationAnalysis> AnalyzeAsync(CancellationToken ct = default)
    {
        var model = _modelProvider.Current;
        if (model is null)
            return ConsolidationAnalysis.Locked();

        var pricing = LoadPricing();
        if (pricing is null)
        {
            _logger.LogWarning("[Consolidation] Model present but pricing missing; cannot price the plan.");
            return ConsolidationAnalysis.Locked();
        }

        var servers = await _probe.ProbeEstateAsync(ct);
        if (servers.Count == 0)
            return new ConsolidationAnalysis { IsUnlocked = true, IsLicensed = _modelProvider.IsLicensed, NoData = true };

        var clockGhz = model.GhzNormalisation.BaselineGhzPerCore <= 0 ? 2.7 : model.GhzNormalisation.BaselineGhzPerCore;
        var targetUtil = Clamp(model.Sizing.TargetUtilization, 0.2, 0.95);
        var growthFactor = ComputeGrowthFactor(model);

        // ── Per-server demand + disposition ──
        var rows = new List<ServerDisposition>();
        foreach (var s in servers)
        {
            // CPU-demand source priority: Query Store (long horizon) > worker-time bridge > ring-buffer snapshot.
            double meanCores, peakCores; string cpuSource;
            if (s.QsAvailable && s.QsCpuCoresMean > 0)
            {
                meanCores = s.QsCpuCoresMean;
                peakCores = s.QsCpuCoresP95 > 0 ? s.QsCpuCoresP95 : s.QsCpuCoresMean;
                cpuSource = "Query Store";
            }
            else if (s.PlanCacheWorkerCores > 0)
            {
                meanCores = s.PlanCacheWorkerCores;
                peakCores = s.PlanCacheWorkerCores;
                cpuSource = "Worker-time";
            }
            else
            {
                meanCores = s.Cores * (Math.Max(0, s.AvgCpuPercent) / 100.0);
                peakCores = meanCores;
                cpuSource = "Ring-buffer";
            }

            var demandMean = meanCores * growthFactor;                  // pooled for consolidation
            var demandPeak = peakCores * growthFactor;                  // sized against, so we don't undersize
            var iopsPerSecPerCore = s.Cores > 0 ? (s.DailyTotalIops / 86400.0) / s.Cores : 0;
            var cpuPctForDisp = s.Cores > 0 ? meanCores / s.Cores * 100.0 : s.AvgCpuPercent;

            var rightSized = ClampEvenMin(model, (int)Math.Ceiling(demandPeak / targetUtil));
            rightSized = Math.Min(rightSized, Math.Max(model.Sizing.MinCores, s.Cores));

            var (disposition, note) = Classify(model, cpuPctForDisp, iopsPerSecPerCore);
            var postTuning = PostTuningCores(model, disposition, demandPeak, targetUtil);
            var (cl, _, _) = ConfidenceFor(model, s);

            rows.Add(new ServerDisposition
            {
                ServerName = s.ServerName,
                EditionClass = ClassifyEdition(s.Edition),
                Cores = s.Cores,
                LicensedCoresNow = s.LicensedCores,
                AvgCpuPercent = s.AvgCpuPercent,
                IopsPerCore = Math.Round(iopsPerSecPerCore, 1),
                MemoryGb = Math.Round(s.PhysicalMemoryMb / 1024.0, 1),
                UptimeDays = s.UptimeDays,
                ObservedDemandCores = Math.Round(meanCores, 2),
                ProjectedDemandCores = Math.Round(demandMean, 2),
                RightSizedCores = rightSized,
                Disposition = disposition,
                DispositionNote = note,
                PostTuningDemandCores = postTuning,
                CpuSource = cpuSource,
                PeakCpuCores = Math.Round(peakCores, 2),
                QsWindowHours = s.QsWindowHours,
                QsDbCount = s.QsDbCount,
                WorkerCpuCores = s.PlanCacheWorkerCores,
                LogicalReadsPerSec = s.LogicalReadsPerSec,
                PhysicalReadsPerSec = s.PhysicalReadsPerSec,
                DailyIops = s.DailyTotalIops,
                ConfidenceLevel = cl,
            });
        }

        // ── Per-edition plans ──
        var plans = new List<EditionPlan>();
        foreach (var group in rows.GroupBy(r => r.EditionClass))
        {
            var list = group.ToList();
            var cls = group.Key;
            var perCore = PerCore(pricing, cls);
            var saFactor = pricing.AnnualSAFactor <= 0 ? 0.5833 : pricing.AnnualSAFactor;
            var years = Math.Max(1, model.Lifecycle.Years);

            var currentCores = list.Sum(r => r.LicensedCoresNow);
            var rightSizedCores = list.Sum(r => r.RightSizedCores);

            // Consolidation: pool growth-adjusted demand across the class, then size the host set.
            var pooledDemand = list.Sum(r => r.ProjectedDemandCores);
            var consolidatedCores = ClampEvenMin(model, (int)Math.Ceiling(pooledDemand / targetUtil));
            consolidatedCores = Math.Min(consolidatedCores, rightSizedCores); // never worse than right-size

            // Standard can't carry a large pooled footprint on one VM — emit the cross-AG pair topology.
            string? topology = null;
            if (cls == "Standard" && model.Edition.StandardTopology.PreferCrossAgPair
                && consolidatedCores > model.Edition.StandardCoreCap)
            {
                topology = $"Exceeds Standard's {model.Edition.StandardCoreCap}-core cap — split into a cross-replicated " +
                           "active/active pair (each host primary for one instance, passive secondary for the other; " +
                           $"passive free under SA; {model.Edition.StandardAgType} AG).";
            }

            var instancesOnTarget = list.Count;
            var blastWarn = instancesOnTarget > model.BlastRadius.WarnInstancesPerHost;

            plans.Add(new EditionPlan
            {
                EditionClass = cls,
                ServerCount = list.Count,
                CurrentCores = currentCores,
                RightSizedCores = rightSizedCores,
                ConsolidatedCores = consolidatedCores,
                CurrentAnnualUSD = currentCores * perCore * saFactor,
                RightSizedAnnualUSD = rightSizedCores * perCore * saFactor,
                ConsolidatedAnnualUSD = consolidatedCores * perCore * saFactor,
                LifecycleYears = years,
                PerCorePrice = perCore,
                Topology = topology,
                BlastRadiusWarning = blastWarn
                    ? $"Stacking {instancesOnTarget} instances concentrates failure — spread across at least two fault domains."
                    : null,
            });
        }

        // Estate confidence = the weakest (least-observed) server in the stack.
        ServerResource weakest = servers.OrderBy(x => ConfidenceFor(model, x).cl).First();
        var (eCl, eWindow, eCaveat) = ConfidenceFor(model, weakest);

        // The experiment: does the long-used daily-IO calc track measured CPU on the SAME long horizon?
        var corrPts = rows.Where(r => r.Cores > 0)
            .Select(r => (x: r.IopsPerCore, y: r.ObservedDemandCores / r.Cores * 100.0))
            .ToList();
        var (r2, corrN) = Pearson(corrPts);

        var analysis = new ConsolidationAnalysis
        {
            IsUnlocked = true,
            IsLicensed = _modelProvider.IsLicensed,
            ModelName = model.ModelName,
            GeneratedUtc = DateTime.UtcNow,
            LifecycleYears = Math.Max(1, model.Lifecycle.Years),
            Servers = rows.OrderBy(r => r.AvgCpuPercent).ToList(),
            Plans = plans,
            ConfidenceLevel = eCl,
            ConfidenceWindow = eWindow,
            ConfidenceCaveat = eCaveat,
            EstateConfidenceNote = model.Confidence.EstateEqualsWeakestServer
                ? $"Estate confidence equals the least-observed server ({weakest.ServerName})."
                : null,
            QsServerCount = rows.Count(r => r.CpuSource == "Query Store"),
            IoCpuCorrelationR = r2,
            CorrelationN = corrN,
            SavingsHedge = model.Voice.SavingsHedge,
            ThatsRightAnchor = model.Voice.ThatsRightAnchor,
        };

        analysis.CurrentAnnualUSD = plans.Sum(p => p.CurrentAnnualUSD);
        analysis.RightSizedAnnualUSD = plans.Sum(p => p.RightSizedAnnualUSD);
        analysis.ConsolidatedAnnualUSD = plans.Sum(p => p.ConsolidatedAnnualUSD);
        return analysis;
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private double ComputeGrowthFactor(ConsolidationModel model)
    {
        var g = model.Growth;
        if (g.DefaultAnnualGrowthPct <= 0 || g.PlanningHorizonMonths <= 0) return 1.0;
        var yearsAhead = g.PlanningHorizonMonths / 12.0;
        return Math.Pow(1.0 + g.DefaultAnnualGrowthPct, yearsAhead);
    }

    private static (string disposition, string note) Classify(ConsolidationModel m, double cpuPct, double iopsPerCore)
    {
        var d = m.Disposition;
        bool lowCpu = cpuPct < d.LowCpuPct, highCpu = cpuPct > d.HighCpuPct;
        bool lowIo = iopsPerCore < d.LowIopsPerCore, highIo = iopsPerCore > d.HighIopsPerCore;

        string sig =
            lowCpu && lowIo ? "lowCpu_lowIo_lowWait"
            : highCpu && highIo ? "highCpu_highIo_tuningWaits"
            : highCpu && lowIo ? "highCpu_lowIo_lowWait"
            : lowCpu && highIo ? "lowCpu_highIo"
            : "";

        var rule = m.Disposition.Rules.FirstOrDefault(r => r.Signature == sig);
        if (rule != null) return (rule.Disposition, rule.Note);
        return ("Consolidate", "Moderate load — candidate for stacking on observed demand.");
    }

    private static int PostTuningCores(ConsolidationModel m, string disposition, double demandCores, double targetUtil)
    {
        if (disposition is not ("TuneFirst" or "CpuBound")) return ClampEvenMin(m, (int)Math.Ceiling(demandCores / targetUtil));
        var reduction = (m.Disposition.Tuning.ReductionPctLow + m.Disposition.Tuning.ReductionPctHigh) / 2.0;
        var tuned = demandCores * (1.0 - reduction);
        return ClampEvenMin(m, (int)Math.Ceiling(tuned / targetUtil));
    }

    private static int ClampEvenMin(ConsolidationModel m, int cores)
    {
        var min = Math.Max(2, m.Sizing.MinCores);
        var pack = Math.Max(1, m.Sizing.CorePackSize);
        if (cores < min) cores = min;
        if (pack > 1 && cores % pack != 0) cores += pack - (cores % pack);
        return cores;
    }

    /// <summary>Free editions (Developer/Express/Web/Evaluation) carry no licence cost.</summary>
    public const string FreeEditionClass = "Free (Dev/Express)";

    private static string ClassifyEdition(string edition)
    {
        if (edition.Contains("Enterprise", StringComparison.OrdinalIgnoreCase)) return "Enterprise";
        if (edition.Contains("Standard", StringComparison.OrdinalIgnoreCase)) return "Standard";
        if (edition.Contains("Developer", StringComparison.OrdinalIgnoreCase)
            || edition.Contains("Express", StringComparison.OrdinalIgnoreCase)
            || edition.Contains("Web", StringComparison.OrdinalIgnoreCase)
            || edition.Contains("Evaluation", StringComparison.OrdinalIgnoreCase))
            return FreeEditionClass;
        return "Standard"; // conservative default for unknown editions — never free by accident
    }

    private static double PerCore(LicensingPricingData pricing, string editionClass)
    {
        if (editionClass == FreeEditionClass) return 0;          // Developer/Express/Web = no licence cost
        if (pricing.PerpetualPerCoreUSD is null) return 0;
        if (pricing.PerpetualPerCoreUSD.TryGetValue(editionClass, out var p)) return p;
        pricing.PerpetualPerCoreUSD.TryGetValue("Standard", out var std);
        return std;
    }

    private static double Clamp(double v, double lo, double hi) => Math.Max(lo, Math.Min(hi, v));

    /// <summary>Per-server confidence from Query Store coverage, mapped onto the model's ladder.</summary>
    private static (double cl, string window, string caveat) ConfidenceFor(ConsolidationModel m, ServerResource s)
    {
        var ladder = m.Confidence.Ladder;
        if (!s.QsAvailable || ladder.Count == 0)
        {
            var snap = ladder.FirstOrDefault();
            return (snap?.Cl ?? 0.65, snap?.Window ?? "snapshot", snap?.Caveat ?? "Point-in-time snapshot.");
        }
        // Ladder order: snapshot / 1week / 4-5week / quarter → hours thresholds.
        double[] thr = { 0, 168, 720, 2160 };
        int pick = 0;
        for (int i = 0; i < ladder.Count && i < thr.Length; i++)
            if (s.QsWindowHours >= thr[i]) pick = i;
        var rung = ladder[Math.Min(pick, ladder.Count - 1)];
        return (rung.Cl, rung.Window, rung.Caveat);
    }

    /// <summary>Pearson correlation over (x,y) points. Returns (r, n); r=0 when n&lt;3 or no variance.</summary>
    private static (double r, int n) Pearson(System.Collections.Generic.List<(double x, double y)> pts)
    {
        int n = pts.Count;
        if (n < 3) return (0, n);
        double mx = pts.Average(p => p.x), my = pts.Average(p => p.y);
        double sxy = 0, sxx = 0, syy = 0;
        foreach (var (x, y) in pts) { var dx = x - mx; var dy = y - my; sxy += dx * dy; sxx += dx * dx; syy += dy * dy; }
        if (sxx <= 0 || syy <= 0) return (0, n);
        return (Math.Round(sxy / Math.Sqrt(sxx * syy), 3), n);
    }

    private LicensingPricingData? LoadPricing()
    {
        var text = _bundle.GetText("Config/sql-licensing-pricing.json");

#if DEBUG
        // Dev ergonomics: the pricing file is committed (list prices — not secret), so when no
        // bundle is loaded on a dev box, read it from disk. Compiled OUT of Release.
        if (text is null)
        {
            foreach (var p in new[]
            {
                System.IO.Path.Combine(AppContext.BaseDirectory, "Config", "sql-licensing-pricing.json"),
                System.IO.Path.Combine(AppContext.BaseDirectory, "sql-licensing-pricing.json"),
            })
            {
                if (System.IO.File.Exists(p)) { text = System.IO.File.ReadAllText(p); break; }
            }
        }
#endif

        if (text is null) return null;
        try
        {
            return JsonSerializer.Deserialize<LicensingPricingData>(
                text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Consolidation] Failed to parse pricing JSON.");
            return null;
        }
    }
}

// ── Report DTOs ──────────────────────────────────────────────────────────────

public sealed class ConsolidationAnalysis
{
    public bool IsUnlocked { get; set; }
    public bool IsLicensed { get; set; }
    public bool NoData { get; set; }
    public string ModelName { get; set; } = "";
    public DateTime GeneratedUtc { get; set; }
    public int LifecycleYears { get; set; } = 7;

    public List<ServerDisposition> Servers { get; set; } = new();
    public List<EditionPlan> Plans { get; set; } = new();

    public double CurrentAnnualUSD { get; set; }
    public double RightSizedAnnualUSD { get; set; }
    public double ConsolidatedAnnualUSD { get; set; }

    public double ConfidenceLevel { get; set; }
    public string ConfidenceWindow { get; set; } = "";
    public string ConfidenceCaveat { get; set; } = "";
    public string? EstateConfidenceNote { get; set; }
    public int QsServerCount { get; set; }

    // The experiment: correlation between the long-used daily-IO calc and measured CPU utilisation.
    public double IoCpuCorrelationR { get; set; }
    public int CorrelationN { get; set; }
    public string CorrelationVerdict => CorrelationN < 3
        ? $"Not enough QS-covered servers yet (n={CorrelationN}) — correlation needs at least 3."
        : Math.Abs(IoCpuCorrelationR) < 0.3 ? $"Weak (r={IoCpuCorrelationR:0.00}, n={CorrelationN}) — daily-IO does not track CPU; they measure different bottlenecks."
        : Math.Abs(IoCpuCorrelationR) < 0.6 ? $"Moderate (r={IoCpuCorrelationR:0.00}, n={CorrelationN})."
        : $"Strong (r={IoCpuCorrelationR:0.00}, n={CorrelationN}) — on this estate daily-IO does track CPU.";

    public string SavingsHedge { get; set; } = "It looks like there could be";
    public string ThatsRightAnchor { get; set; } = "";

    // ── Derived, hedged figures (never exact claims) ──
    public double RightSizeAnnualSaving => Math.Max(0, CurrentAnnualUSD - RightSizedAnnualUSD);
    public double ConsolidatedAnnualSaving => Math.Max(0, CurrentAnnualUSD - ConsolidatedAnnualUSD);
    public double ConsolidatedLifecycleSaving => ConsolidatedAnnualSaving * LifecycleYears;
    public double CurrentLifecycleUSD => CurrentAnnualUSD * LifecycleYears;
    public double SavingPctLow => CurrentAnnualUSD > 0 ? RightSizeAnnualSaving / CurrentAnnualUSD * 100.0 : 0;
    public double SavingPctHigh => CurrentAnnualUSD > 0 ? ConsolidatedAnnualSaving / CurrentAnnualUSD * 100.0 : 0;

    public static ConsolidationAnalysis Locked() => new() { IsUnlocked = false };
}

public sealed class ServerDisposition
{
    public string ServerName { get; set; } = "";
    public string EditionClass { get; set; } = "";
    public int Cores { get; set; }
    public int LicensedCoresNow { get; set; }
    public int AvgCpuPercent { get; set; }
    public double IopsPerCore { get; set; }
    public double MemoryGb { get; set; }
    public double UptimeDays { get; set; }
    public double ObservedDemandCores { get; set; }
    public double ProjectedDemandCores { get; set; }
    public int RightSizedCores { get; set; }
    public int PostTuningDemandCores { get; set; }
    public string Disposition { get; set; } = "";
    public string DispositionNote { get; set; } = "";

    // ── Telemetry provenance + the daily-IO-vs-CPU comparison ──
    public string CpuSource { get; set; } = "";          // Query Store | Worker-time | Ring-buffer
    public double PeakCpuCores { get; set; }
    public double QsWindowHours { get; set; }
    public int QsDbCount { get; set; }
    public double WorkerCpuCores { get; set; }           // plan-cache worker-time bridge
    public double LogicalReadsPerSec { get; set; }       // the direct CPU partner
    public double PhysicalReadsPerSec { get; set; }
    public long DailyIops { get; set; }                  // the long-used daily-IO calc
    public double ConfidenceLevel { get; set; }
    public double CpuUtilPercent => Cores > 0 ? ObservedDemandCores / Cores * 100.0 : 0;
}

public sealed class EditionPlan
{
    public string EditionClass { get; set; } = "";
    public int ServerCount { get; set; }
    public int CurrentCores { get; set; }
    public int RightSizedCores { get; set; }
    public int ConsolidatedCores { get; set; }
    public double CurrentAnnualUSD { get; set; }
    public double RightSizedAnnualUSD { get; set; }
    public double ConsolidatedAnnualUSD { get; set; }
    public int LifecycleYears { get; set; }
    public double PerCorePrice { get; set; }
    public string? Topology { get; set; }
    public string? BlastRadiusWarning { get; set; }

    public int ReclaimedCores => Math.Max(0, CurrentCores - ConsolidatedCores);
    public double ConsolidatedLifecycleSaving => Math.Max(0, CurrentAnnualUSD - ConsolidatedAnnualUSD) * LifecycleYears;
}
