/* In the name of God, the Merciful, the Compassionate */

using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SqlHealthAssessment.Data.Models;

namespace SqlHealthAssessment.Data.Services
{
    /// <summary>
    /// Registers REST API endpoints on the Kestrel server started by ServerModeService.
    /// Designed for RMM/PSA integration (ConnectWise, Datto, Autotask, etc.).
    ///
    /// All endpoints are prefixed with /api/v1/ and secured by API key header.
    /// </summary>
    public static class ApiEndpoints
    {
        private const string ApiKeyHeader = "X-API-Key";

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
                AlertingService alerting,
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
                    activeAlerts = alerting.GetUnacknowledgedCount(),
                    lastCheckSummaries = summaries.ToDictionary(
                        kvp => kvp.Key,
                        kvp => new
                        {
                            passed = kvp.Value.Passed,
                            failed = kvp.Value.Failed,
                            errors = kvp.Value.Errors,
                            total = kvp.Value.TotalChecks,
                            startedAt = kvp.Value.StartedAt,
                            completedAt = kvp.Value.CompletedAt,
                            durationMs = kvp.Value.Duration.TotalMilliseconds
                        })
                });
            });

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
            });

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
            });

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
                        actualValue = r.ActualValue,
                        expectedValue = r.ExpectedValue,
                        errorMessage = r.ErrorMessage,
                        executedAt = r.ExecutedAt,
                        durationMs = r.DurationMs,
                        recommendedAction = r.RecommendedAction
                    })
                });
            });

            api.MapGet("/checks/summary", (CheckExecutionService checkExec) =>
            {
                var summaries = checkExec.GetAllSummaries();
                return Results.Ok(summaries.Select(kvp => new
                {
                    instance = kvp.Key,
                    passed = kvp.Value.Passed,
                    failed = kvp.Value.Failed,
                    errors = kvp.Value.Errors,
                    total = kvp.Value.TotalChecks,
                    startedAt = kvp.Value.StartedAt,
                    completedAt = kvp.Value.CompletedAt,
                    durationMs = kvp.Value.Duration.TotalMilliseconds
                }));
            });

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
            });

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
            });

            // ── Alerts ───────────────────────────────────────────────────────
            api.MapGet("/alerts", (AlertingService alerting, int? maxCount) =>
            {
                var notifications = alerting.GetNotifications(maxCount ?? 50);
                return Results.Ok(new
                {
                    unacknowledgedCount = alerting.GetUnacknowledgedCount(),
                    total = notifications.Count,
                    alerts = notifications.Select(n => new
                    {
                        id = n.Id,
                        alertName = n.AlertName,
                        metric = n.Metric,
                        currentValue = n.CurrentValue,
                        thresholdValue = n.ThresholdValue,
                        severity = n.Severity,
                        instanceName = n.InstanceName,
                        message = n.Message,
                        triggeredAt = n.TriggeredAt,
                        isAcknowledged = n.IsAcknowledged
                    })
                });
            });

            api.MapPost("/alerts/{id}/acknowledge", (string id, AlertingService alerting) =>
            {
                alerting.AcknowledgeNotification(id);
                return Results.Ok(new { acknowledged = true, id });
            });

            api.MapPost("/alerts/acknowledge-all", (AlertingService alerting) =>
            {
                alerting.AcknowledgeAll();
                return Results.Ok(new { acknowledged = true, all = true });
            });

            // ── Alert Thresholds (CRUD) ──────────────────────────────────────
            api.MapGet("/alerts/thresholds", (AlertingService alerting) =>
            {
                return Results.Ok(alerting.GetThresholds());
            });

            api.MapPost("/alerts/thresholds", (AlertThreshold threshold, AlertingService alerting) =>
            {
                if (string.IsNullOrWhiteSpace(threshold.Name))
                    return Results.BadRequest(new { error = "Name is required" });

                threshold.Id = Guid.NewGuid().ToString();
                alerting.AddThreshold(threshold);
                return Results.Created($"/api/v1/alerts/thresholds/{threshold.Id}", threshold);
            });

            api.MapPut("/alerts/thresholds/{id}", (
                string id, AlertThreshold threshold, AlertingService alerting) =>
            {
                threshold.Id = id;
                alerting.UpdateThreshold(threshold);
                return Results.Ok(threshold);
            });

            api.MapDelete("/alerts/thresholds/{id}", (string id, AlertingService alerting) =>
            {
                alerting.RemoveThreshold(id);
                return Results.Ok(new { deleted = true, id });
            });

            // ── Checks Repository ────────────────────────────────────────────
            api.MapGet("/checks", (CheckRepositoryService checkRepo) =>
            {
                return Results.Ok(checkRepo.Checks);
            });

            api.MapGet("/checks/enabled", (CheckRepositoryService checkRepo) =>
            {
                return Results.Ok(checkRepo.GetEnabledChecks());
            });

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
            });

            // ── RBAC Users ───────────────────────────────────────────────────
            api.MapGet("/rbac/users", (RbacService rbac) =>
            {
                return Results.Ok(rbac.GetUsers().Select(u => new
                {
                    u.Id, u.Email, u.DisplayName, u.Provider, u.Role,
                    u.Enabled, u.CreatedAt, u.LastLogin
                }));
            });

            api.MapPost("/rbac/users", (RbacUser user, RbacService rbac) =>
            {
                if (string.IsNullOrWhiteSpace(user.Email))
                    return Results.BadRequest(new { error = "Email is required" });
                if (!AppRoles.IsValid(user.Role))
                    return Results.BadRequest(new { error = $"Invalid role. Must be one of: {string.Join(", ", AppRoles.All)}" });

                rbac.AddUser(user);
                return Results.Created($"/api/v1/rbac/users/{user.Id}", user);
            });

            api.MapPut("/rbac/users/{id}", (string id, RbacUser user, RbacService rbac) =>
            {
                user.Id = id;
                if (!AppRoles.IsValid(user.Role))
                    return Results.BadRequest(new { error = $"Invalid role. Must be one of: {string.Join(", ", AppRoles.All)}" });

                rbac.UpdateUser(user);
                return Results.Ok(user);
            });

            api.MapDelete("/rbac/users/{id}", (string id, RbacService rbac) =>
            {
                rbac.RemoveUser(id);
                return Results.Ok(new { deleted = true, id });
            });
        }

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

                // If no API key is configured, allow all requests (open mode)
                if (string.IsNullOrWhiteSpace(expectedKey))
                {
                    await next();
                    return;
                }

                // Validate the API key header
                if (!context.Request.Headers.TryGetValue(ApiKeyHeader, out var providedKey)
                    || providedKey.ToString() != expectedKey)
                {
                    context.Response.StatusCode = 401;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(
                        JsonSerializer.Serialize(new { error = "Invalid or missing API key", header = ApiKeyHeader }));
                    return;
                }

                await next();
            });
        }
    }
}
