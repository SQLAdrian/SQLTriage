/* In the name of God, the Merciful, the Compassionate */

namespace SQLTriage.Data.Services
{
    // BM:HostEnvironmentInfo.Class — tells a circuit which host it is running under
    /// <summary>
    /// Which host a DI container belongs to. Registered once per container, so a process
    /// running BOTH the WPF shell and a Kestrel listener answers differently on each side.
    ///
    /// <para>This replaces reading <c>ServerModeService.IsRunning</c> to decide whether auth
    /// applies, which was wrong in both directions:</para>
    /// <list type="bullet">
    /// <item><b>False negative.</b> The headless host (<c>--server</c> / the installed Windows
    /// service) never calls <c>ServerModeService.StartAsync</c>, so <c>IsRunning</c> was false
    /// and every browser circuit took the "WPF desktop, single user" branch and resolved to
    /// <b>admin</b>. On a listener bound to 0.0.0.0 that made every page — /settings, /query,
    /// /server-configuration — reachable as admin by any unauthenticated client on the network.</item>
    /// <item><b>False positive.</b> In the WPF process, turning server mode ON flipped
    /// <c>IsRunning</c> true for the DESKTOP container too, demoting the desktop user's own
    /// circuits to viewer.</item>
    /// </list>
    ///
    /// <para>The flag is a property of the container, not of the process, so neither confusion
    /// is expressible.</para>
    /// </summary>
    public class HostEnvironmentInfo
    {
        /// <summary>
        /// True when circuits from this container are served to a BROWSER over Kestrel and are
        /// therefore subject to RBAC. False for the WPF BlazorWebView, which is a single-user
        /// desktop surface with no network listener and no authentication.
        /// </summary>
        public bool IsBrowserHosted { get; init; }

        /// <summary>The WPF desktop container: single user, always admin.</summary>
        public static HostEnvironmentInfo Desktop => new() { IsBrowserHosted = false };

        /// <summary>A Kestrel container: server mode, the headless host, or the Windows service.</summary>
        public static HostEnvironmentInfo BrowserHosted => new() { IsBrowserHosted = true };
    }
}
