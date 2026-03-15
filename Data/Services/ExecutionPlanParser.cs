/* In the name of God, the Merciful, the Compassionate */

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace SqlHealthAssessment.Data.Services;

// ── Data contracts ────────────────────────────────────────────────────────────

public class PlanGraph
{
    [JsonPropertyName("nodes")]          public List<PlanNode> Nodes           { get; set; } = [];
    [JsonPropertyName("edges")]          public List<PlanEdge> Edges           { get; set; } = [];
    [JsonPropertyName("query")]          public string?        Query            { get; set; }
    [JsonPropertyName("recommendations")] public List<string>  Recommendations  { get; set; } = [];
}

public class PlanNode
{
    [JsonPropertyName("id")]            public int      Id           { get; set; }
    [JsonPropertyName("type")]          public string   Type         { get; set; } = "";
    [JsonPropertyName("physicalType")]  public string   PhysicalType { get; set; } = "";
    [JsonPropertyName("cost")]          public double   Cost         { get; set; }
    [JsonPropertyName("subTreeCost")]   public double   SubTreeCost  { get; set; }
    [JsonPropertyName("relativeCost")]  public double   RelativeCost { get; set; }
    [JsonPropertyName("estimateRows")]  public double   EstimateRows { get; set; }
    [JsonPropertyName("actualRows")]    public double?  ActualRows   { get; set; }
    [JsonPropertyName("estimateCPU")]   public double   EstimateCPU  { get; set; }
    [JsonPropertyName("estimateIO")]    public double   EstimateIO   { get; set; }
    [JsonPropertyName("avgRowSize")]    public double   AvgRowSize   { get; set; }
    [JsonPropertyName("isParallel")]         public bool     IsParallel         { get; set; }
    [JsonPropertyName("estimateExecutions")] public double   EstimateExecutions  { get; set; } = 1;
    [JsonPropertyName("objectDb")]           public string?  ObjectDb            { get; set; }
    [JsonPropertyName("objectSchema")]       public string?  ObjectSchema        { get; set; }
    [JsonPropertyName("predicate")]          public string?  Predicate           { get; set; }
    [JsonPropertyName("badges")]             public List<string> Badges          { get; set; } = [];
    [JsonPropertyName("subtext")]            public string[] Subtext             { get; set; } = [];
    [JsonPropertyName("properties")]         public Dictionary<string, string> Properties { get; set; } = [];
}

public class PlanEdge
{
    [JsonPropertyName("source")]    public int    Source   { get; set; }
    [JsonPropertyName("target")]    public int    Target   { get; set; }
    [JsonPropertyName("rowCount")]  public double RowCount { get; set; }
    [JsonPropertyName("rowSize")]   public double RowSize  { get; set; }
}

// ── Parser ────────────────────────────────────────────────────────────────────

public static class ExecutionPlanParser
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly HashSet<string> MetaElementNames =
    [
        "OutputList", "RunTimeInformation", "Warnings",
        "MemoryFractions", "MemoryGrant", "InternalInfo",
        "RunTimePartitionSummary", "SeekPredicateNew", "SeekPredicates",
        "ParameterList"
    ];

    /// <summary>
    /// Parse showplan XML and return a JSON string representing the plan graph.
    /// </summary>
    public static string ParseToJson(string xml)
    {
        var graph = Parse(xml);
        return JsonSerializer.Serialize(graph, JsonOpts);
    }

    private static PlanGraph Parse(string xml)
    {
        var doc   = XDocument.Parse(xml);
        var graph = new PlanGraph();

        // Find the first statement element that has a QueryPlan child.
        // Using StartsWith("Stmt") handles all variants (StmtSimple, StmtCond, StmtCursor, etc.)
        // and correctly skips SET/USE statements that appear before the real query in a batch.
        var stmtSimple = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName.StartsWith("Stmt") &&
                                 e.Elements().Any(c => c.Name.LocalName == "QueryPlan"));
        if (stmtSimple == null) return graph;

        graph.Query = stmtSimple.Attribute("StatementText")?.Value;

        double rootCost = ParseDouble(stmtSimple.Attribute("StatementSubTreeCost")?.Value);
        if (rootCost <= 0) rootCost = 1.0;

        // .First is safe here — we already verified the child exists above
        var queryPlan = stmtSimple.Elements()
            .First(e => e.Name.LocalName == "QueryPlan");

        // Missing index recommendations
        foreach (var mig in queryPlan.Descendants()
                                     .Where(e => e.Name.LocalName == "MissingIndexGroup"))
        {
            var impact = mig.Attribute("Impact")?.Value;
            var mi = mig.Elements().FirstOrDefault(e => e.Name.LocalName == "MissingIndex");
            if (mi == null) continue;

            var table  = mi.Attribute("Table")?.Value?.Trim('[', ']');
            var schema = mi.Attribute("Schema")?.Value?.Trim('[', ']');
            var db     = mi.Attribute("Database")?.Value?.Trim('[', ']');

            // Extract columns by usage type
            var equality   = new List<string>();
            var inequality = new List<string>();
            var include    = new List<string>();
            foreach (var cg in mi.Elements().Where(e => e.Name.LocalName == "ColumnGroup"))
            {
                var usage = cg.Attribute("Usage")?.Value;
                var cols = cg.Elements()
                    .Where(e => e.Name.LocalName == "Column")
                    .Select(c => c.Attribute("Name")?.Value?.Trim('[', ']'))
                    .Where(c => c != null)
                    .ToList();
                switch (usage)
                {
                    case "EQUALITY":   equality.AddRange(cols!);   break;
                    case "INEQUALITY": inequality.AddRange(cols!); break;
                    case "INCLUDE":    include.AddRange(cols!);    break;
                }
            }

            var keyCols = equality.Concat(inequality).ToList();
            var qualifiedTable = schema != null ? $"[{schema}].[{table}]" : $"[{table}]";

            // Build CREATE INDEX DDL
            var ddl = $"CREATE NONCLUSTERED INDEX [IX_{table}_{string.Join("_", keyCols)}]";
            ddl += $"\n    ON {qualifiedTable} ({string.Join(", ", keyCols.Select(c => $"[{c}]"))})";
            if (include.Count > 0)
                ddl += $"\n    INCLUDE ({string.Join(", ", include.Select(c => $"[{c}]"))})";

            var summary = $"Missing index on {db}.{qualifiedTable} (Impact: {impact}%)";
            var full = $"{summary}\n{ddl}";
            graph.Recommendations.Add(full);
        }

        // Synthetic SELECT root node — SSMS always renders the statement wrapper as the
        // leftmost box (Cost: 0 %). NodeId = -1 so it never collides with XML NodeIds (≥ 0).
        const int selectNodeId = -1;
        graph.Nodes.Add(new PlanNode
        {
            Id           = selectNodeId,
            Type         = "Select",
            PhysicalType = "Select",
            SubTreeCost  = rootCost,
            RelativeCost = 100.0,
        });

        // Parse operator tree from first RelOp under QueryPlan, wired as child of SELECT.
        var rootRelOp = queryPlan.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "RelOp");
        if (rootRelOp != null)
            ParseRelOp(rootRelOp, parentId: selectNodeId, graph, rootCost);

        return graph;
    }

    private static void ParseRelOp(XElement relOp, int parentId, PlanGraph graph, double rootCost)
    {
        int nodeId = int.TryParse(relOp.Attribute("NodeId")?.Value, out var nid)
            ? nid
            : graph.Nodes.Count;

        double subTreeCost = ParseDouble(relOp.Attribute("EstimatedTotalSubtreeCost")?.Value);

        var node = new PlanNode
        {
            Id           = nodeId,
            Type         = relOp.Attribute("LogicalOp")?.Value
                        ?? relOp.Attribute("PhysicalOp")?.Value
                        ?? "Unknown",
            PhysicalType = relOp.Attribute("PhysicalOp")?.Value ?? "",
            EstimateRows = ParseDouble(relOp.Attribute("EstimateRows")?.Value),
            EstimateCPU  = ParseDouble(relOp.Attribute("EstimateCPU")?.Value),
            EstimateIO   = ParseDouble(relOp.Attribute("EstimateIO")?.Value),
            AvgRowSize   = ParseDouble(relOp.Attribute("AvgRowSize")?.Value),
            SubTreeCost        = subTreeCost,
            RelativeCost       = rootCost > 0 ? subTreeCost / rootCost * 100.0 : 0,
            IsParallel         = relOp.Attribute("Parallel")?.Value is "1" or "true",
            EstimateExecutions = ParseDouble(relOp.Attribute("EstimateExecutions")?.Value) is > 0 and var ee ? ee : 1.0,
        };

        node.Cost = node.EstimateCPU + node.EstimateIO;

        // Badges
        bool hasWarnings = relOp.Descendants()
            .Any(e => e.Name.LocalName == "Warnings");
        if (hasWarnings)   node.Badges.Add("Warning");
        if (node.IsParallel) node.Badges.Add("Parallelism");

        // Actual rows from runtime counters
        var rtCounter = relOp.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "RunTimeCountersPerThread");
        if (rtCounter != null)
        {
            double ar = ParseDouble(rtCounter.Attribute("ActualRows")?.Value);
            if (ar > 0) node.ActualRows = ar;
        }

        // ── Additional RelOp attributes ─────────────────────────────────────────
        var rebinds  = ParseDouble(relOp.Attribute("EstimateRebinds")?.Value);
        var rewinds  = ParseDouble(relOp.Attribute("EstimateRewinds")?.Value);
        var rowsRead = ParseDouble(relOp.Attribute("EstimatedRowsRead")?.Value);
        var tableCard = ParseDouble(relOp.Attribute("TableCardinality")?.Value);
        var ordered   = relOp.Attribute("Ordered")?.Value;

        if (rebinds > 0)   node.Properties["Estimated Rebinds"]  = rebinds.ToString("N0");
        if (rewinds > 0)   node.Properties["Estimated Rewinds"]  = rewinds.ToString("N0");
        if (rowsRead > 0)  node.Properties["Est. Rows Read"]     = rowsRead.ToString("N0");
        if (tableCard > 0) node.Properties["Table Cardinality"]  = tableCard.ToString("N0");
        if (ordered != null) node.Properties["Ordered"]           = ordered;

        // ── Runtime counters (actual execution plan) ─────────────────────────────
        var rtCounters = relOp.Descendants()
            .Where(e => e.Name.LocalName == "RunTimeCountersPerThread")
            .ToList();
        if (rtCounters.Count > 0)
        {
            double totalActualExec = 0, totalElapsedMs = 0, totalCpuMs = 0;
            double totalActualRowsRead = 0, totalScans = 0, totalLogicalReads = 0;
            double totalPhysicalReads = 0, totalReadAheadReads = 0;
            int threadCount = 0;
            foreach (var rt in rtCounters)
            {
                totalActualExec      += ParseDouble(rt.Attribute("ActualExecutions")?.Value);
                totalElapsedMs       += ParseDouble(rt.Attribute("ActualElapsedms")?.Value);
                totalCpuMs           += ParseDouble(rt.Attribute("ActualCPUms")?.Value);
                totalActualRowsRead  += ParseDouble(rt.Attribute("ActualRowsRead")?.Value);
                totalScans           += ParseDouble(rt.Attribute("ActualScans")?.Value);
                totalLogicalReads    += ParseDouble(rt.Attribute("ActualLogicalReads")?.Value);
                totalPhysicalReads   += ParseDouble(rt.Attribute("ActualPhysicalReads")?.Value);
                totalReadAheadReads  += ParseDouble(rt.Attribute("ActualReadAheadReads")?.Value);
                threadCount++;
            }
            if (totalActualExec > 0)     node.Properties["Actual Executions"]    = totalActualExec.ToString("N0");
            if (totalElapsedMs > 0)      node.Properties["Actual Elapsed (ms)"]  = totalElapsedMs.ToString("N1");
            if (totalCpuMs > 0)          node.Properties["Actual CPU (ms)"]      = totalCpuMs.ToString("N1");
            if (totalActualRowsRead > 0) node.Properties["Actual Rows Read"]     = totalActualRowsRead.ToString("N0");
            if (totalScans > 0)          node.Properties["Actual Scans"]         = totalScans.ToString("N0");
            if (totalLogicalReads > 0)   node.Properties["Actual Logical Reads"] = totalLogicalReads.ToString("N0");
            if (totalPhysicalReads > 0)  node.Properties["Actual Physical Reads"]= totalPhysicalReads.ToString("N0");
            if (totalReadAheadReads > 0) node.Properties["Actual Read-Ahead"]    = totalReadAheadReads.ToString("N0");
            if (threadCount > 1)         node.Properties["Thread Count"]         = threadCount.ToString();
        }

        // ── Warnings detail ──────────────────────────────────────────────────────
        var warningsEl = relOp.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "Warnings");
        if (warningsEl != null)
        {
            var warningParts = new List<string>();
            if (warningsEl.Attribute("NoJoinPredicate")?.Value is "true" or "1")
                warningParts.Add("No Join Predicate");
            foreach (var spill in warningsEl.Descendants()
                .Where(e => e.Name.LocalName == "SpillToTempDb"))
            {
                var spillLevel = spill.Attribute("SpillLevel")?.Value;
                warningParts.Add(spillLevel != null ? $"Spill (Level {spillLevel})" : "Spill to TempDb");
            }
            foreach (var wait in warningsEl.Descendants()
                .Where(e => e.Name.LocalName == "Wait"))
            {
                var waitType = wait.Attribute("WaitType")?.Value;
                if (waitType != null) warningParts.Add($"Wait: {waitType}");
            }
            if (warningsEl.Descendants().Any(e => e.Name.LocalName == "ColumnsWithNoStatistics"))
                warningParts.Add("Missing Column Statistics");
            if (warningsEl.Descendants().Any(e => e.Name.LocalName == "UnmatchedIndexes"))
                warningParts.Add("Unmatched Indexes");
            foreach (var conv in warningsEl.Descendants()
                .Where(e => e.Name.LocalName == "PlanAffectingConvert"))
            {
                var expr = conv.Attribute("Expression")?.Value;
                warningParts.Add(expr != null ? $"Implicit Convert: {expr}" : "Implicit Convert");
            }
            if (warningsEl.Descendants().Any(e => e.Name.LocalName == "SortSpillDetails"))
                warningParts.Add("Sort Spill");
            if (warningsEl.Descendants().Any(e => e.Name.LocalName == "HashSpillDetails"))
                warningParts.Add("Hash Spill");
            var memoryGrant = warningsEl.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "MemoryGrantWarning");
            if (memoryGrant != null)
            {
                var grantKind = memoryGrant.Attribute("GrantWarningKind")?.Value;
                warningParts.Add(grantKind != null ? $"Memory Grant: {grantKind}" : "Memory Grant Warning");
            }
            if (warningParts.Count > 0)
                node.Properties["Warning Details"] = string.Join("; ", warningParts);
        }

        // ── OutputList ───────────────────────────────────────────────────────────
        var outputList = relOp.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "OutputList");
        if (outputList != null)
        {
            var cols = outputList.Elements()
                .Where(e => e.Name.LocalName == "ColumnReference")
                .Select(c =>
                {
                    var col   = c.Attribute("Column")?.Value;
                    var table = c.Attribute("Table")?.Value?.Trim('[', ']');
                    return table != null ? $"{table}.{col}" : col;
                })
                .Where(c => c != null)
                .ToList();
            if (cols.Count > 0)
                node.Properties["Output"] = string.Join(", ", cols);
        }

        // Object subtext (table / index name) and full path from operator element
        var opEl = GetOperatorElement(relOp);
        if (opEl != null)
        {
            // ── Operator-specific attributes ─────────────────────────────────────
            var opOrdered    = opEl.Attribute("Ordered")?.Value;
            var opLookup     = opEl.Attribute("Lookup")?.Value;
            var forcedIndex  = opEl.Attribute("ForcedIndex")?.Value;
            var forceScan    = opEl.Attribute("ForceScan")?.Value;
            var noExpand     = opEl.Attribute("NoExpandHint")?.Value;
            var storage      = opEl.Attribute("Storage")?.Value;
            var scanDir      = opEl.Attribute("ScanDirection")?.Value;

            if (opOrdered is "true" or "1")  node.Properties["Scan Ordered"] = "True";
            if (opLookup is "true" or "1")   node.Properties["Lookup"]       = "True";
            if (forcedIndex is "true" or "1") node.Properties["Forced Index"] = "True";
            if (forceScan is "true" or "1")   node.Properties["Force Scan"]   = "True";
            if (noExpand is "true" or "1")    node.Properties["NOEXPAND Hint"]= "True";
            if (storage != null)              node.Properties["Storage"]      = storage;
            if (scanDir != null)              node.Properties["Scan Direction"]= scanDir;

            // Only search within this operator's own XML — stop at nested RelOp boundaries
            // so Compute Scalar doesn't inherit the Object/Predicate from a child TVF or scan.
            var objEl = opEl.Descendants()
                .Where(e => e.Name.LocalName == "Object")
                .FirstOrDefault(e => !e.Ancestors()
                    .TakeWhile(a => a != opEl)
                    .Any(a => a.Name.LocalName == "RelOp"));
            if (objEl != null)
            {
                var items = new List<string>(2);
                var table  = objEl.Attribute("Table")?.Value?.Trim('[', ']');
                var index  = objEl.Attribute("Index")?.Value?.Trim('[', ']');
                if (table != null) items.Add(table);
                if (index != null) items.Add(index);
                node.Subtext      = [.. items];
                node.ObjectDb     = objEl.Attribute("Database")?.Value?.Trim('[', ']');
                node.ObjectSchema = objEl.Attribute("Schema")?.Value?.Trim('[', ']');
                if (index != null) node.Properties["Index"]  = index;
                if (table != null) node.Properties["Table"]  = table;
            }

            // ── Predicate ────────────────────────────────────────────────────────
            var predEl = opEl.Descendants()
                .Where(e => e.Name.LocalName == "Predicate")
                .FirstOrDefault(e => !e.Ancestors()
                    .TakeWhile(a => a != opEl)
                    .Any(a => a.Name.LocalName == "RelOp"));
            if (predEl != null)
            {
                var scalar = predEl.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "ScalarOperator");
                node.Predicate = scalar?.Attribute("ScalarString")?.Value;
            }

            // ── Seek Predicates (separate from Predicate) ────────────────────────
            var seekEl = opEl.Descendants()
                .Where(e => e.Name.LocalName is "SeekPredicateNew" or "SeekPredicates")
                .FirstOrDefault(e => !e.Ancestors()
                    .TakeWhile(a => a != opEl)
                    .Any(a => a.Name.LocalName == "RelOp"));
            if (seekEl != null)
            {
                var seekScalars = seekEl.Descendants()
                    .Where(e => e.Name.LocalName == "ScalarOperator")
                    .Select(s => s.Attribute("ScalarString")?.Value)
                    .Where(v => v != null)
                    .ToList();
                if (seekScalars.Count > 0)
                    node.Properties["Seek Predicate"] = string.Join(" AND ", seekScalars);
            }

            // ── DefinedValues (computed columns) ─────────────────────────────────
            var defVals = opEl.Descendants()
                .Where(e => e.Name.LocalName == "DefinedValue")
                .Where(e => !e.Ancestors()
                    .TakeWhile(a => a != opEl)
                    .Any(a => a.Name.LocalName == "RelOp"))
                .ToList();
            if (defVals.Count > 0)
            {
                var defs = new List<string>();
                foreach (var dv in defVals)
                {
                    var colRef = dv.Elements()
                        .FirstOrDefault(e => e.Name.LocalName == "ColumnReference");
                    var scalarOp = dv.Descendants()
                        .FirstOrDefault(e => e.Name.LocalName == "ScalarOperator");
                    var colName = colRef?.Attribute("Column")?.Value;
                    var expr = scalarOp?.Attribute("ScalarString")?.Value;
                    if (colName != null && expr != null)
                        defs.Add($"{colName} = {expr}");
                    else if (colName != null)
                        defs.Add(colName);
                }
                if (defs.Count > 0)
                    node.Properties["Defined Values"] = string.Join("; ", defs);
            }

            // ── Hash Keys / Order By / Group By ──────────────────────────────────
            ExtractColumnList(opEl, "HashKeysProbe",  "Hash Keys (Probe)",  node);
            ExtractColumnList(opEl, "HashKeysBuild",  "Hash Keys (Build)",  node);
            ExtractColumnList(opEl, "ProbeColumn",    "Probe Column",       node);
            ExtractColumnList(opEl, "BuildColumn",    "Build Column",       node);
            ExtractColumnList(opEl, "OrderBy",        "Order By",           node);
            ExtractColumnList(opEl, "GroupBy",        "Group By",           node);
            ExtractColumnList(opEl, "PartitionColumns","Partition Columns", node);

            // ── Nested Loops outer references ────────────────────────────────────
            var outerRefs = opEl.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "OuterReferences");
            if (outerRefs != null)
            {
                var refs = outerRefs.Elements()
                    .Where(e => e.Name.LocalName == "ColumnReference")
                    .Select(c => c.Attribute("Column")?.Value)
                    .Where(v => v != null)
                    .ToList();
                if (refs.Count > 0)
                    node.Properties["Outer References"] = string.Join(", ", refs);
            }
        }

        // ── Memory Grant ─────────────────────────────────────────────────────────
        var memGrant = relOp.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "MemoryGrant");
        if (memGrant != null)
        {
            var serial  = memGrant.Attribute("SerialRequiredMemory")?.Value;
            var desired = memGrant.Attribute("SerialDesiredMemory")?.Value;
            var granted = memGrant.Attribute("GrantedMemory")?.Value;
            var maxUsed = memGrant.Attribute("MaxUsedMemory")?.Value;
            if (serial  != null) node.Properties["Required Memory (KB)"] = serial;
            if (desired != null) node.Properties["Desired Memory (KB)"]  = desired;
            if (granted != null) node.Properties["Granted Memory (KB)"]  = granted;
            if (maxUsed != null) node.Properties["Max Used Memory (KB)"] = maxUsed;
        }

        graph.Nodes.Add(node);

        if (parentId != int.MinValue)   // int.MinValue = "no parent" sentinel; -1 is valid (SELECT root)
            graph.Edges.Add(new PlanEdge
            {
                Source   = parentId,
                Target   = nodeId,
                RowCount = node.EstimateRows,
                RowSize  = node.AvgRowSize
            });

        // Recurse into direct RelOp children
        foreach (var child in GetDirectRelOpChildren(relOp))
            ParseRelOp(child, nodeId, graph, rootCost);
    }

    /// <summary>
    /// Returns the first child of relOp that is not a metadata element
    /// (OutputList, Warnings, RunTimeInformation, etc.).
    /// This is the operator-specific element (e.g. NestedLoops, IndexScan).
    /// </summary>
    private static XElement? GetOperatorElement(XElement relOp) =>
        relOp.Elements()
             .FirstOrDefault(e => !MetaElementNames.Contains(e.Name.LocalName));

    /// <summary>
    /// Returns RelOp elements whose nearest RelOp ancestor is <paramref name="relOp"/>.
    /// This correctly identifies direct children regardless of nesting depth inside
    /// operator-specific wrapper elements (e.g. NestedLoops, HashMatch).
    /// </summary>
    private static IEnumerable<XElement> GetDirectRelOpChildren(XElement relOp) =>
        relOp.Descendants()
             .Where(e => e.Name.LocalName == "RelOp")
             .Where(r => r.Ancestors()
                          .FirstOrDefault(a => a.Name.LocalName == "RelOp") == relOp);

    private static void ExtractColumnList(XElement opEl, string elementName, string label, PlanNode node)
    {
        var el = opEl.Elements()
            .FirstOrDefault(e => e.Name.LocalName == elementName);
        if (el == null) return;

        // Some elements (OrderBy) wrap columns in OrderByColumn elements
        var colRefs = el.Descendants()
            .Where(e => e.Name.LocalName == "ColumnReference")
            .ToList();
        if (colRefs.Count == 0) return;

        var cols = colRefs.Select(c =>
        {
            var col   = c.Attribute("Column")?.Value;
            var table = c.Attribute("Table")?.Value?.Trim('[', ']');
            var asc   = c.Parent?.Attribute("Ascending")?.Value;
            var name  = table != null ? $"{table}.{col}" : col;
            if (asc != null) name += asc is "true" or "1" ? " ASC" : " DESC";
            return name;
        })
        .Where(c => c != null)
        .ToList();

        if (cols.Count > 0)
            node.Properties[label] = string.Join(", ", cols);
    }

    private static double ParseDouble(string? s)
    {
        if (s == null) return 0;
        return double.TryParse(s,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var v) ? v : 0;
    }
}
