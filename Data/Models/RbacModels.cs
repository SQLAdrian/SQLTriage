/* In the name of God, the Merciful, the Compassionate */

using System.Text.Json.Serialization;

namespace SQLTriage.Data.Models
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
    /// Identity providers a user record can be bound to, and the provider CLASSES that
    /// decide how two identity keys are compared.
    ///
    /// <para>Matching is by (provider-class, key) — never by the shape of the string. A
    /// UPN-form Windows account <c>adrian@contoso.com</c> and a Google identity
    /// <c>adrian@contoso.com</c> are the same text and different principals; sniffing the
    /// shape would conflate them and silently hand one principal the other's role.</para>
    /// </summary>
    public static class AuthProviders
    {
        public const string Google = "google";
        public const string Microsoft = "microsoft";
        public const string Local = "local";

        /// <summary>Windows/Negotiate (Kerberos or NTLM). Key is a down-level DOMAIN\user name.</summary>
        public const string Windows = "windows";

        public static readonly string[] All = { Google, Microsoft, Local, Windows };

        /// <summary>Identity keys compared as email addresses (ordinal, case-insensitive).</summary>
        public const string ClassEmail = "email";

        /// <summary>Identity keys compared as Windows account names (domain + account, case-insensitive).</summary>
        public const string ClassWindows = "windows";

        /// <summary>
        /// The comparison class for a provider. Anything unrecognised is treated as an
        /// email-class provider, which is what every pre-Windows user record was.
        /// </summary>
        public static string ClassOf(string? provider) =>
            IsWindows(provider) ? ClassWindows : ClassEmail;

        /// <summary>
        /// True for the Windows provider. Tolerant of the raw scheme names ASP.NET Core
        /// hands back on <c>Identity.AuthenticationType</c> — "Negotiate", "NTLM", "Kerberos".
        /// </summary>
        public static bool IsWindows(string? provider)
        {
            if (string.IsNullOrWhiteSpace(provider)) return false;
            var p = provider.Trim();
            return p.Equals(Windows, StringComparison.OrdinalIgnoreCase)
                || p.Equals("negotiate", StringComparison.OrdinalIgnoreCase)
                || p.Equals("ntlm", StringComparison.OrdinalIgnoreCase)
                || p.Equals("kerberos", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// A user-to-role mapping, persisted in Config/rbac-users.json.
    /// </summary>
    public class RbacUser
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>
        /// The identity key. For email-class providers (Google/Microsoft/local) this is an
        /// email address; for the Windows provider it is a down-level <c>DOMAIN\user</c> name.
        ///
        /// <para>The JSON property stays <c>email</c> for back-compat with every existing
        /// rbac-users.json on disk. Prefer <see cref="Principal"/> when reading in new code —
        /// a property called Email holding <c>MSI\admin.adrian</c> is a maintenance trap.</para>
        /// </summary>
        [JsonPropertyName("email")]
        public string Email { get; set; } = string.Empty;

        /// <summary>
        /// Alias for <see cref="Email"/> that says what the value actually is. Not serialised —
        /// it is the same storage, exposed under an honest name.
        /// </summary>
        [JsonIgnore]
        public string Principal
        {
            get => Email;
            set => Email = value;
        }

        /// <summary>Display name from the identity provider.</summary>
        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>Identity provider: see <see cref="AuthProviders"/>.</summary>
        [JsonPropertyName("provider")]
        public string Provider { get; set; } = AuthProviders.Local;

        /// <summary>
        /// Windows SID, captured on the first successful Windows sign-in. Account NAMES get
        /// renamed; SIDs do not — without this, renaming a local account silently strips its
        /// admin role. Matched in preference to the name; the name stays for display.
        /// </summary>
        [JsonPropertyName("sid")]
        public string? Sid { get; set; }

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

        /// <summary>Argon2id hash for local password authentication.</summary>
        [JsonPropertyName("passwordHash")]
        public string? PasswordHash { get; set; }

        /// <summary>SOC2 CC6.3: when an admin last reviewed this user's access. Null = never reviewed.</summary>
        [JsonPropertyName("lastReviewedAt")]
        public DateTime? LastReviewedAt { get; set; }

        /// <summary>SOC2 CC6.3: display name of the reviewer who last reviewed this user's access.</summary>
        [JsonPropertyName("lastReviewedBy")]
        public string? LastReviewedBy { get; set; }
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

        /// <summary>Windows (Negotiate) authentication — local SAM and domain accounts.</summary>
        [JsonPropertyName("windows")]
        public WindowsAuthConfig Windows { get; set; } = new();

        /// <summary>Local username + Argon2id password sign-in.</summary>
        [JsonPropertyName("localPassword")]
        public LocalPasswordConfig LocalPassword { get; set; } = new();

        // ── There is no remote bootstrap flag, and this is where it used to be ───────────
        //
        // `allowRemoteBootstrapAdmin` (bool) and `bootstrapCompletedUtc` (DateTime?) were removed
        // on 2026-08-03. Between them they said "an unauthenticated client anywhere on the network
        // is admin until this install has had a working administrator". FOUR consecutive rounds
        // wrote an expiry for that grant and four were defeated by a state transition nobody had
        // enumerated: no expiry at all; then `!IsRbacEnforced()`, a FAIL-SAFE predicate whose
        // polarity inverts when read as an expiry, so a truncated rbac-users.json reopened it;
        // then `Config.Enabled`, which is a mirror of a switch and not an expiry, so unticking
        // Enable RBAC reopened it on an undamaged install; then a written-down one-way latch,
        // defeated because RecordLogin never raised it — with requireExplicitAccess=false and
        // defaultRole=admin (both shipped Settings controls) an operator signs in through the
        // normal path, the install is fully bootstrapped, and the latch is still null.
        //
        // The last one is the shape of the whole problem: a latch is only as good as every mutator
        // remembering to raise it, and "one method so a new mutator gets both or neither" was one
        // enumeration short. A fifth guard would be a fifth enumeration.
        //
        // So the grant is gone rather than guarded. Loopback bootstrap (RbacService.IsBootstrapEligible)
        // covers the operator standing at the box, and a genuinely headless server is administered
        // over RDP or SSH like every other task on it — at which point the console IS loopback.
        //
        // Both keys are simply ignored if they are still present in an existing rbac-config.json:
        // System.Text.Json drops unmapped members, and the next save rewrites the file without
        // them. Do not reintroduce either name.
    }

    /// <summary>
    /// Windows (Negotiate) authentication settings. On a domain-joined host this is Kerberos;
    /// on a workgroup machine SSPI falls back to NTLM validated against the local SAM, so
    /// <c>MACHINE\user</c> accounts sign in.
    /// </summary>
    public class WindowsAuthConfig
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Optional allow-list of NetBIOS domain / machine names. Empty means any domain the
        /// host can authenticate against. This is the Windows analogue of
        /// <see cref="OAuthProviderConfig.AllowedDomain"/>; the OAuth email-suffix check cannot
        /// be applied to a Windows principal, which has no email claim.
        /// </summary>
        [JsonPropertyName("allowedDomains")]
        public List<string> AllowedDomains { get; set; } = new();
    }

    /// <summary>Local username + password sign-in, backed by the Argon2id hashes in rbac-users.json.</summary>
    public class LocalPasswordConfig
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = false;
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
