/* In the name of God, the Merciful, the Compassionate */

using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SqlHealthAssessment.Data.Models;

namespace SqlHealthAssessment.Data.Services
{
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
        {
            _logger = logger;
            _configPath = Path.Combine(AppContext.BaseDirectory, "Config", "rbac-config.json");
            _usersPath = Path.Combine(AppContext.BaseDirectory, "Config", "rbac-users.json");
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
            return permission switch
            {
                // Admin-only operations
                "settings" or "manage_servers" or "manage_users" or "manage_alerts"
                    => role == AppRoles.Admin,

                // Admin + Operator
                "execute_checks" or "run_scripts" or "export_data" or "acknowledge_alerts"
                    => role is AppRoles.Admin or AppRoles.Operator,

                // Everyone (including viewer)
                "view_dashboard" or "view_results" or "view_audit_log"
                    => true,

                // Unknown permissions default to admin-only
                _ => role == AppRoles.Admin
            };
        }

        /// <summary>
        /// Returns the role for the current desktop user (WPF mode = always Admin).
        /// </summary>
        public string GetDesktopUserRole() => AppRoles.Admin;

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
