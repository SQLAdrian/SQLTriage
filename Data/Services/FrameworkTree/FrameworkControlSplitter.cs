/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SQLTriage.Data.Services.FrameworkTree
{
    /// <summary>
    /// Pure control-id hierarchy grammar. Given a framework string and a corpus
    /// <c>control_id</c>, returns the cumulative list of node full-ids from level 1
    /// down to the leaf.
    ///
    /// LAW (a real defect shipped and was fixed over this in the demo build): the
    /// full-ids are CUMULATIVE — every child's full-id extends its parent's, and a
    /// leaf's full-id equals the VERBATIM control_id. This is load-bearing because
    /// name-attachment fires only when a node's full-id equals a mapped control_id;
    /// a non-cumulative chain silently drops every control_name (the GDPR bug).
    ///
    /// Known framework families are matched by a lowercase substring of the framework
    /// string. Everything else — including frameworks not yet seen — falls to the
    /// generic dotted+paren split, so a new framework added to the corpus later
    /// renders with zero code changes.
    ///
    /// Ported from the verified demo generator (framework-tree/build-tree.py,
    /// 2026-07-22). The corpus itself is never read here; this is string grammar only.
    /// </summary>
    public static class FrameworkControlSplitter
    {
        // ── generic dotted + optional single trailing paren ──────────────────
        private static readonly Regex ParenSuffix = new(@"^(.*?)(\([^()]+\))$", RegexOptions.Compiled);

        private static List<string> DotLevels(string prefix)
        {
            var parts = prefix.Split('.');
            var levels = new List<string>(parts.Length);
            string acc = "";
            foreach (var p in parts)
            {
                acc = acc.Length == 0 ? p : acc + "." + p;
                levels.Add(acc);
            }
            return levels;
        }

        /// <summary>ISO 27001/27017/27018/22301, PCI-DSS, CIS Controls v8, CIS SQL
        /// Server Benchmark, NIST 800-171, NZISM, OWASP — and the catch-all default
        /// for any unknown/future framework: plain dot-split, each dot a cumulative
        /// level, with an optional single trailing parenthetical as one further leaf
        /// level (e.g. <c>8.13(a)</c> -> <c>8</c> -> <c>8.13</c> -> <c>8.13(a)</c>).</summary>
        private static List<string>? SplitGenericDottedParen(string controlId)
        {
            var m = ParenSuffix.Match(controlId);
            if (m.Success)
            {
                var prefix = m.Groups[1].Value;
                var paren = m.Groups[2].Value;
                if (prefix.Length == 0) return null;
                var levels = DotLevels(prefix);
                levels.Add(prefix + paren);
                return levels;
            }
            return DotLevels(controlId);
        }

        // ── NIST SP 800-53 + FedRAMP: CP-9(2) -> CP -> CP-9 -> CP-9(2) ────────
        private static readonly Regex DashFamily = new(@"^([A-Za-z]+)-(\d+)(\([^()]+\))?$", RegexOptions.Compiled);

        private static List<string>? SplitDashFamily(string controlId)
        {
            var m = DashFamily.Match(controlId);
            if (!m.Success) return null;
            var family = m.Groups[1].Value;
            var num = m.Groups[2].Value;
            var paren = m.Groups[3].Value;
            var levels = new List<string> { family, $"{family}-{num}" };
            if (paren.Length > 0) levels.Add($"{family}-{num}{paren}");
            return levels;
        }

        // ── NIST CSF 2.0: PR.PS-05 -> PR -> PR.PS -> PR.PS-05 ────────────────
        private static readonly Regex Csf = new(@"^([A-Z]{2})\.([A-Z]{2})-(\w+)$", RegexOptions.Compiled);

        private static List<string>? SplitCsf(string controlId)
        {
            var m = Csf.Match(controlId);
            if (!m.Success) return null;
            var func = m.Groups[1].Value;
            var cat = m.Groups[2].Value;
            var num = m.Groups[3].Value;
            return new List<string> { func, $"{func}.{cat}", $"{func}.{cat}-{num}" };
        }

        // ── CMMC 2.0: AC.L2-3.1.1 -> AC -> AC.L2 -> AC.L2-3.1.1 ──────────────
        //    (the trailing 800-171 ref is NEVER split further)
        private static readonly Regex Cmmc = new(@"^([A-Z]{2,4})\.(L\d)-(.+)$", RegexOptions.Compiled);

        private static List<string>? SplitCmmc(string controlId)
        {
            var m = Cmmc.Match(controlId);
            if (!m.Success) return null;
            var domain = m.Groups[1].Value;
            var level = m.Groups[2].Value;
            var reference = m.Groups[3].Value;
            return new List<string> { domain, $"{domain}.{level}", $"{domain}.{level}-{reference}" };
        }

        // ── HIPAA: 164.308(a)(7)(ii)(A) -> 164.308 -> (a) -> (7) -> (ii) -> (A)
        private static readonly Regex Hipaa = new(@"^(\d+\.\d+)((?:\([^()]+\))+)$", RegexOptions.Compiled);
        private static readonly Regex HipaaBase = new(@"^\d+\.\d+$", RegexOptions.Compiled);
        private static readonly Regex ParenGroup = new(@"\([^()]+\)", RegexOptions.Compiled);

        private static List<string>? SplitHipaa(string controlId)
        {
            var m = Hipaa.Match(controlId);
            if (m.Success)
            {
                var baseId = m.Groups[1].Value;
                var levels = new List<string> { baseId };
                var acc = baseId;
                foreach (Match p in ParenGroup.Matches(m.Groups[2].Value))
                {
                    acc += p.Value;
                    levels.Add(acc);
                }
                return levels;
            }
            if (HipaaBase.IsMatch(controlId)) return new List<string> { controlId };
            return null;
        }

        // ── GDPR: 'Article 32.1(c)' -> 'Article 32' -> 'Article 32.1' -> 'Article 32.1(c)'
        //    CUMULATIVE — the leaf full-id equals the verbatim control_id, or the
        //    control_name never attaches (the exact defect fixed 2026-07-22).
        private static readonly Regex Gdpr = new(@"^(?:Article\s+)?(\d+)(?:\.(\d+))?((?:\([^()]+\))+)?$", RegexOptions.Compiled);

        private static List<string>? SplitGdpr(string controlId)
        {
            var stripped = controlId.Trim();
            var m = Gdpr.Match(stripped);
            if (!m.Success) return null;
            var article = m.Groups[1].Value;
            var para = m.Groups[2].Success ? m.Groups[2].Value : null;
            var parensBlob = m.Groups[3].Success ? m.Groups[3].Value : null;
            var hadArticle = stripped.StartsWith("article", StringComparison.OrdinalIgnoreCase);
            var level1 = hadArticle ? $"Article {article}" : article;
            var levels = new List<string> { level1 };
            var acc = level1;
            if (!string.IsNullOrEmpty(para))
            {
                acc = $"{acc}.{para}";
                levels.Add(acc);
            }
            if (!string.IsNullOrEmpty(parensBlob))
            {
                foreach (Match p in ParenGroup.Matches(parensBlob))
                {
                    acc += p.Value;
                    levels.Add(acc);
                }
            }
            return levels;
        }

        // ── SOX ITGC / COBIT: BAI10.02 -> BAI -> BAI10 -> BAI10.02 ───────────
        private static readonly Regex SoxCobit = new(@"^([A-Za-z]+)(\d+)\.(\d+)$", RegexOptions.Compiled);

        private static List<string>? SplitSoxCobit(string controlId)
        {
            var m = SoxCobit.Match(controlId);
            if (!m.Success) return null;
            var letters = m.Groups[1].Value;
            var num1 = m.Groups[2].Value;
            var num2 = m.Groups[3].Value;
            return new List<string> { letters, $"{letters}{num1}", $"{letters}{num1}.{num2}" };
        }

        // ── SOC 2: CC6.1 -> CC6 -> CC6.1 (family = leading letters + digit run) ─
        private static readonly Regex Soc2 = new(@"^([A-Za-z]+\d+)\.(\d+)$", RegexOptions.Compiled);

        private static List<string>? SplitSoc2(string controlId)
        {
            var m = Soc2.Match(controlId);
            if (!m.Success) return null;
            var fam = m.Groups[1].Value;
            var num = m.Groups[2].Value;
            return new List<string> { fam, $"{fam}.{num}" };
        }

        // ── Essential Eight: ML1-P7 -> ML1 -> ML1-P7 ─────────────────────────
        private static readonly Regex EssentialEight = new(@"^(ML\d)-(.+)$", RegexOptions.Compiled);

        private static List<string>? SplitEssentialEight(string controlId)
        {
            var m = EssentialEight.Match(controlId);
            if (!m.Success) return null;
            var level = m.Groups[1].Value;
            var rest = m.Groups[2].Value;
            return new List<string> { level, $"{level}-{rest}" };
        }

        // ── Cyber Essentials: a single flat node, whatever the id is ─────────
        private static List<string>? SplitSingleLevel(string controlId) => new() { controlId };

        private static Func<string, List<string>?> FamilyFor(string framework)
        {
            var n = (framework ?? string.Empty).ToLowerInvariant();
            if (n.Contains("800-171")) return SplitGenericDottedParen;
            if (n.Contains("800-53") || n.Contains("fedramp")) return SplitDashFamily;
            if (n.Contains("csf")) return SplitCsf;
            if (n.Contains("cmmc")) return SplitCmmc;
            if (n.Contains("hipaa")) return SplitHipaa;
            if (n.Contains("gdpr")) return SplitGdpr;
            if (n.Contains("sox") || n.Contains("cobit")) return SplitSoxCobit;
            if (n.Contains("essential eight")) return SplitEssentialEight;
            if (n.Contains("cyber essentials")) return SplitSingleLevel;
            if (n.Contains("soc")) return SplitSoc2;
            // ISO 27001/27017/27018/22301, PCI-DSS, CIS, NZISM, OWASP, and any
            // framework not seen during scouting: generic dotted+paren.
            return SplitGenericDottedParen;
        }

        /// <summary>
        /// Cumulative level full-ids from level 1 to leaf. Never returns empty: an id
        /// that does not fit its family's shape (or throws) falls back to a single flat
        /// node equal to the verbatim control_id, and <paramref name="usedFallback"/> is
        /// set true so callers can surface it.
        /// </summary>
        public static IReadOnlyList<string> ComputeLevels(string framework, string controlId, out bool usedFallback)
        {
            usedFallback = false;
            List<string>? levels;
            try { levels = FamilyFor(framework)(controlId); }
            catch { levels = null; }
            if (levels is null || levels.Count == 0)
            {
                usedFallback = true;
                return new List<string> { controlId };
            }
            return levels;
        }

        /// <summary>
        /// Display segment for each level: the part of a level's full-id beyond its
        /// parent's, with a leading dot/dash trimmed. When a level does not textually
        /// extend its parent (defensive), the whole level is the segment.
        /// </summary>
        public static IReadOnlyList<string> ComputeSegs(IReadOnlyList<string> levels)
        {
            var segs = new List<string>(levels.Count);
            var prev = "";
            foreach (var lvl in levels)
            {
                string seg;
                if (prev.Length > 0 && lvl.StartsWith(prev, StringComparison.Ordinal))
                {
                    seg = lvl.Substring(prev.Length).TrimStart('.', '-');
                    if (seg.Length == 0) seg = lvl;
                }
                else
                {
                    seg = lvl;
                }
                segs.Add(seg);
                prev = lvl;
            }
            return segs;
        }
    }
}
