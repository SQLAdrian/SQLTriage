/* In the name of God, the Merciful, the Compassionate */

using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace SQLTriage.Data.Services;

// BM:AdminAuthService.Class — PBKDF2-SHA256 admin authentication with rate limiting
/// <summary>
/// PBKDF2-SHA256 based admin authentication.
/// Hash and salt are stored in appsettings.json under AdminAuth:Hash and AdminAuth:Salt.
/// Even with source code access, a correct password is required because the hash
/// is derived from a per-installation random salt stored only in the deployment config.
///
/// When NO hash is configured the behaviour depends on install provenance
/// (<see cref="InstallProvenanceService"/>), per Adrian's ruling of 2026-07-19:
///  • NEW install  → <see cref="RequiresSetup"/> is true, <see cref="IsUnlocked"/> is FALSE.
///    The admin write path is closed until a password is set. Fail closed.
///  • EXISTING install → <see cref="IsOpenUnprotected"/> is true and the area stays open,
///    so an upgrade never locks a live client out — but callers MUST surface the
///    persistent warning that the gate is unset.
/// </summary>
public class AdminAuthService
{
    private readonly IConfiguration _config;
    private readonly InstallProvenanceService _provenance;
    private bool _sessionUnlocked;

    // Credentials set during this process (first-run setup) take precedence over config,
    // because IConfiguration will not necessarily observe our appsettings.json write in time.
    private string? _runtimeHash;
    private string? _runtimeSalt;

    /// <summary>Shortest password accepted by <see cref="SetInitialPassword"/>.</summary>
    public const int MinimumPasswordLength = 8;

    // ── Rate limiting ──
    private int _failedAttempts;
    private DateTime _lockoutUntil = DateTime.MinValue;
    private const int MaxAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(1);

    // Test seam (InternalsVisibleTo SQLTriage.Tests): lets a test point the persistence path at
    // its own appsettings.json instead of the one under AppContext.BaseDirectory.
    private readonly string? _configPathOverride;

    public AdminAuthService(IConfiguration config, InstallProvenanceService provenance)
        : this(config, provenance, null) { }

    internal AdminAuthService(IConfiguration config, InstallProvenanceService provenance, string? configPathOverride)
    {
        _config = config;
        _provenance = provenance;
        _configPathOverride = configPathOverride;
    }

    private string? EffectiveHash => _runtimeHash ?? _config["AdminAuth:Hash"];
    private string? EffectiveSalt => _runtimeSalt ?? _config["AdminAuth:Salt"];

    /// <summary>True when a hash is present (in config, or set during this session).</summary>
    public bool HasPassword =>
        !string.IsNullOrWhiteSpace(EffectiveHash) &&
        !string.IsNullOrWhiteSpace(EffectiveSalt);

    /// <summary>
    /// True on a fresh install with no password configured: the admin area is CLOSED and the
    /// only way through is to set a password.
    /// </summary>
    public bool RequiresSetup => !HasPassword && !_provenance.IsExistingInstall;

    /// <summary>
    /// True on a pre-existing install with no password configured: the admin area is open,
    /// unprotected, and callers must show a persistent warning saying so.
    /// </summary>
    public bool IsOpenUnprotected => !HasPassword && _provenance.IsExistingInstall;

    /// <summary>
    /// True when the admin area may be entered: a correct password was supplied this session,
    /// or this is a grandfathered install running without a password.
    /// A fresh install with no password is NOT unlocked.
    /// </summary>
    public bool IsUnlocked => HasPassword ? _sessionUnlocked : IsOpenUnprotected;

    /// <summary>True when too many failed attempts have triggered a lockout.</summary>
    public bool IsLockedOut => _lockoutUntil > DateTime.UtcNow;

    /// <summary>Remaining lockout time, or zero if not locked out.</summary>
    public TimeSpan LockoutRemaining => IsLockedOut ? _lockoutUntil - DateTime.UtcNow : TimeSpan.Zero;

    /// <summary>Verify the supplied password and unlock the session if correct.</summary>
    public bool Unlock(string password)
    {
        // Fresh install, no password set: there is nothing to verify and nothing to bypass.
        // Setting a password is the only way in.
        if (RequiresSetup)
        {
            Serilog.Log.Warning("[AdminAuth] Unlock refused — no admin password is set on this new install; setup is required");
            return false;
        }

        if (!HasPassword) { _sessionUnlocked = true; return true; }

        // Enforce lockout
        if (IsLockedOut)
        {
            Serilog.Log.Warning("[AdminAuth] Unlock attempt rejected — locked out for {Remaining:N0}s", LockoutRemaining.TotalSeconds);
            return false;
        }

        var hash = EffectiveHash!;
        var salt = EffectiveSalt!;
        var computed = ComputeHash(password, Convert.FromBase64String(salt));
        _sessionUnlocked = CryptographicOperations.FixedTimeEquals(
            Convert.FromBase64String(hash),
            Convert.FromBase64String(computed));

        if (_sessionUnlocked)
        {
            _failedAttempts = 0;
        }
        else
        {
            _failedAttempts++;
            Serilog.Log.Warning("[AdminAuth] Failed unlock attempt {Attempt}/{Max}", _failedAttempts, MaxAttempts);
            if (_failedAttempts >= MaxAttempts)
            {
                _lockoutUntil = DateTime.UtcNow + LockoutDuration;
                Serilog.Log.Warning("[AdminAuth] Locked out for {Duration} after {Attempts} failed attempts",
                    LockoutDuration, _failedAttempts);
            }
        }

        return _sessionUnlocked;
    }

    /// <summary>
    /// Sets the admin password for an installation that has none — the first-run setup path,
    /// and also the remedy offered to a grandfathered install running unprotected.
    /// Persists Hash/Salt to Config/appsettings.json and unlocks the current session.
    /// Refuses if a password is already configured (changing a known password is a different
    /// operation and is not offered here).
    /// </summary>
    /// <returns>null on success, otherwise a message explaining the refusal.</returns>
    public string? SetInitialPassword(string password)
    {
        if (HasPassword)
            return "An admin password is already configured.";
        if (string.IsNullOrWhiteSpace(password) || password.Length < MinimumPasswordLength)
            return $"Password must be at least {MinimumPasswordLength} characters.";

        var (hash, salt) = HashPassword(password);

        try
        {
            PersistToAppSettings(hash, salt, _configPathOverride);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[AdminAuth] Could not persist the new admin password to appsettings.json");
            return "Could not save the password to Config/appsettings.json. Check file permissions and try again.";
        }

        _runtimeHash = hash;
        _runtimeSalt = salt;
        _sessionUnlocked = true;
        _failedAttempts = 0;
        _lockoutUntil = DateTime.MinValue;
        Serilog.Log.Information("[AdminAuth] Admin password set; the admin write path is now protected");
        return null;
    }

    /// <summary>Lock the current session.</summary>
    public void Lock() => _sessionUnlocked = false;

    /// <summary>Hash a new password and return (base64Hash, base64Salt) for storing in config.</summary>
    public static (string Hash, string Salt) HashPassword(string password)
    {
        var saltBytes = RandomNumberGenerator.GetBytes(32);
        var hash = ComputeHash(password, saltBytes);
        return (hash, Convert.ToBase64String(saltBytes));
    }

    private static string ComputeHash(string password, byte[] salt)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, 200_000, HashAlgorithmName.SHA256);
        return Convert.ToBase64String(pbkdf2.GetBytes(32));
    }

    /// <summary>
    /// Rewrites the AdminAuth section of Config/appsettings.json, preserving every other section
    /// verbatim. Mirrors the approach already used by AzureBlobExportService.SaveToConfig().
    /// </summary>
    internal static void PersistToAppSettings(string hash, string salt, string? configPath = null)
    {
        configPath ??= Path.Combine(AppContext.BaseDirectory, "Config", "appsettings.json");
        if (!File.Exists(configPath))
            throw new FileNotFoundException("appsettings.json not found", configPath);

        using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
        var root = new Dictionary<string, object>();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            root[prop.Name] = prop.Name == "AdminAuth"
                ? new Dictionary<string, object> { ["Hash"] = hash, ["Salt"] = salt }
                : prop.Value;
        }
        if (!root.ContainsKey("AdminAuth"))
            root["AdminAuth"] = new Dictionary<string, object> { ["Hash"] = hash, ["Salt"] = salt };

        File.WriteAllText(configPath,
            JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true }));
    }
}
