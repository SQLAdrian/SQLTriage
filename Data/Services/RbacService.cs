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

        /// <summary>
        /// How the user store came off disk. NOT cosmetic: an empty <see cref="_users"/> means
        /// "no users configured" for a <see cref="ConfigLoadOutcome.Missing"/> file and "every
        /// account on this install is unreadable" for an <see cref="ConfigLoadOutcome.Unreadable"/>
        /// one, and those two must not produce the same security answer.
        /// </summary>
        private ConfigLoadOutcome _usersLoad = ConfigLoadOutcome.Missing;

        /// <summary>
        /// How <c>rbac-config.json</c> came off disk. The same discriminator the user store got,
        /// for the same reason and one round later: the expiry and the lapse report both read
        /// <c>_config.Enabled</c>, and a damaged config file yields defaults — <c>Enabled=false</c>,
        /// byte-identical to "the operator switched it off". Reasoning correctly over a fact that
        /// cannot be trusted is still reasoning over nothing.
        /// </summary>
        private ConfigLoadOutcome _configLoad = ConfigLoadOutcome.Missing;

        /// <summary>
        /// Path of the <c>.rejected-</c> copy taken when the store last failed to load, or
        /// <c>null</c> when no copy exists. See <see cref="DescribeStoreRecovery"/> — this is a
        /// measurement, and the recovery advice is composed from it rather than assumed.
        /// </summary>
        private string? _configQuarantine;

        private string? _usersQuarantine;

        /// <summary>The last enforcement posture reported to the log, so a lapse is announced when it happens rather than on every read.</summary>
        private string? _reportedPosture;

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
            ReportEnforcementPosture("startup");
        }

        // ── Configuration ────────────────────────────────────────────────

        /// <summary>
        /// Replaces the stored RBAC configuration.
        ///
        /// <para><b>Refused, by default, when <c>rbac-config.json</c> exists and did not load.</b>
        /// In that state <see cref="Config"/> is <see cref="RbacConfig"/> defaults, so anything a
        /// caller built from it — a Settings form pre-filled from those defaults, Onboarding
        /// mutating one field of them — is this code's guess about the operator's intent, and
        /// persisting it records the guess as their choice and destroys the damaged file that was
        /// the only evidence otherwise. Measured on 2026-08-04: the Enable-RBAC checkbox bound
        /// <c>@bind:after="SaveRbacConfig"</c>, so one click did it.</para>
        ///
        /// <para>The escape is <see cref="StoreWriteIntent.ReplaceUnreadableStore"/>, and it is the
        /// operator's to give, not a caller's to assume: the page must have told them the file is
        /// unreadable, that the values in front of them are defaults, and that saving overwrites it.
        /// That keeps the repair path the enforcement banner advertises — "re-enter the settings and
        /// save, which rewrites it" — while closing the accidental one.</para>
        ///
        /// <para>Returns rather than throws: a Blazor <c>@bind:after</c> handler that throws takes
        /// the circuit down. A caller that ignores the result still gets the safe behaviour, which
        /// is the point of putting the guard here instead of at each control.</para>
        /// </summary>
        public StoreWriteOutcome UpdateConfig(RbacConfig config,
                                              StoreWriteIntent intent = StoreWriteIntent.FromLoadedStore)
        {
            lock (_lock)
            {
                // Checked BEFORE _config is replaced, not just before the write: a refused save must
                // leave this service reporting the same thing it reported a moment ago, or the
                // banner and the log start describing a configuration that exists nowhere.
                if (ConfigFileHelper.WouldOverwriteUnreadStore(_configLoad, intent))
                {
                    _logger.LogWarning(
                        "[RBAC] Refused to overwrite {Path}: it exists and did not load ({Outcome}), so the "
                        + "configuration offered for saving descends from built-in defaults rather than from "
                        + "anything this process read. The file is unchanged.",
                        _configPath, _configLoad);
                    return StoreWriteOutcome.RefusedStoreUnreadable;
                }

                // Encrypt OAuth client secrets before persisting
                EncryptSecrets(config);

                // No carry-over of anything the incoming config does not carry. There used to be
                // one — `config.BootstrapCompletedUtc ??= _config.BootstrapCompletedUtc` — because
                // Settings.SaveRbacConfig builds a fresh RbacConfig from the form fields, so a
                // save would otherwise have cleared the latch that expired the remote bootstrap
                // grant. That whole mechanism is gone with the grant it expired: bootstrap
                // eligibility is now the caller's socket address and nothing persisted, so there
                // is no stored security fact here for a save to drop.
                var previous = _config;
                _config = config;
                if (!SaveConfig(intent))
                {
                    // Same rollback rule as the user mutators: the file did not change, so neither
                    // does what this process reports. Without it, Settings showed the new posture,
                    // the log announced it, and a restart silently reverted the lot.
                    _config = previous;
                    return StoreWriteOutcome.WriteFailed;
                }
            }
            ReportEnforcementPosture("configuration saved");
            OnConfigChanged?.Invoke();
            return StoreWriteOutcome.Saved;
        }

        /// <summary>
        /// The file path this service reads its configuration from, so a page that has to tell an
        /// operator WHICH file did not load can name it instead of guessing at a layout. Read-only.
        /// </summary>
        public string ConfigPath => _configPath;

        private void EncryptSecrets(RbacConfig config)
        {
            if (!string.IsNullOrEmpty(config.Google.ClientSecret)
                && !CredentialProtector.IsEncrypted(config.Google.ClientSecret))
                config.Google.ClientSecret = CredentialProtector.Encrypt(config.Google.ClientSecret);

            if (!string.IsNullOrEmpty(config.Microsoft.ClientSecret)
                && !CredentialProtector.IsEncrypted(config.Microsoft.ClientSecret))
                config.Microsoft.ClientSecret = CredentialProtector.Encrypt(config.Microsoft.ClientSecret);
        }

        /// <summary>
        /// Reads the RBAC config straight off disk without building a service.
        ///
        /// <para>Both hosts need it BEFORE the DI container exists: whether Windows auth is on
        /// decides whether the HTTPS listener must be pinned to HTTP/1.1, and Kestrel is
        /// configured before <c>builder.Build()</c>. Read-only; returns defaults on any failure.</para>
        ///
        /// <para><b>Deliberately outcome-BLIND, reviewed 2026-08-04 when the write guard went out to
        /// the other config stores.</b> The guard is about writes, and this method has none — it is
        /// the only <c>ConfigFileHelper.Load</c> caller in the codebase with no save anywhere near
        /// it. Under damage it returns defaults, Windows auth reads as off, and the listener is not
        /// pinned to HTTP/1.1; that agrees with the rest of the process, which has loaded the same
        /// damaged file and is also treating every provider as off, and it is the same transport
        /// configuration a genuinely unconfigured install gets. The service instance separately
        /// records the real outcome and the Settings banner says the file did not load. Making this
        /// discriminate would add a second, earlier voice on the same fact for no decision it
        /// changes.</para>
        /// </summary>
        public static RbacConfig PeekConfig(string? configPath = null)
        {
            var path = configPath ?? Path.Combine(AppContext.BaseDirectory, "Config", "rbac-config.json");
            try
            {
                return ConfigFileHelper.Load<RbacConfig>(path, _jsonOptions) ?? new RbacConfig();
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[Auth] Could not read {Path} — assuming an unconfigured install", path);
                return new RbacConfig();
            }
        }

        private void LoadConfig()
        {
            try
            {
                // The REPORTING overload, for the same reason the user store uses it. Every answer
                // this service gives about the operator's intent — is RBAC switched on, which
                // sign-in providers did they configure — is read out of this object, and the plain
                // overload hands back defaults for four different reasons without saying which.
                // A damaged file then reads as a deliberate "off", and the Settings banner and the
                // log both say so on this discriminator rather than on Enabled alone.
                _config = ConfigFileHelper.Load<RbacConfig>(_configPath, _jsonOptions, out _configLoad, out _configQuarantine);
                _logger.LogInformation("RBAC config loaded: Enabled={Enabled}, Google={Google}, Microsoft={Microsoft} (store: {Outcome})",
                    _config.Enabled, _config.Google.Enabled, _config.Microsoft.Enabled, _configLoad);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load RBAC config");
                _config = new();
                _configLoad = ConfigLoadOutcome.Unreadable;
            }
        }

        /// <summary>
        /// The write chokepoint for <c>rbac-config.json</c>. The intent is a REQUIRED parameter, not
        /// an optional one, so a future mutator cannot reach the disk without stating which kind of
        /// write it is — it will not compile. See <see cref="StoreWriteIntent"/>.
        /// </summary>
        private bool SaveConfig(StoreWriteIntent intent)
        {
            // Second statement of the same guard, on purpose. UpdateConfig checks it before touching
            // _config so a refusal leaves the service coherent; this one is the guarantee that holds
            // for whatever calls SaveConfig next, written by whoever writes it.
            if (ConfigFileHelper.WouldOverwriteUnreadStore(_configLoad, intent))
            {
                _logger.LogWarning("[RBAC] Refused to write {Path} over an unreadable store ({Outcome})",
                    _configPath, _configLoad);
                return false;
            }

            try
            {
                ConfigFileHelper.Save(_configPath, _config, _jsonOptions);

                // The file on disk is now this object, written whole — so an operator who repaired
                // a damaged config through the UI clears the banner without a restart. Mirrors
                // SaveUsers. NOT set when the write throws: a failed save leaves the damage intact
                // and the lapse must keep saying so.
                _configLoad = ConfigLoadOutcome.Loaded;
                _logger.LogInformation("RBAC config saved");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save RBAC config");
                return false;
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

        // ── Identity matching ────────────────────────────────────────────
        //
        // Match by (provider-class, key), never by the shape of the string. See AuthProviders
        // for why: a UPN-form Windows account and a Google address can be byte-identical and
        // are different principals.

        /// <summary>
        /// True when a stored user record is the same principal as an incoming
        /// (provider, key, sid) identity.
        ///
        /// <para>SID wins when both sides have one — account names get renamed, SIDs do not.
        /// A record with a SID that does NOT match is rejected outright rather than falling
        /// back to the name, because a name collision after a rename is exactly the case the
        /// SID exists to disambiguate.</para>
        /// </summary>
        private static bool IsSamePrincipal(RbacUser user, string provider, string key, string? sid)
        {
            if (AuthProviders.ClassOf(user.Provider) != AuthProviders.ClassOf(provider))
                return false;

            if (AuthProviders.IsWindows(provider))
            {
                if (!string.IsNullOrEmpty(user.Sid) && !string.IsNullOrEmpty(sid))
                    return string.Equals(user.Sid, sid, StringComparison.OrdinalIgnoreCase);

                return WindowsIdentityKey.Equal(user.Email, key);
            }

            return user.Email.Equals(key, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Finds the user record for an incoming identity, matched by provider class.
        /// Returns null when no record matches — which under RequireExplicitAccess is a denial.
        /// </summary>
        public RbacUser? FindUser(string provider, string key, string? sid = null)
        {
            lock (_lock)
                return _users.FirstOrDefault(u => IsSamePrincipal(u, provider, key, sid));
        }

        /// <summary>
        /// Adds a user. Returns what happened, so a caller cannot write an audit record for a change
        /// that did not reach the disk — <c>Settings.AddRbacUser</c> did exactly that until this
        /// returned anything, logging "Added x as admin" while the store refused the write.
        /// </summary>
        public StoreWriteOutcome AddUser(RbacUser user)
        {
            lock (_lock)
            {
                // Refused before the in-memory list is touched, so a refusal leaves no phantom
                // account that this process would go on authorizing until the next restart.
                if (ConfigFileHelper.WouldOverwriteUnreadStore(_usersLoad, StoreWriteIntent.FromLoadedStore))
                {
                    _logger.LogWarning("[RBAC] Refused to add {Principal}: the user store did not load ({Outcome})",
                        user.Email, _usersLoad);
                    return StoreWriteOutcome.RefusedStoreUnreadable;
                }

                // Prevent duplicates within the same provider class. Deliberately NOT a plain
                // string compare across all users: MSI\adrian (windows) and adrian@x.com (google)
                // are different principals and both are allowed to exist.
                if (_users.Any(u => IsSamePrincipal(u, user.Provider, user.Email, user.Sid)))
                {
                    _logger.LogWarning("User {Principal} already exists for provider {Provider}",
                        user.Email, user.Provider);
                    return StoreWriteOutcome.NothingToWrite;
                }
                _users.Add(user);
                if (!SaveUsers(StoreWriteIntent.FromLoadedStore))
                {
                    // Rolled back — see the class rule on SaveUsers. The account is not on disk, so
                    // this process must not go on authorizing it: an admin added during a full disk
                    // would work until the next restart and then be gone, which is the worst of the
                    // three possible states to hand an operator.
                    _users.Remove(user);
                    return StoreWriteOutcome.WriteFailed;
                }
            }
            _logger.LogInformation("Added RBAC user: {Principal} ({Provider}) as {Role}",
                user.Email, user.Provider, user.Role);
            ReportEnforcementPosture("user added");
            return StoreWriteOutcome.Saved;
        }

        public StoreWriteOutcome UpdateUser(RbacUser user)
        {
            lock (_lock)
            {
                if (ConfigFileHelper.WouldOverwriteUnreadStore(_usersLoad, StoreWriteIntent.FromLoadedStore))
                {
                    _logger.LogWarning("[RBAC] Refused to update {Email}: the user store did not load ({Outcome})",
                        user.Email, _usersLoad);
                    return StoreWriteOutcome.RefusedStoreUnreadable;
                }

                var index = _users.FindIndex(u => u.Id == user.Id);
                if (index < 0) return StoreWriteOutcome.NothingToWrite;

                var previous = _users[index];
                _users[index] = user;
                if (!SaveUsers(StoreWriteIntent.FromLoadedStore))
                {
                    _users[index] = previous;
                    return StoreWriteOutcome.WriteFailed;
                }
                _logger.LogInformation("Updated RBAC user: {Email} → role={Role}, enabled={Enabled}",
                    user.Email, user.Role, user.Enabled);
            }
            ReportEnforcementPosture("user updated");
            return StoreWriteOutcome.Saved;
        }

        public StoreWriteOutcome RemoveUser(string id)
        {
            lock (_lock)
            {
                if (ConfigFileHelper.WouldOverwriteUnreadStore(_usersLoad, StoreWriteIntent.FromLoadedStore))
                {
                    _logger.LogWarning("[RBAC] Refused to remove {Id}: the user store did not load ({Outcome})",
                        id, _usersLoad);
                    return StoreWriteOutcome.RefusedStoreUnreadable;
                }

                var user = _users.FirstOrDefault(u => u.Id == id);
                if (user == null) return StoreWriteOutcome.NothingToWrite;

                var at = _users.IndexOf(user);
                _users.Remove(user);
                if (!SaveUsers(StoreWriteIntent.FromLoadedStore))
                {
                    // The revoke did NOT happen. Restoring the record is the honest direction even
                    // though it is the less restrictive one: a removal held only in memory reverses
                    // itself at the next restart, so the operator would be told it failed while the
                    // account was actually locked out for the rest of the session and back
                    // afterwards. What this process enforces now equals what is on disk, and the
                    // caller is told to retry.
                    _users.Insert(at, user);
                    return StoreWriteOutcome.WriteFailed;
                }
                _logger.LogInformation("Removed RBAC user: {Email}", user.Email);
            }
            ReportEnforcementPosture("user removed");
            return StoreWriteOutcome.Saved;
        }

        /// <summary>
        /// Records a login event for the given email. Creates a new user record
        /// with the default role if RequireExplicitAccess is false and the user
        /// doesn't exist yet.
        /// </summary>
        /// <returns>The user if login is allowed, null if denied.</returns>
        public RbacUser? RecordLogin(string email, string displayName, string provider)
            => RecordLogin(email, displayName, provider, null);

        /// <summary>
        /// Records a login for an identity key, matched by provider class.
        /// </summary>
        /// <param name="sid">
        /// Windows SID, when the authenticating scheme supplied one. Persisted on first sight
        /// and preferred over the account name on every later match, so a renamed account keeps
        /// its role instead of silently dropping to the default.
        /// </param>
        public RbacUser? RecordLogin(string email, string displayName, string provider, string? sid)
        {
            var key = AuthProviders.IsWindows(provider)
                ? WindowsIdentityKey.Normalize(email)
                : email;

            lock (_lock)
            {
                var user = _users.FirstOrDefault(u => IsSamePrincipal(u, provider, key, sid));

                if (user == null)
                {
                    if (_config.RequireExplicitAccess)
                    {
                        _logger.LogWarning("Login denied for {Principal} ({Provider}) — not in RBAC user list",
                            key, provider);
                        return null;
                    }

                    // Auto-create with default role
                    user = new RbacUser
                    {
                        Email = key,
                        DisplayName = displayName,
                        Provider = AuthProviders.IsWindows(provider) ? AuthProviders.Windows : provider,
                        Role = _config.DefaultRole,
                        CreatedAt = DateTime.UtcNow
                    };
                    _users.Add(user);
                    _logger.LogInformation("Auto-created RBAC user: {Principal} as {Role}", key, user.Role);
                }

                if (!user.Enabled)
                {
                    _logger.LogWarning("Login denied for {Principal} — account disabled", key);
                    return null;
                }

                // Durability: bind the SID on first successful Windows sign-in.
                if (!string.IsNullOrEmpty(sid) && string.IsNullOrEmpty(user.Sid))
                {
                    user.Sid = sid;
                    _logger.LogInformation("Bound SID to RBAC user {Principal} — future matches survive a rename", key);
                }

                // Keep the stored key current when a SID match found a renamed account.
                if (AuthProviders.IsWindows(provider) && !string.IsNullOrEmpty(key)
                    && !WindowsIdentityKey.Equal(user.Email, key))
                {
                    _logger.LogInformation("RBAC user {Old} matched by SID under new name {New} — updating stored key",
                        user.Email, key);
                    user.Email = key;
                }

                user.LastLogin = DateTime.UtcNow;
                if (!string.IsNullOrEmpty(displayName))
                    user.DisplayName = displayName;

                // Deliberately NOT refused up front, and deliberately not returning null: this
                // method's answer is an authorization one and this change does not touch it. The
                // WRITE is refused, which is the whole exposure — an auto-created user persisted
                // over a store that did not load would delete every real account. What survives a
                // refusal is an in-memory record that dies at the next restart, on an install whose
                // enforcement has already lapsed for the same reason (see DescribeEnforcementLapse),
                // so nothing is being granted here that the damaged store was not already granting.
                SaveUsers(StoreWriteIntent.FromLoadedStore);
                return user;
            }
        }

        // ── Authorization Checks ─────────────────────────────────────────

        /// <summary>
        /// The raw permission matrix: role against permission, and nothing else. It does not know
        /// whether RBAC is being enforced, and it does not know whether this caller is entitled to
        /// the unconfigured-install bootstrap hatch. Both of those are the caller's job, which is
        /// why the only callers are the ones listed below.
        ///
        /// <para><b>PRIVATE since 2026-08-17, and the modifier IS the control.</b> From 2026-08-01
        /// to 2026-08-16, seven rounds of <c>RbacServerModeLockoutTests</c> tried to stop a shipped
        /// page spelling a call to this method in a way a text scan could not see. Each round closed
        /// a spelling; each was defeated by the next, because a text instrument can always be
        /// out-spelt. <c>private</c> ends the class by construction: a page cannot NAME this method,
        /// so there is no spelling left to hide. Every escape the census chased — the <c>@@</c>
        /// escape, an <c>@{ }</c> markup block, a raw string literal, a mid-line block comment, an
        /// interpolation hole, a namespace alias, a split <c>[Inject]</c>, a run-away <c>@*</c> —
        /// now fails to COMPILE with CS0122 rather than failing a test.</para>
        ///
        /// <para><b><c>internal</c> would not have done it.</b> <c>Pages/</c> and <c>Components/</c>
        /// compile into this same assembly, so an internal member is fully visible to every page.
        /// Only <c>private</c> puts the boundary where a page cannot reach.</para>
        ///
        /// <para>Callers, all of them: <see cref="IsAuthorized(string, string, AppUserState.BootstrapEligibilityProof)"/> — the one
        /// true gate, which every UI surface reaches through
        /// <see cref="AppUserState.IsAuthorized(string)"/> — and
        /// <see cref="EvaluateApiKeyPermission"/>, the API-key trust tier, which has its own note
        /// explaining why it is a different question.</para>
        /// </summary>
        private static bool HasPermission(string role, string permission)
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
        /// The permission matrix, for the API-key REST tier and for nothing else.
        ///
        /// <para><b>Pages and components must never call this, and the reason is not style.</b> It
        /// is the raw matrix. It does not know whether RBAC is enforced, and it does not know
        /// whether the caller may use the unconfigured-install bootstrap hatch. A UI surface calling
        /// it would be answering its own authorization question from a table, which is exactly the
        /// defect <see cref="AppUserState.IsAuthorized(string)"/> exists to prevent. UI gates go
        /// through that method. Always. There is no exception and no allowlist.</para>
        ///
        /// <para><b>Why <c>/api</c> is a different question.</b> <see cref="ApiAuthorization"/> is a
        /// separate trust tier that has already decided the parts this method does not cover, in its
        /// own file and by its own rules: it resolves a principal from the store and refuses
        /// anything not <c>IsActive</c> before it asks the matrix at all, and it handles the
        /// bootstrap-equivalent case SEPARATELY and afterwards — an explicit branch requiring both a
        /// dormant install and a loopback address, skipped entirely on a fail-closed endpoint.
        /// Handing that caller the matrix hands it precisely the one part it has not already
        /// settled, so this is a narrowing, not a hatch.</para>
        ///
        /// <para><b><c>internal</c> rather than <c>private</c>, and that makes it the one remaining
        /// surface.</b> <see cref="ApiAuthorization"/> is a different file in the same assembly, so
        /// private is not available. Internal means a page technically COULD name this — which is
        /// why <c>RbacChokepointTests.NoShippedUiNamesTheApiKeyPermissionEvaluator</c> exists. That
        /// is one small lint on one named method, and it is proportionate for exactly that reason:
        /// the open-ended spelling war ended when <see cref="HasPermission"/> went private, and this
        /// is not a revival of it.</para>
        /// </summary>
        internal static bool EvaluateApiKeyPermission(string role, string permission)
            => HasPermission(role, permission);

        /// <summary>
        /// THE definition of "this caller may use the unconfigured-install bootstrap hatch", in one
        /// place. Three call sites used to spell it out by hand — the HTTP admission gate, the
        /// per-circuit <see cref="AppUserState"/>, and the <c>/auth/me</c> report — and one of them
        /// spelled it differently.
        ///
        /// <para><b>ONE term: the caller is on loopback.</b> A person on loopback already has the
        /// box — they can stop the service, edit <c>Config\rbac-users.json</c> and restart — so the
        /// hatch grants them no authority they lack; it removes an hour of downtime from the
        /// recovery path. It is unconditional, including under enforced RBAC, because break-glass
        /// has to survive enforcement or an operator who mis-configures RBAC has no way back. That
        /// is the lockout the L1/L2/L3 guards exist to prevent.</para>
        ///
        /// <para><b>There is no second term, and the history of the one there used to be is the
        /// reason.</b> <c>allowRemoteBootstrapAdmin</c> extended this to any unauthenticated client
        /// that could reach the listener, until some expiry closed it. Four rounds wrote that
        /// expiry and four were defeated, each by a state transition nobody had enumerated:</para>
        /// <list type="number">
        /// <item>No expiry at all. Measured with the flag on and RBAC ENFORCED: an unauthenticated
        ///   non-loopback caller was admitted, negotiated a Blazor circuit and was rendered ungated
        ///   controls on a fully configured install.</item>
        /// <item><c>&amp;&amp; !IsRbacEnforced()</c>. <see cref="IsRbacEnforced"/> is a RUNTIME,
        ///   FAIL-SAFE predicate that WITHHOLDS enforcement whenever the install looks unusable, so
        ///   that an operator cannot lock themselves out; read as an expiry its polarity inverts and
        ///   "this install looks unusable" starts to mean "admit anonymous strangers". Truncating
        ///   <c>rbac-users.json</c> reopened the door with no attacker and no operator action.</item>
        /// <item><c>Config.Enabled</c>. Not an expiry but a MIRROR of a switch: "over once RBAC is
        ///   on" is identically "back once RBAC is off". Measured with NO file damage of any kind —
        ///   a stranger from 192.168.10.32 got <c>/servers</c>, <c>/query</c>, <c>/settings</c>,
        ///   <c>/server-configuration</c>, <c>/audit-log</c> and <c>/service-management</c> at 200
        ///   and a live circuit.</item>
        /// <item>A written-down one-way latch, <c>bootstrapCompletedUtc</c>, raised by every RBAC
        ///   mutator. Defeated because <c>RecordLogin</c> is not one of them: with
        ///   <c>requireExplicitAccess=false</c> and <c>defaultRole=admin</c> — both first-class
        ///   Settings controls — an operator signs in through the shipped path, the install is
        ///   fully bootstrapped, and the latch is still null until some later settings save.</item>
        /// </list>
        ///
        /// <para>The fourth is the shape of the whole problem: a latch is only as good as every
        /// mutator remembering to raise it, and the comment claiming "one method so that a new
        /// mutator gets both or neither" was one enumeration short of true. A fifth guard would be
        /// a fifth enumeration of the same open-ended set, and this lane has now proved that shape
        /// unwinnable four separate times. So the grant is DELETED rather than guarded again: the
        /// flag, the latch and the machinery that expired it are gone from the model, the service,
        /// the Settings page and the config file.</para>
        ///
        /// <para><b>What that costs, stated plainly.</b> A headless server with no console cannot
        /// be bootstrapped from another machine over HTTP any more. It is administered over RDP or
        /// SSH like every other task on it — and from that session the browser is on loopback, so
        /// this arm covers it. What is no longer possible is bootstrapping the app from anywhere on
        /// the network with no credential at all, which was never distinguishable from the attack.</para>
        /// </summary>
        /// <param name="loopback">Whether this caller's SOCKET address is a loopback address.</param>
        public bool IsBootstrapEligible(bool loopback) => loopback;

        /// <summary>
        /// True when <c>rbac-users.json</c> exists but did not yield its contents. Distinct from
        /// "no users": a MISSING file is a fresh install and is not damage.
        /// </summary>
        public bool IsUserStoreDamaged
        {
            get { lock (_lock) return IsUserStoreDamagedLocked(); }
        }

        /// <summary>
        /// True when <c>rbac-config.json</c> exists but did not yield its contents — so
        /// <c>_config</c> is <see cref="RbacConfig"/> defaults and everything it says about the
        /// operator's intent, <c>Enabled</c> included, is this code's guess rather than their
        /// choice. Distinct from "never configured": a MISSING file is a fresh install.
        /// </summary>
        public bool IsConfigStoreDamaged
        {
            get { lock (_lock) return IsConfigStoreDamagedLocked(); }
        }

        private bool IsUserStoreDamagedLocked()
            => _usersLoad is ConfigLoadOutcome.Unreadable or ConfigLoadOutcome.Empty;

        private bool IsConfigStoreDamagedLocked()
            => _configLoad is ConfigLoadOutcome.Unreadable or ConfigLoadOutcome.Empty;

        /// <summary>
        /// How to recover one of THIS service's two stores. A naming convenience over the register —
        /// <see cref="ConfigFileHelper.DescribeStoreRecovery"/> — which is where the sentence itself
        /// lives and is the only place it is written.
        ///
        /// <para>It started here, covering these two files. Extending the write guard to the other
        /// config stores on 2026-08-04 would have needed the same sentence in seven more services,
        /// which is the defect below with a bigger denominator, so the text moved to
        /// <c>ConfigFileHelper</c> and this became a two-line forward. Callers, tests and the
        /// Settings banner are unchanged.</para>
        ///
        /// <para><b>Why it exists.</b> Four surfaces — this service's lapse reason,
        /// <c>Settings.razor</c>, <c>Settings.razor.cs</c> and <c>Onboarding.razor</c> — each carried
        /// their own copy of "a copy of the unparseable one is kept beside it with a .rejected-
        /// suffix", unconditionally. The quarantine is NOT unconditional: an empty file is damage and
        /// gets no copy, because copying zero bytes preserves nothing. So on the 0-byte shape all
        /// four surfaces pointed the operator at a file that had never been written. Measured live by
        /// the 2026-08-04 cold gate, on a fix whose entire subject was this same defect class one
        /// control higher up the page.</para>
        ///
        /// <para>Four copies of a sentence drift because nothing makes them agree. One register
        /// cannot: this is the same correction <see cref="DescribeEnforcementPosture"/> applied to
        /// the banner. The sentence is conditioned on <see cref="_configQuarantine"/> /
        /// <see cref="_usersQuarantine"/>, which are what <c>ConfigFileHelper</c> actually did, not
        /// what it usually does.</para>
        /// </summary>
        /// <param name="forConfigStore">true for rbac-config.json, false for rbac-users.json.</param>
        public string DescribeStoreRecovery(bool forConfigStore)
        {
            lock (_lock)
                return ConfigFileHelper.DescribeStoreRecovery(
                    forConfigStore ? _configLoad : _usersLoad,
                    forConfigStore ? _configQuarantine : _usersQuarantine);
        }

        /// <summary>
        /// Returns the role for the current desktop user (WPF mode = always Admin).
        /// </summary>
        public string GetDesktopUserRole() => AppRoles.Admin;

        /// <summary>
        /// True when RBAC enforcement is active. THREE conditions, all required:
        /// <list type="number">
        /// <item>the feature is explicitly enabled;</item>
        /// <item>at least one enabled Admin user exists;</item>
        /// <item>at least one USABLE way to sign in as that admin is configured.</item>
        /// </list>
        ///
        /// <para>Condition 3 is the lockout guard (L1). Without it, flipping Enabled with one
        /// admin and no sign-in method closed every gate — including Settings, the only page
        /// that can undo the change — leaving the install recoverable only by deleting
        /// Config/rbac-users.json on disk. Adrian was one click from that on 2026-08-01.</para>
        ///
        /// <para>This is a fail-SAFE, not a policy: it only ever WITHHOLDS enforcement in a
        /// state where enforcement is unusable, so it is strictly narrower than the dormancy
        /// that already existed. It cannot be bypassed by a UI path that forgot to check.</para>
        ///
        /// <para><b>Therefore: never compose this into an authorization decision.</b> A predicate
        /// whose failure mode is "grant less enforcement" becomes "grant more access" the instant
        /// it is read as a permission term. <see cref="IsBootstrapEligible"/> did exactly that for
        /// one round and turned a truncated file into an open door. It does not call this at all
        /// now, and cannot: it reads one socket-level fact and nothing off disk.</para>
        ///
        /// <para>Answered as "switched on, and nothing lapsed" so this and
        /// <see cref="DescribeEnforcementLapse"/> cannot disagree — the state that the UI reports
        /// and the state the gates act on are one register.</para>
        /// </summary>
        public bool IsRbacEnforced()
        {
            lock (_lock) return _config.Enabled && DescribeEnforcementLapse().Count == 0;
        }

        /// <summary>
        /// Why this install is not enforcing RBAC when it should be, or when it cannot say. Empty
        /// means either enforcing, or deliberately switched off — both states the operator chose.
        ///
        /// <para>This exists because the fail-safe above is silent. Enforcement can lapse with no
        /// operator action at all: a half-written user store, a disabled sign-in provider, an OAuth
        /// client secret that expired on a calendar the install knows nothing about. Before
        /// 2026-08-03 the only place that showed was the <c>enforced</c> field of <c>/auth/me</c>,
        /// which nobody reads on a normal day. A security posture that changes quietly because a
        /// file got truncated is the defect; the door it opened was the symptom. The Settings page
        /// renders these strings as a banner and
        /// <see cref="ReportEnforcementPosture"/> writes them to the log on every transition.</para>
        ///
        /// <para><b>The config file is checked BEFORE what the config says</b>, because
        /// "switched off is not a lapse" is only sound when the switch was actually read. A damaged
        /// <c>rbac-config.json</c> yields <see cref="RbacConfig"/> defaults, <c>Enabled=false</c>,
        /// indistinguishable from an operator who turned it off — and the early return below then
        /// reports "nothing to see" about an install running on a configuration nobody wrote. The
        /// boundary still holds in that state — the hatch's flag reads false with the rest of the
        /// defaults, so a non-loopback caller gets no eligibility and is refused like any other
        /// anonymous caller — which makes this fail-SILENT rather than fail-open; but
        /// "the install says when it stops enforcing" is the deliverable, and an install that
        /// cannot read its own configuration must say so rather than infer consent from it.</para>
        /// </summary>
        public IReadOnlyList<string> DescribeEnforcementLapse()
        {
            lock (_lock)
            {
                var reasons = new List<string>();

                if (IsConfigStoreDamagedLocked())
                {
                    reasons.Add(
                        $"The access-control configuration ({_configPath}) exists but did not load ({_configLoad}). "
                        + "This process is running on built-in defaults, which means access control is OFF and "
                        + "every sign-in method is off — whatever the file was meant to say. Nothing here can tell "
                        + "you what the operator configured, so nothing here should be read as their choice. "
                        + DescribeStoreRecovery(forConfigStore: true)
                        + " Or re-enter the settings and save, which rewrites it.");
                    return reasons;
                }

                // Switched off is not a lapse — there is nothing to have fallen out of. Sound only
                // because the check above established that this IS the operator's switch.
                if (!_config.Enabled) return reasons;

                if (IsUserStoreDamagedLocked())
                    reasons.Add(
                        $"The user store ({_usersPath}) exists but did not load ({_usersLoad}). "
                        + "Every account, role and password hash on this install is missing from this process, "
                        + "so nobody can be recognised and RBAC cannot be enforced. "
                        + DescribeStoreRecovery(forConfigStore: false));
                else if (!HasEnabledAdmin(_users))
                    reasons.Add(
                        "No enabled Admin user exists, so enforcing RBAC would lock everyone out. Add an Admin account.");
                else if (!HasUsableLoginProvider(_config, _users))
                    reasons.Add(
                        "No enabled Admin can sign in with the sign-in methods currently configured — "
                        + DescribeSignInGap()
                        + " Until an Admin can sign in, enforcing RBAC would lock everyone out, so it is not enforced.");

                return reasons;
            }
        }

        /// <summary>
        /// Names the specific reason no admin can sign in, so the banner says what to fix rather
        /// than that something is wrong. Called under <c>_lock</c>.
        /// </summary>
        private string DescribeSignInGap()
        {
            var admins = _users
                .Where(u => u.Enabled && string.Equals(u.Role, AppRoles.Admin, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (admins.Any(u => AuthProviders.IsWindows(u.Provider)) && !_config.Windows.Enabled)
                return "an Admin holds a Windows account but Windows authentication is switched off.";

            if (admins.Any(u => !AuthProviders.IsWindows(u.Provider)))
            {
                if (_config.LocalPassword.Enabled && admins.All(u => string.IsNullOrEmpty(u.PasswordHash)))
                    return "local passwords are on, but no Admin has a password set.";

                var google = _config.Google.Enabled ? DescribeOAuthProblem(_config.Google) : null;
                if (google != null) return $"the Google provider is switched on but {google}.";

                var microsoft = _config.Microsoft.Enabled ? DescribeOAuthProblem(_config.Microsoft) : null;
                if (microsoft != null) return $"the Microsoft provider is switched on but {microsoft}.";
            }

            return "no sign-in method matches an enabled Admin account.";
        }

        /// <summary>
        /// Which of the four states this install is in, and the exact sentence that says so.
        ///
        /// <para>Deliberately carries the WORDS and not just the state, because the words are the
        /// thing that was wrong. The Settings banner used to hard-code the headline "RBAC is
        /// switched on but is NOT being enforced" and render it on <c>lapse.Count &gt; 0</c>, while
        /// the log branched on <see cref="IsConfigStoreDamaged"/> and said DID NOT LOAD. Driven
        /// across all seven damaged-config shapes — truncated, empty, whitespace, wrong-type,
        /// wrong-shape, literal null, bad-latch-type — every one produced <c>Enabled=false</c> and
        /// <c>lapse.Count=1</c>, so the banner rendered a claim about a switch nobody had read.
        /// Two registers for one fact is how a human-read surface comes to assert more than the
        /// code established, and this lane has now shipped that defect seven rounds running.</para>
        ///
        /// <para>So there is one register. <see cref="Headline"/> is the sentence, and it goes to
        /// the log and the banner unaltered — byte for byte, so an operator at the screen and an
        /// auditor in the log file are reading the same claim. It is the caller's job to render it,
        /// never to compose its own.</para>
        /// </summary>
        public sealed class EnforcementPosture
        {
            internal EnforcementPosture(PostureKind kind, string headline, string detail, IReadOnlyList<string> reasons)
            {
                Kind = kind;
                Headline = headline;
                Detail = detail;
                Reasons = reasons;
            }

            public PostureKind Kind { get; }

            /// <summary>The one-sentence claim. Rendered as the banner headline and logged verbatim.</summary>
            public string Headline { get; }

            /// <summary>The consequence, or empty when the state has none worth stating.</summary>
            public string Detail { get; }

            /// <summary>The specific things to fix. Empty unless something has actually lapsed.</summary>
            public IReadOnlyList<string> Reasons { get; }

            /// <summary>
            /// Whether this state needs an operator's attention — the one condition the banner and
            /// the log's WARNING level are both keyed off, so neither can shout while the other
            /// stays quiet.
            /// </summary>
            public bool IsProblem => Kind is PostureKind.ConfigDidNotLoad or PostureKind.Lapsed;
        }

        public enum PostureKind
        {
            /// <summary><c>rbac-config.json</c> exists and did not parse, so nothing it appears to say is the operator's.</summary>
            ConfigDidNotLoad,

            /// <summary>Switched on — established from a config that DID load — and not being enforced.</summary>
            Lapsed,

            /// <summary>Switched on and enforced.</summary>
            Enforcing,

            /// <summary>Switched off, by an operator whose switch was actually read.</summary>
            Off
        }

        /// <summary>
        /// The posture, for anything that has to TELL somebody: the Settings banner and
        /// <see cref="ReportEnforcementPosture"/>. See <see cref="EnforcementPosture"/>.
        /// </summary>
        public EnforcementPosture DescribeEnforcementPosture()
        {
            lock (_lock)
            {
                var reasons = DescribeEnforcementLapse();

                // Checked FIRST, and in its own words, for the reason DescribeEnforcementLapse
                // checks it first: this state arrives with Enabled=false because defaults are all
                // there is, so any sentence about the switch would attribute to the operator a
                // state they never chose.
                if (IsConfigStoreDamagedLocked())
                    return new EnforcementPosture(
                        PostureKind.ConfigDidNotLoad,
                        "The access-control configuration DID NOT LOAD.",
                        "This process is running on built-in defaults and cannot say what the operator configured, "
                        + "so nothing here should be read as their choice. Every request is being served as though "
                        + "access control were off.",
                        reasons);

                if (reasons.Count > 0)
                    return new EnforcementPosture(
                        PostureKind.Lapsed,
                        "Access control is switched ON but is NOT BEING ENFORCED.",
                        "Every request is being served as though access control were off.",
                        reasons);

                return _config.Enabled
                    ? new EnforcementPosture(PostureKind.Enforcing, "Access control is switched on and ENFORCED.", string.Empty, reasons)
                    : new EnforcementPosture(PostureKind.Off, "Access control is switched OFF.", string.Empty, reasons);
            }
        }

        /// <summary>
        /// Writes the enforcement posture to the log, once per TRANSITION rather than per read.
        /// Called from every path that can change it: startup, a config save, and any user add,
        /// update or removal.
        ///
        /// <para>Composes NOTHING. The sentence comes from <see cref="DescribeEnforcementPosture"/>
        /// and so does the decision to warn, which is what makes this and the banner one register
        /// rather than two things that agreed when they were written.</para>
        /// </summary>
        private void ReportEnforcementPosture(string trigger)
        {
            EnforcementPosture posture;

            lock (_lock)
            {
                posture = DescribeEnforcementPosture();

                // Transition key: the state AND the specific reasons, so a lapse that changes
                // cause is announced again rather than swallowed as "same posture".
                var key = posture.Kind + "|" + string.Join(" ", posture.Reasons);
                if (key == _reportedPosture) return;
                _reportedPosture = key;
            }

            if (posture.IsProblem)
                _logger.LogWarning(
                    "[RBAC] {Headline} ({Trigger}) {Detail} Reason: {Reasons}",
                    posture.Headline, trigger, posture.Detail, string.Join(" ", posture.Reasons));
            else
                _logger.LogInformation("[RBAC] {Headline} ({Trigger})", posture.Headline, trigger);
        }

        /// <summary>
        /// True when at least one enabled Admin could actually sign in with the configuration as
        /// it stands. "Configured" means what the auth pipeline requires, not what the flag says.
        /// </summary>
        public bool HasUsableLoginProvider()
        {
            lock (_lock) return HasUsableLoginProvider(_config, _users);
        }

        private static bool HasEnabledAdmin(IEnumerable<RbacUser> users) =>
            users.Any(u => u.Enabled && string.Equals(u.Role, AppRoles.Admin, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// True when SOME ENABLED ADMIN can use SOME ENABLED PROVIDER.
        ///
        /// <para>This used to ask the two questions separately — "is a provider on?" and, elsewhere,
        /// "is an admin present?" — and answering both yes is not the same as an admin who can get
        /// in. Windows authentication on with only an email-identity admin passed the old test and
        /// locked the box: Negotiate hands back <c>MSI\afsul</c>, no stored record matches it by
        /// provider class, and the only way back was loopback break-glass, which a headless server
        /// does not have. Pair the admin to the provider or the guard is decorative.</para>
        /// </summary>
        private static bool HasUsableLoginProvider(RbacConfig config, IReadOnlyCollection<RbacUser> users) =>
            users.Any(u => u.Enabled
                           && string.Equals(u.Role, AppRoles.Admin, StringComparison.OrdinalIgnoreCase)
                           && CanSignIn(u, config));

        /// <summary>
        /// Whether this specific stored principal has a way in under this configuration.
        ///
        /// <para>Windows-class records need Negotiate; email-class records need local passwords
        /// with a hash on the record, or a fully configured OAuth provider whose domain
        /// restriction admits them. Email-class is deliberately not per-provider: <see
        /// cref="RecordLogin(string, string, string, string?)"/> matches by provider CLASS, so a
        /// record stored as <c>google</c> signs in through Microsoft too, and both sign-in paths
        /// reach the same record.</para>
        /// </summary>
        private static bool CanSignIn(RbacUser user, RbacConfig config)
        {
            if (AuthProviders.IsWindows(user.Provider))
                return config.Windows.Enabled;

            // Local password needs an admin who actually HAS a hash. This is the exact shape the
            // Onboarding SetPassword defect produced: local auth "on", admin present, hash null,
            // nobody can sign in.
            if (config.LocalPassword.Enabled && !string.IsNullOrEmpty(user.PasswordHash))
                return true;

            if (IsOAuthProviderUsable(config.Google) && OAuthDomainAdmits(config.Google, user.Email))
                return true;

            if (IsOAuthProviderUsable(config.Microsoft) && OAuthDomainAdmits(config.Microsoft, user.Email))
                return true;

            return false;
        }

        /// <summary>
        /// True when an OAuth provider is enabled AND completely enough configured that the auth
        /// pipeline will register it — see <see cref="DescribeOAuthProblem"/>.
        /// </summary>
        public static bool IsOAuthProviderUsable(OAuthProviderConfig? provider) =>
            provider is { Enabled: true } && DescribeOAuthProblem(provider) == null;

        /// <summary>
        /// Names what stops an OAuth provider from working, or null when it is fully configured.
        /// Only meaningful for a provider that is switched on.
        ///
        /// <para><b>Why the secret is decrypted here.</b> <c>AddGoogle</c>/<c>AddMicrosoftAccount</c>
        /// validate their options on the FIRST REQUEST, inside <c>UseAuthentication()</c>, and an
        /// empty ClientSecret throws <c>ArgumentException: The value cannot be an empty string</c>
        /// there. That middleware runs before routing, so the throw is not scoped to the OAuth
        /// endpoints — it takes out <c>/settings</c>, <c>/auth/login</c> and <c>/_server/health</c>
        /// alike, on loopback as well as the LAN, with RBAC on or off. A half-typed provider
        /// (client id saved, secret not) is therefore a TOTAL, UI-REACHABLE LOCKOUT that
        /// break-glass cannot reach, because break-glass also needs the host to answer. Measured
        /// on 2026-08-01. An "aes:" blob that no longer decrypts (key regenerated, config copied
        /// from another machine) produces the same empty string and the same dead host, which is
        /// why this decrypts rather than merely checking for a non-empty ciphertext.</para>
        /// </summary>
        public static string? DescribeOAuthProblem(OAuthProviderConfig provider)
        {
            if (provider == null) return "the provider is not configured at all";
            if (string.IsNullOrWhiteSpace(provider.ClientId)) return "no Client ID is set";
            if (string.IsNullOrWhiteSpace(provider.ClientSecret)) return "no Client Secret is set";

            string secret;
            try
            {
                secret = CredentialProtector.Decrypt(provider.ClientSecret);
            }
            catch (Exception ex)
            {
                return "the stored Client Secret could not be decrypted (" + ex.GetType().Name + ")";
            }

            if (string.IsNullOrWhiteSpace(secret))
                return "the stored Client Secret could not be decrypted — re-enter it";

            return null;
        }

        /// <summary>
        /// Whether a provider's optional domain restriction admits this identity key. Mirrors the
        /// check <c>/auth/complete</c> applies, so the lockout guard sees the same answer the
        /// sign-in path will.
        /// </summary>
        private static bool OAuthDomainAdmits(OAuthProviderConfig provider, string principal)
        {
            if (string.IsNullOrWhiteSpace(provider.AllowedDomain)) return true;
            var domain = (principal ?? "").Split('@').LastOrDefault() ?? "";
            return domain.Equals(provider.AllowedDomain.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// L2 of the lockout guard: pre-flights a PROSPECTIVE configuration and returns the
        /// reasons it could not be enforced. Empty means the change is safe to save.
        ///
        /// <para>L1 alone silently doing nothing is its own trap — "I enabled RBAC and nothing
        /// changed". The Settings save calls this and refuses, naming what is missing. The same
        /// check guards removing or disabling the last usable admin.</para>
        /// </summary>
        /// <param name="prospectiveConfig">The config about to be saved. Null = the current one.</param>
        /// <param name="prospectiveUsers">The user list about to be saved. Null = the current one.</param>
        public IReadOnlyList<string> DescribeEnforcementBlockers(
            RbacConfig? prospectiveConfig = null,
            IEnumerable<RbacUser>? prospectiveUsers = null)
        {
            RbacConfig config;
            List<RbacUser> users;
            lock (_lock)
            {
                config = prospectiveConfig ?? _config;
                users = (prospectiveUsers ?? _users).ToList();
            }

            var blockers = new List<string>();

            // Not turning RBAC on? Then there is nothing to lock yourself out of.
            if (!config.Enabled) return blockers;

            if (!HasEnabledAdmin(users))
                blockers.Add("No enabled Admin user exists. Add an Admin account before enforcing RBAC.");

            if (!HasUsableLoginProvider(config, users))
                blockers.Add(
                    "No enabled Admin can sign in with this configuration. An Admin needs a sign-in method that "
                    + "matches the account: a Windows account needs Windows authentication enabled, and an email "
                    + "account needs local passwords with a password set on it, or a fully configured OAuth "
                    + "provider that admits its domain — otherwise nobody, including you, will be able to sign in.");

            return blockers;
        }

        /// <summary>
        /// Names every enabled-but-incomplete OAuth provider in a prospective config. Empty means
        /// the auth pipeline can be built from it.
        ///
        /// <para>Deliberately SEPARATE from <see cref="DescribeEnforcementBlockers"/>, and checked
        /// regardless of <c>config.Enabled</c>: this is not an RBAC-lockout question, it is a DEAD
        /// HOST question. <c>AddSqlTriageAuth</c> registers providers whether or not RBAC is
        /// enforced, and an enabled provider whose secret is missing or undecryptable makes
        /// <c>UseAuthentication()</c> throw on EVERY request — /settings, /auth/login and
        /// /_server/health all 500, loopback included, so there is no surface left to undo it
        /// from. The Settings save calls this and refuses; it must never reach disk.</para>
        ///
        /// <para>Kept out of the enforcement blockers so it cannot false-positive the OTHER caller:
        /// the guard on removing or disabling a user asks "would THIS change lock everyone out",
        /// and a provider that was already half-configured before the change is not that change's
        /// fault.</para>
        /// </summary>
        public IReadOnlyList<string> DescribeProviderConfigProblems(RbacConfig? prospectiveConfig = null)
        {
            RbacConfig config;
            lock (_lock) config = prospectiveConfig ?? _config;

            var problems = new List<string>();
            foreach (var (name, provider) in new[]
                     {
                         ("Google", config.Google),
                         ("Microsoft", config.Microsoft)
                     })
            {
                if (provider is not { Enabled: true }) continue;
                var problem = DescribeOAuthProblem(provider);
                if (problem != null)
                    problems.Add($"{name} sign-in is switched on but {problem}. Complete it or switch it off — "
                                 + "a half-configured provider stops this server answering ANY request, "
                                 + "including this page.");
            }
            return problems;
        }

        // ── Principal resolution ─────────────────────────────────────────

        /// <summary>Whether a stored record backs an authenticated principal, and in what state.</summary>
        public enum PrincipalStatus
        {
            /// <summary>A matching record exists and is enabled.</summary>
            Active,

            /// <summary>No record matches this (provider-class, key/SID) — removed, or never listed.</summary>
            Unknown,

            /// <summary>A record matches but is disabled.</summary>
            Disabled
        }

        /// <summary>The store's answer for an authenticated principal.</summary>
        public readonly record struct PrincipalResolution(PrincipalStatus Status, string Role, RbacUser? User)
        {
            /// <summary>True when the store still backs this principal.</summary>
            public bool IsActive => Status == PrincipalStatus.Active;
        }

        /// <summary>
        /// Resolves an authenticated principal against the USER STORE and returns the role the
        /// store says it has — never the role the caller's cookie says.
        ///
        /// <para><b>Why.</b> The session cookie carries <c>ClaimTypes.Role</c>, stamped at
        /// sign-in and valid for 8 hours with sliding renewal. Trusting it made Settings' remove,
        /// disable and demote advisory: a user deleted from the store kept full admin from the
        /// LAN under enforced RBAC, across service restarts, renewing indefinitely as long as
        /// they kept browsing. Measured 2026-08-01. Revocation has to take effect on the next
        /// request, so the store is consulted on every circuit and every <c>/auth/me</c>.</para>
        ///
        /// <para>A missing or disabled record resolves to <see cref="AppRoles.Viewer"/>, never to
        /// the cookie's claim. The caller decides whether viewer is a demotion or a refusal.</para>
        /// </summary>
        public PrincipalResolution ResolvePrincipal(string? provider, string? identityKey, string? sid = null)
        {
            if (string.IsNullOrWhiteSpace(identityKey))
                return new PrincipalResolution(PrincipalStatus.Unknown, AppRoles.Viewer, null);

            var key = AuthProviders.IsWindows(provider)
                ? WindowsIdentityKey.Normalize(identityKey)
                : identityKey;

            var user = FindUser(provider ?? string.Empty, key, sid);
            if (user == null)
                return new PrincipalResolution(PrincipalStatus.Unknown, AppRoles.Viewer, null);

            if (!user.Enabled)
                return new PrincipalResolution(PrincipalStatus.Disabled, AppRoles.Viewer, user);

            var role = AppRoles.IsValid(user.Role) ? user.Role.Trim().ToLowerInvariant() : AppRoles.Viewer;
            return new PrincipalResolution(PrincipalStatus.Active, role, user);
        }

        /// <summary>
        /// <b>THE authorization gate</b>, with the bootstrap hatch scoped to callers entitled to it.
        /// When RBAC is not enforced an eligible caller is allowed outright; otherwise the answer is
        /// the permission matrix. Every UI surface reaches this through
        /// <see cref="AppUserState.IsAuthorized(string)"/>, which supplies both the role and the
        /// eligibility — a page supplies neither, and that is the whole design.
        ///
        /// <para><b>THE THIRD ARGUMENT IS A CAPABILITY, NOT A BOOL — closed 2026-08-17, the lane
        /// after the chokepoint.</b> Until that day this method was <c>public</c> and took
        /// <c>bootstrapEligible</c> as an ordinary <c>bool</c>, so a page holding an
        /// <c>RbacService</c> could pass a literal <c>true</c> and reproduce the deleted two-argument
        /// overload's semantics exactly — that overload was nothing but <c>=&gt; IsAuthorized(role,
        /// permission, bootstrapEligible: true)</c>. That was not hypothetical: MEASURED at 8959044,
        /// <c>@if (!RbacService.IsAuthorized(UserState.Role, "settings", true))</c> planted in
        /// <c>Pages/Settings.razor</c> — the page that carried this very defect for 77 days — built
        /// with 0 errors. The parameter is now
        /// <see cref="AppUserState.BootstrapEligibilityProof"/>, a sealed class whose only
        /// constructor is private to the token type itself: it mints one instance at type
        /// initialisation and hands it to a private field of <see cref="AppUserState"/>, and THAT
        /// instance is the only one this method accepts (<see cref="AppUserState.IsTheMintedProof"/>,
        /// added in the same lane's fix round when a compiling mint defeated the modifier — see the
        /// paragraph below). MEASURED on the same page after the
        /// change, one plant at a time, each exactly one error: the literal <c>true</c> is
        /// <c>CS1503</c> (cannot convert from <c>bool</c>), <c>new
        /// AppUserState.BootstrapEligibilityProof()</c> is <c>CS0122</c> (inaccessible due to its
        /// protection level), and a subclass carrying its own constructor is <c>CS0509</c> (cannot
        /// derive from sealed type). The control that makes those mean something is the first plant
        /// building clean at the base SHA.</para>
        ///
        /// <para><b>What is still NOT closed, so no sentence here is read as more than it is.</b>
        /// (1) <c>null</c> is spellable from anywhere, and deliberately so — it is the DENY case, and
        /// a page writing it gets a caller with no hatch, which is the safe answer, pinned by
        /// <c>RbacChokepointTests.ANullProofDeniesWhereARealProofWouldHaveGranted</c>. MEASURED on
        /// the same page: <c>IsAuthorized(role, perm, default)</c> BUILDS, 0 errors — because
        /// <c>default</c> on a reference type is <c>null</c>, which denies. That plant is the reason
        /// the token is a class: on a struct, <c>default</c> would have been a constructor-free
        /// instance and the same line would have GRANTED. (2) A caller can still OBTAIN an instance
        /// without naming the constructor — <c>Activator.CreateInstance(..., nonPublic: true)</c>, or
        /// <c>[UnsafeAccessor(UnsafeAccessorKind.Constructor)]</c>, which is a declaration the
        /// compiler accepts rather than a reflection call. That is why this method tests IDENTITY:
        /// both of those produce a second instance, and a second instance denies (measured — the
        /// UnsafeAccessor plant granted before the check and denies after it). (3) THEFT of the one
        /// minted instance out of <c>AppUserState</c>'s private field grants, because the stolen token
        /// is the real one. Nothing in-process closes that; the census in
        /// <c>RbacChokepointTests.NothingInTheAssemblyReachesTheBootstrapProof</c> is what stands
        /// against it, together with
        /// <c>RbacServerModeLockoutTests.NoShippedUiComputesAnAuthorizationDecisionItself</c>, which
        /// bans a page computing the verdict at all whatever the third argument is.</para>
        ///
        /// <para><b>DELETED 2026-08-17: <c>public bool IsAuthorized(string role, string permission)</c>.</b>
        /// The fail-open two-argument overload stood here. It passed
        /// <c>bootstrapEligible: true</c> unconditionally, so a page calling it returned true for
        /// ANY caller on an unconfigured install — including one arriving over the LAN. It had zero
        /// callers in the tree (measured exhaustively 2026-08-17), so nothing needed rerouting and
        /// it is simply gone. <b>MEASURED 2026-08-17</b>, each shape compiled in a shipped UI file:
        /// calling it with two arguments is <c>CS7036</c> ("no argument given that corresponds to the
        /// required parameter 'bootstrapEligible'"), and naming it as a method group is <c>CS0123</c>
        /// ("no overload for 'IsAuthorized' matches delegate"). It is <b>not</b> <c>CS1061</c> — that
        /// code means the type has no such member, and the name still resolves, to the three-argument
        /// gate this comment documents. An earlier draft of this line said CS1061; it was never
        /// measured.</para>
        ///
        /// <para>Its history is kept here rather than discarded, because the history is the argument
        /// for the deletion. This is the record of two METHODS closed by the compiler on that day;
        /// the eligibility TERM was closed the same way one lane later, and the paragraphs above say
        /// what each of the three closes and what it does not. The surviving gate is
        /// <see cref="IsAuthorized(string, string, AppUserState.BootstrapEligibilityProof)"/> — the method this comment documents —
        /// reached by every UI surface through <see cref="AppUserState.IsAuthorized(string)"/>.</para>
        ///
        /// <para><b>What the old doc's claim rested on, and what it did not (corrected three times on
        /// 2026-08-15, rounds 1-3 of one lane).</b> It was written as a property of the codebase and
        /// was a property of neither the code nor the test. Three separate holes, all now
        /// closed:</para>
        /// <list type="number">
        ///   <item><description>The test carried an allowlist, <c>KnownOffenders</c>, holding
        ///   <c>Pages/AuditLogViewer.razor</c> from 2026-08-01. The census named that page on every
        ///   run and was told to ignore it. Measured live: on a fresh browser-hosted install the page
        ///   refused a loopback caller the hatch had already admitted everywhere else. The allowlist
        ///   was emptied, and a test held it empty until 2026-08-17, when both were retired with the
        ///   scan they belonged to. The lesson outlived them and is restated in
        ///   <c>RbacChokepointTests</c>: an exemption is permanent by default, so it may never rest
        ///   on a promise that another lane will land the fix.</description></item>
        ///   <item><description>The census banned ONE token, <c>RbacService.HasPermission(</c>, while
        ///   this paragraph claimed it banned both. The unguarded half was the dangerous one:
        ///   <c>HasPermission</c> fails CLOSED, whereas THIS overload passes
        ///   <c>bootstrapEligible: true</c> unconditionally, so a page calling it would return true
        ///   for any caller on an unconfigured install — the LAN outcome named two paragraphs up.
        ///   Nothing in the tree calls it TODAY (measured 2026-08-15), but this shape is not
        ///   hypothetical: <c>Pages/Settings.razor</c> gated on this overload from 84b944e
        ///   (2026-05-16) to a5497e5 (2026-08-01), 77 days, on the app's largest settings surface.
        ///   a5497e5 replaced it with <c>UserState.IsAuthorizedWithBreakGlass("settings")</c> and
        ///   created the census in the SAME commit — so the census never overlapped the live call, and
        ///   would not have named it if it had. Read "the hole was in the instrument" as luck of
        ///   timing, not as a property of the codebase.
        ///   <c>NoShippedUiCallsTheTwoArgIsAuthorizedDirectly</c> was added for it, and retired on
        ///   2026-08-17 when the overload it banned was deleted.</description></item>
        ///   <item><description>Both of those fixes banned a RECEIVER, and a receiver is a name the
        ///   page chooses. This overload is an INSTANCE method, so it is always called through a
        ///   variable — and <c>@inject RbacService Rbac</c> renames that variable in one line.
        ///   <c>Pages/ServerDocs.razor:11</c> held exactly that inject, dangling and unused, from
        ///   bec0d4e. MEASURED at 3fe19a3: a gate planted through it —
        ///   <c>@if (!Rbac.IsAuthorized(UserState.Role, "settings"))</c> — compiled with 0 errors and
        ///   passed all 88 census tests, exit 0. The same fact rereads the item above: the historical
        ///   <c>Pages/Settings.razor</c> call was an instance call too (<c>a5497e5^:25</c> injects the
        ///   service under the variable name <c>RbacService</c>, and <c>:35</c> calls through it), so
        ///   the regex named it by coincidence of that name and not because it was a static call.
        ///   Closed by <c>NoShippedUiComputesAnAuthorizationDecisionItself</c>, which asks what the
        ///   CALL is — any <c>HasPermission(</c>, or any <c>IsAuthorized(</c> with two or more
        ///   arguments — and never who the receiver is. Argument count is fixed by the overload, not
        ///   chosen by the caller, so it survives every rename.
        ///   <c>TheOnlyShippedUiFilesHoldingAnRbacServiceInstanceAreThePinnedThree</c> additionally
        ///   holds the set of shipped UI files that may hold the service at Login, Onboarding and
        ///   Settings — the three that administer RBAC. ServerDocs' inject and a second dangling one
        ///   at <c>Pages/ReportBundles.razor:12</c> were deleted the same day.</description></item>
        ///   <item><description><b>2026-08-16, round 4 — three holes in the INSTRUMENT, not in the
        ///   tree.</b> Rounds 1-3 each closed a way a page could SPELL a bypass; round 4 closed three
        ///   ways the census could fail to LOOK. (a) Four of its regexes spelled the type qualifier
        ///   <c>[\w.]*</c>, which cannot match a colon, so
        ///   <c>@inject global::SQLTriage.Data.Services.RbacService Rbac</c> acquired the service
        ///   unseen. (b) A line-initial <c>/* … */</c> span in Razor MARKUP is not a comment — Razor
        ///   emits the slashes as literal text and compiles every <c>@</c> transition between them,
        ///   PROVED by planting an invalid member inside one in <c>Pages/ServerDocs.razor</c> and
        ///   getting <c>CS1061</c> at that line, while the identical lines inside that file's
        ///   <c>@code</c> block built clean — yet the census blanked the span as prose. (c) The two
        ///   scans round 3 added had no tail-blindness guard of their own. The method-group
        ///   indirection round 3 documented as a limit (<c>_gate = Rbac.IsAuthorized;</c>, then
        ///   <c>_gate(role, permission)</c> with no banned name at the call) is now an offence too.
        ///   All three holes were LATENT — a raw grep proved the tree at e5ef5cc hid nothing in any of
        ///   them — and all three were proved by exercise: planted together they compiled with 0
        ///   errors and left the e5ef5cc census green (41/41 in the census class, 740/740 across the
        ///   whole Rbac filter, exit 0), and with the fixes in place the same plants are named by
        ///   file and line.</description></item>
        ///   <item><description><b>2026-08-16, round 5 — round 4 closed three holes and left three
        ///   SIBLINGS of them open, all silent.</b> (a) The ruling behind (b) above was "a Razor
        ///   MARKUP span is not a comment", and it reached one of the two comment strippers. The
        ///   other still blanked any line beginning <c>//</c>, <c>///</c> or <c>*</c> wherever it
        ///   stood — all three are text in markup, PROVED by <c>CS1061</c> on each — so a fail-open
        ///   gate written on such a line, reaching the service through a two-line <c>[Inject]</c>,
        ///   was invisible to every scan. (b) The holder pin claimed "the four DI acquisition forms …
        ///   any spelling of the type" while matching one LINE at a time, so the ordinary two-line
        ///   <c>[Inject]</c> spelling, a verbatim <c>@RbacService</c>, and a generic split across
        ///   lines all acquired the service unseen; the verbatim spelling defeated both import bans
        ///   too, so the layer named as the pin's backstop shared the blind spot. (c) Round 4's
        ///   sentence "this closes the run-away class by construction … not as silence" was false as
        ///   written: a <c>/*</c> inside a STRING LITERAL in a <c>@code</c> block still opened a span
        ///   whose <c>*/</c> was hunted unbounded into markup, and one in
        ///   <c>Pages/ScheduledTasks.razor</c> blanked a live static two-argument gate 200 lines
        ///   below. All three were LATENT at 28f7669 and all three were PROVED in the tree: planted
        ///   together they compiled with 0 errors and left the census at 59/59 and the whole Rbac
        ///   filter at 758/758, exit 0; with the fixes in place each is named by file and line. The
        ///   comment strippers now read the language on both sides, the acquisition scan reads the
        ///   whole file, and the block-comment walk reads each C# region as C# — skipping literals
        ///   and requiring the close inside the same region.</description></item>
        ///   <item><description><b>2026-08-17 — the structural close, and the end of the list.</b>
        ///   Rounds 6 and 7 closed four more <c>@*</c> and literal-scope siblings and the round-7
        ///   re-gate predicted an eighth spelling before anyone wrote it, which is the point at which
        ///   the arms race was ruled unwinnable: every round closed a SPELLING, and spellings are
        ///   unbounded. The two methods every round was chasing are now unreachable from a page by
        ///   the language rather than by a scan — this overload deleted, and
        ///   <see cref="HasPermission"/> made <c>private</c>. All eight of rounds 4-7's evasion
        ///   shapes were re-planted in a shipped page on this date and every one failed to COMPILE —
        ///   six <c>CS0122</c> naming the private matrix, two <c>CS7036</c> calling the deleted
        ///   overload. (An earlier draft of this line said CS1061 for the deleted overload; the
        ///   measurement says CS7036, and the correction is recorded here rather than quietly
        ///   applied.) A spelling that cannot compile does not need to be detectable — but that day
        ///   closed the two METHODS and not the fail-open capability, which survived one more lane in
        ///   this gate's third parameter.</description></item>
        ///   <item><description><b>2026-08-17, the capability-token lane — the last spellable
        ///   shape.</b> The <c>bool bootstrapEligible</c> parameter became
        ///   <see cref="AppUserState.BootstrapEligibilityProof"/>, minted at one private site inside
        ///   <see cref="AppUserState.IsAuthorized(string)"/>. A page can no longer WRITE the term
        ///   that grants the hatch in ordinary C#; the spellings it can write are <c>null</c> and
        ///   <c>default</c>, which deny. Measured plants and their codes are in the second paragraph
        ///   of this comment.</description></item>
        ///   <item><description><b>2026-08-17, the fix round of that same lane — where the close
        ///   actually is.</b> Accessibility turned out not to hold it:
        ///   <c>[UnsafeAccessor(UnsafeAccessorKind.Constructor)]</c> mints the proof from anywhere in
        ///   the assembly in source that BUILDS with 0 errors, using no reflection API, so the
        ///   verifier reproduced the deleted overload's semantics one file away from a page. The
        ///   eligibility term is now settled by IDENTITY —
        ///   <see cref="AppUserState.IsTheMintedProof"/>, a reference comparison against the single
        ///   minted instance — so a forged token denies. What is left is THEFT of that instance, which
        ///   is the reflection class and is held by a census, not by the
        ///   language.</description></item>
        /// </list>
        /// <para>The rounds above are the record of a TEXT LINT, and it is kept because the lint's
        /// defeat is the argument for the modifier. The boundary was always that
        /// <see cref="AppUserState"/> is the only type that knows <c>IsBootstrapEligible</c>. The
        /// compiler enforces that boundary for two of the three surfaces the rounds were about — the
        /// deleted two-argument overload and the private matrix — and it is the wrong instrument for
        /// the third. This gate's eligibility term is enforced at RUNTIME by
        /// <see cref="AppUserState.IsTheMintedProof"/>, because an accessibility modifier cannot stop
        /// <c>[UnsafeAccessor]</c> and no version of this type could have. What remains of the census
        /// is described in its own header — see <c>RbacServerModeLockoutTests</c>.</para>
        /// </summary>
        /// <param name="bootstrapProof">
        /// Non-null when this caller may use the unconfigured-install hatch: the WPF desktop, or a
        /// LOOPBACK connection. There is no third case — see <see cref="IsBootstrapEligible"/>.
        ///
        /// <para><b><c>null</c> FAILS CLOSED and that is a deliberate, pinned decision.</b> A null
        /// proof is read as "no hatch", never as "unknown, be generous": the caller gets the plain
        /// permission matrix, which for an unauthenticated circuit is viewer. Every remote client
        /// arrives this way, and so does every caller that simply has no proof to give — a test, a
        /// future service, a page. The one direction that costs anything is the safe one: a
        /// legitimate hatch caller whose proof went missing sees a denial, not an escalation.</para>
        /// </param>
        public bool IsAuthorized(string role, string permission, AppUserState.BootstrapEligibilityProof? bootstrapProof)
        {
            // IDENTITY is the claim, not presence. The token is minted once, held privately by
            // AppUserState, and handed over only on the branch where the circuit is
            // bootstrap-eligible; AppUserState is the only type that can say whether THIS reference is
            // that one. Presence was the original test and it was defeated by measurement: an
            // [UnsafeAccessor(UnsafeAccessorKind.Constructor)] declaration compiles anywhere in the
            // assembly and mints a second instance, so "a token exists" was manufacturable by any
            // file. No token, or a token we did not mint, means no hatch.
            var bootstrapEligible = AppUserState.IsTheMintedProof(bootstrapProof);

            if (!IsRbacEnforced())
                return bootstrapEligible || HasPermission(role, permission);

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
        /// Sets a password for a user, identified by either its identity key or its record Id.
        /// The plaintext is hashed with Argon2id before storage; plaintext is never persisted.
        /// </summary>
        /// <param name="emailOrId">
        /// The user's identity key OR its <see cref="RbacUser.Id"/>.
        ///
        /// <para>Accepting the Id is not convenience, it is a defect fix. Onboarding created the
        /// first admin and then called <c>SetPassword(admin.Id, …)</c> against a lookup that only
        /// matched on Email. A GUID never matches an email, so it took the not-found branch,
        /// logged a warning nobody read, and wrote the admin to rbac-users.json with
        /// <c>PasswordHash: null</c> — an account that could never sign in. The call site is
        /// fixed too; this makes the same mistake impossible to make again silently.</para>
        /// </param>
        /// <returns>True when a user was found and updated.</returns>
        public bool SetPassword(string emailOrId, string password)
        {
            if (string.IsNullOrEmpty(password))
                throw new ArgumentException("Password cannot be empty", nameof(password));

            var hash = HashPassword(password);
            lock (_lock)
            {
                var user = _users.FirstOrDefault(u =>
                                u.Email.Equals(emailOrId, StringComparison.OrdinalIgnoreCase))
                           ?? _users.FirstOrDefault(u =>
                                u.Id.Equals(emailOrId, StringComparison.OrdinalIgnoreCase));

                if (user == null)
                {
                    _logger.LogWarning("SetPassword called for unknown user: {Principal}", emailOrId);
                    return false;
                }

                user.PasswordHash = hash;
                if (!SaveUsers(StoreWriteIntent.FromLoadedStore))
                    return false;   // the hash is not on disk; saying otherwise is how Onboarding
                                    // shipped an account that could never sign in.
                _logger.LogInformation("Password set for user: {Principal}", user.Email);
            }

            // This is a state change like any other, and it is the one that completes the
            // Onboarding path: local passwords on, admin present, hash written — the admin can now
            // sign in, so bootstrap is done and the hatch latches shut. It was the only mutator
            // that reported nothing, which is why the two calls are now one.
            ReportEnforcementPosture("password set");
            return true;
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
                // Email-class providers only. A Windows-provider record must not be signable
                // with a password: its authority is the Windows credential, and a password on
                // that record would be a second, weaker key to the same principal.
                user = _users.FirstOrDefault(u =>
                    AuthProviders.ClassOf(u.Provider) == AuthProviders.ClassEmail
                    && u.Email.Equals(email, StringComparison.OrdinalIgnoreCase));
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
                SaveUsers(StoreWriteIntent.FromLoadedStore);
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
                _users = ConfigFileHelper.Load<List<RbacUser>>(_usersPath, _jsonOptions, out _usersLoad, out _usersQuarantine);
                _logger.LogInformation("Loaded {Count} RBAC users (store: {Outcome})", _users.Count, _usersLoad);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load RBAC users");
                _users = new();
                _usersLoad = ConfigLoadOutcome.Unreadable;
            }
        }

        /// <summary>
        /// The write chokepoint for <c>rbac-users.json</c>, and the reason it is guarded the same way
        /// <see cref="SaveConfig"/> is: a truncated user store deserialises to an EMPTY LIST, which
        /// is byte-identical in memory to an install that has no users yet. Every mutator here then
        /// writes that empty list back plus whatever it just did — one add, one login — and every
        /// account, role and password hash on the install is gone, with the damaged file that would
        /// have proved it overwritten in the same stroke.
        ///
        /// <para>Intent is REQUIRED, so a new mutator has to answer the question at the call site.
        /// All seven existing ones answer <see cref="StoreWriteIntent.FromLoadedStore"/>: none of
        /// them is an operator saying "discard the file". The enforcement banner's advice for this
        /// store is to restore the file, and unlike the config store there is no re-enter-and-save
        /// repair, so no UI path passes the other value.</para>
        ///
        /// <para><b>Returning false means NOTHING reached the disk, for either reason</b> — refused,
        /// or allowed and then thrown (full disk, ACL, lock). The rule the mutators apply to it is
        /// one line: <i>on a false return, roll the in-memory change back, so what this process
        /// enforces is what is on disk.</i> Before 2026-08-04 they ignored it on the IO path and
        /// returned <see cref="StoreWriteOutcome.Saved"/>, so <c>Settings ▸ Access Control</c>
        /// announced an added admin, an audit line recorded it, and the account existed only until
        /// the next restart. The guard against the damaged store was pre-checked and correct; the
        /// claim that followed it was not conditioned on the write.</para>
        /// </summary>
        private bool SaveUsers(StoreWriteIntent intent)
        {
            if (ConfigFileHelper.WouldOverwriteUnreadStore(_usersLoad, intent))
            {
                _logger.LogWarning(
                    "[RBAC] Refused to write {Path}: it exists and did not load ({Outcome}), so the list in "
                    + "memory is not this install's accounts and writing it would delete them. The file is unchanged.",
                    _usersPath, _usersLoad);
                return false;
            }

            try
            {
                ConfigFileHelper.Save(_usersPath, _users, _jsonOptions);

                // The store on disk is now this object, written whole. A damaged store that an
                // operator repaired through the UI must clear the lapse without a restart —
                // otherwise the banner outlives the defect and stops meaning anything.
                _usersLoad = ConfigLoadOutcome.Loaded;
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save RBAC users");
                return false;
            }
        }
    }
}
