/* In the name of God, the Merciful, the Compassionate */

using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.Logging;

namespace SqlHealthAssessment.Data.Services
{
    /// <summary>
    /// Monitors Blazor Server circuit lifecycle for diagnostics and cleanup.
    /// Logs when circuits connect/disconnect so server-mode stability can be audited.
    /// </summary>
    public class AppCircuitHandler : CircuitHandler
    {
        private readonly ILogger<AppCircuitHandler> _logger;
        private static int _activeCircuits;

        public static int ActiveCircuits => _activeCircuits;

        public AppCircuitHandler(ILogger<AppCircuitHandler> logger)
        {
            _logger = logger;
        }

        public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref _activeCircuits);
            _logger.LogInformation("Blazor circuit opened: {CircuitId} (active: {Count})", circuit.Id, count);
            return Task.CompletedTask;
        }

        public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
        {
            var count = Interlocked.Decrement(ref _activeCircuits);
            _logger.LogInformation("Blazor circuit closed: {CircuitId} (active: {Count})", circuit.Id, count);
            return Task.CompletedTask;
        }

        public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
        {
            _logger.LogDebug("Blazor circuit reconnected: {CircuitId}", circuit.Id);
            return Task.CompletedTask;
        }

        public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
        {
            _logger.LogWarning("Blazor circuit connection lost: {CircuitId}", circuit.Id);
            return Task.CompletedTask;
        }
    }
}
