/* In the name of God, the Merciful, the Compassionate */

using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;

namespace SQLTriage.Data.Services;

/// <summary>
/// PBKDF2-SHA256 based admin authentication.
/// Hash and salt are stored in appsettings.json under AdminAuth:Hash and AdminAuth:Salt.
/// If no hash is configured the admin area is always accessible.
/// Even with source code access, a correct password is required because the hash
/// is derived from a per-installation random salt stored only in the deployment config.
/// </summary>
public class AdminAuthService
{
    private readonly IConfiguration _config;
    private bool _sessionUnlocked;

    // ── Rate limiting ──
    private int _failedAttempts;
    private DateTime _lockoutUntil = DateTime.MinValue;
    private const int MaxAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(1);

    public AdminAuthService(IConfiguration config)
    {
        _config = config;
    }

    /// <summary>True when no password is configured, or the session has been unlocked.</summary>
    public bool IsUnlocked => !HasPassword || _sessionUnlocked;

    /// <summary>True when a hash is present in config.</summary>
    public bool HasPassword =>
        !string.IsNullOrWhiteSpace(_config["AdminAuth:Hash"]) &&
        !string.IsNullOrWhiteSpace(_config["AdminAuth:Salt"]);

    /// <summary>True when too many failed attempts have triggered a lockout.</summary>
    public bool IsLockedOut => _lockoutUntil > DateTime.UtcNow;

    /// <summary>Remaining lockout time, or zero if not locked out.</summary>
    public TimeSpan LockoutRemaining => IsLockedOut ? _lockoutUntil - DateTime.UtcNow : TimeSpan.Zero;

    /// <summary>Verify the supplied password and unlock the session if correct.</summary>
    public bool Unlock(string password)
    {
        if (!HasPassword) { _sessionUnlocked = true; return true; }

        // Enforce lockout
        if (IsLockedOut)
        {
            Serilog.Log.Warning("[AdminAuth] Unlock attempt rejected — locked out for {Remaining:N0}s", LockoutRemaining.TotalSeconds);
            return false;
        }

        var hash = _config["AdminAuth:Hash"]!;
        var salt = _config["AdminAuth:Salt"]!;
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
}
