/* In the name of God, the Merciful, the Compassionate */

using System.Text.RegularExpressions;

namespace SQLTriage.Data.Services
{
    // BM:WindowsIdentityKey.Class — normalises and compares Windows account names as RBAC identity keys
    /// <summary>
    /// Normalisation and comparison for Windows account names used as RBAC identity keys.
    ///
    /// <para>Windows hands the same principal to us in several shapes: down-level
    /// <c>DOMAIN\user</c> (what <c>WindowsIdentity.Name</c> returns), UPN
    /// <c>user@domain.tld</c> (Kerberos), <c>.\user</c> shorthand, and a bare <c>user</c>.
    /// All of them have to land on ONE stored key or a user who signs in successfully still
    /// fails to match their own record.</para>
    ///
    /// <para>Everything here is pure — no Windows API calls, no directory lookups — so it is
    /// unit-testable on any host and cannot stall a login on an unreachable domain controller.</para>
    /// </summary>
    public static class WindowsIdentityKey
    {
        /// <summary>
        /// Names that look like a domain but are not one. <c>local</c> is the one Adrian typed:
        /// it is not a machine name and not a NetBIOS domain, so <c>local\admin.adrian</c> would
        /// be looked up as a domain called "local" and fail at logon every time.
        /// </summary>
        private static readonly string[] NotADomain = { "local", "localhost", "workgroup" };

        // Windows account names cannot contain any of " / \ [ ] : ; | = , + * ? < >
        private static readonly Regex IllegalAccountChars = new(@"[""/\\\[\]:;|=,+*?<>]", RegexOptions.Compiled);

        /// <summary>Result of parsing a typed or supplied Windows identity.</summary>
        /// <param name="Ok">True when <paramref name="Key"/> is a usable identity key.</param>
        /// <param name="Key">The normalised down-level key, <c>DOMAIN\user</c> or a UPN.</param>
        /// <param name="Domain">The domain / machine part, or empty for a UPN.</param>
        /// <param name="Account">The account part.</param>
        /// <param name="Error">Operator-facing reason when <paramref name="Ok"/> is false.</param>
        public readonly record struct ParseResult(bool Ok, string Key, string Domain, string Account, string Error);

        /// <summary>
        /// Normalises a Windows identity for storage and comparison.
        ///
        /// <list type="bullet">
        /// <item><c>MSI\admin.adrian</c> → <c>MSI\admin.adrian</c></item>
        /// <item><c>.\admin.adrian</c> → <c>&lt;machine&gt;\admin.adrian</c></item>
        /// <item><c>admin.adrian</c> → <c>&lt;machine&gt;\admin.adrian</c></item>
        /// <item><c>CONTOSO\adrian</c> → <c>CONTOSO\adrian</c></item>
        /// <item><c>adrian@contoso.com</c> → kept as a UPN (no resolvable down-level form here)</item>
        /// <item><c>local\admin.adrian</c> → rejected, with the fix named</item>
        /// </list>
        /// </summary>
        /// <param name="raw">The typed or supplied identity.</param>
        /// <param name="machineName">
        /// The host name used to expand <c>.\</c> and bare accounts. Defaults to this machine;
        /// injectable so tests are not host-dependent.
        /// </param>
        public static ParseResult Parse(string? raw, string? machineName = null)
        {
            var machine = string.IsNullOrWhiteSpace(machineName) ? Environment.MachineName : machineName!.Trim();
            var input = (raw ?? string.Empty).Trim();

            if (input.Length == 0)
                return new ParseResult(false, "", "", "", "Enter a Windows account, e.g. " + machine + @"\admin.adrian.");

            // UPN form (user@domain.tld) with no backslash. Kerberos can present this; we cannot
            // resolve it to a down-level name without a directory, so it is stored as typed. It
            // can never collide with an OAuth address because the provider CLASS differs.
            if (!input.Contains('\\') && input.Contains('@'))
            {
                var at = input.IndexOf('@');
                var upnAccount = input[..at];
                var upnDomain = input[(at + 1)..];
                if (upnAccount.Length == 0 || upnDomain.Length == 0)
                    return new ParseResult(false, "", "", "", "'" + input + "' is not a valid user principal name.");
                return new ParseResult(true, input, upnDomain, upnAccount, "");
            }

            string domain;
            string account;

            var lastSlash = input.LastIndexOf('\\');
            if (lastSlash >= 0)
            {
                domain = input[..lastSlash].Trim();
                account = input[(lastSlash + 1)..].Trim();

                if (domain.Length == 0)
                    return new ParseResult(false, "", "", "",
                        @"Missing the machine or domain name before the '\'. Use " + machine + @"\" + account + " for a local account.");

                if (domain == ".")
                    domain = machine;
            }
            else
            {
                // Bare account name — a local account on this machine.
                domain = machine;
                account = input;
            }

            if (account.Length == 0)
                return new ParseResult(false, "", "", "",
                    @"Missing the account name after the '\'. Use " + domain + @"\admin.adrian.");

            foreach (var bad in NotADomain)
            {
                if (!domain.Equals(bad, StringComparison.OrdinalIgnoreCase)) continue;

                // Name the fix, not the rule. "'local' is not a valid domain" leaves the operator
                // exactly where they were; this tells them what to type instead.
                return new ParseResult(false, "", "", "",
                    "'" + domain + "' is not a machine or domain name. Use " + machine + @"\" + account
                    + " for a local account, .\\" + account + " for shorthand, or DOMAIN\\"
                    + account + " for a domain account.");
            }

            if (IllegalAccountChars.IsMatch(account))
                return new ParseResult(false, "", "", "",
                    "'" + account + "' contains characters a Windows account name cannot hold.");

            if (IllegalAccountChars.IsMatch(domain))
                return new ParseResult(false, "", "", "",
                    "'" + domain + "' contains characters a machine or domain name cannot hold.");

            return new ParseResult(true, domain + "\\" + account, domain, account, "");
        }

        /// <summary>
        /// True when two Windows identity keys name the same principal. Windows account and
        /// domain names are case-insensitive, so both halves compare ordinal-ignore-case.
        /// A bare or <c>.\</c>-prefixed key on either side is expanded against the same machine
        /// name first, so <c>admin.adrian</c> and <c>MSI\admin.adrian</c> match on host MSI.
        /// </summary>
        public static bool Equal(string? a, string? b, string? machineName = null)
        {
            var pa = Parse(a, machineName);
            var pb = Parse(b, machineName);
            if (!pa.Ok || !pb.Ok) return false;

            return string.Equals(pa.Domain, pb.Domain, StringComparison.OrdinalIgnoreCase)
                && string.Equals(pa.Account, pb.Account, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Normalises for storage, returning the input unchanged if it cannot be parsed.
        /// Used on the read path, where refusing to normalise a legacy record is worse than
        /// leaving it alone.
        /// </summary>
        public static string Normalize(string? raw, string? machineName = null)
        {
            var parsed = Parse(raw, machineName);
            return parsed.Ok ? parsed.Key : (raw ?? string.Empty).Trim();
        }
    }
}
