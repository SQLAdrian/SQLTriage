/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SQLTriage.Data.Models;
using SQLTriage.Data.Parser;
using FrameworkMapping = SQLTriage.Data.Parser.FrameworkMapping;

namespace SQLTriage.Data.Services.FrameworkTree
{
    /// <summary>
    /// Worst-of rollup states for a control node. Precedence (R4, 2026-07-22):
    /// FAIL &gt; WARN &gt; PASS &gt; SKIP &gt; INFO. <see cref="None"/> = unassessed
    /// (no check under the node produced a result). Ranks below encode that ordering;
    /// the worst-of rollup keeps the highest rank among assessed descendants.
    ///
    /// This is the RAW five-state contract straight off <see cref="CheckResult.Verdict"/>
    /// — deliberately different from the Compliance Board's collapsed Passed/Failed/
    /// Unassessed scoring.
    /// </summary>
    public enum FrameworkVerdict
    {
        None = 0,
        Info = 1,
        Skip = 2,
        Pass = 3,
        Warn = 4,
        Fail = 5,
    }

    /// <summary>
    /// Builds the per-framework control hierarchy forest from the corpus framework
    /// mappings + catalogue check metadata, once, cached against the catalogue's
    /// integrity hash (rebuilt when the catalogue reloads). The build logic is a pure
    /// static (<see cref="BuildForest"/>) so it is unit-testable without DI or IO.
    ///
    /// Verdicts are NOT baked into the cached structure — they join per render from the
    /// live <see cref="QuickCheckStateService"/> results by CheckId
    /// (<see cref="IndexResults"/> + <see cref="RollupNode"/>).
    /// </summary>
    public sealed class FrameworkTreeService
    {
        private readonly CheckRepositoryService _repo;
        private readonly object _lock = new();
        private FrameworkForest? _cached;
        private string? _cachedKey;

        public FrameworkTreeService(CheckRepositoryService repo)
        {
            _repo = repo;
        }

        /// <summary>The built forest, cached against the catalogue's integrity hash +
        /// check count so a bundle/source reload transparently rebuilds it.</summary>
        public FrameworkForest GetForest()
        {
            var checks = _repo.GetAllChecks();
            var key = (_repo.SourceIntegrityHash ?? "none") + ":" + checks.Count;
            lock (_lock)
            {
                if (_cached is not null && _cachedKey == key) return _cached;
                _cached = BuildForest(checks, _repo.FrameworkMappings);
                _cachedKey = key;
                return _cached;
            }
        }

        // ─────────────────────────── pure build ───────────────────────────

        private sealed class NodeBuilder
        {
            public string Seg = string.Empty;
            public string FullId = string.Empty;
            public int Depth;
            public readonly Dictionary<string, NodeBuilder> ChildMap = new(StringComparer.Ordinal);
            public readonly List<MappedCheck> Checks = new();
        }

        /// <summary>
        /// Pure forest builder. <paramref name="checks"/> supplies title/severity per
        /// check id and the unmapped denominator; <paramref name="mappings"/> is the
        /// side-index (check id -&gt; framework mappings) from the source parser.
        /// </summary>
        public static FrameworkForest BuildForest(
            IReadOnlyList<SqlCheck> checks,
            IReadOnlyDictionary<string, IReadOnlyList<FrameworkMapping>> mappings)
        {
            var meta = new Dictionary<string, SqlCheck>(StringComparer.Ordinal);
            foreach (var c in checks)
                if (!string.IsNullOrEmpty(c.Id)) meta[c.Id] = c;

            // framework -> list of entries (one per check×mapping)
            var fwEntries = new Dictionary<string, List<(string CheckId, string ControlId, string? ControlName, string? MappingType)>>(StringComparer.Ordinal);
            // framework -> control_id -> control_name -> vote count
            var fwNameVotes = new Dictionary<string, Dictionary<string, Dictionary<string, int>>>(StringComparer.Ordinal);
            var mappedCheckIds = new HashSet<string>(StringComparer.Ordinal);
            var fallback = new SortedSet<string>(StringComparer.Ordinal);
            var totalEntries = 0;

            foreach (var kv in mappings)
            {
                var checkId = kv.Key;
                if (kv.Value is null || kv.Value.Count == 0) continue;
                var any = false;
                foreach (var m in kv.Value)
                {
                    var fw = m.Framework ?? string.Empty;
                    var cid = (m.ControlId ?? string.Empty).Trim();
                    if (fw.Length == 0 || cid.Length == 0) continue;
                    any = true;
                    totalEntries++;

                    if (!fwEntries.TryGetValue(fw, out var list))
                    {
                        list = new();
                        fwEntries[fw] = list;
                    }
                    var cname = string.IsNullOrWhiteSpace(m.ControlName) ? null : m.ControlName!.Trim();
                    list.Add((checkId, cid, cname, m.MappingType));

                    if (cname is not null)
                    {
                        if (!fwNameVotes.TryGetValue(fw, out var byCid))
                        {
                            byCid = new(StringComparer.Ordinal);
                            fwNameVotes[fw] = byCid;
                        }
                        if (!byCid.TryGetValue(cid, out var byName))
                        {
                            byName = new(StringComparer.Ordinal);
                            byCid[cid] = byName;
                        }
                        byName[cname] = byName.TryGetValue(cname, out var n) ? n + 1 : 1;
                    }
                }
                if (any) mappedCheckIds.Add(checkId);
            }

            var trees = new List<FrameworkTree>(fwEntries.Count);
            foreach (var (fw, entries) in fwEntries)
            {
                var roots = new Dictionary<string, NodeBuilder>(StringComparer.Ordinal);
                var maxDepth = 0;
                var controlIds = new HashSet<string>(StringComparer.Ordinal);

                foreach (var e in entries)
                {
                    controlIds.Add(e.ControlId);
                    var levels = FrameworkControlSplitter.ComputeLevels(fw, e.ControlId, out var usedFallback);
                    if (usedFallback) fallback.Add($"{fw} | {e.ControlId}");
                    var segs = FrameworkControlSplitter.ComputeSegs(levels);
                    if (levels.Count > maxDepth) maxDepth = levels.Count;

                    var cur = roots;
                    NodeBuilder? node = null;
                    for (var i = 0; i < levels.Count; i++)
                    {
                        var fullId = levels[i];
                        if (!cur.TryGetValue(fullId, out node!))
                        {
                            node = new NodeBuilder { Seg = segs[i], FullId = fullId, Depth = i + 1 };
                            cur[fullId] = node;
                        }
                        cur = node.ChildMap;
                    }
                    node!.Checks.Add(new MappedCheck(
                        e.CheckId,
                        meta.TryGetValue(e.CheckId, out var sc) ? sc.Name : e.CheckId,
                        meta.TryGetValue(e.CheckId, out var sc2) ? sc2.Severity : string.Empty,
                        e.MappingType));
                }

                var votes = fwNameVotes.TryGetValue(fw, out var v) ? v : null;
                var finalized = Finalize(roots, votes, fw);
                trees.Add(new FrameworkTree
                {
                    Name = fw,
                    EntryCount = entries.Count,
                    ControlCount = controlIds.Count,
                    MaxDepth = maxDepth,
                    Roots = finalized,
                });
            }

            // Largest first (entry count), then name for stable ordering.
            trees.Sort((a, b) =>
            {
                var c = b.EntryCount.CompareTo(a.EntryCount);
                return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.Ordinal);
            });

            var unmapped = checks
                .Where(c => !string.IsNullOrEmpty(c.Id) && !mappedCheckIds.Contains(c.Id))
                .Select(c => new UnmappedCheck(c.Id, c.Name, c.Severity))
                .OrderBy(c => c.CheckId, StringComparer.Ordinal)
                .ToList();

            return new FrameworkForest
            {
                Frameworks = trees,
                UnmappedChecks = unmapped,
                TotalChecks = checks.Count,
                MappedCheckCount = mappedCheckIds.Count,
                TotalEntries = totalEntries,
                FallbackControls = fallback.ToList(),
            };
        }

        private static List<FrameworkTreeNode> Finalize(
            Dictionary<string, NodeBuilder> childMap,
            Dictionary<string, Dictionary<string, int>>? votes,
            string framework)
        {
            var nodes = new List<FrameworkTreeNode>(childMap.Count);
            foreach (var b in childMap.Values)
            {
                var node = new FrameworkTreeNode
                {
                    Seg = b.Seg,
                    FullId = b.FullId,
                    Depth = b.Depth,
                    // Corpus vote first (this id IS a mapped control), then the published
                    // family title for a synthesized ancestor. Never inherit a child's name
                    // upward: "Account Management" names 03.01.01, not the whole 03 family.
                    Name = MajorityName(votes, b.FullId)
                           ?? FrameworkFamilyNames.TryGet(framework, b.FullId),
                };
                node.Checks.AddRange(b.Checks);
                node.Children.AddRange(Finalize(b.ChildMap, votes, framework));

                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var mc in node.Checks) ids.Add(mc.CheckId);
                foreach (var ch in node.Children)
                    CollectCheckIds(ch, ids);
                node.CheckCount = ids.Count;

                nodes.Add(node);
            }
            nodes.Sort((a, b) => NaturalCompare(a.Seg, b.Seg));
            return nodes;
        }

        private static void CollectCheckIds(FrameworkTreeNode node, HashSet<string> into)
        {
            foreach (var mc in node.Checks) into.Add(mc.CheckId);
            foreach (var ch in node.Children) CollectCheckIds(ch, into);
        }

        private static string? MajorityName(Dictionary<string, Dictionary<string, int>>? votes, string fullId)
        {
            if (votes is null || !votes.TryGetValue(fullId, out var byName) || byName.Count == 0) return null;
            // Highest count, ties broken alphabetically (matches the demo generator).
            return byName
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .First().Key;
        }

        // ─────────────────────── numeric-aware sort ───────────────────────
        private static readonly Regex Token = new(@"\d+|\D+", RegexOptions.Compiled);

        /// <summary>Natural comparison so "2" sorts before "10".</summary>
        public static int NaturalCompare(string a, string b)
        {
            var ta = Token.Matches(a);
            var tb = Token.Matches(b);
            var n = Math.Min(ta.Count, tb.Count);
            for (var i = 0; i < n; i++)
            {
                var sa = ta[i].Value;
                var sb = tb[i].Value;
                var da = sa.Length > 0 && char.IsDigit(sa[0]);
                var db = sb.Length > 0 && char.IsDigit(sb[0]);
                if (da && db)
                {
                    // Compare as big integers via length-then-lexical to avoid overflow.
                    var na = sa.TrimStart('0');
                    var nb = sb.TrimStart('0');
                    if (na.Length != nb.Length) return na.Length.CompareTo(nb.Length);
                    var c = string.CompareOrdinal(na, nb);
                    if (c != 0) return c;
                }
                else if (da != db)
                {
                    // digits sort before non-digits (mirrors the demo's (0,...) < (1,...))
                    return da ? -1 : 1;
                }
                else
                {
                    var c = string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase);
                    if (c != 0) return c;
                }
            }
            return ta.Count.CompareTo(tb.Count);
        }

        // ─────────────────────── verdict join (per render) ───────────────────────

        /// <summary>
        /// Map one raw <see cref="CheckResult"/> to its five-state verdict. Uses the raw
        /// <see cref="CheckResult.Verdict"/> string when present (PASS/FAIL/INFO/WARN/SKIP).
        /// A verdict-less result is classified through <see cref="CheckClassification"/> first —
        /// a SKIP (Message-prefix or ErrorMessage) or INFO (Severity) that rides Passed=true is
        /// mapped to Skip / Info, NOT promoted to Pass — and only a genuinely scorable
        /// numeric-contract check then falls back to Passed -&gt; PASS / FAIL. An errored result
        /// (no verdict, Passed=false) reads FAIL — it is not a passing control.
        /// </summary>
        public static FrameworkVerdict ParseVerdict(CheckResult r)
        {
            var raw = r.Verdict?.Trim().ToUpperInvariant();
            return raw switch
            {
                "FAIL" => FrameworkVerdict.Fail,
                "WARN" => FrameworkVerdict.Warn,
                "PASS" => FrameworkVerdict.Pass,
                "SKIP" => FrameworkVerdict.Skip,
                "INFO" => FrameworkVerdict.Info,
                // Verdict-less (numeric-contract) result: INFO (by Severity) and SKIP (by
                // Message-prefix or ErrorMessage) both ride Passed=true, so they must be excluded
                // through CheckClassification BEFORE the pass/fail bit is read — otherwise a
                // non-assertion renders as a green Pass on the compliance tree (the raw-.Passed
                // false-clean the RawPassedGuard exists to catch).
                _ when CheckClassification.IsSkip(r) => FrameworkVerdict.Skip,
                _ when CheckClassification.IsInfo(r) => FrameworkVerdict.Info,
                _ => r.Passed ? FrameworkVerdict.Pass : FrameworkVerdict.Fail,
            };
        }

        /// <summary>Worst-of two verdicts by rank (FAIL highest). <see cref="FrameworkVerdict.None"/>
        /// yields to any assessed state.</summary>
        public static FrameworkVerdict Worse(FrameworkVerdict a, FrameworkVerdict b)
            => (FrameworkVerdict)Math.Max((int)a, (int)b);

        /// <summary>
        /// Index live results by CheckId to the worst verdict seen for that id across all
        /// instances/servers. Blank ids are dropped.
        /// </summary>
        public static Dictionary<string, FrameworkVerdict> IndexResults(IEnumerable<CheckResult> results)
        {
            var byId = new Dictionary<string, FrameworkVerdict>(StringComparer.Ordinal);
            foreach (var r in results)
            {
                if (string.IsNullOrEmpty(r.CheckId)) continue;
                var v = ParseVerdict(r);
                byId[r.CheckId] = byId.TryGetValue(r.CheckId, out var cur) ? Worse(cur, v) : v;
            }
            return byId;
        }

        /// <summary>Worst-of-descendants verdict rollup for a node: the worst verdict
        /// among its own checks and all descendants. <see cref="FrameworkVerdict.None"/>
        /// when nothing under the node was assessed.</summary>
        public static FrameworkVerdict RollupNode(FrameworkTreeNode node, IReadOnlyDictionary<string, FrameworkVerdict> byId)
        {
            var worst = FrameworkVerdict.None;
            foreach (var mc in node.Checks)
                if (byId.TryGetValue(mc.CheckId, out var v))
                    worst = Worse(worst, v);
            foreach (var ch in node.Children)
                worst = Worse(worst, RollupNode(ch, byId));
            return worst;
        }

        /// <summary>Count of result CheckIds that match no mapped check in the forest —
        /// the honest "ignored, not silently folded" number for the readouts.</summary>
        public static int CountUnknownResultIds(FrameworkForest forest, IEnumerable<CheckResult> results)
        {
            var mapped = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in forest.Frameworks)
                foreach (var root in t.Roots)
                    CollectCheckIds(root, mapped);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in results)
                if (!string.IsNullOrEmpty(r.CheckId) && !mapped.Contains(r.CheckId))
                    seen.Add(r.CheckId);
            return seen.Count;
        }
    }
}
