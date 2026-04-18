/* In the name of God, the Merciful, the Compassionate */

using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// Scoped service that holds the current user's role for the duration of a Blazor circuit.
    ///
    /// In WPF mode (BlazorWebView): always returns Admin — single-user desktop, no auth needed.
    /// In Server mode (Kestrel): fetches role from /auth/me on first access, cached for the circuit.
    ///
    /// Usage: @inject AppUserState UserState
    ///        RbacService.HasPermission(UserState.Role, "acknowledge_alerts")
    /// </summary>
    public class AppUserState
    {
        private readonly ServerModeService _serverMode;
        private readonly ILogger<AppUserState> _logger;
        private string? _cachedRole;

        public AppUserState(ServerModeService serverMode, ILogger<AppUserState> logger)
        {
            _serverMode = serverMode;
            _logger = logger;
        }

        /// <summary>
        /// Returns the current user's role. In WPF mode always "admin".
        /// In server mode, returns the role from the session cookie or "viewer" if unauthenticated.
        /// Call InitAsync() once in OnInitializedAsync of the root layout or page if needed.
        /// Synchronous access returns the cached value (defaults to "admin" until fetched).
        /// </summary>
        public string Role => _cachedRole ?? AppRoles.Admin;

        public bool IsAdmin    => Role == AppRoles.Admin;
        public bool IsOperator => Role is AppRoles.Admin or AppRoles.Operator;
        public bool IsViewer   => true; // all roles can view

        /// <summary>
        /// Fetches the current user's role from /auth/me (server mode only).
        /// Safe to call multiple times — cached after first successful fetch.
        /// In WPF mode this is a no-op.
        /// </summary>
        public async Task InitAsync()
        {
            // WPF desktop — always admin, no network call needed
            if (!_serverMode.IsRunning)
            {
                _cachedRole = AppRoles.Admin;
                return;
            }

            if (_cachedRole != null)
                return;

            try
            {
                using var http = new HttpClient();
                // Kestrel listens on the configured port — read from ServerModeService if available
                var port = _serverMode.Port > 0 ? _serverMode.Port : 5150;
                var response = await http.GetStringAsync($"http://localhost:{port}/auth/me");
                var doc = JsonDocument.Parse(response);
                var root = doc.RootElement;

                if (root.TryGetProperty("authenticated", out var auth) && auth.GetBoolean() &&
                    root.TryGetProperty("role", out var role))
                {
                    _cachedRole = role.GetString() ?? AppRoles.Viewer;
                }
                else
                {
                    _cachedRole = AppRoles.Viewer;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("AppUserState.InitAsync — could not fetch /auth/me: {Msg}", ex.Message);
                // Fail safe: if server mode but can't read role, default to viewer
                _cachedRole = AppRoles.Viewer;
            }
        }

        /// <summary>
        /// Sets the role directly — used in WPF mode and unit tests.
        /// </summary>
        public void SetRole(string role) => _cachedRole = role;
    }
}
