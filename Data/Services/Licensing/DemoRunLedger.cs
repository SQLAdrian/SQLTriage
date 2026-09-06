/* In the name of God, the Merciful, the Compassionate */
/*
 * DemoRunLedger — meters CORPUS audit runs (/audit) against the SIGNED per-bundle demo
 * allocation (BundleFeatures.DemoCorpusInstancesPer24h). The allocation is the number of
 * DISTINCT SQL instances the operator may run the corpus against within any rolling 24h
 * window:
 *     community bundle = 1   (the public demo limit)
 *     full bundle      = 0   → UNLIMITED (gate no-ops)
 *
 * The limit lives in the GCM-authenticated bundle, NOT the GitHub source — so recompiling the
 * public build cannot lift it, and tampering the bundle breaks the auth tag at decrypt. This is
 * the same trust model as the remediation credit ledger (PersistedRemediationCreditLedger).
 *
 * Mechanics:
 *   • A "claim" = { instance, firstRunUtc }, recorded on the first SUCCESSFUL corpus run for an
 *     instance. Re-running a CLAIMED instance inside its 24h window is always free.
 *   • A NEW instance is allowed only while the count of DISTINCT claims active in the last 24h is
 *     below the signed N. Claims older than 24h expire (pruned on every read) → a slot frees.
 *   • Allocation is read LIVE from the bundle, so loading a different bundle (community → demo →
 *     full) re-licenses immediately, exactly like the credit ledger.
 *
 * Persistence mirrors PersistedRemediationCreditLedger: atomic tmp→delete→move to
 * Config/demo-run-ledger.json. A crash never corrupts the ledger; at worst a claim is lost,
 * which only ever GRANTS the operator another run (fail-open on persistence, fail-closed on the
 * allocation itself).
 *
 * DevBridge (dev machine) gets an UNLIMITED escape hatch so the gated surface is testable without
 * a stamped bundle; real installs honour only the signed allocation.
 *
 * LANE-NEUTRAL. TryAdmitRun is the ONE admission rule, called by every lane that can start a corpus
 * run — the desktop /audit page and the headless CLI (`--audit`, which runs on client production
 * servers). Until 2026-07-20 the CLI consulted this ledger not at all, so the headline commercial
 * limit was one documented flag away from unlimited. A lane that re-derives the rule, or
 * paraphrases the refusal, is a lane that tells a paying client a different story about the same
 * licence: put new rules HERE, not at the call site.
 */

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data.Services.Licensing;

/// <summary>Why a corpus run is (dis)allowed, plus the figures the UI shows.</summary>
public sealed record DemoRunDecision(
    bool Allowed,
    bool Unlimited,
    int Allowance,          // signed N (0 when unlimited)
    int InstancesUsed,      // distinct instances claimed in the last 24h
    string? Instance,       // the instance this decision was made for (null for Status())
    DateTime? UnlocksUtc,   // when the next slot frees (oldest active claim + 24h); null if a slot is free now
    string? BlockReason,    // human message when !Allowed
    // Provenance of Allowance, so a caller can tell a SIGNED 1 from an `?? 1` supplied for a
    // bundle minted before the field existed. Defaulted so the positional shape stays source-
    // compatible for existing consumers.
    DemoAllocationOrigin Origin = DemoAllocationOrigin.Signed);

/// <summary>
/// Verdict for a WHOLE requested run (which may name several instances) — the unit both lanes
/// actually ask about. <see cref="Status"/> rides along so a caller can render figures without a
/// second, separately-locked read that might disagree with the verdict it just got.
/// </summary>
public sealed record DemoRunAdmission(
    bool Allowed,
    string? BlockReason,
    DemoRunDecision Status);

public interface IDemoRunLedger
{
    /// <summary>True if the operator may run the corpus against <paramref name="instance"/> right now.</summary>
    DemoRunDecision CanRun(string instance);

    /// <summary>
    /// THE admission rule for a corpus run, for EVERY lane (desktop /audit page, CLI --audit,
    /// --server). Whole-request: applies the over-allowance check to the instance COUNT and then
    /// admits each instance individually. Both lanes must call this rather than re-deriving it —
    /// the CLI lane previously derived nothing at all and ran unmetered, which put the headline
    /// commercial limit one documented flag away from unlimited.
    /// </summary>
    DemoRunAdmission TryAdmitRun(IReadOnlyList<string> instances);

    /// <summary>Records a successful corpus run for <paramref name="instance"/> (claims a slot if new).</summary>
    void RecordRun(string instance);

    /// <summary>Snapshot for the UI (no specific instance): allowance, used, next-unlock.</summary>
    DemoRunDecision Status();

    /// <summary>
    /// The one sentence that describes WHAT THIS INSTALL IS ENTITLED TO, worded from the
    /// allocation's provenance. Exposed because the desktop banner needs the same lead words the
    /// refusal uses: when the banner re-derived its own copy it said "Community version" to every
    /// operator including paid Full licence holders — the exact error that cost a client. Callers
    /// render this; they must not rebuild it. Empty string when the allocation is unlimited
    /// (there is nothing to meter, so there is no banner to show).
    /// </summary>
    string DescribeAllowance();
}

public sealed class DemoRunLedger : IDemoRunLedger
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(24);

    private readonly IBundleAccessor _bundle;
    private readonly ILogger<DemoRunLedger> _logger;
    private readonly string _path;
    private readonly object _lock = new();

    // Persisted: one entry per claimed instance, holding the FIRST run time inside the current
    // window. Re-running refreshes nothing (the window is anchored to first use, by design).
    private Dictionary<string, DateTime> _claims = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public DemoRunLedger(IBundleAccessor bundle, ILogger<DemoRunLedger> logger, string? pathOverride = null)
    {
        _bundle = bundle ?? throw new ArgumentNullException(nameof(bundle));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _path = pathOverride ?? Path.Combine(AppContext.BaseDirectory, "Config", "demo-run-ledger.json");
        Load();
    }

    public string LedgerPath => _path;

    // The signed allocation. 0 = unlimited. DevBridge forces unlimited so the surface is testable
    // on a dev build without a stamped bundle.
    private bool IsUnlimited => BuildMode.DevBridgeActive || Allowance == 0;
    private int Allowance => Math.Max(0, _bundle.Features.DemoCorpusInstancesPer24h);
    private DemoAllocationOrigin Origin => _bundle.Features.DemoAllocationOrigin;

    public DemoRunDecision CanRun(string instance)
    {
        if (string.IsNullOrWhiteSpace(instance))
            return new DemoRunDecision(false, false, Allowance, 0, instance, null,
                "No SQL instance selected.", Origin);

        lock (_lock)
        {
            Prune();

            if (IsUnlimited)
                return new DemoRunDecision(true, true, 0, _claims.Count, instance, null, null, Origin);

            // Re-running an already-claimed instance is always free inside its window.
            if (_claims.ContainsKey(instance))
                return new DemoRunDecision(true, false, Allowance, _claims.Count, instance, null, null, Origin);

            // A new instance is allowed only if a slot is free.
            if (_claims.Count < Allowance)
                return new DemoRunDecision(true, false, Allowance, _claims.Count, instance, null, null, Origin);

            // No slot — report when the oldest claim frees one.
            DateTime? unlocks = _claims.Values.Count == 0 ? null : _claims.Values.Min() + Window;
            return new DemoRunDecision(false, false, Allowance, _claims.Count, instance, unlocks,
                BuildBlockReason(instance, unlocks), Origin);
        }
    }

    /// <inheritdoc/>
    public DemoRunAdmission TryAdmitRun(IReadOnlyList<string> instances)
    {
        // One lock across the whole verdict, so the figures returned in Status are the figures the
        // verdict was computed from. Monitor is re-entrant, so the Status() call below re-takes it.
        lock (_lock)
        {
            Prune();

            var status = Status();
            if (status.Unlimited) return new DemoRunAdmission(true, null, status);

            var requested = (instances ?? Array.Empty<string>())
                .Where(i => !string.IsNullOrWhiteSpace(i))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (requested.Count == 0)
                return new DemoRunAdmission(false, "No SQL instance selected.", status);

            // Slots this request would CLAIM. An instance already claimed inside its 24h window is
            // free and consumes nothing, so only genuinely NEW instances count against the meter.
            var newInstances = requested.Where(i => !_claims.ContainsKey(i)).ToList();

            // Asking for more DISTINCT NEW instances than the allocation can ever cover is refused
            // up front, whole — not half-run. Measured on the NEW ones, not on the raw request: an
            // operator re-running the four instances they already hold is asking for nothing, and a
            // licence of 3 refusing them would be failing closed on a client mid-window. The count
            // reported back is still what they ASKED for, because that is the number they typed.
            if (newInstances.Count > status.Allowance)
                return new DemoRunAdmission(false, BuildOverRequestReason(requested.Count), status);

            // THE capacity rule: new claims PLUS claims already standing must fit inside N.
            //
            // What shipped checked the REQUEST against N and then admitted each instance one at a
            // time — but nothing is recorded until a run SUCCEEDS, so every instance in the request
            // was measured against the same untouched free-slot count. N=2 with one claim standing
            // admitted a 2-instance request and consumed 3 slots against an allowance of 2. It
            // leaked in the OPERATOR's favour, which is exactly why no one ever reported it.
            //
            // Guarded on newInstances.Count > 0 so this can only ever refuse a request that is
            // actually asking for a new slot: re-running already-claimed instances stays free even
            // if _claims somehow exceeds N (a bundle downgraded mid-window, a RecordRun that never
            // went through admission). A paying client mid-window must not be locked out by this.
            if (newInstances.Count > 0 && newInstances.Count + _claims.Count > status.Allowance)
            {
                DateTime? unlocks = _claims.Count == 0 ? null : _claims.Values.Min() + Window;
                return new DemoRunAdmission(false, BuildBlockReason(newInstances[0], unlocks), status);
            }

            return new DemoRunAdmission(true, null, status);
        }
    }

    public void RecordRun(string instance)
    {
        if (string.IsNullOrWhiteSpace(instance)) return;
        lock (_lock)
        {
            Prune();
            if (IsUnlimited) return;             // unlimited path never spends the ledger
            if (_claims.ContainsKey(instance)) return; // already claimed inside the window
            _claims[instance] = DateTime.UtcNow;
            Save();
            _logger.LogInformation(
                "[DemoRunLedger] Claimed corpus-demo slot for '{Instance}' ({Used}/{Allow} used in 24h).",
                instance, _claims.Count, Allowance);
        }
    }

    public DemoRunDecision Status()
    {
        lock (_lock)
        {
            Prune();
            if (IsUnlimited)
                return new DemoRunDecision(true, true, 0, _claims.Count, null, null, null, Origin);

            bool slotFree = _claims.Count < Allowance;
            DateTime? unlocks = slotFree || _claims.Count == 0 ? null : _claims.Values.Min() + Window;
            return new DemoRunDecision(slotFree, false, Allowance, _claims.Count, null, unlocks,
                slotFree ? null : BuildBlockReason(null, unlocks), Origin);
        }
    }

    /// <inheritdoc/>
    public string DescribeAllowance() => IsUnlimited ? string.Empty : LeadFor(Allowance);

    // ── The ONE place the paid offer is described. Both community refusals render this constant, so
    //    the two lanes (desktop banner and CLI stderr) cannot describe the service two ways.
    //
    //    NO CURRENCY AMOUNT APPEARS HERE, and none appears anywhere in this seam — not in the copy
    //    and not in this comment, because the public-tree language gate scans both. Adrian's ruling
    //    2026-08-20 SUPERSEDES Ruling A (2026-07-21), which pinned a New Zealand dollar figure into
    //    this string in every profile and forbade scanning it out: the pricing copy is REMOVED, so
    //    the public tree carries no currency amounts. The record is the DECISIONS entry in the
    //    private meta repo, 2026-08-20.
    //
    //    The pointer to sqldba.org is therefore now the whole of the pricing answer, and it is
    //    load-bearing rather than decorative: this app is OFFLINE by design and this string ships
    //    COMPILED into the binary, so a 2026 build still running in 2028 will still be showing this
    //    sentence and there is no CPI mechanism behind it. Somewhere current to look stays true for
    //    the life of the build; a baked figure would not.
    //
    //    What is being priced is deliberately NOT the software. The app is free; the DBA's extended
    //    report and remediation work — produced outside SQLTriage from what SQLTriage captured — is
    //    what costs money.
    private const string ServiceOffer =
        "What costs money is the extended report and the remediation actions a DBA produces from " +
        "what this tool captured. Ask about either, and for current pricing, at sqldba.org.";

    // ── Block copy. ONE rule about who gets told what, so the two lanes cannot drift into
    //    different accounts of the same refusal.
    //
    //    The COMMUNITY wording is claimed ONLY for a FREE install (no bundle, or the bundled
    //    Free-tier one) — the states we can prove are the community build. It is NOT used for a
    //    SIGNED allowance of 1 (a paid licence can legitimately be minted at 1, and that operator is
    //    at the limit of what they bought, not on the free build), and never for an UNSIGNED one.
    //    Telling a paid Full licence holder they are on the free build is the exact message that
    //    shipped and cost a client.
    private string LeadFor(int allowance) => Origin switch
    {
        // Worded for someone who has bought nothing: no bundle to name, nothing to re-mint, and no
        // reason to be sent an apology about a licence they do not hold. The count is interpolated
        // rather than written as a literal "1" so this sentence cannot go stale against the number
        // the operator is actually metered on.
        //
        // What this sentence used to say ("Community version: corpus assessment covers 1 SQL
        // instance per 24h") described the product as gated software with an allocation to buy, and
        // then would not say the price. A seven-persona board scored the surface 1.667/5 on pricing
        // transparency — the lowest element ever recorded here — and an MSP owner with a real
        // deadline churned on it. The correction is the plain fact: the software is free, the cap is
        // a safety control on pointing the tool at live SQL Servers, and it lifts at no charge.
        DemoAllocationOrigin.Community =>
            "SQLTriage is free software and stays free. " +
            $"The cap of {allowance} SQL instance{(allowance == 1 ? "" : "s")} per 24h is a safety " +
            "limit on running the tool against live servers, not a paywall",

        // The `?? 1` supplied this number — we genuinely do not know the entitlement. Say that,
        // and point at the fix, instead of asserting an allocation the licence may not impose.
        // Reachable only from a PAID bundle (BundleAccessor gates this on Tier.Full), so "your
        // licence bundle" and "ask Adrian" both address someone who actually has one.
        DemoAllocationOrigin.Unsigned =>
            "Your licence bundle predates the corpus allocation field, so it is falling back to 1 SQL instance " +
            "per 24h. This is NOT necessarily your entitlement — ask Adrian to re-mint your bundle",

        _ => allowance == 1
            ? "Your licence allocates 1 SQL instance per 24h"
            : $"Your licence allocates {allowance} SQL instances per 24h",
    };

    private string BuildBlockReason(string? instance, DateTime? unlocks)
    {
        var when = unlocks is { } u
            ? FormatRemaining(u - DateTime.UtcNow)
            : "soon";
        var tail = Origin switch
        {
            // The free build. Answer the two questions a blocked operator actually has, in order:
            // when do I get back in, and what would it cost me. Never "get in touch if you need a
            // larger allocation" with no number attached — that is the sentence that read as "there
            // is a thing to buy, and I won't tell you what it costs".
            DemoAllocationOrigin.Community =>
                $". You have used this window's slot; the next frees in {when}, or we can lift the " +
                "cap for you at no charge. " + ServiceOffer,

            DemoAllocationOrigin.Unsigned =>
                $" You have used that allowance for this 24h window; the next slot frees in {when}.",

            // A PAID licence at the limit it was minted at. No price sentence here on purpose: this
            // operator has already agreed a commercial arrangement, and quoting a list price back at
            // them would be a second, contradicting number on the same screen.
            _ => $", and you have used it for this 24h window. Next unlocks in {when}, " +
                 "or get in touch with Adrian if you need a larger allocation.",
        };
        return LeadFor(Allowance) + tail;
    }

    // Refusal for a request naming more DISTINCT instances than the allocation could ever cover.
    // Separate from BuildBlockReason because nothing is "used up" here — the ask itself is too big,
    // and telling the operator to wait for a slot would be false.
    private string BuildOverRequestReason(int requested)
    {
        var tail = Origin switch
        {
            // The estate-sized ask on the free build — someone with 180 instances typing all of them
            // in on their first screen. This is the churn surface: it is the one message that must
            // not leave them thinking the tool is a 1-instance toy they are locked out of.
            DemoAllocationOrigin.Community =>
                $". You asked for {requested}. Run fewer at a time, or we can lift the cap for you " +
                "at no charge. " + ServiceOffer,

            DemoAllocationOrigin.Unsigned =>
                $" — you asked for {requested}. Run fewer instances at a time, or ask Adrian for a re-mint " +
                "so the bundle carries your real allocation.",

            _ => $" — you asked for {requested}. Run them in separate windows, or get in touch with Adrian " +
                 "for a larger allocation.",
        };
        return LeadFor(Allowance) + tail +
               " (Raw Diagnostics and Vulnerability Assessment have no such limit.)";
    }

    private static string FormatRemaining(TimeSpan ts)
    {
        if (ts <= TimeSpan.Zero) return "moments";
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        return $"{ts.Minutes}m";
    }

    // Drop claims whose 24h window has elapsed. Caller holds _lock.
    private void Prune()
    {
        var cutoff = DateTime.UtcNow - Window;
        var expired = _claims.Where(kv => kv.Value <= cutoff).Select(kv => kv.Key).ToList();
        if (expired.Count == 0) return;
        foreach (var k in expired) _claims.Remove(k);
        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                _logger.LogInformation("[DemoRunLedger] No ledger at {Path}; starting fresh.", _path);
                return;
            }
            var payload = JsonSerializer.Deserialize<LedgerPayload>(File.ReadAllText(_path), _json);
            if (payload?.Claims == null) return;
            lock (_lock)
            {
                _claims = new Dictionary<string, DateTime>(payload.Claims, StringComparer.OrdinalIgnoreCase);
                Prune();
            }
            _logger.LogInformation("[DemoRunLedger] Loaded {Count} active demo claim(s).", _claims.Count);
        }
        catch (Exception ex)
        {
            // Fail-open on a corrupt ledger: an unreadable file resets to zero claims, which only
            // ever GRANTS another run — never silently extends a paid allocation.
            _logger.LogWarning(ex, "[DemoRunLedger] Failed to load ledger from {Path}; starting fresh.", _path);
        }
    }

    // Atomic persist (tmp -> delete -> move), mirroring PersistedRemediationCreditLedger. Caller holds _lock.
    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var payload = new LedgerPayload
            {
                SchemaVersion = 1,
                LastUpdatedUtc = DateTime.UtcNow,
                Claims = new Dictionary<string, DateTime>(_claims, StringComparer.OrdinalIgnoreCase),
            };
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(payload, _json));
            if (File.Exists(_path)) File.Delete(_path);
            File.Move(tmp, _path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DemoRunLedger] Failed to persist ledger to {Path}", _path);
        }
    }

    private sealed class LedgerPayload
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; } = 1;

        [JsonPropertyName("lastUpdatedUtc")]
        public DateTime LastUpdatedUtc { get; set; }

        [JsonPropertyName("claims")]
        public Dictionary<string, DateTime> Claims { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
