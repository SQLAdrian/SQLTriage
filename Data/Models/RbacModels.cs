/* In the name of God, the Merciful, the Compassionate */

using System.Text.Json.Serialization;

namespace SqlHealthAssessment.Data.Models
{
    /// <summary>
    /// Defines the roles available in the application.
    /// </summary>
    public static class AppRoles
    {
        /// <summary>Full access: settings, server management, check editing, data export.</summary>
        public const string Admin = "admin";

        /// <summary>Can view dashboards, run checks, view results. Cannot modify settings or servers.</summary>
        public const string Operator = "operator";

        /// <summary>Read-only: can view dashboards and results. Cannot run checks or export.</summary>
        public const string Viewer = "viewer";

        public static readonly string[] All = { Admin, Operator, Viewer };

        public static bool IsValid(string role) =>
            All.Contains(role, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A user-to-role mapping, persisted in Config/rbac-users.json.
    /// </summary>
    public class RbacUser
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>Email address from the identity provider (Google/Microsoft).</summary>
        [JsonPropertyName("email")]
        public string Email { get; set; } = string.Empty;

        /// <summary>Display name from the identity provider.</summary>
        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>Identity provider: "google", "microsoft", or "local".</summary>
        [JsonPropertyName("provider")]
        public string Provider { get; set; } = "local";

        /// <summary>Assigned role: admin, operator, viewer.</summary>
        [JsonPropertyName("role")]
        public string Role { get; set; } = AppRoles.Viewer;

        /// <summary>Whether this user is allowed to log in.</summary>
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        /// <summary>When the user was added.</summary>
        [JsonPropertyName("createdAt")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>Last login timestamp.</summary>
        [JsonPropertyName("lastLogin")]
        public DateTime? LastLogin { get; set; }
    }

    /// <summary>
    /// OAuth provider configuration stored in Config/rbac-config.json.
    /// </summary>
    public class RbacConfig
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// When true, users not in the rbac-users list are denied access.
        /// When false, unknown users are assigned the default role.
        /// </summary>
        [JsonPropertyName("requireExplicitAccess")]
        public bool RequireExplicitAccess { get; set; } = true;

        /// <summary>Role assigned to users not explicitly listed (when RequireExplicitAccess is false).</summary>
        [JsonPropertyName("defaultRole")]
        public string DefaultRole { get; set; } = AppRoles.Viewer;

        [JsonPropertyName("google")]
        public OAuthProviderConfig Google { get; set; } = new();

        [JsonPropertyName("microsoft")]
        public OAuthProviderConfig Microsoft { get; set; } = new();
    }

    public class OAuthProviderConfig
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = false;

        [JsonPropertyName("clientId")]
        public string ClientId { get; set; } = string.Empty;

        /// <summary>Encrypted via CredentialProtector.</summary>
        [JsonPropertyName("clientSecret")]
        public string ClientSecret { get; set; } = string.Empty;

        /// <summary>Optional: restrict to a specific domain (e.g., "contoso.com").</summary>
        [JsonPropertyName("allowedDomain")]
        public string AllowedDomain { get; set; } = string.Empty;
    }
}
