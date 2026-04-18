/* In the name of God, the Merciful, the Compassionate */

using System;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data
{
    /// <summary>
    /// Global service that maintains the currently selected SQL Server instance
    /// across all dashboards. When the user changes the instance dropdown,
    /// all dashboards should query against the newly selected instance.
    /// </summary>
    public class GlobalInstanceSelector
    {
        private string? _selectedInstance;
        private readonly object _lock = new();
        private readonly ILogger<GlobalInstanceSelector> _logger;

        public GlobalInstanceSelector(ILogger<GlobalInstanceSelector> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Raised when the selected instance changes.
        /// Dashboards should subscribe to this event to refresh their data.
        /// </summary>
        public event Action<string>? OnInstanceChanged;

        /// <summary>
        /// Gets the currently selected instance name.
        /// Returns null if no instance is selected.
        /// </summary>
        public string? SelectedInstance
        {
            get
            {
                lock (_lock)
                {
                    return _selectedInstance;
                }
            }
        }

        /// <summary>
        /// Sets the currently selected instance and notifies all subscribers.
        /// This should be called when the user changes the instance dropdown.
        /// </summary>
        public void SetSelectedInstance(string? instanceName)
        {
            bool changed = false;
            lock (_lock)
            {
                if (_selectedInstance != instanceName)
                {
                    _selectedInstance = instanceName;
                    changed = true;
                }
            }

            if (changed && instanceName != null)
            {
                _logger.LogInformation("Instance changed to {InstanceName}", instanceName);
                OnInstanceChanged?.Invoke(instanceName);
            }
        }
    }
}
