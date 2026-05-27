using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Context;
using Serilog.Core;
using Serilog.Events;

namespace SQLTriage.Data;

/// <summary>
/// Adds a per-async-flow correlation ID to every Serilog log event. The id is
/// held in an AsyncLocal so it follows the logical async flow (Blazor circuit,
/// HTTP request, background Task started from one), without needing the
/// Scoped accessor to be resolved from a singleton enricher (which would be a
/// captive dependency and fails on Blazor Server scope validation).
/// </summary>
public class CorrelationIdEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var id = CorrelationIdAccessor.Current;
        if (!string.IsNullOrEmpty(id))
        {
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("CorrelationId", id));
        }
    }
}

/// <summary>
/// Holds a correlation ID in AsyncLocal so it survives across async boundaries
/// inside a Blazor circuit / HTTP request. Registered as Scoped for ergonomic
/// @inject usage in pages, but the underlying value is stored in a static
/// AsyncLocal so the singleton enricher can read it without a captive scope.
/// </summary>
public class CorrelationIdAccessor
{
    private static readonly AsyncLocal<string?> _current = new();

    public static string? Current => _current.Value;

    public string CorrelationId
    {
        get
        {
            if (string.IsNullOrEmpty(_current.Value))
                _current.Value = Guid.NewGuid().ToString("N")[..12];
            return _current.Value!;
        }
    }

    public void Set(string id) => _current.Value = id;

    /// <summary>
    /// Pushes the correlation ID into Serilog's LogContext so every
    /// log entry within this scope includes a CorrelationId property.
    /// Returns an IDisposable that pops the property on disposal.
    /// </summary>
    public IDisposable PushToLogContext()
    {
        return LogContext.PushProperty("CorrelationId", CorrelationId);
    }
}
