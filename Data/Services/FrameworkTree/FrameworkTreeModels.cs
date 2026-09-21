/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Generic;

namespace SQLTriage.Data.Services.FrameworkTree
{
    /// <summary>
    /// A corpus check mapped to a control node, carrying the check's own metadata for
    /// the leaf rows. Every string here is rendered verbatim from the corpus mapping /
    /// catalogue — never synthesised (DD rule).
    /// </summary>
    public sealed record MappedCheck(string CheckId, string Title, string Severity, string? MappingType);

    /// <summary>A catalogue check that carries zero framework mappings — the honest
    /// denominator surfaced in the page's "unmapped checks" panel.</summary>
    public sealed record UnmappedCheck(string CheckId, string Title, string Severity);

    /// <summary>
    /// One node in a framework's control hierarchy. <see cref="FullId"/> is cumulative
    /// (extends the parent) and, for a leaf, equals a verbatim corpus control_id.
    /// <see cref="Name"/> is the majority-vote control_name when this exact full-id is a
    /// mapped control_id, otherwise null (a bare structural node).
    /// </summary>
    public sealed class FrameworkTreeNode
    {
        public string Seg { get; init; } = string.Empty;
        public string FullId { get; init; } = string.Empty;
        public string? Name { get; set; }
        public int Depth { get; init; }
        public List<FrameworkTreeNode> Children { get; } = new();
        public List<MappedCheck> Checks { get; } = new();

        /// <summary>Distinct check ids under this node and all descendants (computed at build).</summary>
        public int CheckCount { get; set; }
    }

    /// <summary>One framework's forest: roots + shape stats.</summary>
    public sealed class FrameworkTree
    {
        public string Name { get; init; } = string.Empty;
        public int EntryCount { get; init; }
        public int ControlCount { get; init; }
        public int MaxDepth { get; init; }
        public IReadOnlyList<FrameworkTreeNode> Roots { get; init; } = new List<FrameworkTreeNode>();
    }

    /// <summary>
    /// The whole built structure: one tree per framework string discovered in the
    /// corpus mappings (sorted largest-first), plus the unmapped-checks denominator and
    /// top-line counts. Cached; carries NO verdict state (verdicts join per render).
    /// </summary>
    public sealed class FrameworkForest
    {
        public IReadOnlyList<FrameworkTree> Frameworks { get; init; } = new List<FrameworkTree>();
        public IReadOnlyList<UnmappedCheck> UnmappedChecks { get; init; } = new List<UnmappedCheck>();
        public int TotalChecks { get; init; }
        public int MappedCheckCount { get; init; }
        public int TotalEntries { get; init; }

        /// <summary>Framework-string + control_id pairs that did not fit their family's
        /// grammar and fell to a single flat node (a QA/honesty surface).</summary>
        public IReadOnlyList<string> FallbackControls { get; init; } = new List<string>();
    }
}
