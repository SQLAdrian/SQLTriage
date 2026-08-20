/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Serilog;

namespace SQLTriage.Data.Services
{
    // BM:InteractiveAppAdmission.Class — the HTTP boundary: who may be handed an interactive app at all
    /// <summary>
    /// THE BOUNDARY. An unauthenticated caller from a non-loopback origin does not receive an
    /// interactive application — no document, no shell, no Blazor circuit, therefore no control
    /// to smuggle.
    ///
    /// <para><b>Why this exists, and why it is not another scanner.</b> Seven rounds tried to
    /// secure the always-rendered shell by ENUMERATING its interactive controls, and four
    /// instruments were each defeated in turn: a verb lexicon missed
    /// <c>SetAnonymiseServerNames</c>; prefix anchoring missed <c>ShortcutSvc.TriggerRun</c>;
    /// full edge enumeration missed a receiver alias (<c>var s = UserSettings; s.M()</c>); and
    /// round 7's markup-position rule was defeated by fourteen shapes from the verifier —
    /// <c>@attributes</c> splat, a <c>RenderFragment</c> building the handler in C#,
    /// <c>MarkupString</c> plus an off-shell <c>[JSInvokable]</c>, <c>DynamicComponent</c>,
    /// fully-qualified tags, a fake <c>&lt;ShellGate&gt;</c> written inside a <c>//</c> comment —
    /// and by two more from the cold gate that are not adversarial at all:</para>
    /// <code>
    /// &lt;input @bind:get="_x" @bind:set="SetX" /&gt;        (no @bind= attribute exists to match)
    /// &lt;EditForm Model="_m" OnValidSubmit="Save"&gt;…       (no &lt;form&gt; tag, no @on… attribute)
    /// </code>
    /// <para>Both are what an ordinary developer writes next week with no adversarial intent.
    /// Every one of those shapes was green on the census and FIRED LIVE from
    /// <c>http://192.10.10.32/scheduled-tasks</c> as an unauthenticated <c>viewer</c>, on a page
    /// reading "restricted to Admin users". The conclusion is not "find a better scanner": a
    /// static scan of component source cannot be the security boundary, because the boundary has
    /// to hold for the shape nobody has thought of yet.</para>
    ///
    /// <para>So the boundary moved down the stack, to the one place where the shape of the
    /// component source is not an input at all. This middleware runs FIRST — before static files,
    /// before routing, before the endpoint that would render a component — and decides on three
    /// facts and nothing else: the socket's remote address, whether a session cookie
    /// authenticates the caller, and the request path. Aliases, lambdas, method groups,
    /// <c>@attributes</c>, <c>MarkupString</c>, <c>DynamicComponent</c>, <c>@bind:get</c> /
    /// <c>@bind:set</c> and <c>EditForm</c> are all equally irrelevant to it, because it never
    /// looks at component source.</para>
    ///
    /// <para><b>The rule.</b></para>
    /// <list type="bullet">
    /// <item><b>Loopback → unchanged.</b> The bootstrap hatch rounds 1-3 built is untouched: an
    ///   unconfigured install is fully usable from the box it runs on, and the <c>/settings</c> +
    ///   <c>/onboarding</c> break-glass route still works. A person on loopback already has the
    ///   box — they can stop the service and edit <c>Config\rbac-users.json</c> — so this grants
    ///   them no authority they lack.</item>
    /// <item><b>Non-loopback + authenticated → proceeds.</b> Everything the lane already built
    ///   stays in force BEHIND this gate as defence in depth: the per-surface
    ///   <see cref="AppUserState.IsAuthorized(string)"/> gates, the <c>ShellGate</c> boundary on
    ///   the shell, store-backed revocation, <see cref="ApiAuthorization"/> on the REST surface.
    ///   Those stop being the boundary; they do not stop being checks.</item>
    /// <item><b>Non-loopback + unauthenticated → the sign-in path and nothing else.</b>
    ///   <c>/auth/*</c> answers, and every other path is refused — including the Blazor circuit
    ///   negotiation.</item>
    /// </list>
    ///
    /// <para><b>What the unauthenticated LAN caller actually receives, and why.</b> A Blazor
    /// Server page is delivered as a static-ish HTML document and only THEN becomes interactive
    /// over <c>/_blazor</c>, so refusing one half is not enough: refuse only the socket and the
    /// caller still gets a prerendered DOM naming every server, finding and route; refuse only
    /// the document and a hand-written client can still open the circuit. This gate is in front
    /// of both, so neither is produced:</para>
    /// <list type="bullet">
    /// <item>a top-level navigation (<c>GET</c> whose <c>Accept</c> asks for <c>text/html</c>)
    ///   gets <b>302 to <c>/auth/login?error=signin_required</c></b> — a refusal that tells the
    ///   person what to do rather than looking broken;</item>
    /// <item>everything else — <c>POST /_blazor/negotiate</c>, the <c>/_blazor</c> WebSocket
    ///   upgrade, <c>/_framework/blazor.web.js</c>, every stylesheet and script — gets
    ///   <b>401</b> with a one-line <c>text/plain</c> body naming the sign-in URL. Not a redirect:
    ///   a 302 answering a WebSocket handshake or a script fetch is a failure dressed as success,
    ///   and the negotiate response in particular must not look like anything a client can
    ///   proceed from.</item>
    /// </list>
    ///
    /// <para><b>The behavioural consequence, stated rather than hidden: this ends ANONYMOUS LAN
    /// browsing of the app.</b> That is the point. It is survivable because this lane already
    /// shipped Negotiate/Windows sign-in — a LAN user signs in as <c>DOMAIN\user</c> or
    /// <c>MACHINE\user</c> and carries on — and because the sign-in page is reachable from a
    /// non-loopback origin by construction (it is the one thing that is). The page itself is
    /// self-contained: <see cref="SqlTriageAuth.RenderLoginPage"/> uses inline styles only and
    /// references no stylesheet, script, font or image, which is why blocking static assets for
    /// an unauthenticated caller costs the recovery screen nothing.
    /// <c>InteractiveAppAdmissionTests.TheSignInPageNeedsNoStaticAsset</c> pins that, so an edit
    /// that adds a <c>&lt;link&gt;</c> to the login page fails rather than silently shipping an
    /// unstyled lockout screen.</para>
    ///
    /// <para><b>The address comes from the socket, never from a header.</b> Same ruling as
    /// <see cref="AppUserState.IsLoopbackAddress"/>: <c>Request.Host.Host</c> and
    /// <c>X-Forwarded-For</c> are attacker-supplied, so <c>curl -H "Host: localhost"</c> from
    /// anywhere on the network would otherwise walk straight through. If SQLTriage is ever put
    /// behind a reverse proxy, this gate must be taught about it deliberately — silently trusting
    /// a forwarding header would hand every remote caller the loopback hatch.</para>
    /// </summary>
    public static class InteractiveAppAdmission
    {
        /// <summary>Where a refused caller is sent, and the only page they can reach.</summary>
        public const string SignInPath = "/auth/login";

        /// <summary>
        /// The <c>?error=</c> code the redirect carries. Rendered by
        /// <c>SqlTriageAuth.DescribeError</c>, so the person who hit the refusal reads a sentence
        /// explaining it instead of an empty sign-in form they did not ask for.
        /// </summary>
        public const string SignInRequiredError = "signin_required";

        /// <summary>
        /// The body of a non-navigation refusal. One line, plain text, naming the way in — an
        /// operator reading a 401 in a browser console or a curl transcript should not have to
        /// guess whether the server is broken.
        /// </summary>
        public const string RefusalBody =
            "SQLTriage requires sign-in for requests from another machine. "
            + "Open " + SignInPath + " in a browser on this address and sign in; "
            + "requests arriving on this server's own loopback address are exempt.";

        /// <summary>
        /// Response header stamped on every refusal. Not load-bearing for the decision — it
        /// exists so a refusal is unambiguous in a capture, and so the runtime test can tell a
        /// deliberate refusal from an incidental 401 raised by something else.
        /// </summary>
        public const string RefusalHeader = "X-SQLTriage-Admission";

        /// <summary>Value of <see cref="RefusalHeader"/> on a refusal.</summary>
        public const string RefusalHeaderValue = "sign-in-required";

        /// <summary>
        /// The ONLY path prefixes an unauthenticated non-loopback caller may reach. Deliberately
        /// tiny, deliberately a prefix list rather than a predicate somebody can extend with a
        /// clever rule, and pinned by
        /// <c>InteractiveAppAdmissionTests.TheAllowListIsExactlyThese</c> so widening it is a
        /// visible diff in a test rather than a quiet one in a lambda.
        ///
        /// <list type="bullet">
        /// <item><c>/auth</c> — the sign-in page itself, the OAuth challenge and callback, the
        ///   Negotiate handshake, the local-password POST, logout and <c>/auth/me</c>. Refusing
        ///   these would refuse the way back in.</item>
        /// <item><c>/_server/health</c> — the liveness probe, already deliberately
        ///   unauthenticated and already reduced to <c>{status:"ok"}</c> so it discloses nothing
        ///   about the host. Monitoring depends on it.</item>
        /// <item><c>/api</c> — the REST surface, which is NOT an interactive application: no route
        ///   under it returns a document, a circuit or a control. Its boundary is
        ///   <see cref="ApiAuthorization"/>, and the exemption is only as good as that boundary's
        ///   COVERAGE. When this list was first written, coverage was partial — the mutating routes
        ///   carried <see cref="ApiAuthorization.RequirePermission"/> and the GET reads carried
        ///   nothing, so exempting the whole prefix disclosed the server inventory, the check
        ///   results and the audit log to any anonymous caller on the LAN (measured 2026-08-03 on
        ///   the desktop host with the default empty ApiKey). Every route in
        ///   <see cref="ApiEndpoints"/> now declares a permission, which is the precondition this
        ///   exemption rests on; <c>RbacHandlerGateCensusTests</c> fails if a new route ever
        ///   does not, so the precondition is checked rather than assumed. Refusing the prefix here
        ///   instead would break the RMM/PSA integration contract the machine credential exists
        ///   for. This is the one exemption that is a judgement rather than a necessity, so it is
        ///   written down as one — conditions included.</item>
        /// </list>
        ///
        /// <para><b>These are PATH prefixes, not places to put files.</b> The gate runs ahead of
        /// <c>UseStaticFiles</c>, so a file physically present under one of these prefixes in
        /// <c>wwwroot</c> would be served to an unauthenticated non-loopback caller — the one class
        /// of static asset this boundary does not refuse. Nothing is served from them today (there
        /// is no <c>wwwroot\auth</c>, <c>wwwroot\api</c> or <c>wwwroot\_server</c> in any shipped
        /// layout) and <c>InteractiveAppAdmissionTests.NoStaticAssetHidesBehindAnAdmittedPrefix</c>
        /// keeps it that way, so a contributor cannot open the hole by dropping in a file. If a
        /// future asset genuinely has to live there, gate it explicitly — do not delete the
        /// test.</para>
        /// </summary>
        internal static readonly string[] AlwaysReachablePrefixes =
        {
            "/auth",
            "/_server/health",
            "/api",
        };

        /// <summary>What this gate decided to do with a request.</summary>
        internal enum AdmissionOutcome
        {
            /// <summary>Hand the request to the rest of the pipeline unchanged.</summary>
            Admit,

            /// <summary>302 to the sign-in page — a browser asked for a page.</summary>
            RedirectToSignIn,

            /// <summary>401 + <see cref="RefusalBody"/> — anything that is not a page request.</summary>
            Refuse,
        }

        /// <summary>
        /// The whole decision, as a pure function of three facts. Extracted for the same reason
        /// <see cref="ApiAuthorization.Evaluate"/> is: a rule that can only be checked by standing
        /// a web server up is a rule nobody checks — though in this case the runtime HTTP test IS
        /// the decider, and this overload exists so the decision table can also be exercised
        /// exhaustively without a socket.
        /// </summary>
        /// <param name="bootstrapEligible">
        /// True for a LOOPBACK connection, and for nothing else. Computed by
        /// <see cref="RbacService.IsBootstrapEligible(bool)"/>, which is also what
        /// <see cref="AppUserState.IsBootstrapEligible"/> and <c>/auth/me</c> ask: one notion of
        /// "this caller is entitled to the unconfigured-install hatch", not three hand-written
        /// copies of which this one was missing a term until 2026-08-03. The opt-in that used to
        /// extend it to remote callers was deleted the same day.
        /// </param>
        /// <param name="authenticated">True when a session cookie authenticated the caller.</param>
        /// <param name="path">The request path.</param>
        /// <param name="isPageRequest">
        /// True when this looks like a top-level browser navigation, which is the only case where
        /// a redirect is the honest answer. See <see cref="LooksLikePageRequest"/>.
        /// </param>
        internal static AdmissionOutcome Evaluate(
            bool bootstrapEligible, bool authenticated, PathString path, bool isPageRequest)
        {
            if (bootstrapEligible) return AdmissionOutcome.Admit;
            if (authenticated) return AdmissionOutcome.Admit;
            if (IsAlwaysReachable(path)) return AdmissionOutcome.Admit;

            return isPageRequest ? AdmissionOutcome.RedirectToSignIn : AdmissionOutcome.Refuse;
        }

        /// <summary>
        /// Whether the path is on <see cref="AlwaysReachablePrefixes"/>. Segment-wise, so
        /// <c>/authoring</c> does not inherit <c>/auth</c>'s exemption and <c>/apiary</c> does not
        /// inherit <c>/api</c>'s — a prefix match on raw strings is how an allow-list quietly
        /// becomes wider than the sentence describing it.
        /// </summary>
        internal static bool IsAlwaysReachable(PathString path)
        {
            foreach (var prefix in AlwaysReachablePrefixes)
                if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>
        /// Whether this request is a top-level browser navigation, i.e. something a 302 to the
        /// sign-in page would actually help.
        ///
        /// <para>A GET whose <c>Accept</c> asks for <c>text/html</c> and which is not a protocol
        /// upgrade. Everything else — the <c>/_blazor</c> WebSocket handshake (a GET carrying
        /// <c>Connection: Upgrade</c> and <c>Accept: *&#47;*</c>), the negotiate POST, a script or
        /// stylesheet fetch, an XHR — is refused with a status rather than redirected, because a
        /// redirect there produces a sign-in PAGE where the caller expected a socket, a script or
        /// JSON, and the failure then surfaces as a parse error three layers away from its
        /// cause.</para>
        /// </summary>
        internal static bool LooksLikePageRequest(HttpRequest request)
        {
            if (!HttpMethods.IsGet(request.Method)) return false;

            // A WebSocket handshake is a GET. It is never a navigation.
            if (request.HttpContext.WebSockets.IsWebSocketRequest) return false;
            if (request.Headers.TryGetValue("Upgrade", out var upgrade) && upgrade.Count > 0) return false;

            var accept = request.Headers.Accept.ToString();
            return accept.Contains("text/html", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Remote addresses already reported as refused, so a scanner hammering the port does not
        /// write a log line per request. First refusal per address is Information (an operator
        /// should see that somebody tried); the rest are Debug.
        /// </summary>
        private static readonly ConcurrentDictionary<string, byte> _reported = new(StringComparer.Ordinal);

        /// <summary>
        /// Installs the gate. Call it FIRST — before <c>UseStaticFiles</c>, before
        /// <c>UseRouting</c>, before anything that maps an endpoint. Position is the whole design:
        /// a boundary that runs after routing has to enumerate what routing can reach, and
        /// enumerating is the thing that lost seven rounds in a row.
        ///
        /// <para>It does not need <c>UseAuthentication</c> to have run: it asks the cookie handler
        /// directly through <see cref="IAuthenticationService"/>, whose per-request handler caches
        /// the result, so the later <c>UseAuthentication</c> does not re-do the work.</para>
        ///
        /// <para>Both browser-facing hosts call this — <see cref="WindowsServiceHost"/> (which is
        /// what <c>--server</c>, <c>--service</c> and the installed service run) and
        /// <see cref="ServerModeService"/> (the WPF desktop's share-via-browser host). They are
        /// the only two, and they are called out by name here for the reason
        /// <see cref="SqlTriageAuth"/> exists at all: on 2026-08-01 the installed service was
        /// found running a pipeline with no authentication in it whatsoever, because the auth work
        /// had been landed in one host and not the other. Anything landed in one host and not the
        /// other fixes the lane nobody is running.</para>
        /// </summary>
        public static WebApplication UseSqlTriageAdmission(this WebApplication app, RbacService rbac)
        {
            // Recorded so UseSqlTriageAuth can REFUSE TO BUILD a pipeline that never installed the
            // gate. A host cannot serve the app without calling UseSqlTriageAuth — that is where
            // the cookie handler and the /auth endpoints come from — so this makes "somebody added
            // a third host and forgot the boundary" a startup exception rather than a silent
            // exposure. It is the composition guarantee, not a positional one: it proves the gate
            // is INSTALLED and ahead of authentication, not that nothing was slipped in front of
            // it. UseSqlTriageFrontDoor is what fixes the position, in one place, for both hosts.
            // (WebApplication implements IApplicationBuilder explicitly, hence the cast.)
            ((IApplicationBuilder)app).Properties[AdmissionInstalledKey] = true;

            app.Use(async (ctx, next) =>
            {
                var loopback = AppUserState.IsLoopbackAddress(ctx.Connection.RemoteIpAddress);

                // The same notion of eligibility AppUserState and /auth/me honour, from the same
                // method, so no caller can spell it differently — which is how this gate came to
                // be missing a term the others had.
                //
                // IT IS THE SOCKET ADDRESS AND NOTHING ELSE, as of 2026-08-03. There used to be a
                // second arm, `allowRemoteBootstrapAdmin`, letting a headless box with no console
                // reach the unconfigured-install hatch from the network long enough to configure a
                // first admin. Four rounds tried to write an expiry that closed it once that job
                // was done, and four were defeated by a state transition nobody had enumerated:
                // no expiry at all (measured: an anonymous LAN caller admitted on a fully ENFORCED
                // install, negotiating a circuit and rendering the BoundaryCanary shapes);
                // `!IsRbacEnforced()`, a fail-SAFE predicate whose polarity inverts as an expiry,
                // so truncating rbac-users.json reopened it; `Config.Enabled`, a mirror of a switch
                // rather than an expiry, so with no damage at all unticking Enable RBAC gave a
                // stranger from 192.168.10.32 /servers, /query, /settings, /server-configuration,
                // /audit-log, /service-management and a live circuit; and a written-down one-way
                // latch, defeated because RecordLogin never raised it, so an operator signing in
                // through the shipped path left it null on a fully bootstrapped install.
                //
                // A fifth guard would have been a fifth enumeration of the same open-ended set, so
                // the grant was deleted instead. An operator on the box is on loopback; a headless
                // server is administered over RDP or SSH, from which the browser is also on
                // loopback. Nothing anonymous from the network is bootstrap-eligible any more, in
                // any configuration, and there is no setting that can make it so.
                var bootstrapEligible = rbac.IsBootstrapEligible(loopback);

                var authenticated = false;
                if (!bootstrapEligible)
                {
                    // Only ask when the answer can change the outcome. On loopback this is a
                    // pointless cookie decrypt on every asset request.
                    try
                    {
                        var result = await ctx.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                        authenticated = result.Succeeded && result.Principal?.Identity?.IsAuthenticated == true;
                    }
                    catch (Exception ex)
                    {
                        // A cookie that will not decrypt (key ring rotated, tampered payload) is
                        // NOT an authenticated caller. Fail closed and say so — the alternative is
                        // an exception page rendered to an unauthenticated stranger.
                        Log.Warning(ex, "[Admission] Cookie authentication threw for {Remote} on {Path} — treating as unauthenticated",
                            Describe(ctx.Connection.RemoteIpAddress), ctx.Request.Path);
                        authenticated = false;
                    }
                }

                var outcome = Evaluate(bootstrapEligible, authenticated, ctx.Request.Path, LooksLikePageRequest(ctx.Request));
                if (outcome == AdmissionOutcome.Admit)
                {
                    await next();
                    return;
                }

                Report(ctx, outcome);

                ctx.Response.Headers[RefusalHeader] = RefusalHeaderValue;

                if (outcome == AdmissionOutcome.RedirectToSignIn)
                {
                    ctx.Response.Redirect(SignInPath + "?error=" + SignInRequiredError);
                    return;
                }

                ctx.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                await ctx.Response.WriteAsync(RefusalBody);
            });

            return app;
        }

        /// <summary>
        /// Key on <c>WebApplication.Properties</c> recording that the gate was installed.
        /// Read by <see cref="SqlTriageAuth.UseSqlTriageAuth"/>, which refuses to build without it.
        /// </summary>
        internal const string AdmissionInstalledKey = "SQLTriage.InteractiveAppAdmission.Installed";

        /// <summary>
        /// The front half of the pipeline, in ONE place, for BOTH browser-facing hosts.
        ///
        /// <para>Order is the security property here, so it is not left to two call sites to
        /// reproduce by hand. The gate must come before <c>UseStaticFiles</c> (or an
        /// unauthenticated stranger still gets every asset), and before <c>UseRouting</c> (or the
        /// boundary would have to enumerate what routing can reach — and enumerating is exactly
        /// what lost rounds 1 through 7).</para>
        ///
        /// <para>This lane has already paid for hand-reproduced pipelines once: on 2026-08-01 the
        /// installed Windows service was found answering 404 on <c>/auth/login</c> from a 0.0.0.0
        /// listener because the authentication work had been landed in one host and not the other.
        /// <see cref="SqlTriageAuth"/> exists to stop that recurring for authentication; this
        /// exists to stop it recurring for admission.</para>
        ///
        /// <para>It is also the seam the decider drives:
        /// <c>InteractiveAppAdmissionTests</c> builds its host through THIS method, so the test is
        /// exercising the shipped composition rather than a copy of it, and deleting the
        /// <c>UseSqlTriageAdmission</c> line below turns the route test red.</para>
        /// </summary>
        public static WebApplication UseSqlTriageFrontDoor(this WebApplication app, RbacService rbac)
        {
            app.UseSqlTriageAdmission(rbac);
            GuardAdmittedPrefixDirectories(app.Environment.WebRootPath);
            app.UseStaticFiles();
            app.UseRouting();
            return app;
        }

        /// <summary>
        /// Refuses to start if the web root contains a directory that would be SERVED from under
        /// one of the <see cref="AlwaysReachablePrefixes"/>.
        ///
        /// <para>The gate runs ahead of <c>UseStaticFiles</c> and admits <c>/auth</c>,
        /// <c>/_server/health</c> and <c>/api</c> segment-wise, so a real file at
        /// <c>wwwroot\auth\anything</c> would be handed to an unauthenticated caller from any
        /// address on the network — the single class of static asset this boundary does not
        /// refuse. Nothing is served from those paths in any shipped layout today, which is why
        /// this has never been an exposure. It is also exactly the kind of latent hole a
        /// contributor opens without noticing: dropping a file into a folder is not a change
        /// anybody reviews as a security change.</para>
        ///
        /// <para>So it is a startup failure rather than a comment. Same doctrine as
        /// <c>UseSqlTriageAuth</c>'s refusal to build without the gate: a service that will not
        /// start is a five-minute fix, and a service that starts while serving an unauthenticated
        /// file is not noticed. Only the FIRST segment matters — <c>_content\Radzen.Blazor\api\…</c>
        /// is served at <c>/_content/…</c> and inherits no exemption.</para>
        /// </summary>
        internal static void GuardAdmittedPrefixDirectories(string? webRoot)
        {
            if (string.IsNullOrWhiteSpace(webRoot) || !Directory.Exists(webRoot)) return;

            foreach (var prefix in AlwaysReachablePrefixes)
            {
                var firstSegment = prefix.Trim('/').Split('/')[0];
                var candidate = Path.Combine(webRoot, firstSegment);
                if (!Directory.Exists(candidate)) continue;

                throw new InvalidOperationException(
                    $"The web root contains '{candidate}', which would be served at '/{firstSegment}/…' — "
                    + $"a path the interactive-application boundary admits without authentication (see "
                    + "InteractiveAppAdmission.AlwaysReachablePrefixes). Any file placed there is handed to "
                    + "an unauthenticated caller from any address on the network. Move the files, or widen "
                    + "the boundary deliberately and update this guard with them.");
            }
        }

        private static void Report(HttpContext ctx, AdmissionOutcome outcome)
        {
            var remote = Describe(ctx.Connection.RemoteIpAddress);
            if (_reported.TryAdd(remote, 0))
            {
                Log.Information(
                    "[Admission] {Remote} asked for {Path} without signing in and was refused the interactive "
                    + "application ({Outcome}). Requests from another machine must sign in at {SignInPath}; "
                    + "loopback is exempt.",
                    remote, ctx.Request.Path, outcome, SignInPath);
            }
            else
            {
                Log.Debug("[Admission] refused {Method} {Path} for {Remote} ({Outcome})",
                    ctx.Request.Method, ctx.Request.Path, remote, outcome);
            }
        }

        private static string Describe(IPAddress? address) => address?.ToString() ?? "(no remote address)";
    }
}
