/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SQLTriage.Data.Services;

// BM:InstallProvenanceService.Class — decides ONCE whether this is a new or a pre-existing install
/// <summary>
/// Answers one question, once, and writes the answer down: is this a NEW install, or an
/// install that already existed before the admin-gate change shipped?
///
/// Why this exists: <see cref="AdminAuthService"/> used to treat "no password configured"
/// as "everyone is an admin". Making that fail closed unconditionally would lock live
/// clients out of the admin write path on their next upgrade. Adrian's ruling (2026-07-19)
/// is fail-closed on NEW installs only.
///
/// The discriminator is NOT the absence of the password — that cannot tell a fresh install
/// from an existing one, which is the whole problem. It is the presence of durable state in
/// the per-user state directory (<c>%APPDATA%\SQLTriage</c>) that only a PRIOR RUN of the
/// application could have written: user-settings.json, portal-settings.json, public-profile.json,
/// licence/seat state, and so on. A genuinely fresh install has an empty (or absent) state
/// directory at the moment this first runs.
///
/// The verdict is stamped to <c>install-provenance.json</c> the first time it is computed, so
/// a new install that later accumulates state does not silently become "existing" — the
/// determination is made once and is durable thereafter.
///
/// KNOWN RESIDUAL (deliberate, and biased to the safe side): if a fresh install is dropped onto
/// a machine that still carries <c>%APPDATA%\SQLTriage</c> from a previous uninstall, or if some
/// other component writes into that directory during startup before this class is first asked,
/// the install is treated as EXISTING. That leaves the gate open with a loud, persistent warning
/// rather than locking a real client out. Every failure mode here resolves toward
/// "open + warn", never toward "locked out".
/// </summary>
public sealed class InstallProvenanceService
{
    /// <summary>Name of the stamped verdict file inside the state directory.</summary>
    public const string MarkerFileName = "install-provenance.json";

    private const int SchemaVersion = 1;

    private readonly string _stateDir;
    private readonly string _markerPath;
    private readonly object _lock = new();

    private bool _resolved;
    private bool _isExistingInstall;
    private string _reason = "not resolved";

    /// <param name="stateDir">
    /// Per-user state directory. Defaults to <c>%APPDATA%\SQLTriage</c> — the same directory
    /// <see cref="SQLTriage.Data.UserSettingsService"/> uses. Injectable for tests.
    /// </param>
    public InstallProvenanceService(string? stateDir = null)
    {
        if (stateDir is null)
        {
            var realDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SQLTriage");

            // Same runtime chokepoint as UserSettingsService — this service defaults to the SAME
            // real per-user directory, so it is the same category even though no test binds it
            // today. Guarding it now is what stops the category quietly reopening through the
            // second door. See SQLTriage.Data.RealUserProfileGuard.
            SQLTriage.Data.RealUserProfileGuard.RefuseRealProfileUnderTest(
                nameof(InstallProvenanceService), realDir,
                "new InstallProvenanceService(myTempDir)");

            _stateDir = realDir;
        }
        else
        {
            _stateDir = stateDir;
        }

        _markerPath = Path.Combine(_stateDir, MarkerFileName);
    }

    /// <summary>
    /// True when this install predates the admin gate and must keep working without a password.
    /// False only when this is provably a fresh install, in which case the admin write path is
    /// closed until a password is set.
    /// </summary>
    public bool IsExistingInstall
    {
        get { Resolve(); return _isExistingInstall; }
    }

    /// <summary>Human-readable account of how <see cref="IsExistingInstall"/> was decided.</summary>
    public string Reason
    {
        get { Resolve(); return _reason; }
    }

    private void Resolve()
    {
        lock (_lock)
        {
            if (_resolved) return;

            try
            {
                // 1. Already stamped? The verdict is durable — never recompute it.
                if (File.Exists(_markerPath))
                {
                    var stamped = ReadMarker(_markerPath);
                    if (stamped.HasValue)
                    {
                        _isExistingInstall = stamped.Value;
                        _reason = $"stamped verdict read from {MarkerFileName}";
                        _resolved = true;
                        return;
                    }
                    // Marker present but unreadable/corrupt: fall through and re-derive, then
                    // overwrite it. Re-deriving is safe because the evidence probe below is
                    // itself biased toward "existing".
                }

                // 2. Not stamped yet. Probe for durable evidence of a prior run.
                var evidence = FindEvidenceOfPriorRun();
                _isExistingInstall = evidence is not null;
                _reason = evidence is not null
                    ? $"prior-run evidence in state directory: {evidence}"
                    : "state directory is absent or empty — fresh install";

                WriteMarker(_isExistingInstall, _reason);
            }
            catch (Exception ex)
            {
                // Cannot read or write the state directory at all (permissions, locked file,
                // read-only volume). Refuse to lock anyone out on the strength of an IO error:
                // treat as EXISTING so the gate stays open, and let the caller's persistent
                // "gate is unset" warning carry the message.
                _isExistingInstall = true;
                _reason = $"provenance undetermined ({ex.GetType().Name}) — defaulting to existing install so no one is locked out";
                Serilog.Log.Warning(ex,
                    "[InstallProvenance] Could not determine install provenance; treating as an existing install (gate stays open, warning shown)");
            }
            finally
            {
                _resolved = true;
            }
        }
    }

    /// <summary>
    /// Returns the name of the first artefact proving the application has run here before,
    /// or null if the state directory holds nothing but (possibly) our own marker.
    /// </summary>
    private string? FindEvidenceOfPriorRun()
    {
        if (!Directory.Exists(_stateDir)) return null;

        // Any entry other than our own marker is evidence: every file in this directory is
        // written by the application itself, so its existence means the application ran here.
        // Deliberately broad — a false "existing" leaves the gate open with a warning, whereas
        // a false "new" locks a paying client out of their own admin page.
        var entry = Directory
            .EnumerateFileSystemEntries(_stateDir)
            .Select(Path.GetFileName)
            .FirstOrDefault(name =>
                !string.Equals(name, MarkerFileName, StringComparison.OrdinalIgnoreCase));

        return entry;
    }

    private static bool? ReadMarker(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("existingInstall", out var v) &&
                (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False))
            {
                return v.GetBoolean();
            }
        }
        catch (JsonException) { /* corrupt — caller re-derives */ }
        catch (IOException) { /* transient — caller re-derives */ }
        return null;
    }

    private void WriteMarker(bool existingInstall, string reason)
    {
        Directory.CreateDirectory(_stateDir);
        var payload = JsonSerializer.Serialize(new
        {
            schema = SchemaVersion,
            existingInstall,
            reason,
            stampedUtc = DateTime.UtcNow.ToString("o"),
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_markerPath, payload);
    }
}
