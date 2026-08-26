/* In the name of God, the Merciful, the Compassionate */

using System;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// #47 (2026-07-15): presentation-only helper that renders the PROBLEM a failing check
    /// found, never the check's own title. Check titles are authored as the DESIRED state
    /// (e.g. "FAILED_LOGIN_GROUP Is Captured by an Enabled Audit Specification") — printing
    /// that title as the headline of a CRITICAL-badged card reads as an assertion that the
    /// control IS in place, when the opposite is true.
    ///
    /// Some corpus checks compute a genuine, pass/fail-differentiated <see cref="CheckResult.Message"/>
    /// (e.g. "3 of 3 required change-event groups ... are NOT captured..." — used as-is here).
    /// Others return the exact same literal title text as their Message regardless of PASS or
    /// FAIL (a corpus-content gap, out of scope for this app-side fix) — for those, an explicit
    /// "Check failed:" framing is used instead, so a CRITICAL-badged card can never again read
    /// as the good state. This never invents per-check finding text; it only chooses between
    /// data the check already returned.
    /// </summary>
    public static class FindingPresentation
    {
        public static string ProblemStatement(CheckResult r)
        {
            if (r == null) return "";
            var title = (r.CheckName ?? "").Trim();
            var message = (r.Message ?? "").Trim();

            var hasGenuineMessage = !string.IsNullOrWhiteSpace(message)
                && !string.Equals(message, title, StringComparison.OrdinalIgnoreCase);

            if (hasGenuineMessage) return message;

            return string.IsNullOrWhiteSpace(title)
                ? "Check failed — no finding detail available."
                : $"Check failed: {title}";
        }
    }
}
