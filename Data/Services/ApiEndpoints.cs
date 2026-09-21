/* In the name of God, the Merciful, the Compassionate */

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services
{
    // BM:ApiEndpoints.Class — registers REST API endpoints for RMM/PSA integration
    /// <summary>
    /// Registers REST API endpoints on the Kestrel server started by ServerModeService.
    /// Designed for RMM/PSA integration (ConnectWise, Datto, Autotask, etc.).
    ///
    /// <para>All endpoints are prefixed with <c>/api/v1/</c>. <b>Every one of them declares a
    /// permission</b> — reads included, since 2026-08-03. <see cref="ApiAuthorization"/> is what
    /// enforces it, and it is the whole boundary for this surface: <c>/api</c> is exempt from
    /// <see cref="InteractiveAppAdmission"/>, so nothing else stands between these routes and any
    /// caller who can reach the listener.</para>
    ///
    /// <para><b>The reads used to be open, and "secured by API key header" was not true of
    /// them.</b> With no <c>ApiKey</c> configured — the default —
    /// <see cref="UseApiKeyAuth"/> degrades to a same-origin CSRF heuristic that lets every GET
    /// straight through. Measured on the desktop's <see cref="ServerModeService"/> host: an
    /// anonymous non-loopback caller got 200 from <c>/status</c>, <c>/servers</c>, <c>/checks*</c>,
    /// <c>/alerts*</c> and <c>/audit-log</c>, disclosing the client server inventory, the check
    /// results and the audit log. Configuring an ApiKey closed it, which is precisely the problem:
    /// the disclosure depended on an optional setting rather than on a rule.</para>
    ///
    /// <para><b>The permission per read is the one the equivalent UI surface asks for</b>, so the
    /// API cannot hand out what the app itself would refuse: <c>manage_servers</c> for the server
    /// inventory (<c>Pages/Servers.razor</c> gates the whole page on it), <c>manage_alerts</c> for
    /// the alert thresholds (<c>Pages/AlertingConfig.razor</c>), <c>view_audit_log</c> for the
    /// audit log, <c>view_dashboard</c>/<c>view_results</c> for the rest. The last two are held by
    /// every role, so they cost a signed-in integrator nothing and cost an unauthenticated one
    /// everything — which is the intended difference.</para>
    ///
    /// <para><b>What still works.</b> A configured API key satisfies every route (the RMM/PSA
    /// machine credential this API exists for). Loopback on an install with RBAC dormant satisfies
    /// every non-fail-closed route — a bootstrap hatch of its own, narrower than the UI's: loopback
    /// AND dormant, where the UI's loopback arm is unconditional. Neither has a remote arm. A
    /// signed-in principal satisfies whatever their stored role holds. What no longer works is
    /// "no credential at all, from anywhere on the network".</para>
    /// </summary>
    public static class ApiEndpoints
    {
        private const string ApiKeyHeader = "X-API-Key";

        /// <summary>Re-exported so UseApiKeyAuth and the filter agree on one key. See ApiAuthorization.</summary>
        private const string ApiKeyValidatedItem = ApiAuthorization.ApiKeyValidatedItem;

        /// <summary>
        /// Maps all API endpoints onto the given endpoint route builder.
        /// Call this after app.UseRouting() in ServerModeService.
        /// </summary>
        public static void MapApiEndpoints(this IEndpointRouteBuilder app)
        {
            var api = app.MapGroup("/api/v1");

            // ── Health / Status ──────────────────────────────────────────────
            api.MapGet("/status", (
                ServerConnectionManager connMgr,
                CheckExecutionService checkExec,
                AlertHistoryService alertHistory,
                AutoUpdateService update) =>
            {
                var connections = connMgr.GetEnabledConnections();
                var summaries = checkExec.GetAllSummaries();

                return Results.Ok(new
                {
                    status = "ok",
                    version = update.GetCurrentVersion(),
                    timestamp = DateTime.UtcNow,
                    servers = connections.Count,
                    monitoredInstances = checkExec.GetMonitoredInstances().Count,
                    // r2-06 (2026-08-26): this read the AlertingService notification queue, whose
                    // sole writer (EvaluateAlerts) has no call site anywhere in the repo — so it was
                    // 0 on every install, always, even while alerts fired. It now reports the real
                    // count of Active alerts from the persisted history the UI and NOC read.
                    activeAlerts = alertHistory.GetActiveCount(),
                    lastCheckSummaries = summaries.ToDictionary(
                        kvp => kvp.Key,
                        kvp => new
                        {
                            passed = kvp.Value.Passed,
                            failed = kvp.Value.Failed,
                            errors = kvp.Value.Errors,
                            // 2026-07-20 sweep: WARN/SKIP/INFO land in Informational, which was not
                            // exposed — so passed+failed+errors did not foot to total on any run
                            // with a blind spot, and the gap was invisible rather than explained.
                            informational = kvp.Value.Informational,
                            total = kvp.Value.TotalChecks,
                            startedAt = kvp.Value.StartedAt,
                            completedAt = kvp.Value.CompletedAt,
                            durationMs = kvp.Value.Duration.TotalMilliseconds
                        })
                });
            }).RequirePermission("view_dashboard");

            // ── Servers ──────────────────────────────────────────────────────
            api.MapGet("/servers", (ServerConnectionManager connMgr) =>
            {
                var connections = connMgr.GetConnections();
                return Results.Ok(connections.Select(c => new
                {
                    id = c.Id,
                    serverNames = c.ServerNames,
                    database = c.Database,
                    authType = c.EffectiveAuthType,
                    enabled = c.IsEnabled,
                    hasSqlWatch = c.HasSqlWatch,
                    serverCount = c.GetServerCount(),
                    serverList = c.GetServerList(),
                    environment = c.Environment,
                    tags = c.Tags
                }));
            }).RequirePermission("manage_servers");

            api.MapGet("/servers/{id}", (string id, ServerConnectionManager connMgr) =>
            {
                var conn = connMgr.GetConnections().FirstOrDefault(c => c.Id == id);
                if (conn == null) return Results.NotFound(new { error = "Server not found" });

                return Results.Ok(new
                {
                    id = conn.Id,
                    serverNames = conn.ServerNames,
                    database = conn.Database,
                    authType = conn.EffectiveAuthType,
                    enabled = conn.IsEnabled,
                    hasSqlWatch = conn.HasSqlWatch,
                    serverCount = conn.GetServerCount(),
                    serverList = conn.GetServerList(),
                    environment = conn.Environment,
                    tags = conn.Tags
                });
            }).RequirePermission("manage_servers");

            // ── Check Results ────────────────────────────────────────────────
            api.MapGet("/checks/results/{instanceName}", (
                string instanceName,
                CheckExecutionService checkExec,
                int? maxCount,
                string? category,
                bool? passedOnly) =>
            {
                var results = checkExec.GetResults(instanceName,
                    maxCount ?? 100, category, passedOnly);

                return Results.Ok(new
                {
                    instance = instanceName,
                    count = results.Count,
                    results = results.Select(r => new
                    {
                        checkId = r.CheckId,
                        checkName = r.CheckName,
                        category = r.Category,
                        severity = r.Severity,
                        passed = r.Passed,
                        // 2026-07-20 sweep: `passed` alone CANNOT represent a WARN/SKIP/INFO — all
                        // three ride Passed=true — and unlike the CSV export this payload had no
                        // status column, so a machine consumer of a "could not fully assess" result
                        // saw an unqualified passed:true and nothing else. Additive field, so no
                        // existing consumer's values change meaning; null on a plain pass/fail.
                        verdict = r.Verdict,
                        actualValue = r.ActualValue,
                        expectedValue = r.ExpectedValue,
                        errorMessage = r.ErrorMessage,
                        executedAt = r.ExecutedAt,
                        durationMs = r.DurationMs,
                        recommendedAction = r.RecommendedAction
                    })
                });
            }).RequirePermission("view_results");

            api.MapGet("/checks/summary", (CheckExecutionService checkExec) =>
            {
                var summaries = checkExec.GetAllSummaries();
                return Results.Ok(summaries.Select(kvp => new
                {
                    instance = kvp.Key,
                    passed = kvp.Value.Passed,
                    failed = kvp.Value.Failed,
                    errors = kvp.Value.Errors,
                    informational = kvp.Value.Informational,   // see /status above
                    total = kvp.Value.TotalChecks,
                    startedAt = kvp.Value.StartedAt,
                    completedAt = kvp.Value.CompletedAt,
                    durationMs = kvp.Value.Duration.TotalMilliseconds
                }));
            }).RequirePermission("view_results");

            api.MapGet("/checks/summary/{instanceName}", (
                string instanceName, CheckExecutionService checkExec) =>
            {
                var summary = checkExec.GetLastSummary(instanceName);
                if (summary == null)
                    return Results.NotFound(new { error = $"No results for instance '{instanceName}'" });

                return Results.Ok(new
                {
                    instance = instanceName,
                    passed = summary.Passed,
                    failed = summary.Failed,
                    errors = summary.Errors,
                    total = summary.TotalChecks,
                    startedAt = summary.StartedAt,
                    completedAt = summary.CompletedAt,
                    durationMs = summary.Duration.TotalMilliseconds
                });
            }).RequirePermission("view_results");

            // ── Execute Checks (trigger on-demand) ──────────────────────────
            api.MapPost("/checks/execute/{serverId}", async (
                string serverId,
                ServerConnectionManager connMgr,
                CheckExecutionService checkExec,
                CancellationToken ct) =>
            {
                var conn = connMgr.GetConnections().FirstOrDefault(c => c.Id == serverId);
                if (conn == null)
                    return Results.NotFound(new { error = "Server not found" });

                var summaries = new List<object>();
                foreach (var server in conn.GetServerList())
                {
                    var summary = await checkExec.ExecuteChecksAsync(conn, server, ct);
                    summaries.Add(new
                    {
                        instance = server,
                        passed = summary.Passed,
                        failed = summary.Failed,
                        errors = summary.Errors,
                        total = summary.TotalChecks,
                        durationMs = summary.Duration.TotalMilliseconds
                    });
                }

                return Results.Ok(new { executed = true, results = summaries });
            }).RequirePermission("execute_checks");

            // ── Alerts ───────────────────────────────────────────────────────
            // r2-06 (2026-08-26): these read/acknowledged the AlertingService notification queue,
            // whose only writer — EvaluateAlerts — has no call site in the repository, so the queue
            // was permanently empty. A Grafana/Zabbix poller got 200 OK, valid JSON, and a
            // {unacknowledgedCount:0, total:0, alerts:[]} that could never be anything else while
            // alerts fired. They now read and mutate the real persisted alert state (the same
            // AlertHistoryService the Alerts page and NOC render), so the API tells the truth.
            api.MapGet("/alerts", (AlertHistoryService alertHistory, int? maxCount) =>
            {
                var active = alertHistory.GetActiveAlerts(maxCount ?? 50);
                return Results.Ok(new
                {
                    unacknowledgedCount = alertHistory.GetActiveCount(),
                    total = active.Count,
                    alerts = active.Select(r => new
                    {
                        id = r.Id,
                        alertName = r.AlertName,
                        metric = r.AlertId,
                        currentValue = r.Value,
                        // null (not 0) when the condition that fired carried no threshold, and
                        // the basis beside it so a machine consumer can tell which (C3).
                        thresholdValue = r.ThresholdValue,
                        thresholdBasis = r.BasisKind,
                        severity = r.Severity,
                        instanceName = r.ServerName,
                        message = r.Message,
                        triggeredAt = r.LastTriggered,
                        isAcknowledged = string.Equals(r.Status, "Acknowledged", StringComparison.OrdinalIgnoreCase)
                    })
                });
            }).RequirePermission("view_results");

            api.MapPost("/alerts/{id}/acknowledge", (string id, AlertHistoryService alertHistory) =>
            {
                if (!long.TryParse(id, out var rowId))
                    return Results.BadRequest(new { error = "Alert id must be the numeric id from /alerts", id });
                // Answer what actually happened: a blanket {acknowledged:true} over a no-op was the
                // machine-readable half of the same lie r2-06 fixes on the read side.
                return alertHistory.AcknowledgeById(rowId)
                    ? Results.Ok(new { acknowledged = true, id = rowId })
                    : Results.NotFound(new { error = "No active alert with that id", id = rowId });
            }).RequirePermission("acknowledge_alerts");

            api.MapPost("/alerts/acknowledge-all", (AlertHistoryService alertHistory) =>
            {
                var acknowledged = alertHistory.AcknowledgeAll();
                return Results.Ok(new { acknowledged = true, all = true, count = acknowledged });
            }).RequirePermission("acknowledge_alerts");

            // ── Alert Thresholds (read only) ─────────────────────────────────
            // The write routes — POST/PUT/DELETE /alerts/thresholds — were removed 2026-08-26
            // (DECISIONS 04:20, ruling 3). They wrote AlertingService's alert-thresholds.json, a
            // store read only by EvaluateAlerts, and EvaluateAlerts has no call site anywhere in the
            // repo — so a threshold created through them never fired. An endpoint that silently does
            // nothing is worse than none, so it is gone rather than deprecated. The GET stays: it is
            // the authenticated read the RBAC handler census pins, and it reports the stored
            // thresholds honestly.
            api.MapGet("/alerts/thresholds", (AlertingService alerting) =>
            {
                return Results.Ok(alerting.GetThresholds());
            }).RequirePermission("manage_alerts");

            // ── Checks Repository ────────────────────────────────────────────
            api.MapGet("/checks", (CheckRepositoryService checkRepo) =>
            {
                return Results.Ok(checkRepo.Checks);
            }).RequirePermission("view_results");

            api.MapGet("/checks/enabled", (CheckRepositoryService checkRepo) =>
            {
                return Results.Ok(checkRepo.GetEnabledChecks());
            }).RequirePermission("view_results");

            // ── Audit Log ────────────────────────────────────────────────────
            api.MapGet("/audit-log", (AuditLogService auditLog, int? days) =>
            {
                var from = DateTime.UtcNow.AddDays(-(days ?? 7));
                var entries = auditLog.GetEntries(from, DateTime.UtcNow);
                return Results.Ok(new
                {
                    count = entries.Count,
                    days = days ?? 7,
                    entries
                });
            }).RequirePermission("view_audit_log");

            // ── RBAC Users ───────────────────────────────────────────────────
            //
            // USER MANAGEMENT. Every route here is gated on `settings` and FAIL-CLOSED: with no
            // ApiKey configured there is no path in at all, because the alternative — the
            // same-origin degradation in UseApiKeyAuth — is not authorization and passes any
            // header-less request. Proven from 192.168.10.32 on 2026-08-02 against a replica of
            // the ServerModeService pipeline: POST here with an attacker-chosen Argon2id
            // passwordHash returned 201, then POST /auth/local returned a session cookie and
            // /auth/me answered role:admin. PUT and DELETE answered 200 as well.
            //
            // No lockout is created by failing closed: the recovery path for an unconfigured
            // install is /onboarding and /settings in a browser on loopback, both of which keep
            // break-glass. Nothing legitimately needs to mint an admin over HTTP with no
            // credential at all.
            //
            // GET is gated too. It is not a mutation, but it returns the identity of every
            // principal who can sign in to this install — the roster an attacker would read
            // before choosing whom to impersonate. Gating it costs a legitimate integrator
            // nothing (an API key satisfies it) and costs an unauthenticated caller the map.
            api.MapGet("/rbac/users", (RbacService rbac) =>
            {
                return Results.Ok(rbac.GetUsers().Select(u => new
                {
                    u.Id,
                    u.Email,
                    u.DisplayName,
                    u.Provider,
                    u.Role,
                    u.Enabled,
                    u.CreatedAt,
                    u.LastLogin
                }));
            }).RequirePermission("settings", failClosedWithoutApiKey: true);

            api.MapPost("/rbac/users", (RbacUser user, RbacService rbac) =>
            {
                // Refused BEFORE the permission gate's effect is relied on and independently of
                // it: a caller must never supply a password hash, whatever credential they hold.
                var hashRefusal = ApiAuthorization.RefuseInboundPasswordHash(user);
                if (hashRefusal != null) return hashRefusal;

                if (string.IsNullOrWhiteSpace(user.Email))
                    return Results.BadRequest(new { error = "Email is required" });
                if (!AppRoles.IsValid(user.Role))
                    return Results.BadRequest(new { error = $"Invalid role. Must be one of: {string.Join(", ", AppRoles.All)}" });

                // 201 was returned unconditionally, so a refused or failed write answered "Created"
                // with a body describing an account that is not on the server. Same class as the
                // Settings page announcing an added admin over a write that never landed — this is
                // just the machine-readable copy of the claim, which is worse: a caller automating
                // against it has nothing else to check.
                return DescribeUserStoreWrite(rbac.AddUser(user), rbac)
                       ?? Results.Created($"/api/v1/rbac/users/{user.Id}", user);
            }).RequirePermission("settings", failClosedWithoutApiKey: true);

            api.MapPut("/rbac/users/{id}", (string id, RbacUser user, RbacService rbac) =>
            {
                var hashRefusal = ApiAuthorization.RefuseInboundPasswordHash(user);
                if (hashRefusal != null) return hashRefusal;

                user.Id = id;
                if (!AppRoles.IsValid(user.Role))
                    return Results.BadRequest(new { error = $"Invalid role. Must be one of: {string.Join(", ", AppRoles.All)}" });

                // UpdateUser REPLACES the stored record wholesale, so an update that (correctly)
                // carries no hash would silently wipe the local password of whoever is being
                // edited — locking out the one admin who can sign in without OAuth. Carry the
                // stored hash forward; it is never something this endpoint can set.
                var existing = rbac.GetUsers().FirstOrDefault(u => u.Id == id);
                if (existing == null)
                    return Results.NotFound(new { error = "User not found", id });
                user.PasswordHash = existing.PasswordHash;

                return DescribeUserStoreWrite(rbac.UpdateUser(user), rbac) ?? Results.Ok(user);
            }).RequirePermission("settings", failClosedWithoutApiKey: true);

            api.MapDelete("/rbac/users/{id}", (string id, RbacService rbac) =>
            {
                var removed = rbac.RemoveUser(id);
                if (removed == StoreWriteOutcome.NothingToWrite)
                    return Results.NotFound(new { error = "User not found", id });
                return DescribeUserStoreWrite(removed, rbac) ?? Results.Ok(new { deleted = true, id });
            }).RequirePermission("settings", failClosedWithoutApiKey: true);
        }

        /// <summary>
        /// Turns a user-store write outcome into the response it deserves, or <c>null</c> when the
        /// write landed and the caller should answer normally.
        ///
        /// <para>ONE place, for the same reason the recovery sentence is one place: three endpoints
        /// each writing their own version of this is three chances for one of them to keep claiming
        /// success. <see cref="StoreWriteOutcome.NothingToWrite"/> is deliberately not handled here —
        /// it means different things per verb (a duplicate on POST, an unknown id on DELETE) and
        /// only the endpoint knows which.</para>
        /// </summary>
        private static IResult? DescribeUserStoreWrite(StoreWriteOutcome outcome, RbacService rbac)
            => outcome switch
            {
                StoreWriteOutcome.RefusedStoreUnreadable => Results.Conflict(new
                {
                    error = "The user store exists and did not load, so writing to it would delete every "
                          + "account, role and password on this install. Nothing was changed.",
                    recovery = rbac.DescribeStoreRecovery(forConfigStore: false),
                }),
                StoreWriteOutcome.WriteFailed => Results.Json(new
                {
                    error = "The user store could not be written (disk full, file locked, or permissions "
                          + "denied). Nothing was changed. See the server log for the exact error.",
                }, statusCode: 500),
                _ => null,
            };

        /// <summary>
        /// Adds API key authentication middleware. Validates X-API-Key header
        /// against the configured key. Skips validation for non-API paths.
        /// </summary>
        public static void UseApiKeyAuth(this IApplicationBuilder app)
        {
            app.Use(async (context, next) =>
            {
                var path = context.Request.Path.Value ?? "";

                // Only protect /api/ routes
                if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
                {
                    await next();
                    return;
                }

                var config = context.RequestServices.GetService<Microsoft.Extensions.Configuration.IConfiguration>();
                var expectedKey = config?["ApiKey"];

                // If no API key is configured, enforce same-origin check on
                // state-changing methods to prevent CSRF attacks in open mode.
                //
                // NOTE (2026-08-02): this branch is NOT authorization and never was. It asks
                // "does this look same-origin?", and IsLocalOrSameOriginRequest deliberately
                // answers yes for any request with no Origin and no Referer — i.e. for curl. It
                // also reads Request.Host.Host, which is the attacker-supplied HOST HEADER, so
                // `-H "Host: localhost"` passes it too. Endpoints that need a caller identity
                // now say so with RequirePermission(), which runs AFTER this middleware and does
                // not consult it.
                if (string.IsNullOrWhiteSpace(expectedKey))
                {
                    var method = context.Request.Method;
                    if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method))
                    {
                        if (!IsLocalOrSameOriginRequest(context))
                        {
                            context.Response.StatusCode = 403;
                            context.Response.ContentType = "application/json";
                            await context.Response.WriteAsync(
                                JsonSerializer.Serialize(new { error = "Cross-origin state-changing request blocked. Configure an API key or use same-origin requests." }));
                            return;
                        }
                    }
                    await next();
                    return;
                }

                // Validate the API key header (constant-time comparison to prevent timing attacks)
                if (!context.Request.Headers.TryGetValue(ApiKeyHeader, out var providedKey)
                    || !CryptographicOperations.FixedTimeEquals(
                        Encoding.UTF8.GetBytes(providedKey.ToString()),
                        Encoding.UTF8.GetBytes(expectedKey)))
                {
                    context.Response.StatusCode = 401;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(
                        JsonSerializer.Serialize(new { error = "Invalid or missing API key", header = ApiKeyHeader }));
                    return;
                }

                // The key checked out. Record that fact so RequirePermission() can honour the
                // machine credential without re-reading configuration per endpoint.
                context.Items[ApiKeyValidatedItem] = true;

                await next();
            });
        }

        /// <summary>
        /// Checks if a request originates from the same host (localhost or matching Origin/Referer).
        /// Used to prevent CSRF on open-mode (no API key) state-changing requests.
        ///
        /// <para><b>This is a CSRF heuristic, not authorization, and it is important that nobody
        /// mistakes it for one again.</b> It reads <c>Request.Host.Host</c> — the client-supplied
        /// Host header — so <c>-H "Host: localhost"</c> satisfies its first branch; and when a
        /// request carries neither Origin nor Referer it returns TRUE by design, because a
        /// programmatic caller has no browser context for CSRF to exploit. Both are correct for
        /// what it is for and useless as a gate. <c>internal</c> rather than <c>private</c> so
        /// <c>RbacRound3RegressionTests</c> can EXERCISE that, instead of the commit merely
        /// asserting it.</para>
        /// </summary>
        internal static bool IsLocalOrSameOriginRequest(HttpContext context)
        {
            var host = context.Request.Host.Host;

            // Allow requests from localhost / 127.0.0.1 / ::1
            if (host == "localhost" || host == "127.0.0.1" || host == "::1")
                return true;

            // Check Origin header first (set by browsers on cross-origin requests)
            var origin = context.Request.Headers["Origin"].FirstOrDefault();
            if (!string.IsNullOrEmpty(origin))
            {
                if (Uri.TryCreate(origin, UriKind.Absolute, out var originUri))
                    return string.Equals(originUri.Host, host, StringComparison.OrdinalIgnoreCase);
                return false;
            }

            // Fall back to Referer header
            var referer = context.Request.Headers["Referer"].FirstOrDefault();
            if (!string.IsNullOrEmpty(referer))
            {
                if (Uri.TryCreate(referer, UriKind.Absolute, out var refererUri))
                    return string.Equals(refererUri.Host, host, StringComparison.OrdinalIgnoreCase);
                return false;
            }

            // No Origin or Referer — likely a programmatic call (curl, PowerShell, RMM agent)
            // Allow it since there's no browser context for CSRF to exploit
            return true;
        }

        // ── Structured JSON error envelope ──
        /// <summary>Wraps all API errors in a consistent JSON envelope.</summary>
        internal static IResult ApiError(HttpContext ctx, string error, int statusCode, string? detail = null)
        {
            var correlationId = ctx.TraceIdentifier ?? Guid.NewGuid().ToString("N")[..12];
            var envelope = new
            {
                error,
                detail,
                correlationId,
                status = statusCode,
                path = ctx.Request.Path.Value,
                timestamp = DateTime.UtcNow.ToString("o"),
            };
            return Results.Json(envelope, statusCode: statusCode);
        }

        /// <summary>Catches unhandled exceptions in API pipeline and returns consistent JSON.</summary>
        internal static async Task ExceptionHandler(HttpContext ctx, Func<Task> next)
        {
            try
            {
                await next();
            }
            catch (Exception ex)
            {
                var corrId = ctx.TraceIdentifier ?? Guid.NewGuid().ToString("N")[..12];
                Console.Error.WriteLine($"[API] Unhandled exception {corrId}: {ex}");
                var envelope = new
                {
                    error = "Internal server error",
                    detail = ex.Message,
                    correlationId = corrId,
                    status = 500,
                    path = ctx.Request.Path.Value,
                    timestamp = DateTime.UtcNow.ToString("o"),
                };
                ctx.Response.StatusCode = 500;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(envelope));
            }
        }

        /// <summary>Validates that required query parameters are present and non-empty.</summary>
        internal static IResult? ValidateRequiredQuery(HttpContext ctx, params string[] paramNames)
        {
            foreach (var name in paramNames)
            {
                if (string.IsNullOrWhiteSpace(ctx.Request.Query[name]))
                    return ApiError(ctx, $"Missing required parameter: '{name}'", 400);
            }
            return null;
        }
    }
}
