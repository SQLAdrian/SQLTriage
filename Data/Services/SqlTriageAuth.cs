/* In the name of God, the Merciful, the Compassionate */

using System.Net;
using System.Security.Claims;
using System.Security.Principal;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.MicrosoftAccount;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services
{
    /// <summary>Claim types SQLTriage stamps on the circuit principal.</summary>
    public static class SqlTriageAuthClaims
    {
        /// <summary>"true" / "false" — whether the connection arrived over loopback.</summary>
        public const string Loopback = "sqltriage:loopback";

        /// <summary>The RBAC identity key (email address, or a down-level DOMAIN\user name).</summary>
        public const string IdentityKey = "sqltriage:identity";

        /// <summary>The provider that authenticated this principal.</summary>
        public const string Provider = "sqltriage:provider";
    }

    // BM:SqlTriageAuth.Class — the ONE authentication pipeline, shared by every Kestrel host
    /// <summary>
    /// The authentication stack for every browser-facing host: server mode inside the WPF
    /// process, <c>--server</c>, and the installed Windows service.
    ///
    /// <para><b>Why this exists as a shared extension.</b> All of the auth work used to live in
    /// <see cref="ServerModeService"/>. The headless host built its pipeline as
    /// <c>UseStaticFiles → UseRouting → UseAntiforgery → MapRazorComponents</c> — no
    /// <c>UseAuthentication</c>, no auth endpoints at all. Probed live on 2026-08-01, the
    /// installed service answered 404 on both <c>/auth/me</c> and <c>/auth/login</c> while
    /// listening on 0.0.0.0. Anything landed in one host and not the other fixes the lane
    /// nobody is running. Both hosts now call the same two methods and there is no second
    /// pipeline to forget.</para>
    /// </summary>
    public static class SqlTriageAuth
    {
        /// <summary>
        /// Registers the authentication services. Called UNCONDITIONALLY, whether or not RBAC is
        /// enabled — the cookie scheme, the authorization services and the cascading
        /// authentication state must exist even on an unconfigured install, because that is how
        /// the circuit learns it is on loopback and is entitled to the bootstrap hatch. Guarding
        /// this behind <c>rbac.Enabled</c> is what left the headless host with no way to tell a
        /// console user from the network.
        /// </summary>
        public static IServiceCollection AddSqlTriageAuth(this IServiceCollection services, RbacConfig config)
        {
            services.AddHttpContextAccessor();
            services.AddCascadingAuthenticationState();

            var authBuilder = services.AddAuthentication(options =>
            {
                // The cookie stays the session mechanism for every provider, including Windows:
                // one session model, one /auth/me, one logout. Negotiate must NOT become the
                // default scheme — it is connection-based and would re-challenge per connection.
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            })
            .AddCookie(options =>
            {
                options.LoginPath = "/auth/login";
                options.LogoutPath = "/auth/logout";
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = true;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
            });

            // A provider is registered ONLY when it is complete enough to work. The old test was
            // `Enabled && ClientId`, which let a half-typed Google config through with an empty
            // secret — and OAuth options are validated inside UseAuthentication(), on the first
            // request, before routing. Every request then threw
            // `ArgumentException: The value cannot be an empty string. (Parameter 'ClientSecret')`:
            // /settings, /auth/login, /auth/me, /servers and /_server/health all 500, loopback and
            // LAN alike, RBAC on or off. Break-glass cannot reach a host that does not answer, so
            // one mistyped field was a total lockout with no way back but the filesystem.
            // A misconfigured provider must degrade to "that provider is unavailable".
            RegisterOAuthProvider(config.Google, "Google", "/auth/challenge/google", () =>
                authBuilder.AddGoogle(options =>
                {
                    options.ClientId = config.Google.ClientId;
                    options.ClientSecret = CredentialProtector.Decrypt(config.Google.ClientSecret);
                    options.CallbackPath = "/auth/callback/google";
                }));

            RegisterOAuthProvider(config.Microsoft, "Microsoft", "/auth/challenge/microsoft", () =>
                authBuilder.AddMicrosoftAccount(options =>
                {
                    options.ClientId = config.Microsoft.ClientId;
                    options.ClientSecret = CredentialProtector.Decrypt(config.Microsoft.ClientSecret);
                    options.CallbackPath = "/auth/callback/microsoft";
                }));

            if (config.Windows.Enabled)
            {
                // SSPI: Kerberos where a domain is reachable, NTLM against the local SAM
                // otherwise — which is how MACHINE\user signs in on a workgroup box. On a
                // non-domain host AddNegotiate logs a startup warning about the absence of a
                // Kerberos-capable environment; that is expected, not fatal.
                authBuilder.AddNegotiate();
                Log.Information("[Auth] Windows (Negotiate) authentication enabled");
            }

            services.AddAuthorization();
            return services;
        }

        /// <summary>
        /// Registers an OAuth provider if — and only if — it is usable, and says LOUDLY when an
        /// enabled provider is skipped. Silence here is how a provider that the operator switched
        /// on ends up quietly absent from the login page with nothing in the log to explain it.
        /// </summary>
        private static void RegisterOAuthProvider(
            OAuthProviderConfig provider, string name, string route, Action register)
        {
            if (!provider.Enabled) return;

            var problem = RbacService.DescribeOAuthProblem(provider);
            if (problem != null)
            {
                Log.Error(
                    "[Auth] {Provider} sign-in is ENABLED but NOT REGISTERED — {Problem}. {Route} will report the "
                    + "provider as unavailable. Fix it in Settings ▸ Access Control. (Registering it in this state "
                    + "would make every request on this host fail with an empty-ClientSecret exception.)",
                    name, problem, route);
                return;
            }

            register();
            Log.Information("[Auth] {Provider} authentication enabled", name);
        }

        /// <summary>
        /// Kestrel HTTPS listener options for a host that may serve Negotiate.
        ///
        /// <para>HTTP/2 does not support Negotiate. Both hosts bind HTTPS with
        /// <c>ListenAnyIP(port, o =&gt; o.UseHttps(cert))</c> and default protocols, so ALPN
        /// negotiates h2 and the Windows handshake fails on exactly the endpoint an operator is
        /// most likely to use. Pin HTTP/1.1 when Windows auth is on.</para>
        /// </summary>
        public static void ApplyNegotiateProtocolConstraint(ListenOptions listenOptions, RbacConfig config)
        {
            if (!config.Windows.Enabled) return;
            listenOptions.Protocols = HttpProtocols.Http1;
            Log.Information("[Auth] HTTPS listener pinned to HTTP/1.1 — HTTP/2 cannot carry Negotiate");
        }

        /// <summary>
        /// Wires the authentication middleware and endpoints. Call after <c>UseRouting</c> and
        /// before <c>UseAntiforgery</c>.
        /// </summary>
        public static WebApplication UseSqlTriageAuth(this WebApplication app, RbacService rbac)
        {
            // REFUSE TO BUILD without the boundary. Since 2026-08-03 the thing that keeps an
            // unauthenticated stranger off this listener is InteractiveAppAdmission, not the
            // per-surface gates behind it — and a host that forgets to install it would look
            // entirely healthy while serving the whole interactive application to the LAN, which
            // is the exact failure this lane found on the installed service on 2026-08-01 (no
            // authentication pipeline at all, 404 on /auth/login, listening on 0.0.0.0).
            //
            // Every browser-facing host must call this method — it is where the cookie handler and
            // the /auth endpoints come from — so making it a hard startup failure means no third
            // host can ever ship without the boundary. Loud beats silent: a service that will not
            // start is a five-minute fix; a service that starts wide open is not noticed.
            if (!((IApplicationBuilder)app).Properties.ContainsKey(InteractiveAppAdmission.AdmissionInstalledKey))
                throw new InvalidOperationException(
                    "UseSqlTriageAdmission must be installed before UseSqlTriageAuth. Call "
                    + "app.UseSqlTriageFrontDoor(rbac) first — it installs the interactive-application "
                    + "boundary ahead of static files and routing. Without it, an unauthenticated "
                    + "caller from any address on the network is served the whole application.");

            app.UseAuthentication();

            // Stamp the connection facts onto the principal. This runs for EVERY request,
            // including the /_blazor SignalR connection — which is the request Blazor takes
            // HttpContext.User from when it seeds the circuit, so this is how the loopback fact
            // reaches a live circuit that has no HttpContext of its own.
            app.Use(async (ctx, next) =>
            {
                var loopback = AppUserState.IsLoopbackAddress(ctx.Connection.RemoteIpAddress);
                ctx.User.AddIdentity(new ClaimsIdentity(new[]
                {
                    new Claim(SqlTriageAuthClaims.Loopback, loopback ? "true" : "false")
                }));
                await next();
            });

            app.UseAuthorization();
            MapAuthEndpoints(app, rbac);
            return app;
        }

        /// <summary>
        /// Content type for the sign-in page. The charset is REQUIRED: served as bare
        /// <c>text/html</c> the page's em dashes and ▸ rendered as mojibake in the browser,
        /// because a browser with no declared charset falls back to its locale default (cp1252
        /// here), not to UTF-8. This is the recovery screen — it has to be legible.
        /// </summary>
        internal const string LoginPageContentType = "text/html; charset=utf-8";

        // ── Endpoints ────────────────────────────────────────────────────

        private static void MapAuthEndpoints(WebApplication app, RbacService rbac)
        {
            // Owned here rather than resolved from DI, deliberately. This is a security control in
            // front of an expensive verify, and a control that only exists when somebody remembered
            // to register it is a control that will one day be missing on the host that needed it
            // most. Created where the endpoint is created: one instance per application, and no way
            // to map /auth/local without it.
            var loginThrottle = new LocalLoginThrottle();

            app.MapGet("/auth/login", (HttpContext ctx, IAntiforgery antiforgery) =>
                Results.Content(RenderLoginPage(ctx, rbac, antiforgery), LoginPageContentType));

            // OAuth challenge — redirects to the provider.
            app.MapGet("/auth/challenge/{provider}", (string provider, HttpContext ctx) =>
            {
                // Usable, not merely Enabled: challenging a scheme that was never registered
                // (because its secret is missing) throws instead of redirecting.
                var scheme = provider.ToLowerInvariant() switch
                {
                    "google" when RbacService.IsOAuthProviderUsable(rbac.Config.Google)
                        => GoogleDefaults.AuthenticationScheme,
                    "microsoft" when RbacService.IsOAuthProviderUsable(rbac.Config.Microsoft)
                        => MicrosoftAccountDefaults.AuthenticationScheme,
                    _ => null
                };
                if (scheme == null) return Results.Redirect("/auth/login?error=unknown_provider");

                return Results.Challenge(
                    new AuthenticationProperties { RedirectUri = "/auth/complete" }, new[] { scheme });
            });

            // Windows challenge + completion in one endpoint. Negotiate answers a challenge with
            // 401 + WWW-Authenticate and the browser re-requests THIS SAME url carrying the
            // token, so the second pass lands in the authenticated branch.
            app.MapGet("/auth/challenge/windows", async (HttpContext ctx) =>
            {
                if (!rbac.Config.Windows.Enabled)
                    return Results.Redirect("/auth/login?error=windows_disabled");

                var result = await ctx.AuthenticateAsync(NegotiateDefaults.AuthenticationScheme);
                if (!result.Succeeded || result.Principal?.Identity?.IsAuthenticated != true)
                    return Results.Challenge(new AuthenticationProperties(),
                        new[] { NegotiateDefaults.AuthenticationScheme });

                return await CompleteWindowsSignIn(ctx, rbac, result.Principal);
            });

            // OAuth completion — the cookie the OAuth handler signed in with is already present.
            app.MapGet("/auth/complete", async (HttpContext ctx) =>
            {
                var result = await ctx.AuthenticateAsync();
                if (!result.Succeeded || result.Principal == null)
                    return Results.Redirect("/auth/login?error=auth_failed");

                var provider = result.Principal.Identity?.AuthenticationType ?? "unknown";

                // A Windows principal has no email claim. If one somehow reaches here, fork
                // rather than bouncing it to error=no_email — which is what the original,
                // email-only completion did to every Windows login.
                if (AuthProviders.IsWindows(provider))
                    return await CompleteWindowsSignIn(ctx, rbac, result.Principal);

                var email = result.Principal.FindFirstValue(ClaimTypes.Email) ?? "";
                var name = result.Principal.FindFirstValue(ClaimTypes.Name) ?? email;
                if (string.IsNullOrEmpty(email))
                    return Results.Redirect("/auth/login?error=no_email");

                var config = rbac.Config;
                var providerConfig = provider.Contains("Google", StringComparison.OrdinalIgnoreCase)
                    ? config.Google : config.Microsoft;
                if (!string.IsNullOrEmpty(providerConfig.AllowedDomain))
                {
                    var domain = email.Split('@').LastOrDefault() ?? "";
                    if (!domain.Equals(providerConfig.AllowedDomain, StringComparison.OrdinalIgnoreCase))
                        return Results.Redirect("/auth/login?error=domain_restricted");
                }

                var user = rbac.RecordLogin(email, name, provider);
                if (user == null) return Results.Redirect("/auth/login?error=access_denied");

                await IssueSessionCookie(ctx, email, name, user.Role, provider);
                return Results.Redirect("/");
            });

            // Local username + password. The Argon2id machinery in RbacService was wired to no
            // endpoint at all — the hashes were written and never readable by any sign-in path.
            app.MapPost("/auth/local", async (HttpContext ctx, IAntiforgery antiforgery) =>
            {
                if (!rbac.Config.LocalPassword.Enabled)
                    return Results.Redirect("/auth/login?error=local_disabled");

                try { await antiforgery.ValidateRequestAsync(ctx); }
                catch (AntiforgeryValidationException) { return Results.Redirect("/auth/login?error=expired"); }

                var form = await ctx.Request.ReadFormAsync();
                var username = form["username"].ToString().Trim();
                var password = form["password"].ToString();

                // The verify below costs 19 MiB and 2 Argon2id iterations WHETHER OR NOT the
                // account exists — ValidateLocalLogin runs a dummy hash on a miss so a wrong
                // username and a wrong password take the same time, which is deliberate. Nothing
                // stood in front of it: no attempt limit, and no bound on how many could run at
                // once. Both are LocalLoginThrottle's job, and the two answers it can give are
                // different states on purpose.
                var caller = LocalLoginThrottle.KeyFor(ctx.Connection.RemoteIpAddress);
                var admission = await loginThrottle.TryBeginVerificationAsync(caller, ctx.RequestAborted);

                if (admission == LoginAdmission.LockedOut)
                {
                    Log.Warning(
                        "[Auth] Local sign-in refused for {Remote} — locked out for {Remaining:N0}s "
                        + "after {Max} failed attempts",
                        ctx.Connection.RemoteIpAddress, loginThrottle.LockoutRemaining(caller).TotalSeconds,
                        LocalLoginThrottle.MaxAttempts);
                    return Results.Redirect("/auth/login?error=too_many_attempts");
                }

                if (admission == LoginAdmission.Busy)
                {
                    // NOT recorded as a failure. Counting it would let a flood lock out every
                    // legitimate caller without one password ever being guessed.
                    Log.Warning(
                        "[Auth] Local sign-in deferred for {Remote} — {Max} password verifications "
                        + "already running", ctx.Connection.RemoteIpAddress,
                        LocalLoginThrottle.MaxConcurrentVerifications);
                    return Results.Redirect("/auth/login?error=busy");
                }

                RbacUser? user;
                try
                {
                    user = rbac.ValidateLocalLogin(username, password);
                }
                finally
                {
                    // In a finally because a slot that is taken and never returned shrinks the
                    // endpoint's capacity for the life of the process.
                    loginThrottle.Release();
                }

                if (user == null)
                {
                    loginThrottle.RecordFailure(caller);
                    Log.Warning("[Auth] Local sign-in failed for {User} from {Remote}",
                        username, ctx.Connection.RemoteIpAddress);
                    return Results.Redirect("/auth/login?error=bad_credentials");
                }

                loginThrottle.RecordSuccess(caller);
                await IssueSessionCookie(ctx, user.Email, user.DisplayName, user.Role, AuthProviders.Local);
                return Results.Redirect("/");
            }).DisableAntiforgery();   // validated explicitly above, against the token in the rendered form

            app.MapGet("/auth/logout", async (HttpContext ctx) =>
            {
                await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return Results.Redirect("/auth/login");
            });

            app.MapGet("/auth/me", (HttpContext ctx) =>
            {
                var loopback = AppUserState.IsLoopbackAddress(ctx.Connection.RemoteIpAddress);
                if (ctx.User.Identity?.IsAuthenticated != true)
                {
                    return Results.Json(new
                    {
                        authenticated = false,
                        enforced = rbac.IsRbacEnforced(),
                        loopback,
                        // The SAME call the admission gate makes, so this report cannot say a
                        // caller holds the hatch when the gate would not give it to them. It used
                        // to be `loopback || AllowRemoteBootstrapAdmin`, which reported
                        // bootstrapEligible:true on a fully enforced install. That flag no longer
                        // exists (2026-08-03), so this field now tracks the caller's socket
                        // address and nothing else. It is still the honest one to read when
                        // auditing the hatch: `enforced` says whether RBAC is being applied right
                        // now, which is a different question.
                        bootstrapEligible = rbac.IsBootstrapEligible(loopback)
                    });
                }

                // The ROLE comes from the store, never from the cookie. A cookie minted for a user
                // who has since been removed or disabled would otherwise keep reporting the role
                // it was stamped with for the full 8-hour sliding lifetime.
                var identity = ctx.User.FindFirstValue(SqlTriageAuthClaims.IdentityKey);
                var providerName = ctx.User.FindFirstValue(SqlTriageAuthClaims.Provider);
                var resolved = rbac.ResolvePrincipal(
                    providerName, identity, ctx.User.FindFirstValue(ClaimTypes.PrimarySid));

                return Results.Json(new
                {
                    authenticated = true,
                    identity,
                    email = ctx.User.FindFirstValue(ClaimTypes.Email),
                    name = ctx.User.FindFirstValue(ClaimTypes.Name),
                    role = resolved.Role,
                    accountStatus = resolved.Status.ToString().ToLowerInvariant(),
                    provider = providerName,
                    enforced = rbac.IsRbacEnforced(),
                    loopback
                });
            });
        }

        // ── Windows sign-in ──────────────────────────────────────────────

        private static async Task<IResult> CompleteWindowsSignIn(
            HttpContext ctx, RbacService rbac, ClaimsPrincipal principal)
        {
            var rawName = principal.Identity?.Name ?? "";
            if (string.IsNullOrWhiteSpace(rawName))
                return Results.Redirect("/auth/login?error=no_windows_identity");

            var parsed = WindowsIdentityKey.Parse(rawName);
            var key = parsed.Ok ? parsed.Key : rawName;

            // SIDs survive a rename; names do not. Bind it on the way in.
            var sid = (principal.Identity as WindowsIdentity)?.User?.Value
                      ?? principal.FindFirstValue(ClaimTypes.PrimarySid);

            // The OAuth email-suffix check cannot apply to a principal with no email claim —
            // this is the Windows-domain analogue.
            var allowed = rbac.Config.Windows.AllowedDomains;
            if (allowed is { Count: > 0 }
                && !allowed.Any(d => d.Trim().Equals(parsed.Domain, StringComparison.OrdinalIgnoreCase)))
            {
                Log.Warning("[Auth] Windows sign-in refused for {Principal} — domain not in the allow-list", key);
                return Results.Redirect("/auth/login?error=domain_restricted");
            }

            var user = rbac.RecordLogin(key, rawName, AuthProviders.Windows, sid);
            if (user == null) return Results.Redirect("/auth/login?error=access_denied");

            await IssueSessionCookie(ctx, key, user.DisplayName, user.Role, AuthProviders.Windows, sid);
            return Results.Redirect("/");
        }

        /// <summary>
        /// Issues the SAME cookie for every provider. One session model means one /auth/me,
        /// one logout, and one place a role can come from.
        /// </summary>
        private static async Task IssueSessionCookie(
            HttpContext ctx, string identityKey, string displayName, string role, string provider,
            string? sid = null)
        {
            var claims = new List<Claim>
            {
                new(SqlTriageAuthClaims.IdentityKey, identityKey),
                new(ClaimTypes.Name, string.IsNullOrEmpty(displayName) ? identityKey : displayName),
                new(ClaimTypes.Role, role),
                new(SqlTriageAuthClaims.Provider, provider),
                new("provider", provider)   // back-compat with readers of the original claim name
            };

            // Carry the SID so the per-request store lookup can match a Windows principal that was
            // renamed mid-session — the same reason the record stores one. The role claim above is
            // now advisory only: every reader resolves against the store.
            if (!string.IsNullOrEmpty(sid))
                claims.Add(new Claim(ClaimTypes.PrimarySid, sid));

            // Only email-class principals get an Email claim. Stamping DOMAIN\user into
            // ClaimTypes.Email would hand every downstream email reader a value that is not one.
            if (!AuthProviders.IsWindows(provider) && identityKey.Contains('@'))
                claims.Add(new Claim(ClaimTypes.Email, identityKey));

            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

            Log.Information("[Auth] Signed in {Principal} via {Provider} as {Role}", identityKey, provider, role);
        }

        // ── Login page ───────────────────────────────────────────────────

        /// <summary>
        /// Renders the sign-in page. It must NEVER be blank.
        ///
        /// <para>The original built a list of provider links and interpolated it into the page.
        /// With both providers off, that list was empty and the page rendered a heading over
        /// nothing — no explanation, no recovery route, on the exact screen an operator reaches
        /// after locking themselves out. When there is nothing to offer, this says so in plain
        /// words and names the way back in.</para>
        ///
        /// <para><b>It must also stay SELF-CONTAINED.</b> Since
        /// <see cref="InteractiveAppAdmission"/> moved the boundary, this is the only page an
        /// unauthenticated non-loopback caller can reach — and the gate refuses static assets to
        /// that caller along with everything else. Every style here is therefore inline and there
        /// is no <c>&lt;link&gt;</c>, <c>&lt;script&gt;</c>, font or image reference anywhere in
        /// it. Adding one would not 404; it would 401, and the recovery screen would render
        /// unstyled to the person who is already locked out.
        /// <c>InteractiveAppAdmissionTests.TheSignInPageNeedsNoStaticAsset</c> fails if that
        /// changes, so the constraint is enforced rather than remembered.</para>
        /// </summary>
        internal static string RenderLoginPage(HttpContext ctx, RbacService rbac, IAntiforgery? antiforgery)
        {
            var config = rbac.Config;
            var enc = HtmlEncoder.Default;
            var body = new System.Text.StringBuilder();

            var error = ctx.Request.Query["error"].ToString();
            if (!string.IsNullOrEmpty(error))
                body.Append("<p style='color:#f88;margin:0 0 24px'>").Append(enc.Encode(DescribeError(error))).Append("</p>");

            var options = 0;

            if (config.Windows.Enabled)
            {
                options++;
                body.Append("<p><a style='color:#4af' href='/auth/challenge/windows'>Sign in with your Windows account</a></p>");
            }

            // Offer a provider only if it was actually registered — an enabled-but-incomplete
            // provider is skipped by RegisterOAuthProvider, and a link to a scheme that does not
            // exist is a dead end dressed as a way in. Say so instead.
            AppendOAuthOption(body, config.Google, "Google", "/auth/challenge/google", ref options);
            AppendOAuthOption(body, config.Microsoft, "Microsoft", "/auth/challenge/microsoft", ref options);

            if (config.LocalPassword.Enabled)
            {
                options++;
                var token = antiforgery?.GetAndStoreTokens(ctx);
                body.Append("<form method='post' action='/auth/local' style='margin:24px auto;max-width:320px;text-align:left'>");
                if (token?.RequestToken != null)
                    body.Append("<input type='hidden' name='").Append(enc.Encode(token.FormFieldName))
                        .Append("' value='").Append(enc.Encode(token.RequestToken)).Append("' />");
                body.Append("<label style='display:block;margin:8px 0 4px'>Username</label>")
                    .Append("<input name='username' autocomplete='username' style='width:100%;padding:8px' />")
                    .Append("<label style='display:block;margin:12px 0 4px'>Password</label>")
                    .Append("<input name='password' type='password' autocomplete='current-password' style='width:100%;padding:8px' />")
                    .Append("<button type='submit' style='margin-top:16px;padding:8px 16px;width:100%'>Sign in</button>")
                    .Append("</form>");
            }

            if (options == 0)
            {
                // The blank-page case. Say what is wrong and how to get back in.
                var port = ctx.Request.Host.Port ?? 80;
                body.Append("<div style='max-width:640px;margin:0 auto;text-align:left;line-height:1.6'>")
                    .Append("<p><strong>There is no way to sign in, because no sign-in method is configured.</strong></p>")
                    .Append("<p>RBAC is switched on but Windows authentication, Google, Microsoft and local ")
                    .Append("passwords are all off or incomplete, so this page has nothing to offer you.</p>")
                    .Append("<p><strong>To get back in:</strong> open <code>http://localhost:")
                    .Append(port)
                    .Append("/settings</code> <em>from the console of this machine</em> (or over RDP). ")
                    .Append("Requests arriving on loopback keep access to Settings for exactly this reason. ")
                    .Append("From there, enable Windows authentication or configure an OAuth provider, ")
                    .Append("then sign in normally.</p>")
                    .Append("<p>If you cannot reach the console, stop the SQLTriage service, delete ")
                    .Append("<code>Config\\rbac-users.json</code> next to the executable, and restart it — ")
                    .Append("that returns the install to its unconfigured state.</p>")
                    .Append("</div>");
            }

            // The charset declaration is not cosmetic. This page is UTF-8 (em dashes in the
            // recovery copy, ▸ in the Settings path) and was served as bare `text/html`, so
            // browsers fell back to their locale default and rendered mojibake — on the one screen
            // an operator reaches when they are already locked out. Declared twice on purpose: the
            // response header (below, at the Results.Content call) and the document itself, so a
            // saved copy or a proxy that rewrites the header still reads correctly.
            return $@"<!DOCTYPE html>
<html><head><meta charset='utf-8' /><title>Sign In — SQLTriage</title><meta name='viewport' content='width=device-width,initial-scale=1' /></head>
<body style='background:#1a1a2e;color:#eee;font-family:Consolas,monospace;padding:40px;text-align:center'>
<h1>SQLTriage</h1>
<h2>Sign In</h2>
<div style='margin:24px auto;max-width:720px'>{body}</div>
</body></html>";
        }

        /// <summary>
        /// Appends a provider's sign-in link when it is usable, or a one-line explanation when it
        /// is switched on but incomplete. An operator who enabled Google and sees nothing has no
        /// way to tell a missing secret from a missing feature.
        /// </summary>
        private static void AppendOAuthOption(
            System.Text.StringBuilder body, OAuthProviderConfig provider, string name, string route, ref int options)
        {
            if (!provider.Enabled) return;

            if (RbacService.IsOAuthProviderUsable(provider))
            {
                options++;
                body.Append("<p><a style='color:#4af' href='").Append(route).Append("'>Sign in with ")
                    .Append(name).Append("</a></p>");
                return;
            }

            body.Append("<p style='color:#c93'>").Append(name)
                .Append(" sign-in is switched on but not fully configured, so it is unavailable. ")
                .Append("An administrator can complete it in Settings ▸ Access Control.</p>");
        }

        private static string DescribeError(string code) => code switch
        {
            // The refusal from InteractiveAppAdmission. This is the sentence an ordinary LAN user
            // reads the first time they open SQLTriage after the boundary moved, so it says what
            // changed and what to do, not "access denied". Keyed off the shipped constant so the
            // gate and the copy cannot drift into disagreeing about the code.
            InteractiveAppAdmission.SignInRequiredError =>
                "SQLTriage needs you to sign in before it will open. Requests from another machine "
                + "are no longer served the application anonymously — pick a sign-in method below. "
                + "(Browsing from the console of the server itself is unaffected.)",

            // The two states LocalLoginThrottle can return. They were falling through to
            // "Sign-in failed." — one sentence for a lockout, a load refusal and every unknown
            // code alike, which tells a locked-out operator to keep retrying (each retry is
            // another refusal) and tells a caller refused for SERVER LOAD that their credentials
            // were the problem. Both are read off the shipped constants so the copy cannot drift
            // from the limiter.
            //
            // What this names is the POLICY, not this caller's remaining time: the redirect
            // carries an error code and nothing else, so the page cannot know how much of the
            // window is left. LocalLoginThrottle.LockoutRemaining is logged at the endpoint, where
            // it is actually known.
            "too_many_attempts" =>
                $"Too many failed sign-in attempts from this address. After "
                + $"{LocalLoginThrottle.MaxAttempts} failures, sign-in from here is refused for "
                + $"{DescribeLockout(LocalLoginThrottle.LockoutDuration)}. Wait for that to pass, "
                + "then try again.",

            "busy" =>
                $"The server is already running the {LocalLoginThrottle.MaxConcurrentVerifications} "
                + "password checks it will run at once, so this sign-in was not attempted at all. "
                + "This says nothing about your username or password. Try again in a moment.",

            "auth_failed" => "Sign-in did not complete. Please try again.",
            "no_email" => "That provider did not return an email address, so you could not be identified.",
            "no_windows_identity" => "Windows did not supply an account name for this connection.",
            "domain_restricted" => "Your account is not in an allowed domain for this server.",
            "access_denied" => "Your account is not authorised to use this server. Ask an administrator to add it.",
            "bad_credentials" => "That username or password was not recognised.",
            "windows_disabled" => "Windows authentication is not enabled on this server.",
            "local_disabled" => "Local password sign-in is not enabled on this server.",
            "unknown_provider" => "That sign-in provider is not available on this server — it is switched off, "
                                  + "or its configuration is incomplete.",
            "expired" => "The sign-in form expired. Please try again.",
            _ => "Sign-in failed."
        };

        /// <summary>
        /// Renders <see cref="LocalLoginThrottle.LockoutDuration"/> for a human. Derived from the
        /// TimeSpan rather than written out, so raising the constant changes the sentence too — a
        /// hard-coded "one minute" beside a configurable duration is the same class of defect as
        /// the message this method was added for.
        /// </summary>
        private static string DescribeLockout(TimeSpan window)
        {
            if (window.TotalSeconds < 60) return $"{window.TotalSeconds:0} seconds";

            var minutes = window.TotalMinutes;
            return minutes == Math.Floor(minutes)
                ? $"{minutes:0} minute" + (minutes >= 2 ? "s" : "")
                : $"{window.TotalSeconds:0} seconds";
        }
    }
}
