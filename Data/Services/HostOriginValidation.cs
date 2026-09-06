/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace SQLTriage.Data.Services
{
    // BM:HostOriginValidation.Class — the raw-Host allow-list in front of the admission boundary
    /// <summary>
    /// THE HOST GATE. A request whose raw <c>Host</c> header is not a name this installation
    /// answers to is refused BEFORE <see cref="InteractiveAppAdmission"/> ever sees it.
    ///
    /// <para><b>Why this exists, and why it sits in front of admission rather than behind it.</b>
    /// <see cref="InteractiveAppAdmission"/> exempts loopback — a caller arriving on the box's own
    /// loopback interface is admitted without a sign-in, because a person on the box already owns
    /// it. That exemption is correct and load-bearing, and it is also the one thing a DNS-rebinding
    /// attack is built to reach. Rebinding is a technique for making a victim's browser send
    /// requests that arrive on the target's loopback interface while carrying an attacker-chosen
    /// <c>Host</c> header: the browser's same-origin policy is satisfied (the origin's NAME did not
    /// change, only the address behind it), the socket is loopback, and admission waves it through.
    /// A check that ran BEHIND admission would never see the request it exists to refuse, because
    /// admission would already have admitted it. So this runs first.</para>
    ///
    /// <para><b>The rule.</b> The <c>Host</c> header names the origin the browser believes it is
    /// talking to. A rebinding browser sends the ATTACKER'S name (<c>attacker.example</c>), never
    /// <c>localhost</c> or this machine's real name — it cannot, because the same-origin policy it
    /// is exploiting is what forbids it from forging the name. So an allow-list of the names this
    /// server actually answers to closes the path: the attacker's page can aim requests at loopback,
    /// but it cannot make the browser label them with a name on the list.</para>
    ///
    /// <para><b>The address of the socket is NOT consulted here, deliberately.</b> This gate reads
    /// only <c>Request.Host</c>, and it does not exempt loopback — exempting loopback would be
    /// circular, because loopback is the exact origin the rebinding request arrives on and the thing
    /// this gate protects. It is the mirror of the ruling in <see cref="AppUserState.IsLoopbackAddress"/>
    /// (which reads the socket and never a header): there the socket is trusted and the header is
    /// not; here the header is judged and the socket is irrelevant. The two together are the whole
    /// control — admission decides WHO on the strength of the socket, this decides WHICH NAME on the
    /// strength of the header, and neither reads the other's input.</para>
    ///
    /// <para><b>Forwarding headers are NOT trusted (raw Host only).</b> This product is not
    /// supported behind a reverse proxy (ruled 2026-09-01), so <c>X-Forwarded-Host</c> and its kin
    /// are never read — for the same reason <see cref="AppUserState.IsLoopbackAddress"/> refuses to
    /// read <c>X-Forwarded-For</c>: a forwarding header is attacker-supplied, and trusting one would
    /// hand every remote caller a name off the list. If SQLTriage is ever put behind a proxy, this
    /// gate must be taught about it deliberately.</para>
    ///
    /// <para><b>The allow-list is DERIVED from <c>ServiceBindAddress</c>, resolved ONCE at startup,
    /// and logged loudly.</b> The bind address and the Host allow-list decide the same question —
    /// "which callers is this listener for" — from two ends, so the list is derived from the same
    /// <see cref="WindowsServiceHost.ResolveBindAddress"/> the bind uses, and the two cannot
    /// disagree:</para>
    /// <list type="bullet">
    /// <item><b>Loopback bind</b> → a loopback-only list: <c>localhost</c>, <c>127.0.0.1</c>,
    ///   <c>::1</c>, plus any operator-configured names. Nothing off the box was ever meant to reach
    ///   it, so there is nothing to break.</item>
    /// <item><b>Every-interface bind (the shipped default) or a specific-address bind</b> → the
    ///   machine's OWN names: the loopback set, the hostname, the FQDN, every local IP, plus any
    ///   operator-configured names. This is what keeps <c>http://sqlbox:5155/</c> working — the
    ///   product ships <c>ServiceBindAddress:"any"</c> precisely so it can be reached by name from
    ///   the LAN, and a loopback-only list would silently 400 every such install. Deriving is the
    ///   only non-breaking real control.</item>
    /// </list>
    ///
    /// <para><b>No silent lockout.</b> A CNAME an internal DNS zone points at the box, or a
    /// load-balancer name, is invisible to the process — any list derived from what the machine can
    /// see about itself will be incomplete for somebody. So the failure direction is a STARTUP
    /// MESSAGE, never a silent per-request 400: if the machine's own names cannot be enumerated, or
    /// the resolved list is surprisingly narrow (loopback only), the resolved list is logged at
    /// startup as a warning naming exactly what resolved and how to add more (the
    /// <see cref="AllowListConfigKey"/> setting). A by-name install that is not on the list still
    /// gets a refusal — but the operator can diagnose it from the startup log rather than staring at
    /// an unexplained 400.</para>
    ///
    /// <para><b>The refusal is loud.</b> Mirroring <see cref="InteractiveAppAdmission"/>: a distinct
    /// response header (<see cref="RefusalHeader"/>), a plain-text body naming the way to fix it,
    /// and a bounded, de-duplicated log line naming the refused Host and the resolved allow-list.
    /// Kestrel's own <c>AllowedHosts</c> would do part of this job but refuses with a bare 400 and
    /// no explanation, which is why <c>AllowedHosts</c> in <c>appsettings.json</c> is left at
    /// <c>"*"</c> and THIS explicit, logged check is the control.</para>
    /// </summary>
    public static class HostOriginValidation
    {
        /// <summary>
        /// Response header stamped on every Host refusal. Distinct from
        /// <see cref="InteractiveAppAdmission.RefusalHeader"/> so a capture can tell a Host refusal
        /// (this gate) from a sign-in refusal (admission) at a glance, and so the runtime tests can
        /// tell a deliberate Host refusal from an incidental 400 raised by something else.
        /// </summary>
        public const string RefusalHeader = "X-SQLTriage-Host";

        /// <summary>Value of <see cref="RefusalHeader"/> on a refusal.</summary>
        public const string RefusalHeaderValue = "host-not-allowed";

        /// <summary>
        /// Status of a Host refusal. 400 Bad Request — the request named an authority this server
        /// does not answer to. It is the same code Kestrel's <c>AllowedHosts</c> filter uses, but
        /// unlike that bare 400 it arrives with <see cref="RefusalHeader"/>, <see cref="RefusalBody"/>
        /// and a log line, which is the whole difference between a control and an outage.
        /// </summary>
        public const int RefusalStatusCode = 400;

        /// <summary>
        /// The body of a Host refusal. Plain text, naming the fix — an operator reading a 400 in a
        /// browser console or a curl transcript should not have to guess whether the server is
        /// broken. The refused Host itself is NOT reflected into the body (it is attacker-supplied);
        /// it is named in the log instead.
        /// </summary>
        public const string RefusalBody =
            "SQLTriage refused this request because its Host header is not a name this server answers "
            + "to. This protects the server's loopback interface from DNS-rebinding. If you reached "
            + "this server by a legitimate name, add that name to the '" + AllowListConfigKey
            + "' setting and restart the service; the resolved allow-list is written to the log at "
            + "startup.";

        /// <summary>
        /// Configuration key (a string array) for names an operator adds to the allow-list beyond
        /// the ones the machine can see about itself — a CNAME, a load-balancer name, a vanity host.
        /// This is the "how to add hosts" named by the startup message when the derived list is
        /// incomplete. Deliberately NOT Kestrel's <c>AllowedHosts</c>: that one drives Kestrel's own
        /// bare-400 filter, which this control replaces.
        /// </summary>
        public const string AllowListConfigKey = "HostOriginAllowList";

        /// <summary>
        /// Key on <c>WebApplication.Properties</c> recording that this gate was installed. Read by
        /// <see cref="InteractiveAppAdmission.UseSqlTriageAdmission"/>, which refuses to build a
        /// pipeline that puts admission ahead of the Host check — the same composition guarantee
        /// <see cref="InteractiveAppAdmission.AdmissionInstalledKey"/> gives auth.
        /// </summary>
        internal const string HostOriginInstalledKey = "SQLTriage.HostOriginValidation.Installed";

        /// <summary>The loopback names accepted under every bind mode.</summary>
        private static readonly string[] LoopbackTokens = { "localhost", "127.0.0.1", "::1" };

        /// <summary>The loopback names in normalized form — used to tell "loopback only" from "has a
        /// name a remote caller could use", which is the "surprisingly narrow" trip-wire.</summary>
        internal static readonly HashSet<string> LoopbackNormalized =
            new(LoopbackTokens.Select(NormalizeHostToken), StringComparer.Ordinal);

        // ── The resolved allow-list ──────────────────────────────────────

        /// <summary>
        /// The set of host names this installation answers to, resolved once at startup. Matching is
        /// on the host NAME only (the port is not a security discriminator — the service only listens
        /// on its own ports), case-insensitively, and with IP literals canonicalised so
        /// <c>[::1]</c>, <c>::1</c> and <c>0:0:0:0:0:0:0:1</c> are one entry.
        /// </summary>
        public sealed class HostAllowList
        {
            /// <summary>How the list was derived, for the startup log and the tests.</summary>
            public enum BindMode
            {
                /// <summary>Loopback bind → loopback-only names.</summary>
                Loopback,

                /// <summary>Every-interface bind (the default) → the machine's own names.</summary>
                AnyInterface,

                /// <summary>A specific-address bind → the machine's own names plus the bound address.</summary>
                SpecificAddress,
            }

            private readonly HashSet<string> _match;

            /// <summary>Which derivation produced this list.</summary>
            public BindMode Mode { get; }

            /// <summary>The accepted names, readable and de-duplicated, for the startup log.</summary>
            public IReadOnlyList<string> Entries { get; }

            internal HostAllowList(BindMode mode, IEnumerable<string> tokens)
            {
                Mode = mode;
                _match = new HashSet<string>(StringComparer.Ordinal);
                var display = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var token in tokens)
                {
                    if (string.IsNullOrWhiteSpace(token)) continue;
                    var norm = HostOriginValidation.NormalizeHostToken(token);
                    if (norm.Length == 0) continue;
                    _match.Add(norm);
                    display.Add(token.Trim());
                }
                Entries = display.ToList();
            }

            /// <summary>Whether the raw <c>Host</c> header of a request is on the list. Reads the
            /// host NAME (<see cref="HostString.Host"/> strips the port); an empty Host is refused,
            /// because a request with no authority names no origin this server can vouch for.</summary>
            public bool IsHostAllowed(HostString host)
            {
                if (!host.HasValue) return false;
                var name = host.Host;
                if (string.IsNullOrEmpty(name)) return false;
                return _match.Contains(HostOriginValidation.NormalizeHostToken(name));
            }

            /// <summary>String overload — for the decision-table tests, which do not stand a socket
            /// up. Accepts a raw <c>host[:port]</c> header value.</summary>
            public bool IsHostAllowed(string? hostHeaderValue)
            {
                if (string.IsNullOrWhiteSpace(hostHeaderValue)) return false;
                return IsHostAllowed(new HostString(hostHeaderValue));
            }

            /// <summary>True when the list holds at least one name that is not loopback — i.e. a name
            /// a caller from another machine could actually use. False here is the "surprisingly
            /// narrow" state that has to surface at startup.</summary>
            internal bool HasNonLoopbackEntry => _match.Any(m => !HostOriginValidation.LoopbackNormalized.Contains(m));
        }

        /// <summary>
        /// Canonicalises a host token for matching: strips IPv6 brackets, canonicalises an IP
        /// literal (so <c>[::1]</c>, <c>::1</c> and the long form collapse to one), and lower-cases a
        /// host name. Used for both the stored allow-list and the incoming Host, so the two are
        /// compared in the same shape.
        /// </summary>
        internal static string NormalizeHostToken(string token)
        {
            var t = token.Trim();
            if (t.Length >= 2 && t[0] == '[' && t[^1] == ']') t = t.Substring(1, t.Length - 2);
            if (IPAddress.TryParse(t, out var ip)) return ip.ToString().ToLowerInvariant();
            return t.ToLowerInvariant();
        }

        /// <summary>
        /// Resolves the allow-list from configuration. Pure and side-effect-free (it does not log),
        /// so the decision can be exercised exhaustively without standing a host up — the same reason
        /// <see cref="WindowsServiceHost.ResolveBindAddress"/> and
        /// <see cref="InteractiveAppAdmission.Evaluate"/> are extracted. The bind mode is taken from
        /// the SAME <see cref="WindowsServiceHost.ResolveBindAddress"/> the listener uses, so the
        /// list and the bind cannot drift.
        /// </summary>
        /// <param name="description">A human sentence for the startup log naming how the list was derived.</param>
        /// <param name="startupWarning">
        /// Non-null when the list must be surfaced loudly at startup: the machine's names could not
        /// be enumerated, or the list came out loopback-only on a bind that expected remote callers.
        /// It names the resolved list and how to add to it — the "never a silent per-request 400"
        /// guarantee.
        /// </param>
        public static HostAllowList ResolveAllowList(
            IConfiguration configuration, out string description, out string? startupWarning)
        {
            startupWarning = null;

            var bind = WindowsServiceHost.ResolveBindAddress(configuration, out var bindDescription, out _);
            var configured = ReadConfiguredHosts(configuration);

            // Loopback bind → loopback-only names (+ operator extras). Nothing off the box was meant
            // to reach it, so there is nothing to break and no enumeration to do.
            if (bind != null && IPAddress.IsLoopback(bind))
            {
                var tokens = new List<string>(LoopbackTokens);
                tokens.AddRange(configured);
                description = $"loopback bind ({bindDescription}) resolved to a loopback-only Host allow-list";
                return new HostAllowList(HostAllowList.BindMode.Loopback, tokens);
            }

            // Every-interface bind (bind == null) OR a specific non-loopback address → the machine's
            // own names, so by-name LAN access keeps working. A specific bind additionally names its
            // own bound address explicitly; the socket is already narrowed to it, so a generous host
            // list here is defence in depth, never the control.
            var mode = bind == null ? HostAllowList.BindMode.AnyInterface : HostAllowList.BindMode.SpecificAddress;
            var names = new List<string>(LoopbackTokens);
            var enumerated = TryAddMachineNames(names, out var enumerationDetail);
            if (bind != null) names.Add(bind.ToString());
            names.AddRange(configured);

            var list = new HostAllowList(mode, names);

            description = mode == HostAllowList.BindMode.AnyInterface
                ? $"every-interface bind ({bindDescription}) resolved to a machine-name Host allow-list"
                : $"specific-address bind ({bindDescription}) resolved to a machine-name Host allow-list";

            // #5 NO SILENT LOCKOUT. If enumeration degraded, or the list is loopback-only on a bind
            // that expects remote callers, say so at STARTUP — naming what resolved and how to add —
            // so a by-name lockout is diagnosable rather than a silent per-request 400.
            if (!enumerated || !list.HasNonLoopbackEntry)
            {
                startupWarning =
                    "[HostOrigin] The machine's own names could not be fully enumerated"
                    + (enumerationDetail != null ? $" ({enumerationDetail})" : "")
                    + $", so the Host allow-list may be incomplete. It resolved to: "
                    + $"{string.Join(", ", list.Entries)}. A request that reaches this server by a name "
                    + "NOT on that list will be refused with a 400. If this server is reached by a name "
                    + $"the enumeration missed (a CNAME, a load-balancer name), add it to the "
                    + $"'{AllowListConfigKey}' setting and restart. This is surfaced here, at startup, so "
                    + "a by-name lockout can be diagnosed rather than appearing as an unexplained 400.";
            }

            return list;
        }

        /// <summary>Reads <see cref="AllowListConfigKey"/> (a string array) — the operator's extra
        /// names. A malformed value yields no extras rather than a startup crash; the derived machine
        /// names still stand.</summary>
        private static string[] ReadConfiguredHosts(IConfiguration configuration)
        {
            try
            {
                var arr = configuration.GetSection(AllowListConfigKey).Get<string[]>();
                if (arr != null)
                    return arr.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToArray();
            }
            catch
            {
                /* malformed config → no extras */
            }
            return Array.Empty<string>();
        }

        /// <summary>
        /// Adds the machine's own names to <paramref name="into"/>: the hostname, the FQDN, and every
        /// local IP. Returns false if it could not fully enumerate (which trips the startup warning).
        /// Local IPs come from <see cref="NetworkInterface"/> rather than only DNS, so every address a
        /// client could actually connect to is on the list — a name that resolves via DNS but an
        /// address the box answers on that DNS does not know about would otherwise be missed.
        /// </summary>
        private static bool TryAddMachineNames(List<string> into, out string? detail)
        {
            detail = null;
            var gotHostName = false;
            var gotIps = false;
            string? hostName = null;

            try
            {
                hostName = Dns.GetHostName();
                if (!string.IsNullOrWhiteSpace(hostName))
                {
                    into.Add(hostName);
                    gotHostName = true;
                }
            }
            catch (Exception ex)
            {
                detail = "Dns.GetHostName failed: " + ex.Message;
            }

            if (gotHostName)
            {
                try
                {
                    var entry = Dns.GetHostEntry(hostName!);
                    if (!string.IsNullOrWhiteSpace(entry.HostName)) into.Add(entry.HostName);
                    foreach (var ip in entry.AddressList) into.Add(ip.ToString());
                }
                catch (Exception ex)
                {
                    detail = Append(detail, "Dns.GetHostEntry failed: " + ex.Message);
                }
            }

            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                        into.Add(unicast.Address.ToString());
                }
                gotIps = true;
            }
            catch (Exception ex)
            {
                detail = Append(detail, "NetworkInterface enumeration failed: " + ex.Message);
            }

            return gotHostName && gotIps;
        }

        private static string Append(string? existing, string more)
            => string.IsNullOrEmpty(existing) ? more : existing + "; " + more;

        // ── The middleware ───────────────────────────────────────────────

        /// <summary>
        /// Installs the Host gate. Call it FIRST — ahead of <see cref="InteractiveAppAdmission"/>,
        /// which is why <see cref="InteractiveAppAdmission.UseSqlTriageFrontDoor"/> calls this before
        /// <c>UseSqlTriageAdmission</c> and <c>UseSqlTriageAdmission</c> throws if it did not.
        ///
        /// <para>The allow-list is resolved ONCE here, at composition, and captured by the
        /// middleware — a per-request re-resolve would re-enumerate the machine's interfaces on every
        /// asset fetch. The resolved list is logged loudly, and its startup warning (if any) with it.</para>
        /// </summary>
        public static WebApplication UseSqlTriageHostOrigin(this WebApplication app)
        {
            // The composition guarantee: recorded so UseSqlTriageAdmission can REFUSE TO BUILD a
            // pipeline that puts admission ahead of the Host check. (WebApplication implements
            // IApplicationBuilder explicitly, hence the cast.)
            ((IApplicationBuilder)app).Properties[HostOriginInstalledKey] = true;

            var allowList = ResolveAllowList(app.Configuration, out var description, out var startupWarning);

            Log.Information(
                "[HostOrigin] {Description}; {Count} host name(s) accepted: {Entries}. A request whose raw "
                + "Host header is not one of these is refused {Status} before admission. Forwarding headers "
                + "(X-Forwarded-Host) are NOT trusted. Add names via the '{ConfigKey}' setting.",
                description, allowList.Entries.Count, string.Join(", ", allowList.Entries),
                RefusalStatusCode, AllowListConfigKey);

            if (startupWarning != null) Log.Warning("{Warning}", startupWarning);

            app.Use(async (ctx, next) =>
            {
                if (allowList.IsHostAllowed(ctx.Request.Host))
                {
                    await next();
                    return;
                }

                Report(ctx, allowList);

                ctx.Response.Headers[RefusalHeader] = RefusalHeaderValue;
                ctx.Response.StatusCode = RefusalStatusCode;
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                await ctx.Response.WriteAsync(RefusalBody);
            });

            return app;
        }

        // ── Loud, bounded refusal logging ────────────────────────────────

        /// <summary>
        /// Host values already reported as refused, so a scanner rotating the Host header does not
        /// write an Information line per request. First refusal per distinct Host is Information (an
        /// operator should see a by-name request being refused — it is either an attack or a missing
        /// allow-list entry); the rest are Debug.
        /// </summary>
        private static readonly ConcurrentDictionary<string, byte> _reportedHosts =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The dedup set above is BOUNDED, exactly as <see cref="InteractiveAppAdmission"/>'s is: an
        /// entry per distinct refused Host, added and never removed, so a Host-rotating scanner would
        /// grow it for the process lifetime. It is only a log-noise optimisation, so a hard cap costs
        /// nothing real — past it a genuinely new Host logs at Debug rather than Information. The
        /// bound is the point; the exact number is not.
        /// </summary>
        internal const int MaxReportedHosts = 4096;

        /// <summary>True the first time this Host should get an Information line, false thereafter —
        /// and false, without growing the set, once <see cref="MaxReportedHosts"/> distinct Hosts have
        /// been recorded. Split out so the bound can be exercised without a full pipeline.</summary>
        internal static bool ShouldAnnounceRefusal(string host)
            => _reportedHosts.Count < MaxReportedHosts && _reportedHosts.TryAdd(host, 0);

        /// <summary>How many distinct refused Hosts are currently tracked. Test seam.</summary>
        internal static int ReportedHostCount => _reportedHosts.Count;

        /// <summary>Clears the dedup set. Test seam only — production never resets it.</summary>
        internal static void ResetReportedForTests() => _reportedHosts.Clear();

        private static void Report(HttpContext ctx, HostAllowList allowList)
        {
            var host = ctx.Request.Host.HasValue ? ctx.Request.Host.Value : "(no host header)";
            var remote = Describe(ctx.Connection.RemoteIpAddress);

            if (ShouldAnnounceRefusal(host))
            {
                Log.Information(
                    "[HostOrigin] Refused {Method} {Path} from {Remote}: its Host header '{Host}' is not in "
                    + "the allow-list ({Entries}). If that name is legitimate for this server, add it to the "
                    + "'{ConfigKey}' setting and restart.",
                    ctx.Request.Method, ctx.Request.Path, remote, host,
                    string.Join(", ", allowList.Entries), AllowListConfigKey);
            }
            else
            {
                Log.Debug("[HostOrigin] refused {Method} {Path} for Host '{Host}' from {Remote}",
                    ctx.Request.Method, ctx.Request.Path, host, remote);
            }
        }

        private static string Describe(IPAddress? address) => address?.ToString() ?? "(no remote address)";
    }
}
