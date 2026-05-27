/* In the name of God, the Merciful, the Compassionate */

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Konscious.Security.Cryptography;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services
{
    // BM:RbacService.Class — role-based access control for users and OAuth providers
    /// <summary>
    /// Role-based access control service. Manages user-role mappings and OAuth
    /// provider configuration. Persists to Config/rbac-config.json and Config/rbac-users.json.
    ///
    /// In WPF mode, the local Windows user is treated as Admin (single-user desktop app).
    /// In Server Mode (Kestrel), RBAC is enforced via OAuth + cookie authentication.
    /// </summary>
    public class RbacService
    {
        private readonly ILogger<RbacService> _logger;
        private readonly string _configPath;
        private readonly string _usersPath;
        private readonly object _lock = new();
        private RbacConfig _config = new();
        private List<RbacUser> _users = new();
        private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

        public RbacConfig Config
        {
            get { lock (_lock) return _config; }
        }

        public event Action? OnConfigChanged;

        public RbacService(ILogger<RbacService> logger)
            : this(logger,
                   Path.Combine(AppContext.BaseDirectory, "Config", "rbac-config.json"),
                   Path.Combine(AppContext.BaseDirectory, "Config", "rbac-users.json"))
        {
        }

        // Test seam: lets callers point at arbitrary files (used by RbacServiceTests).
        public RbacService(ILogger<RbacService> logger, string configPath, string usersPath)
        {
            _logger = logger;
            _configPath = configPath;
            _usersPath = usersPath;
            LoadConfig();
            LoadUsers();
        }

        // ── Configuration ────────────────────────────────────────────────

        public void UpdateConfig(RbacConfig config)
        {
            lock (_lock)
            {
                // Encrypt OAuth client secrets before persisting
                EncryptSecrets(config);
                _config = config;
                SaveConfig();
            }
            OnConfigChanged?.Invoke();
        }

        private void EncryptSecrets(RbacConfig config)
        {
            if (!string.IsNullOrEmpty(config.Google.ClientSecret)
                && !CredentialProtector.IsEncrypted(config.Google.ClientSecret))
                config.Google.ClientSecret = CredentialProtector.Encrypt(config.Google.ClientSecret);

            if (!string.IsNullOrEmpty(config.Microsoft.ClientSecret)
                && !CredentialProtector.IsEncrypted(config.Microsoft.ClientSecret))
                config.Microsoft.ClientSecret = CredentialProtector.Encrypt(config.Microsoft.ClientSecret);
        }

        private void LoadConfig()
        {
            try
            {
                _config = ConfigFileHelper.Load<RbacConfig>(_configPath, _jsonOptions);
                _logger.LogInformation("RBAC config loaded: Enabled={Enabled}, Google={Google}, Microsoft={Microsoft}",
                    _config.Enabled, _config.Google.Enabled, _config.Microsoft.Enabled);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load RBAC config");
                _config = new();
            }
        }

        private void SaveConfig()
        {
            try
            {
                ConfigFileHelper.Save(_configPath, _config, _jsonOptions);
                _logger.LogInformation("RBAC config saved");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save RBAC config");
            }
        }

        // ── User Management ──────────────────────────────────────────────

        public List<RbacUser> GetUsers()
        {
            lock (_lock) return _users.ToList();
        }

        public RbacUser? GetUserByEmail(string email)
        {
            lock (_lock)
                return _users.FirstOrDefault(u =>
                    u.Email.Equals(email, StringComparison.OrdinalIgnoreCase));
        }

        public void AddUser(RbacUser user)
        {
            lock (_lock)
            {
                // Prevent duplicates by email
                if (_users.Any(u => u.Email.Equals(user.Email, StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogWarning("User with email {Email} already exists", user.Email);
                    return;
                }
                _users.Add(user);
                SaveUsers();
            }
            _logger.LogInformation("Added RBAC user: {Email} as {Role}", user.Email, user.Role);
        }

        public void UpdateUser(RbacUser user)
        {
            lock (_lock)
            {
                var index = _users.FindIndex(u => u.Id == user.Id);
                if (index >= 0)
                {
                    _users[index] = user;
                    SaveUsers();
                    _logger.LogInformation("Updated RBAC user: {Email} → role={Role}, enabled={Enabled}",
                        user.Email, user.Role, user.Enabled);
                }
            }
        }

        public void RemoveUser(string id)
        {
            lock (_lock)
            {
                var user = _users.FirstOrDefault(u => u.Id == id);
                if (user != null)
                {
                    _users.Remove(user);
                    SaveUsers();
                    _logger.LogInformation("Removed RBAC user: {Email}", user.Email);
                }
            }
        }

        /// <summary>
        /// Records a login event for the given email. Creates a new user record
        /// with the default role if RequireExplicitAccess is false and the user
        /// doesn't exist yet.
        /// </summary>
        /// <returns>The user if login is allowed, null if denied.</returns>
        public RbacUser? RecordLogin(string email, string displayName, string provider)
        {
            lock (_lock)
            {
                var user = _users.FirstOrDefault(u =>
                    u.Email.Equals(email, StringComparison.OrdinalIgnoreCase));

                if (user == null)
                {
                    if (_config.RequireExplicitAccess)
                    {
                        _logger.LogWarning("Login denied for {Email} — not in RBAC user list", email);
                        return null;
                    }

                    // Auto-create with default role
                    user = new RbacUser
                    {
                        Email = email,
                        DisplayName = displayName,
                        Provider = provider,
                        Role = _config.DefaultRole,
                        CreatedAt = DateTime.UtcNow
                    };
                    _users.Add(user);
                    _logger.LogInformation("Auto-created RBAC user: {Email} as {Role}", email, user.Role);
                }

                if (!user.Enabled)
                {
                    _logger.LogWarning("Login denied for {Email} — account disabled", email);
                    return null;
                }

                user.LastLogin = DateTime.UtcNow;
                if (!string.IsNullOrEmpty(displayName))
                    user.DisplayName = displayName;

                SaveUsers();
                return user;
            }
        }

        // ── Authorization Checks ─────────────────────────────────────────

        /// <summary>
        /// Checks if a user with the given role has permission to perform an action.
        /// </summary>
        public static bool HasPermission(string role, string permission)
        {
            // Normalise both inputs so callers with mixed casing are handled consistently.
            var normRole = role?.Trim().ToLowerInvariant() ?? "";
            var normPerm = permission?.Trim().ToLowerInvariant() ?? "";

            return normPerm switch
            {
                // Admin-only operations
                "settings" or "manage_servers" or "manage_users" or "manage_alerts"
                    => normRole == AppRoles.Admin,

                // Admin + Operator
                "execute_checks" or "run_scripts" or "export_data" or "acknowledge_alerts"
                    => normRole is AppRoles.Admin or AppRoles.Operator,

                // Everyone (including viewer)
                "view_dashboard" or "view_results" or "view_audit_log"
                    => true,

                // Unknown permissions default to admin-only
                _ => normRole == AppRoles.Admin
            };
        }

        /// <summary>
        /// Returns the role for the current desktop user (WPF mode = always Admin).
        /// </summary>
        public string GetDesktopUserRole() => AppRoles.Admin;

        /// <summary>
        /// True when RBAC enforcement is active: the feature is explicitly enabled
        /// AND at least one Admin user exists. Until an admin is configured, RBAC
        /// stays dormant so a single-user / unconfigured install is never locked
        /// out of the Settings page it needs to configure RBAC in the first place.
        /// </summary>
        public bool IsRbacEnforced()
        {
            lock (_lock)
            {
                if (!_config.Enabled) return false;
                return _users.Any(u =>
                    u.Enabled &&
                    string.Equals(u.Role, AppRoles.Admin, StringComparison.OrdinalIgnoreCase));
            }
        }

        /// <summary>
        /// Authorization gate for UI pages. Returns true (allow) when RBAC is not
        /// being enforced — i.e. single-user / no-admin-configured installs behave
        /// as full admin. When RBAC is enforced, defers to the permission matrix.
        /// All Admin-gated pages should call this rather than HasPermission directly.
        /// </summary>
        public bool IsAuthorized(string role, string permission)
        {
            if (!IsRbacEnforced()) return true;
            return HasPermission(role, permission);
        }

        // ── Local password authentication (Argon2id) ─────────────────────
        //
        // Format: argon2id$v=19$m=19456,t=2,p=1$<salt-b64>$<hash-b64>
        // Parameters follow OWASP 2024 recommendations (19 MiB memory, 2 iterations, 1 lane).
        // Salt: 16 bytes random. Hash: 32 bytes. Verification is constant-time.

        private const int Argon2MemoryKib = 19456;   // 19 MiB
        private const int Argon2Iterations = 2;
        private const int Argon2Parallelism = 1;
        private const int Argon2SaltBytes = 16;
        private const int Argon2HashBytes = 32;

        /// <summary>
        /// Hashes a password with Argon2id. Returns a self-describing string
        /// that can be stored as-is and later passed to <see cref="VerifyPassword"/>.
        /// </summary>
        public static string HashPassword(string password)
        {
            if (string.IsNullOrEmpty(password))
                throw new ArgumentException("Password cannot be empty", nameof(password));

            var salt = RandomNumberGenerator.GetBytes(Argon2SaltBytes);
            var hash = ComputeArgon2(password, salt);
            return $"argon2id$v=19$m={Argon2MemoryKib},t={Argon2Iterations},p={Argon2Parallelism}$" +
                   $"{Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
        }

        /// <summary>
        /// Verifies a password against a stored Argon2id hash in constant time.
        /// Returns false for malformed hashes; never throws on user input.
        /// </summary>
        public static bool VerifyPassword(string password, string storedHash)
        {
            if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(storedHash))
                return false;

            var parts = storedHash.Split('$');
            if (parts.Length != 5 || parts[0] != "argon2id") return false;

            try
            {
                var salt = Convert.FromBase64String(parts[3]);
                var expected = Convert.FromBase64String(parts[4]);
                var actual = ComputeArgon2(password, salt);
                return CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            catch
            {
                return false;
            }
        }

        private static byte[] ComputeArgon2(string password, byte[] salt)
        {
            using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
            {
                Salt = salt,
                DegreeOfParallelism = Argon2Parallelism,
                MemorySize = Argon2MemoryKib,
                Iterations = Argon2Iterations
            };
            return argon2.GetBytes(Argon2HashBytes);
        }

        /// <summary>
        /// Sets a password for a user. The plaintext is hashed with Argon2id
        /// before storage; plaintext is never persisted.
        /// </summary>
        public void SetPassword(string email, string password)
        {
            if (string.IsNullOrEmpty(password))
                throw new ArgumentException("Password cannot be empty", nameof(password));

            var hash = HashPassword(password);
            lock (_lock)
            {
                var user = _users.FirstOrDefault(u =>
                    u.Email.Equals(email, StringComparison.OrdinalIgnoreCase));
                if (user != null)
                {
                    user.PasswordHash = hash;
                    SaveUsers();
                    _logger.LogInformation("Password set for user: {Email}", email);
                }
                else
                {
                    _logger.LogWarning("SetPassword called for unknown user: {Email}", email);
                }
            }
        }

        /// <summary>
        /// Validates a local-auth login. Returns the user if the password matches
        /// and the account is enabled; null otherwise. Runs in roughly constant
        /// time regardless of whether the user exists (to limit enumeration).
        /// </summary>
        public RbacUser? ValidateLocalLogin(string email, string password)
        {
            RbacUser? user;
            string? storedHash;

            lock (_lock)
            {
                user = _users.FirstOrDefault(u =>
                    u.Email.Equals(email, StringComparison.OrdinalIgnoreCase));
                storedHash = user?.PasswordHash;
            }

            // Always run the Argon2 work — even on miss — so timing does not
            // reveal whether the email exists. The dummy hash is well-formed but
            // never produced by HashPassword for any real password.
            if (string.IsNullOrEmpty(storedHash))
            {
                _ = VerifyPassword(password, "argon2id$v=19$m=19456,t=2,p=1$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");
                return null;
            }

            if (!VerifyPassword(password, storedHash))
                return null;

            if (user == null || !user.Enabled)
                return null;

            lock (_lock)
            {
                user.LastLogin = DateTime.UtcNow;
                SaveUsers();
            }
            return user;
        }

        /// <summary>Checks if a user has the specified role.</summary>
        public bool HasRole(string email, string role)
        {
            lock (_lock)
            {
                var user = _users.FirstOrDefault(u =>
                    u.Email.Equals(email, StringComparison.OrdinalIgnoreCase));
                return user != null && user.Role.Equals(role, StringComparison.OrdinalIgnoreCase);
            }
        }

        // ── Persistence ──────────────────────────────────────────────────

        private void LoadUsers()
        {
            try
            {
                _users = ConfigFileHelper.Load<List<RbacUser>>(_usersPath, _jsonOptions);
                _logger.LogInformation("Loaded {Count} RBAC users", _users.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load RBAC users");
                _users = new();
            }
        }

        private void SaveUsers()
        {
            try
            {
                ConfigFileHelper.Save(_usersPath, _users, _jsonOptions);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save RBAC users");
            }
        }
    }
}
