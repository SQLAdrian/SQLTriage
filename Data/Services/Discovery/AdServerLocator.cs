// In the name of God, the Merciful, the Compassionate
// Locate SQL Server instances via Active Directory SPNs (MSSQLSvc/*). NEVER port-scans.
// Windows-only (TFM net8.0-windows). Requires the System.DirectoryServices package.
using System;
using System.Collections.Generic;
using System.DirectoryServices;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data.Services.Discovery
{
    public sealed class AdServerLocator
    {
        private readonly ILogger<AdServerLocator>? _log;
        public AdServerLocator(ILogger<AdServerLocator>? log = null) => _log = log;

        /// <summary>
        /// Returns SQL Server instance names registered in AD as MSSQLSvc SPNs. Read-only LDAP query against
        /// the current domain under the running user's context. Returns an empty list (never throws) when not
        /// domain-joined or AD is unreachable — the caller falls back to a seed-only crawl.
        /// </summary>
        public IReadOnlyList<string> FindSqlServers()
        {
            var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string namingContext;
                using (var root = new DirectoryEntry("LDAP://RootDSE"))
                {
                    namingContext = root.Properties["defaultNamingContext"]?.Value?.ToString() ?? "";
                }
                if (string.IsNullOrEmpty(namingContext)) return Array.Empty<string>();

                using var domain = new DirectoryEntry("LDAP://" + namingContext);
                using var searcher = new DirectorySearcher(domain)
                {
                    Filter = "(servicePrincipalName=MSSQLSvc/*)",
                    PageSize = 500,
                    SizeLimit = 0,
                };
                searcher.PropertiesToLoad.Add("servicePrincipalName");

                using var found = searcher.FindAll();
                foreach (SearchResult sr in found)
                {
                    foreach (var spnObj in sr.Properties["servicePrincipalName"])
                    {
                        var name = ParseSpn(spnObj?.ToString());
                        if (!string.IsNullOrEmpty(name)) results.Add(name!);
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.LogInformation(ex, "AD SPN enumeration unavailable; falling back to seed-only crawl.");
                return Array.Empty<string>();
            }
            return results.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// MSSQLSvc/host.fqdn:1433 -> host (default instance); MSSQLSvc/host.fqdn:INSTANCE -> host\INSTANCE.
        /// The host is shortened to its NetBIOS label (left of the first dot) to match how SQL Server names
        /// instances; FQDN still resolves on connect.
        /// </summary>
        internal static string? ParseSpn(string? spn)
        {
            if (string.IsNullOrWhiteSpace(spn)) return null;
            // strip "MSSQLSvc/"
            var slash = spn.IndexOf('/');
            if (slash < 0) return null;
            var hostPort = spn.Substring(slash + 1).Trim();
            if (hostPort.Length == 0) return null;

            string host = hostPort;
            string? suffix = null;
            var colon = hostPort.LastIndexOf(':');
            if (colon > 0)
            {
                host = hostPort.Substring(0, colon);
                suffix = hostPort.Substring(colon + 1).Trim();
            }

            var shortHost = host.Split('.')[0].Trim();
            if (shortHost.Length == 0) return null;

            // Numeric suffix = TCP port (default-instance or unknown instance) -> just the host.
            // Non-numeric suffix = named instance -> host\INSTANCE.
            if (!string.IsNullOrEmpty(suffix) && !int.TryParse(suffix, out _))
                return shortHost + "\\" + suffix;

            return shortHost;
        }
    }
}
