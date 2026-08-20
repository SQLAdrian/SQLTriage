/* In the name of God, the Merciful, the Compassionate */
/*
 * Names for SYNTHESIZED ancestor nodes in the control hierarchy.
 *
 * The mapping corpus supplies control_name only for the exact control a check maps to — a
 * leaf, e.g. "03.01.01 Account Management". FrameworkControlSplitter then synthesizes the
 * ancestors ("03", "03.01") so the tree has a shape, and those have no name of their own:
 * they were never a mapped control, so no vote exists for them and the row renders bare.
 *
 * The fix is NOT to inherit a child's name upward. "Account Management" is the name of
 * 03.01.01, not of the whole 03 family; propagating it would be actively misleading, and
 * which child won would depend on sort order. Instead this file carries the published
 * family/section titles, which are stable, publicly documented, and small in number.
 *
 * SCOPE, honestly stated: this covers the frameworks whose top-level families are both
 * well-known and numerous enough to matter (NIST 800-171 rev3, NIST 800-53 / FedRAMP, ISO
 * 27001:2022 Annex A themes, CIS Controls v8, PCI DSS v4). Everything else falls through to
 * null, and ComplianceTree renders a neutral child/check summary rather than a blank.
 *
 * OPEN QUESTION for Adrian: authoritative family titles arguably belong corpus-side, next to
 * the control names they sit above, rather than in app code. This table is the app-side
 * stopgap that makes the tree readable today; treat it as provisional.
 *
 * Matching mirrors FrameworkControlSplitter.FamilyFor — lowercase substring on the framework
 * label — so a corpus that writes "NIST SP 800-171 rev3" still resolves.
 */

using System;
using System.Collections.Generic;

namespace SQLTriage.Data.Services.FrameworkTree
{
    public static class FrameworkFamilyNames
    {
        /// <summary>
        /// The published title for a synthesized ancestor id, or null when we do not have one.
        /// Null is a perfectly good answer — the UI has a neutral fallback.
        /// </summary>
        public static string? TryGet(string? framework, string? fullId)
        {
            var id = (fullId ?? string.Empty).Trim();
            if (id.Length == 0) return null;

            var table = TableFor(framework);
            if (table is null) return null;

            return table.TryGetValue(id, out var name) ? name : null;
        }

        private static IReadOnlyDictionary<string, string>? TableFor(string? framework)
        {
            var n = (framework ?? string.Empty).ToLowerInvariant();

            if (n.Contains("800-171")) return Nist800171;
            if (n.Contains("800-53") || n.Contains("fedramp")) return Nist80053;
            // Bare "27001" on purpose: the corpus writes "ISO/IEC 27001:2022", which contains
            // neither "iso 27001" nor "iso27001".
            if (n.Contains("27001")) return Iso27001;
            if (n.Contains("pci")) return PciDss;
            // "CIS Controls" only — the CIS SQL Server Benchmark is a different numbering
            // scheme whose sections are product areas, not these control families.
            if (n.Contains("cis") && !n.Contains("sql")) return CisControlsV8;

            return null;
        }

        // NIST SP 800-171 rev3 families. Ids are the two-digit family prefix used by rev3
        // (03.xx.yy), so the synthesized level-1 node is "03" and level-2 is "03.01".
        private static readonly Dictionary<string, string> Nist800171 = new(StringComparer.Ordinal)
        {
            ["03"]    = "Security Requirements",
            ["03.01"] = "Access Control",
            ["03.02"] = "Awareness and Training",
            ["03.03"] = "Audit and Accountability",
            ["03.04"] = "Configuration Management",
            ["03.05"] = "Identification and Authentication",
            ["03.06"] = "Incident Response",
            ["03.07"] = "Maintenance",
            ["03.08"] = "Media Protection",
            ["03.09"] = "Personnel Security",
            ["03.10"] = "Physical Protection",
            ["03.11"] = "Risk Assessment",
            ["03.12"] = "Security Assessment and Monitoring",
            ["03.13"] = "System and Communications Protection",
            ["03.14"] = "System and Information Integrity",
            ["03.15"] = "Planning",
            ["03.16"] = "System and Services Acquisition",
            ["03.17"] = "Supply Chain Risk Management",

            // rev2 numbering (3.x.y) is still in the wild — same families, no leading zero.
            ["3"]     = "Security Requirements",
            ["3.1"]   = "Access Control",
            ["3.2"]   = "Awareness and Training",
            ["3.3"]   = "Audit and Accountability",
            ["3.4"]   = "Configuration Management",
            ["3.5"]   = "Identification and Authentication",
            ["3.6"]   = "Incident Response",
            ["3.7"]   = "Maintenance",
            ["3.8"]   = "Media Protection",
            ["3.9"]   = "Personnel Security",
            ["3.10"]  = "Physical Protection",
            ["3.11"]  = "Risk Assessment",
            ["3.12"]  = "Security Assessment",
            ["3.13"]  = "System and Communications Protection",
            ["3.14"]  = "System and Information Integrity",
        };

        // NIST SP 800-53 rev5 control families (FedRAMP uses the same identifiers).
        private static readonly Dictionary<string, string> Nist80053 = new(StringComparer.OrdinalIgnoreCase)
        {
            ["AC"] = "Access Control",
            ["AT"] = "Awareness and Training",
            ["AU"] = "Audit and Accountability",
            ["CA"] = "Assessment, Authorization, and Monitoring",
            ["CM"] = "Configuration Management",
            ["CP"] = "Contingency Planning",
            ["IA"] = "Identification and Authentication",
            ["IR"] = "Incident Response",
            ["MA"] = "Maintenance",
            ["MP"] = "Media Protection",
            ["PE"] = "Physical and Environmental Protection",
            ["PL"] = "Planning",
            ["PM"] = "Program Management",
            ["PS"] = "Personnel Security",
            ["PT"] = "Personally Identifiable Information Processing and Transparency",
            ["RA"] = "Risk Assessment",
            ["SA"] = "System and Services Acquisition",
            ["SC"] = "System and Communications Protection",
            ["SI"] = "System and Information Integrity",
            ["SR"] = "Supply Chain Risk Management",
        };

        // ISO/IEC 27001:2022 Annex A themes.
        private static readonly Dictionary<string, string> Iso27001 = new(StringComparer.Ordinal)
        {
            ["5"] = "Organizational Controls",
            ["6"] = "People Controls",
            ["7"] = "Physical Controls",
            ["8"] = "Technological Controls",
        };

        // PCI DSS v4.0 top-level requirements.
        private static readonly Dictionary<string, string> PciDss = new(StringComparer.Ordinal)
        {
            ["1"]  = "Install and Maintain Network Security Controls",
            ["2"]  = "Apply Secure Configurations",
            ["3"]  = "Protect Stored Account Data",
            ["4"]  = "Protect Cardholder Data with Strong Cryptography During Transmission",
            ["5"]  = "Protect All Systems and Networks from Malicious Software",
            ["6"]  = "Develop and Maintain Secure Systems and Software",
            ["7"]  = "Restrict Access by Business Need to Know",
            ["8"]  = "Identify Users and Authenticate Access",
            ["9"]  = "Restrict Physical Access to Cardholder Data",
            ["10"] = "Log and Monitor All Access",
            ["11"] = "Test Security of Systems and Networks Regularly",
            ["12"] = "Support Information Security with Policies and Programs",
        };

        // CIS Critical Security Controls v8.
        private static readonly Dictionary<string, string> CisControlsV8 = new(StringComparer.Ordinal)
        {
            ["1"]  = "Inventory and Control of Enterprise Assets",
            ["2"]  = "Inventory and Control of Software Assets",
            ["3"]  = "Data Protection",
            ["4"]  = "Secure Configuration of Enterprise Assets and Software",
            ["5"]  = "Account Management",
            ["6"]  = "Access Control Management",
            ["7"]  = "Continuous Vulnerability Management",
            ["8"]  = "Audit Log Management",
            ["9"]  = "Email and Web Browser Protections",
            ["10"] = "Malware Defenses",
            ["11"] = "Data Recovery",
            ["12"] = "Network Infrastructure Management",
            ["13"] = "Network Monitoring and Defense",
            ["14"] = "Security Awareness and Skills Training",
            ["15"] = "Service Provider Management",
            ["16"] = "Application Software Security",
            ["17"] = "Incident Response Management",
            ["18"] = "Penetration Testing",
        };
    }
}
