/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using Microsoft.Extensions.Logging;

namespace SQLTriage.Data.Services.Licensing;

/// <summary>
/// Sealed implementation of <see cref="IBundleAccessor"/>.
/// Holds an immutable snapshot of the active <see cref="BundleManifest"/>.
/// Thread-safe: the manifest reference is replaced atomically under a lock;
/// all reads after the lock see the new snapshot.
///
/// Registered as a singleton. <see cref="LicenseService.Initialize"/> calls
/// <see cref="Replace"/> at startup; the same method is called on activation
/// or deactivation. Consumers subscribe to <see cref="BundleStateChanged"/>
/// to refresh their own cached state.
/// </summary>
public sealed class BundleAccessor : IBundleAccessor
{
    private readonly object _lock = new();
    private BundleManifest? _manifest;
    private Tier _tier = Tier.Free;

    /// <summary>
    /// Optional sink for the one line <see cref="Replace"/> writes when a
    /// <see cref="BundleStateChanged"/> subscriber throws. Optional because this type is built
    /// parameterless in dozens of tests; when it is null a failing subscriber is still isolated and
    /// still skipped, it is merely not recorded.
    /// </summary>
    private readonly ILogger<BundleAccessor>? _logger;

    /// <summary>Parameterless, for the existing call sites.</summary>
    public BundleAccessor() : this(null) { }

    /// <summary>DI resolves this overload and supplies the logger.</summary>
    public BundleAccessor(ILogger<BundleAccessor>? logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// The portal client-id rule: first char lowercase-alnum, then 1..40 of lowercase-alnum-or-hyphen
    /// (total length 2..41). Semantically identical to PortalPublishRunner.IsValidClientId.
    ///
    /// DUPLICATED rather than called because PortalPublishRunner lives under the GATED
    /// Data/Services/Portal/** tree, which is Compile-Removed from the community build; this
    /// accessor ships in BOTH builds and must not take a gated dependency.
    ///
    /// HAND-ROLLED, not a Regex, deliberately — mirroring that file's stated choice. A
    /// <c>^[a-z0-9][a-z0-9-]{1,40}$</c> Regex is NOT equivalent: .NET's <c>$</c> also matches
    /// before a trailing newline, so "acme\n" would pass the Regex but fail IsValidClientId.
    /// The two rules must not drift; this loop cannot.
    /// </summary>
    private static bool IsValidPortalClientId(string? id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        if (id.Length < 2 || id.Length > 41) return false;

        char first = id[0];
        if (!((first >= 'a' && first <= 'z') || (first >= '0' && first <= '9')))
            return false;

        for (int i = 1; i < id.Length; i++)
        {
            char c = id[i];
            bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-';
            if (!ok) return false;
        }

        return true;
    }

    // ── IBundleAccessor ─────────────────────────────────────────────────────

    /// <inheritdoc/>
    public bool IsUnlocked
    {
        get { lock (_lock) return _manifest is not null; }
    }

    /// <inheritdoc/>
    public Tier Tier
    {
        get { lock (_lock) return _tier; }
    }

    /// <inheritdoc/>
    public bool IsDemo
    {
        get
        {
            // One lock acquisition, not IsUnlocked + Features: those are two locks, and a Replace
            // landing between them would answer from two different bundles.
            //
            // Reads the RAW manifest string, deliberately, where the Features projection above
            // reads a PARSED DateTime. The two want opposite failure modes on a malformed value:
            // the projection must not treat an unparseable date as "expired" and downgrade a paid
            // allocation, so it drops it; this one must not treat an unparseable date as "not a
            // demo" and open six paid surfaces, so it counts the field's PRESENCE. Both directions
            // are the fail-closed one for their own consumer.
            lock (_lock)
                return _manifest is not null
                       && !string.IsNullOrWhiteSpace(_manifest.Features?.DemoExpiryUtc);
        }
    }

    /// <inheritdoc/>
    public string? ClientName
    {
        get
        {
            lock (_lock)
                return string.IsNullOrEmpty(_manifest?.ClientName) ? null : _manifest.ClientName;
        }
    }

    /// <inheritdoc/>
    public string? LicenseId
    {
        get
        {
            lock (_lock)
                return string.IsNullOrEmpty(_manifest?.Features?.LicenseId) ? null : _manifest.Features.LicenseId;
        }
    }

    /// <inheritdoc/>
    public int BuildNumber
    {
        get { lock (_lock) return _manifest?.BuildNumber ?? 0; }
    }

    /// <inheritdoc/>
    public BundleFeatures Features
    {
        get
        {
            lock (_lock)
            {
                if (_manifest is null)
                    // No bundle = "Not Activated": dev-tools fail CLOSED with everything else
                    // (2026-08-05 ruling — the claim is the mechanism, so no bundle means no
                    // claim means no authoring surface; --devbridge is the dev-build unlock).
                    // Remediation is a WRITE capability and fails CLOSED — no bundle, no writes.
                    // Corpus-demo fails CLOSED to the community limit (1) — no bundle must NOT
                    // grant unlimited corpus. DevBridge gets its own escape hatch in DemoRunLedger.
                    // Seats fail OPEN (null = unlimited): no bundle must not lock the server list.
                    return new BundleFeatures(false, false, false, Array.Empty<int>(),
                        DevToolsCapability: false, Remediation: false, RemediationCreditsPerServer: 0,
                        DemoCorpusInstancesPer24h: 1, DemoExpiryUtc: null,
                        InstanceSeats: null, InstanceSwapsAllowed: 2, PortalClientId: null,
                        DemoAllocationOrigin: DemoAllocationOrigin.Community);

                var f = _manifest.Features;

                // Corpus-demo allocation: signed knob, fail-closed to community (1) when absent.
                // Honour expiry — once DemoExpiryUtc has passed, a bumped allocation reverts to 1.
                int demoInstances = f.DemoCorpusInstancesPer24h ?? 1;
                // Remember whether that 1 was SIGNED or SUPPLIED BY THE `?? 1` ABOVE — and, when it
                // was supplied, WHICH KIND of install supplied it. Both live clients have been
                // re-minted with the field present, but an older PAID bundle in the wild still lands
                // on Unsigned and is told to ask for a re-mint rather than that it is the community
                // version.
                //
                // The tier test is load-bearing, not defensive. The community build loads a real
                // Free-tier manifest (LicenseService.LoadFreeBundle → Replace(manifest, Tier.Free)),
                // and that manifest carries no demoCorpusInstancesPer24h — so keying "Unsigned" on
                // the manifest merely being PRESENT sent every free user the re-mint apology, copy
                // written for a paying customer. A free install has no bundle to re-mint and no
                // relationship to invoke: it gets the community sentence. Only Tier.Full can be
                // Unsigned.
                var demoOrigin = f.DemoCorpusInstancesPer24h.HasValue
                    ? DemoAllocationOrigin.Signed
                    : _tier == Tier.Full
                        ? DemoAllocationOrigin.Unsigned
                        : DemoAllocationOrigin.Community;
                DateTime? demoExpiry = ParseUtc(f.DemoExpiryUtc);
                if (demoExpiry is { } exp && DateTime.UtcNow >= exp)
                    demoInstances = 1;

                // Seats: null/absent = UNLIMITED (fail-OPEN by ruling — see BundleManifest.InstanceSeats).
                // A NEGATIVE or zero signed value is nonsense we must not silently reinterpret as
                // "unlimited"; clamp to 0 = "no seats", which locks rather than grants. (A legitimate
                // no-seats licence is expressed by omitting the field, not by signing 0.)
                int? seats = f.InstanceSeats is { } s ? Math.Max(0, s) : (int?)null;
                int swaps = Math.Max(0, f.InstanceSwapsAllowed ?? 2);

                // Portal client id: validate the signed value against the SAME rule the publisher
                // enforces. A malformed id is treated as ABSENT (manual entry) rather than trusted —
                // it can only have come from a bundle we signed, so a bad shape is our bug, not an
                // attack, and degrading to manual entry is the honest failure.
                string? portalClientId = null;
                if (f.Portal is { } p && IsValidPortalClientId(p.ClientId))
                    portalClientId = p.ClientId;

                return new BundleFeatures(
                    f.RagEnabled,
                    f.SpBlitzImport,
                    f.FullCorpus,
                    f.CheckIds.AsReadOnly(),
                    // Fail-CLOSED: only an explicit true grants. A bundle minted before the claim
                    // existed carries null and no longer permits — that is the point of the
                    // 2026-08-05 flip, and it is why Adrian's own maintainer bundle has to be
                    // re-minted with --dev-capability (issue-license.ps1 -DevCapability).
                    DevToolsCapability: f.DevTools ?? false,
                    // WRITE capability — fail CLOSED: null/absent denies, only explicit true grants.
                    Remediation: f.Remediation ?? false,
                    RemediationCreditsPerServer: f.RemediationCreditsPerServer,
                    DemoCorpusInstancesPer24h: demoInstances,
                    DemoExpiryUtc: demoExpiry,
                    InstanceSeats: seats,
                    InstanceSwapsAllowed: swaps,
                    PortalClientId: portalClientId,
                    DemoAllocationOrigin: demoOrigin);
            }
        }
    }

    /// <inheritdoc/>
    public BundlePortalConfig? PortalConfig
    {
        get
        {
            lock (_lock)
            {
                var p = _manifest?.Features?.Portal;
                if (p is null) return null;

                // A block with neither field carries nothing to act on — treat it as absent so the
                // portal lane sees the plain "manual entry, unchanged" contract rather than an
                // empty-but-present block it would have to special-case.
                var cfg = new BundlePortalConfig(p.ClientId, p.EnrolmentToken);
                return cfg.ClientId.Length == 0 && !cfg.HasEnrolmentToken ? null : cfg;
            }
        }
    }

    /// <inheritdoc/>
    public bool IsCheckPermitted(int checkId)
    {
        lock (_lock)
        {
            if (_manifest is null) return false;

            // Full tier with an empty allow-list → all checks are permitted
            if (_tier == Tier.Full && _manifest.Features.CheckIds.Count == 0)
                return true;

            return _manifest.Features.CheckIds.Contains(checkId);
        }
    }

    /// <inheritdoc/>
    public string? GetText(string relativePath)
    {
#if DEBUG
        // Dev-build-only fallback: when running a Debug build from the repo tree, prefer the LOOSE
        // source file at <repo>/<relativePath> over the copy baked into free-bundle.dat. The bundle
        // is packed by an off-repo tool and can lag same-day source edits — and some packed files
        // (control_mappings.json, sql-build-catalogue.json) are deliberately NOT copied to bin, so
        // without this a dev/integration run keeps showing stale content until someone remembers to
        // re-bake the bundle. This branch is compiled out of every Release/client binary, so shipped
        // builds always read the signed bundle. Only files that genuinely exist loose in the repo
        // override; everything else falls through to the bundled copy unchanged.
        var loose = DevLooseSource.TryRead(relativePath);
        if (loose is not null) return loose;
#endif
        lock (_lock)
        {
            if (_manifest is null) return null;
            return _manifest.Files.TryGetValue(relativePath, out var text) ? text : null;
        }
    }

    /// <inheritdoc/>
    public byte[]? GetBytes(string relativePath)
    {
        var text = GetText(relativePath);
        return text is null ? null : System.Text.Encoding.UTF8.GetBytes(text);
    }

    /// <inheritdoc/>
    public IEnumerable<string> EnumerateCorpusYamlHandles()
    {
        lock (_lock)
        {
            if (_manifest is null) return Enumerable.Empty<string>();

            // Snapshot keys under lock to avoid mutation during enumeration
            var keys = _manifest.Corpus.Keys.ToList();
            return keys
                .Where(k => k.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || 
                            k.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    /// <inheritdoc/>
    public string? ReadCorpusYaml(string handle)
    {
        lock (_lock)
        {
            if (_manifest is null) return null;
            // Case-insensitive lookup matching the encryptor's OrdinalIgnoreCase convention
            var key = _manifest.Corpus.Keys
                .FirstOrDefault(k => string.Equals(k, handle, StringComparison.OrdinalIgnoreCase));
            return key is null ? null : _manifest.Corpus[key];
        }
    }

    /// <inheritdoc/>
    public string? ReadCorpusSqlFallback(string handle)
    {
        lock (_lock)
        {
            if (_manifest is null) return null;

            // Replace .yaml extension with .sql (same stem)
            var stem = System.IO.Path.GetFileNameWithoutExtension(handle);
            var sqlHandle = stem + ".sql";

            var key = _manifest.Corpus.Keys
                .FirstOrDefault(k => string.Equals(k, sqlHandle, StringComparison.OrdinalIgnoreCase));
            return key is null ? null : _manifest.Corpus[key];
        }
    }

    /// <inheritdoc/>
    public string? TryGetReportAsset(string reportId)
    {
        lock (_lock)
        {
            if (_manifest is null) return null;
            if (_tier == Tier.Free) return null; // Only Full tier has access to reports

            var key = _manifest.Reports.Keys
                .FirstOrDefault(k => string.Equals(k, reportId, StringComparison.OrdinalIgnoreCase));
            return key is null ? null : _manifest.Reports[key];
        }
    }

    /// <inheritdoc/>
    public IEnumerable<string> EnumerateReportHandles()
    {
        lock (_lock)
        {
            if (_manifest is null) return Enumerable.Empty<string>();
            if (_tier == Tier.Free) return Enumerable.Empty<string>();

            return _manifest.Reports.Keys.ToList();
        }
    }

    /// <inheritdoc/>
    public event EventHandler? BundleStateChanged;

    // ── Mutation API (used only by LicenseService) ──────────────────────────

    /// <summary>
    /// Replaces the active manifest + tier and fires <see cref="BundleStateChanged"/>.
    /// Pass <paramref name="newManifest"/> = null to reset to unlocked=false (no bundle).
    /// </summary>
    public void Replace(BundleManifest? newManifest, Tier tier)
    {
        lock (_lock)
        {
            _manifest = newManifest;
            _tier = tier;
        }

        // THE LICENCE OUTCOME DOES NOT DEPEND ON A SUBSCRIBER. A plain
        // BundleStateChanged?.Invoke(...) runs the whole invocation list on one stack. The first
        // handler that throws ends the list, so every subscriber registered after it never runs, and
        // the exception unwinds into LicenseService.TryActivate or Initialize — the two callers that
        // had already decided the licence question before reaching this line. The manifest swap
        // above is committed under the lock and is complete before any handler is entered, so the
        // decision is made and this fan-out is only notification. Each subscriber therefore gets its
        // own try/catch: a failing one is named by its target type and skipped, and the rest still run.
        var handlers = BundleStateChanged;
        if (handlers is null) return;

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler)handler).Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                // The TARGET type is what an engineer greps for. A static handler has no target, so
                // fall back to the declaring type rather than printing an empty name.
                var target = handler.Target?.GetType().FullName
                             ?? handler.Method.DeclaringType?.FullName
                             ?? "(unknown)";
                // Not `_logger?.LogError(...)`: LogError is an extension method and `?.` cannot
                // invoke one. The null test is explicit.
                if (_logger is not null)
                    _logger.LogError(ex,
                        "[BundleAccessor] Bundle-state subscriber {Target}.{Method} threw and was skipped. " +
                        "The licence state stands. The remaining subscribers still ran.",
                        target, handler.Method.Name);
            }
        }
    }

    /// <summary>
    /// Parses an ISO-8601 UTC timestamp (trailing Z) from the manifest, returning a UTC
    /// <see cref="DateTime"/> or null when absent/unparseable. Unparseable is treated as
    /// "no expiry" (the signed instance count still applies) — never as "expired", to avoid a
    /// malformed field silently downgrading a paid allocation.
    /// </summary>
    private static DateTime? ParseUtc(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return null;
        return DateTime.TryParse(
            iso, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
            out var dt) ? dt : null;
    }

#if DEBUG
    /// <summary>
    /// DEBUG-only helper backing <see cref="GetText"/>'s loose-source preference. Locates the repo
    /// root once (by walking up from the running exe to the <c>SQLTriage.csproj</c> marker) and,
    /// for a repo-relative bundle path, returns the current on-disk source when present. Compiled
    /// out entirely in Release — never reaches a client binary.
    /// </summary>
    private static class DevLooseSource
    {
        // Resolved once. null = repo root not found (running outside the source tree) → no override.
        private static readonly Lazy<string?> RepoRoot = new(FindRepoRoot);

        public static string? TryRead(string relativePath)
        {
            try
            {
                var root = RepoRoot.Value;
                if (root is null || string.IsNullOrWhiteSpace(relativePath)) return null;
                var full = System.IO.Path.Combine(
                    root, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
                return System.IO.File.Exists(full) ? System.IO.File.ReadAllText(full) : null;
            }
            catch
            {
                // A dev convenience must never throw into a caller — fall through to the bundle.
                return null;
            }
        }

        private static string? FindRepoRoot()
        {
            try
            {
                var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
                // Bounded walk up (bin/<cfg>/<tfm>/<rid> from the repo root is 4 levels; test bins
                // sit a couple deeper) — bounded so we never wander the whole disk on a stray layout.
                for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
                    if (System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "SQLTriage.csproj")))
                        return dir.FullName;
                return null;
            }
            catch
            {
                return null;
            }
        }
    }
#endif
}
