/* In the name of God, the Merciful, the Compassionate */

using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services
{
    // BM:ApiAuthorization.Class — permission gate for the /api/v1 REST surface
    /// <summary>
    /// Authorization for the REST API, as an endpoint filter each route opts into by name.
    ///
    /// <para><b>This IS the boundary for <c>/api</c>, and it is the only surface where that is
    /// still true.</b> Round 8 (2026-08-03) put <see cref="InteractiveAppAdmission"/> in front of
    /// everything else — an unauthenticated caller from a non-loopback origin gets no document, no
    /// asset and no Blazor circuit — but <c>/api</c> is deliberately on that gate's allow-list. It
    /// is not an interactive application, and it carries a real machine credential that an RMM/PSA
    /// integration depends on; refusing it at the front door would break the contract the
    /// credential exists for and would buy nothing. The consequence is that this file, unlike the
    /// UI gates, did NOT become a second layer. Everything below is still load-bearing on its
    /// own, for an unauthenticated caller from anywhere on the network.</para>
    ///
    /// <para><b>And that exemption's rationale used to over-claim, which is how it stayed open a
    /// day longer than it should have.</b> The sentence "<c>/api</c> has its own boundary:
    /// <c>ApiAuthorization</c>" was true only of the routes that actually carried
    /// <see cref="RequirePermission"/> — and the GET reads carried none. Measured 2026-08-03 on the
    /// desktop <see cref="ServerModeService"/> host with no ApiKey set, which is the default: an
    /// anonymous caller from a non-loopback origin got 200 from <c>/status</c>, <c>/servers</c>,
    /// <c>/servers/{id}</c>, <c>/checks*</c>, <c>/alerts*</c> and <c>/audit-log</c>, and read the
    /// client server inventory, the check results and the audit log. Production was not affected —
    /// <c>WindowsServiceHost</c> never calls <see cref="ApiEndpoints.MapApiEndpoints"/>, so the
    /// installed service answers 404 for all of them — but the desktop's share-via-browser host
    /// mapped every one. Every route in <see cref="ApiEndpoints"/> now declares a permission, so
    /// the exemption's claim and the code finally agree. The lesson generalises past this file: a
    /// sentence that justifies removing a control has to be conditioned on the same measurement
    /// that would falsify it.</para>
    ///
    /// <para><b>Why this exists.</b> Until 2026-08-02 the ONLY thing in front of <c>/api/*</c> was
    /// <see cref="ApiEndpoints.UseApiKeyAuth"/>, and with the default (empty) <c>ApiKey</c> that
    /// middleware degrades to a same-origin check which <c>IsLocalOrSameOriginRequest</c>
    /// deliberately passes for any request carrying neither Origin nor Referer — i.e. every
    /// programmatic caller — and which reads <c>Request.Host.Host</c>, the attacker-supplied Host
    /// header. Proven from 192.168.10.32 against a faithful replica of the ServerModeService
    /// pipeline: <c>POST /api/v1/rbac/users</c> carrying an attacker-supplied Argon2id
    /// <c>passwordHash</c> returned 201, and <c>POST /auth/local</c> then signed that account in
    /// as <c>role: admin</c>. PUT and DELETE answered 200 too — elevate your own account, or
    /// delete the real admins and lock the owner out of their own box.</para>
    ///
    /// <para><b>Scope, honestly.</b> These endpoints are pre-existing and were untouched by rounds
    /// 1 and 2. They are also NOT mapped by the headless host: <c>WindowsServiceHost</c> never
    /// calls <see cref="ApiEndpoints.MapApiEndpoints"/>, so the installed production service
    /// answers 404 for them — verified by probe, for the writes in round 3 and again for the reads
    /// in round 10. The exposure is the DESKTOP's LAN-bound server mode. It still defeats
    /// round 2's stated objective, so it closes here.</para>
    ///
    /// <para><b>The decision order</b>, in <see cref="Evaluate"/>:</para>
    /// <list type="number">
    /// <item>A VALID API KEY was presented — the RMM/PSA machine credential. Allowed: that is the
    ///   integration contract this API exists for, and it is a real secret.</item>
    /// <item>An AUTHENTICATED PRINCIPAL whose role — resolved from the user STORE, never from the
    ///   cookie, so a removed or disabled account cannot keep its access — holds the permission.</item>
    /// <item>RBAC is DORMANT and the request arrived on LOOPBACK, for an endpoint that is not
    ///   fail-closed — so an unconfigured install stays usable from the box it runs on. Related to
    ///   the pages' bootstrap hatch but deliberately NOT the same predicate, and narrower in the
    ///   only direction that matters: the pages' loopback arm is unconditional (break-glass must
    ///   survive enforcement), while this one also requires <c>!IsRbacEnforced()</c>. Neither has a
    ///   remote arm — the API never had one, and the pages' was deleted on 2026-08-03.</item>
    /// <item>Otherwise DENY — 401 when nobody is signed in, 403 when someone is and lacks it.</item>
    /// </list>
    ///
    /// <para><b>Fail-closed endpoints.</b> <c>/api/v1/rbac/*</c> passes
    /// <c>failClosedWithoutApiKey: true</c>, which removes step 3 — with no API key configured
    /// there is no path in at all. That creates no lockout: the recovery route for an unconfigured
    /// install is /onboarding and /settings in a browser on loopback, both of which keep
    /// break-glass. Nothing needs to mint an admin over HTTP with no credential whatsoever; that
    /// is precisely the hole.</para>
    /// </summary>
    public static class ApiAuthorization
    {
        /// <summary>
        /// Set by <see cref="ApiEndpoints.UseApiKeyAuth"/> when a presented key validated against
        /// the configured one. Keyed on <c>HttpContext.Items</c>, which is per-request state the
        /// client cannot influence — never a header.
        /// </summary>
        internal const string ApiKeyValidatedItem = "SQLTriage.ApiKeyValidated";

        /// <summary>Outcome of <see cref="Evaluate"/>. <c>Allowed</c> carries no response.</summary>
        internal enum ApiAuthOutcome
        {
            Allowed,
            Unauthenticated,
            Forbidden,
        }

        /// <summary>
        /// The whole decision, as a pure function of the request — no middleware, no routing, no
        /// host. Extracted so it can be exercised directly against a
        /// <see cref="DefaultHttpContext"/>: an authorization rule that can only be checked by
        /// standing a web server up is a rule nobody checks.
        /// </summary>
        internal static ApiAuthOutcome Evaluate(
            HttpContext context, RbacService rbac, string permission, bool failClosedWithoutApiKey)
        {
            // 1. Machine credential.
            if (context.Items.TryGetValue(ApiKeyValidatedItem, out var validated) && validated is true)
                return ApiAuthOutcome.Allowed;

            // 2. Authenticated principal, role resolved from the store.
            var authenticated = context.User?.Identity?.IsAuthenticated == true;
            if (authenticated)
            {
                var resolved = rbac.ResolvePrincipal(
                    context.User!.FindFirstValue(SqlTriageAuthClaims.Provider),
                    context.User.FindFirstValue(SqlTriageAuthClaims.IdentityKey),
                    context.User.FindFirstValue(ClaimTypes.PrimarySid));

                // EvaluateApiKeyPermission, not the matrix directly: RbacService.HasPermission went
                // PRIVATE on 2026-08-17 so no page can name it (see its doc). This tier keeps the
                // matrix because it settles the other two terms itself — IsActive on the line above,
                // and the bootstrap-equivalent case in its own branch below, scoped to loopback and
                // skipped entirely when the endpoint is fail-closed.
                if (resolved.IsActive && RbacService.EvaluateApiKeyPermission(resolved.Role, permission))
                    return ApiAuthOutcome.Allowed;
            }

            // 3. Bootstrap hatch — dormant RBAC, on the box, not a fail-closed endpoint.
            if (!failClosedWithoutApiKey
                && !rbac.IsRbacEnforced()
                && AppUserState.IsLoopbackAddress(context.Connection.RemoteIpAddress))
                return ApiAuthOutcome.Allowed;

            return authenticated ? ApiAuthOutcome.Forbidden : ApiAuthOutcome.Unauthenticated;
        }

        /// <summary>
        /// Declares the permission an endpoint requires. Written on the Map call itself
        /// (<c>api.MapPost(...).RequirePermission("settings")</c>) rather than buried in the
        /// handler, so the requirement is part of the route's DECLARATION and a census can see it:
        /// <c>RbacHandlerGateCensusTests</c> parses this call out of ApiEndpoints.cs and fails when
        /// a mutating route carries neither a RequirePermission nor a reviewed-list entry.
        /// </summary>
        public static RouteHandlerBuilder RequirePermission(
            this RouteHandlerBuilder builder, string permission, bool failClosedWithoutApiKey = false)
            => builder.AddEndpointFilter((invocation, next)
                => Filter(invocation, next, permission, failClosedWithoutApiKey));

        /// <summary>The filter body, shared by every gated endpoint.</summary>
        internal static async ValueTask<object?> Filter(
            EndpointFilterInvocationContext invocation,
            EndpointFilterDelegate next,
            string permission,
            bool failClosedWithoutApiKey)
        {
            var ctx = invocation.HttpContext;
            var rbac = ctx.RequestServices.GetService<RbacService>();

            // No RbacService means this is not the app's container. Refuse rather than guess —
            // "the authorization service is missing" must never resolve to "allow".
            if (rbac is null)
                return Results.Json(new { error = "Authorization unavailable" }, statusCode: 503);

            var outcome = Evaluate(ctx, rbac, permission, failClosedWithoutApiKey);
            if (outcome == ApiAuthOutcome.Allowed)
                return await next(invocation);

            return Deny(outcome, permission);
        }

        /// <summary>
        /// The denial body. Names the permission and both remedies, because the caller who trips
        /// this is far more often an integrator with a half-configured script than an attacker.
        /// </summary>
        internal static IResult Deny(ApiAuthOutcome outcome, string permission)
            => outcome == ApiAuthOutcome.Unauthenticated
                ? Results.Json(new
                {
                    error = "Authentication required",
                    detail = $"This endpoint requires the '{permission}' permission. "
                           + "Present a configured X-API-Key header, or sign in at /auth/login.",
                    permission,
                }, statusCode: 401)
                : Results.Json(new
                {
                    error = "Insufficient permissions",
                    detail = $"This endpoint requires the '{permission}' permission.",
                    permission,
                }, statusCode: 403);

        // ── Inbound RbacUser sanitisation ────────────────────────────────

        /// <summary>
        /// Refuses a request body that tries to SUPPLY a password hash, and returns the sanitised
        /// user when it does not.
        ///
        /// <para>A caller must never provide <see cref="RbacUser.PasswordHash"/>. The field is an
        /// Argon2id digest the server computes from a password the server was given; an inbound
        /// one is an attacker choosing the credential for an account they are creating. That is
        /// exactly how the proven <c>POST /api/v1/rbac/users</c> → <c>POST /auth/local</c> chain
        /// worked. This is enforced INDEPENDENTLY of the permission gate: even a caller holding a
        /// valid API key has no business planting a hash, and a defence that only exists inside an
        /// authorization branch is a defence that disappears the day the branch moves.</para>
        ///
        /// <para>Returns an error result when the body carried a hash; otherwise nulls the field
        /// and returns null. Nulling matters as much as refusing: a body that omits the property
        /// deserialises to null anyway, so the sanitising assignment is what guarantees no code
        /// path downstream can see a caller-supplied value.</para>
        /// </summary>
        internal static IResult? RefuseInboundPasswordHash(RbacUser? user)
        {
            if (user is null) return null;
            if (!string.IsNullOrEmpty(user.PasswordHash))
            {
                return Results.Json(new
                {
                    error = "passwordHash may not be supplied by a caller",
                    detail = "A password hash is computed by the server from a password. Create the "
                           + "account without it and set the password from Settings > Access Control.",
                }, statusCode: 400);
            }

            user.PasswordHash = null;
            return null;
        }
    }
}
