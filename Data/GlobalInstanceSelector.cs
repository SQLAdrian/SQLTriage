/* In the name of God, the Merciful, the Compassionate */

using System;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data
{
    /// <summary>
    /// Global service that maintains the currently selected SQL Server instance
    /// across all dashboards. When the user changes the instance dropdown,
    /// all dashboards should query against the newly selected instance.
    ///
    /// <para><b>This is the SECOND process-wide connection edge, and at the point of use it
    /// OUTRANKS the first.</b> <c>SqlServerConnectionFactory.GetCurrentConnectionString</c> — the
    /// resolver for every query that asks for this process's ambient target (NOT every query in the
    /// process: ~95 call sites build a connection from a caller-supplied string and never reach it) —
    /// resolves
    /// <see cref="SelectedInstance"/> before it ever reads
    /// <c>ServerConnectionManager.CurrentServer</c>, and returns that instance's connection string
    /// when it is set. So whoever writes here decides which SQL Server the process talks to, more
    /// strongly than whoever writes the edge that was chokepointed first. The round before this
    /// one closed <c>SetCurrentServer</c>, wrote "THE CHOKEPOINT for the process-wide connection
    /// edge" on the grant type, and left this one open with two ungated writers on a routable
    /// page — the same mistake as enumerating call sites, one level up: enumerating one member of
    /// a category instead of the category.</para>
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
        ///
        /// <para>Takes the same <see cref="ConnectionRetargetGrant"/> as
        /// <see cref="IServerConnectionManager.SetCurrentServer"/>, for the reason recorded on the
        /// type above: this is the stronger of the two process-wide connection edges, so gating
        /// only the other one gated nothing at the point of use. A grant parameter rather than a
        /// guard because a guard is something a new call site can be written without — and this
        /// edge acquired two ungated writers, on a page no census in the tree opens, while a
        /// sibling edge was being closed three times over.</para>
        ///
        /// <para>Only <see cref="ConnectionRetargetKind.Authorised"/> is accepted.
        /// <see cref="ConnectionRetargetKind.EstablishOnly"/> is REFUSED rather than quietly
        /// honoured: nothing bootstraps this value today — every reader treats "none selected" as
        /// "fall through to the current server", which is the edge that has a bootstrap of its
        /// own — so a caller asking to establish one here has not been thought about, and the safe
        /// answer to a caller that has not been thought about is no.</para>
        /// </summary>
        /// <param name="instanceName">The instance to point the process at.</param>
        /// <param name="grant">Who is asking, and under what permission.</param>
        /// <returns>
        /// True when the write was taken — including a write that set the value it already had.
        /// False when the grant did not permit it, in which case nothing was changed and no
        /// subscriber was notified.
        /// </returns>
        public bool SetSelectedInstance(string? instanceName, ConnectionRetargetGrant grant)
        {
            if (grant.Kind != ConnectionRetargetKind.Authorised)
            {
                _logger.LogInformation(
                    "Instance selection refused: the caller is not authorised for {Permission}. "
                    + "The selected instance was left as it is.",
                    grant.Permission ?? "(no permission named)");
                return false;
            }

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

            return true;
        }
    }
}
