/* In the name of God, the Merciful, the Compassionate */

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace SqlHealthAssessment.Data.Services;

// ── Data contracts ────────────────────────────────────────────────────────────

public class PlanGraph
{
    [JsonPropertyName("nodes")] public List<PlanNode> Nodes { get; set; } = [];
    [JsonPropertyName("edges")] public List<PlanEdge> Edges { get; set; } = [];
    [JsonPropertyName("query")] public string? Query { get; set; }
    [JsonPropertyName("recommendations")] public List<string> Recommendations { get; set; } = [];
    [JsonPropertyName("summary")] public PlanSummary? Summary { get; set; }
    /// <summary>1-based index within the batch (for multi-statement plans).</summary>
    [JsonPropertyName("statementNumber")] public int StatementNumber { get; set; } = 1;
    /// <summary>This statement's cost as a % of the total batch cost.</summary>
    [JsonPropertyName("batchPct")] public double BatchPct { get; set; } = 100.0;
}

public class PlanSummary
{
    [JsonPropertyName("ceVersion")] public string? CeVersion { get; set; }
    [JsonPropertyName("compileTimeMs")] public double? CompileTimeMs { get; set; }
    [JsonPropertyName("compileMemoryKB")] public double? CompileMemoryKB { get; set; }
    [JsonPropertyName("compileCpuMs")] public double? CompileCpuMs { get; set; }
    [JsonPropertyName("optimizationLevel")] public string? OptimizationLevel { get; set; }
    [JsonPropertyName("earlyAbortReason")] public string? EarlyAbortReason { get; set; }
    [JsonPropertyName("nonParallelReason")] public string? NonParallelReason { get; set; }
    [JsonPropertyName("queryHash")] public string? QueryHash { get; set; }
    [JsonPropertyName("planHash")] public string? PlanHash { get; set; }
    [JsonPropertyName("cachedPlanSizeKB")] public double? CachedPlanSizeKB { get; set; }
    [JsonPropertyName("dop")] public int? Dop { get; set; }
    [JsonPropertyName("memoryGrantKB")] public double? MemoryGrantKB { get; set; }
    [JsonPropertyName("parameterization")] public string? Parameterization { get; set; }
    [JsonPropertyName("setOptions")] public Dictionary<string, bool>? SetOptions { get; set; }
    [JsonPropertyName("parameters")] public List<PlanParameter>? Parameters { get; set; }
    [JsonPropertyName("statsUsed")] public List<PlanStatistic>? StatsUsed { get; set; }
    [JsonPropertyName("waitStats")] public List<PlanWaitStat>? WaitStats { get; set; }
    [JsonPropertyName("traceFlags")] public List<string>? TraceFlags { get; set; }
    [JsonPropertyName("warnings")] public List<string>? Warnings { get; set; }
    [JsonPropertyName("planCreationTime")] public string? PlanCreationTime { get; set; }
}

public class PlanParameter
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("dataType")] public string? DataType { get; set; }
    [JsonPropertyName("compiledValue")] public string? CompiledValue { get; set; }
    [JsonPropertyName("runtimeValue")] public string? RuntimeValue { get; set; }
}

public class PlanStatistic
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("table")] public string? Table { get; set; }
    [JsonPropertyName("modCount")] public long? ModCount { get; set; }
    [JsonPropertyName("samplingPct")] public double? SamplingPct { get; set; }
    [JsonPropertyName("lastUpdate")] public string? LastUpdate { get; set; }
}

public class PlanWaitStat
{
    [JsonPropertyName("waitType")] public string WaitType { get; set; } = "";
    [JsonPropertyName("waitMs")] public double WaitMs { get; set; }
    [JsonPropertyName("waitCount")] public long WaitCount { get; set; }
}

public class PlanNode
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("physicalType")] public string PhysicalType { get; set; } = "";
    [JsonPropertyName("cost")] public double Cost { get; set; }
    [JsonPropertyName("subTreeCost")] public double SubTreeCost { get; set; }
    [JsonPropertyName("relativeCost")] public double RelativeCost { get; set; }
    [JsonPropertyName("localCost")] public double LocalCost { get; set; }
    [JsonPropertyName("localCostPercentage")] public double LocalCostPercentage { get; set; }
    [JsonPropertyName("estimateRows")] public double EstimateRows { get; set; }
    [JsonPropertyName("actualRows")] public double? ActualRows { get; set; }
    [JsonPropertyName("estimateCPU")] public double EstimateCPU { get; set; }
    [JsonPropertyName("estimateIO")] public double EstimateIO { get; set; }
    [JsonPropertyName("avgRowSize")] public double AvgRowSize { get; set; }
    [JsonPropertyName("isParallel")] public bool IsParallel { get; set; }
    [JsonPropertyName("estimateExecutions")] public double EstimateExecutions { get; set; } = 1;
    [JsonPropertyName("objectDb")] public string? ObjectDb { get; set; }
    [JsonPropertyName("objectSchema")] public string? ObjectSchema { get; set; }
    [JsonPropertyName("predicate")] public string? Predicate { get; set; }
    [JsonPropertyName("badges")] public List<string> Badges { get; set; } = [];
    [JsonPropertyName("subtext")] public string[] Subtext { get; set; } = [];
    [JsonPropertyName("properties")] public Dictionary<string, string> Properties { get; set; } = [];
}

public class PlanEdge
{
    [JsonPropertyName("source")] public int Source { get; set; }
    [JsonPropertyName("target")] public int Target { get; set; }
    [JsonPropertyName("rowCount")] public double RowCount { get; set; }
    [JsonPropertyName("rowSize")] public double RowSize { get; set; }
}

// ── Parser ────────────────────────────────────────────────────────────────────

public static class ExecutionPlanParser
{
    private static string GenerateUniqueIndexName(string? table, List<string> keyCols)
    {
        if (string.IsNullOrEmpty(table)) table = "UnknownTable";
        var baseName = $"IX_{table}_{string.Join("_", keyCols.Take(3))}";
        return baseName.Length > 128 ? baseName.Substring(0, 128) : baseName;
    }

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
    /// Parse showplan XML and return a JSON array string — one graph per statement in the batch.
    /// Multi-statement batches produce multiple entries; single-statement plans produce a 1-element array.
    /// </summary>
    public static string ParseToJson(string xml)
    {
        // Handle UTF-16 encoded plans
        if (xml.Length > 3 && xml[0] == '\uFEFF')
        {
            xml = xml.TrimStart('\uFEFF');
        }
        var graphs = ParseAll(xml);
        return JsonSerializer.Serialize(graphs, JsonOpts);
    }

    public static PlanGraph Parse(string xml)
    {
        try
        {
            // Handle UTF-16 encoded plans
            if (xml.Length > 3 && xml[0] == '\uFEFF')
            {
                xml = xml.TrimStart('\uFEFF');
            }
            var graphs = ParseAll(xml);
            
            Serilog.Log.Debug("Parsed execution plan successfully: {NodeCount} nodes, {EdgeCount} edges", 
                graphs.FirstOrDefault()?.Nodes.Count ?? 0,
                graphs.FirstOrDefault()?.Edges.Count ?? 0);
            
            return graphs.FirstOrDefault() ?? new PlanGraph();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Execution plan parsing failed. XML length: {XmlLength} chars", xml?.Length ?? 0);
            
            // Log first 1000 chars of XML for diagnostics
            if (!string.IsNullOrWhiteSpace(xml))
            {
                var preview = xml.Length > 1000 ? xml.Substring(0, 1000) + "..." : xml;
                Serilog.Log.Debug("Plan XML preview: {PlanPreview}", preview);
            }
            
            return new PlanGraph();
        }
    }

    private static List<PlanGraph> ParseAll(string xml)
    {
        // Strip BOM and handle encoding mismatches
        xml = xml.TrimStart('\uFEFF', '\uFFFE', '\u0000');
        var doc = XDocument.Parse(xml);

        // VSCode-mssql / SSMS compatible selection - ignore namespaces completely
        // Works across ALL showplan schema versions 2005 -> 2022
        var stmts = doc.Descendants()
            .Where(e => 
                e.Name.LocalName.StartsWith("Stmt") &&
                e.Descendants().Any(c => c.Name.LocalName == "QueryPlan")
            )
            .ToList();

        // Handle cursor plans where QueryPlan is nested inside CursorPlan > Operation
        if (stmts.Count == 0)
        {
            stmts = doc.Descendants()
                .Where(e => 
                    e.Name.LocalName.StartsWith("Stmt") &&
                    e.Descendants().Any(c => c.Name.LocalName == "Operation")
                )
                .ToList();
        }

        // Final fallback: find any QueryPlan elements anywhere in the document
        if (stmts.Count == 0)
        {
            stmts = doc.Descendants()
                .Where(e => e.Name.LocalName == "QueryPlan")
                .Select(e => e.Parent)
                .ToList();
        }

        if (stmts.Count == 0) return [new PlanGraph()];

        // Total batch cost = sum of all statement costs (for per-statement % of batch)
        double batchTotal = stmts.Sum(s => ParseDouble(s.Attribute("StatementSubTreeCost")?.Value));
        if (batchTotal <= 0) batchTotal = 1.0;

        var result = new List<PlanGraph>();
        for (int i = 0; i < stmts.Count; i++)
            result.Add(ParseStatement(stmts[i], stmtNumber: i + 1, batchTotal));
        return result;
    }

    private static PlanGraph ParseStatement(XElement stmtSimple, int stmtNumber, double batchTotal)
    {
        var graph = new PlanGraph();
        graph.StatementNumber = stmtNumber;

        graph.Query = stmtSimple.Attribute("StatementText")?.Value;

        double rootCost = ParseDouble(stmtSimple.Attribute("StatementSubTreeCost")?.Value);
        graph.BatchPct = batchTotal > 0 ? rootCost / batchTotal * 100.0 : 100.0;
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

            var table = mi.Attribute("Table")?.Value?.Trim('[', ']');
            var schema = mi.Attribute("Schema")?.Value?.Trim('[', ']');
            var db = mi.Attribute("Database")?.Value?.Trim('[', ']');

            // Extract columns by usage type
            var equality = new List<string>();
            var inequality = new List<string>();
            var include = new List<string>();
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
                    case "EQUALITY": equality.AddRange(cols!); break;
                    case "INEQUALITY": inequality.AddRange(cols!); break;
                    case "INCLUDE": include.AddRange(cols!); break;
                }
            }

            // Prioritize scalar/equality columns first as per best practices
            var keyCols = equality.Concat(inequality).ToList();
            // Ensure scalars (from predicates) are prioritized if not already
            // For now, equality takes precedence as they are more selective
            var qualifiedTable = schema != null ? $"[{schema}].[{table}]" : $"[{table}]";

            // Build CREATE INDEX DDL with production best practices
            var indexName = GenerateUniqueIndexName(table, keyCols);
            var databasePrefix = !string.IsNullOrEmpty(db) ? $"[{db}]." : "";
            var enhancedDdl = $"CREATE NONCLUSTERED INDEX [{indexName}]";
            enhancedDdl += $"\n    ON {databasePrefix}{qualifiedTable} ({string.Join(", ", keyCols.Select(c => $"[{c}]"))})";
            if (include.Count > 0)
                enhancedDdl += $"\n    INCLUDE ({string.Join(", ", include.Select(c => $"[{c}]"))})";
            
            // Add production options
            enhancedDdl += $"\n    WITH (";
            enhancedDdl += $"\n        ONLINE = {{ONLINE}},";
            enhancedDdl += $"\n        SORT_IN_TEMPDB = {{SORT_IN_TEMPDB}},";
            enhancedDdl += $"\n        MAXDOP = {{MAXDOP}},";
            enhancedDdl += $"\n        DATA_COMPRESSION = {{COMPRESSION}}";
            enhancedDdl += $"\n        {{RESUMABLE}}";
            enhancedDdl += $"\n        {{SEQUENTIAL_KEY}}";
            enhancedDdl += $"\n    );";
            enhancedDdl += $"\n    -- Estimated query improvement: {impact}%";
            enhancedDdl += $"\n    -- Automatically generated from execution plan recommendation";
            graph.Recommendations.Add(enhancedDdl);
            enhancedDdl += $"\n    );";
            enhancedDdl += $"\n    -- Estimated query improvement: {impact}%";
            enhancedDdl += $"\n    -- Automatically generated from execution plan recommendation";

            var summary = $"Missing index on {db}.{qualifiedTable} (Impact: {impact}%)";
            graph.Recommendations.Add(summary);
            graph.Recommendations.Add(enhancedDdl);
            graph.Recommendations.Add($"\n💡 Best Practice: Run during off-peak hours. Use ONLINE=OFF if running on SQL Server Standard Edition.");
        }

        // ── Plan Summary (statement-level metadata) ────────────────────────────
        graph.Summary = ExtractSummary(stmtSimple, queryPlan);

        // Synthetic SELECT root node — SSMS always renders the statement wrapper as the
        // leftmost box (Cost: 0 %). NodeId = -1 so it never collides with XML NodeIds (≥ 0).
        const int selectNodeId = -1;
        var selectNode = new PlanNode
        {
            Id = selectNodeId,
            Type = "Select",
            PhysicalType = "Select",
            SubTreeCost = rootCost,
            RelativeCost = 100.0,
        };
        // Surface plan-level metadata on the root node so the detail pane can show it
        if (graph.Summary?.CachedPlanSizeKB > 0)
            selectNode.Properties["Cached Plan Size"] = $"{graph.Summary.CachedPlanSizeKB:N0} KB";
        if (graph.Summary?.MemoryGrantKB > 0)
            selectNode.Properties["Memory Grant (KB)"] = $"{graph.Summary.MemoryGrantKB:N0}";
        if (graph.Summary?.Dop > 0)
            selectNode.Properties["Degree of Parallelism"] = graph.Summary.Dop.ToString();
        if (!string.IsNullOrEmpty(graph.Summary?.OptimizationLevel))
            selectNode.Properties["Optimization Level"] = graph.Summary.OptimizationLevel;
        graph.Nodes.Add(selectNode);

        // Parse operator tree from first RelOp under QueryPlan, wired as child of SELECT.
        var rootRelOp = queryPlan.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "RelOp");
        if (rootRelOp != null)
            ParseRelOp(rootRelOp, parentId: selectNodeId, graph, rootCost);

        // Post-order traversal to calculate local node costs (vscode-mssql algorithm)
        // Process children first, then parent - bottom up
        foreach (var node in graph.Nodes.OrderByDescending(n => n.Id))
        {
            double childCostSum = 0.0;
            foreach (var edge in graph.Edges.Where(e => e.Source == node.Id))
            {
                var childNode = graph.Nodes.FirstOrDefault(n => n.Id == edge.Target);
                if (childNode != null)
                {
                    childCostSum += childNode.SubTreeCost;
                }
            }
            // Local cost = this node's subtree cost minus sum of all children subtree costs
            node.LocalCost = Math.Max(node.SubTreeCost - childCostSum, 0.0);
            node.LocalCostPercentage = rootCost > 0 ? node.LocalCost / rootCost * 100.0 : 0.0;
        }

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
            Id = nodeId,
            Type = relOp.Attribute("LogicalOp")?.Value
                        ?? relOp.Attribute("PhysicalOp")?.Value
                        ?? "Unknown",
            PhysicalType = relOp.Attribute("PhysicalOp")?.Value ?? "",
            EstimateRows = ParseDouble(relOp.Attribute("EstimateRows")?.Value),
            EstimateCPU = ParseDouble(relOp.Attribute("EstimateCPU")?.Value),
            EstimateIO = ParseDouble(relOp.Attribute("EstimateIO")?.Value),
            AvgRowSize = ParseDouble(relOp.Attribute("AvgRowSize")?.Value),
            SubTreeCost = subTreeCost,
            RelativeCost = rootCost > 0 ? subTreeCost / rootCost * 100.0 : 0,
            IsParallel = relOp.Attribute("Parallel")?.Value is "1" or "true",
            EstimateExecutions = ParseDouble(relOp.Attribute("EstimateExecutions")?.Value) is > 0 and var ee ? ee : 1.0,
        };

        node.Cost = node.EstimateCPU + node.EstimateIO;

        // Badges
        bool hasWarnings = relOp.Descendants()
            .Any(e => e.Name.LocalName == "Warnings");
        if (hasWarnings) node.Badges.Add("Warning");
        if (node.IsParallel) node.Badges.Add("Parallelism");
        if (node.RelativeCost > 50) node.Badges.Add("High Cost");

        // Actual rows from runtime counters
        var rtCounter = relOp.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "RunTimeCountersPerThread");
        if (rtCounter != null)
        {
            double ar = ParseDouble(rtCounter.Attribute("ActualRows")?.Value);
            if (ar > 0) node.ActualRows = ar;

            // Best practice: Flag large discrepancies between estimated and actual rows
            if (node.ActualRows.HasValue && node.EstimateRows > 0)
            {
                double ratio = node.ActualRows.Value / node.EstimateRows;
                if (ratio > 10 || ratio < 0.1) node.Badges.Add("Row Estimate Mismatch");
            }
        }

        // ── Additional RelOp attributes ─────────────────────────────────────────
        var rebinds = ParseDouble(relOp.Attribute("EstimateRebinds")?.Value);
        var rewinds = ParseDouble(relOp.Attribute("EstimateRewinds")?.Value);
        var rowsRead = ParseDouble(relOp.Attribute("EstimatedRowsRead")?.Value);
        var tableCard = ParseDouble(relOp.Attribute("TableCardinality")?.Value);
        var ordered = relOp.Attribute("Ordered")?.Value;

        if (rebinds > 0) node.Properties["Estimated Rebinds"] = rebinds.ToString("N0");
        if (rewinds > 0) node.Properties["Estimated Rewinds"] = rewinds.ToString("N0");
        if (rowsRead > 0) node.Properties["Est. Rows Read"] = rowsRead.ToString("N0");
        if (tableCard > 0) node.Properties["Table Cardinality"] = tableCard.ToString("N0");
        if (ordered != null) node.Properties["Ordered"] = ordered;

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
                totalActualExec += ParseDouble(rt.Attribute("ActualExecutions")?.Value);
                totalElapsedMs += ParseDouble(rt.Attribute("ActualElapsedms")?.Value);
                totalCpuMs += ParseDouble(rt.Attribute("ActualCPUms")?.Value);
                totalActualRowsRead += ParseDouble(rt.Attribute("ActualRowsRead")?.Value);
                totalScans += ParseDouble(rt.Attribute("ActualScans")?.Value);
                totalLogicalReads += ParseDouble(rt.Attribute("ActualLogicalReads")?.Value);
                totalPhysicalReads += ParseDouble(rt.Attribute("ActualPhysicalReads")?.Value);
                totalReadAheadReads += ParseDouble(rt.Attribute("ActualReadAheadReads")?.Value);
                threadCount++;
            }
            if (totalActualExec > 0) node.Properties["Actual Executions"] = totalActualExec.ToString("N0");
            if (totalElapsedMs > 0) node.Properties["Actual Elapsed (ms)"] = totalElapsedMs.ToString("N1");
            if (totalCpuMs > 0) node.Properties["Actual CPU (ms)"] = totalCpuMs.ToString("N1");
            if (totalActualRowsRead > 0) node.Properties["Actual Rows Read"] = totalActualRowsRead.ToString("N0");
            if (totalScans > 0) node.Properties["Actual Scans"] = totalScans.ToString("N0");
            if (totalLogicalReads > 0) node.Properties["Actual Logical Reads"] = totalLogicalReads.ToString("N0");
            if (totalPhysicalReads > 0) node.Properties["Actual Physical Reads"] = totalPhysicalReads.ToString("N0");
            if (totalReadAheadReads > 0) node.Properties["Actual Read-Ahead"] = totalReadAheadReads.ToString("N0");
            if (threadCount > 1) node.Properties["Thread Count"] = threadCount.ToString();
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

            // #14 Non-SARGable predicate — surface the column and expression involved
            foreach (var nsp in warningsEl.Descendants()
                .Where(e => e.Name.LocalName == "NonSARGableReason"))
            {
                var colRef = nsp.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "ColumnReference");
                var scalarOp = nsp.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "ScalarOperator");
                var colName = colRef?.Attribute("Column")?.Value;
                var expr    = scalarOp?.Attribute("ScalarString")?.Value ?? nsp.Attribute("Reason")?.Value;
                if (colName != null && expr != null)
                    warningParts.Add($"Non-SARGable: [{colName}] — {expr}");
                else if (colName != null)
                    warningParts.Add($"Non-SARGable predicate on [{colName}]");
                else
                    warningParts.Add("Non-SARGable predicate — consider rewriting to allow index seeks");
            }
            if (warningsEl.Descendants().Any(e => e.Name.LocalName == "UnmatchedIndexes"))
                warningParts.Add("Unmatched Indexes");
            foreach (var conv in warningsEl.Descendants()
                .Where(e => e.Name.LocalName == "PlanAffectingConvert"))
            {
                // #4 Column-level implicit conversion detail
                var expr       = conv.Attribute("Expression")?.Value;
                var convertIssue = conv.Attribute("ConvertIssue")?.Value; // "Seek Plan" or "Cardinality Estimate"
                var fromType   = conv.Attribute("FromType")?.Value;
                var toType     = conv.Attribute("ToType")?.Value;

                var convMsg = "Implicit Convert";
                if (expr != null)          convMsg += $": {expr}";
                if (fromType != null && toType != null) convMsg += $" ({fromType} → {toType})";
                if (convertIssue != null)  convMsg += $" — affects {convertIssue}";
                warningParts.Add(convMsg);
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
                    var col = c.Attribute("Column")?.Value;
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
            var opOrdered = opEl.Attribute("Ordered")?.Value;
            var opLookup = opEl.Attribute("Lookup")?.Value;
            var forcedIndex = opEl.Attribute("ForcedIndex")?.Value;
            var forceScan = opEl.Attribute("ForceScan")?.Value;
            var noExpand = opEl.Attribute("NoExpandHint")?.Value;
            var storage = opEl.Attribute("Storage")?.Value;
            var scanDir = opEl.Attribute("ScanDirection")?.Value;

            if (opOrdered is "true" or "1") node.Properties["Scan Ordered"] = "True";
            if (opLookup is "true" or "1") node.Properties["Lookup"] = "True";
            if (forcedIndex is "true" or "1") node.Properties["Forced Index"] = "True";
            if (forceScan is "true" or "1") node.Properties["Force Scan"] = "True";
            if (noExpand is "true" or "1") node.Properties["NOEXPAND Hint"] = "True";
            if (storage != null) node.Properties["Storage"] = storage;
            if (scanDir != null) node.Properties["Scan Direction"] = scanDir;

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
                var table = objEl.Attribute("Table")?.Value?.Trim('[', ']');
                var index = objEl.Attribute("Index")?.Value?.Trim('[', ']');
                if (table != null) items.Add(table);
                if (index != null) items.Add(index);
                // Add concise cost info like SSMS
                items.Add($"Cost: {node.RelativeCost:F1}%");
                if (node.EstimateRows > 0) items.Add($"Rows: {node.EstimateRows:N0}");
                if (node.EstimateIO > 0) items.Add($"I/O: {node.EstimateIO:F2}");
                node.Subtext = [.. items];
                node.ObjectDb = objEl.Attribute("Database")?.Value?.Trim('[', ']');
                node.ObjectSchema = objEl.Attribute("Schema")?.Value?.Trim('[', ']');
                if (index != null) node.Properties["Index"] = index;
                if (table != null) node.Properties["Table"] = table;
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
            ExtractColumnList(opEl, "HashKeysProbe", "Hash Keys (Probe)", node);
            ExtractColumnList(opEl, "HashKeysBuild", "Hash Keys (Build)", node);
            ExtractColumnList(opEl, "ProbeColumn", "Probe Column", node);
            ExtractColumnList(opEl, "BuildColumn", "Build Column", node);
            ExtractColumnList(opEl, "OrderBy", "Order By", node);
            ExtractColumnList(opEl, "GroupBy", "Group By", node);
            ExtractColumnList(opEl, "PartitionColumns", "Partition Columns", node);

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
            var serial = memGrant.Attribute("SerialRequiredMemory")?.Value;
            var desired = memGrant.Attribute("SerialDesiredMemory")?.Value;
            var granted = memGrant.Attribute("GrantedMemory")?.Value;
            var maxUsed = memGrant.Attribute("MaxUsedMemory")?.Value;
            if (serial != null) node.Properties["Required Memory (KB)"] = serial;
            if (desired != null) node.Properties["Desired Memory (KB)"] = desired;
            if (granted != null) node.Properties["Granted Memory (KB)"] = granted;
            if (maxUsed != null) node.Properties["Max Used Memory (KB)"] = maxUsed;

            // #8 Memory Grant Feedback (SQL Server 2019+) — flag if feedback adjusted the grant
            var mgfAdjusted = memGrant.Attribute("IsMemoryGrantFeedbackAdjusted")?.Value;
            if (mgfAdjusted is "NoFeedback" or "YesAdjusting" or "YesStable")
            {
                node.Badges.Add("MemGrantFeedback");
                node.Properties["Memory Grant Feedback"] = mgfAdjusted switch
                {
                    "YesStable"    => "Feedback applied — grant is stable",
                    "YesAdjusting" => "Feedback adjusting — grant still converging",
                    "NoFeedback"   => "No feedback yet (first execution)",
                    _              => mgfAdjusted
                };
            }
        }

        graph.Nodes.Add(node);

        if (parentId != int.MinValue)   // int.MinValue = "no parent" sentinel; -1 is valid (SELECT root)
            graph.Edges.Add(new PlanEdge
            {
                Source = parentId,
                Target = nodeId,
                RowCount = node.EstimateRows,
                RowSize = node.AvgRowSize
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
            var col = c.Attribute("Column")?.Value;
            var table = c.Attribute("Table")?.Value?.Trim('[', ']');
            var asc = c.Parent?.Attribute("Ascending")?.Value;
            var name = table != null ? $"{table}.{col}" : col;
            if (asc != null) name += asc is "true" or "1" ? " ASC" : " DESC";
            return name;
        })
        .Where(c => c != null)
        .ToList();

        if (cols.Count > 0)
            node.Properties[label] = string.Join(", ", cols);
    }

    private static PlanSummary ExtractSummary(XElement stmt, XElement queryPlan)
    {
        var summary = new PlanSummary();

        // ── Statement-level attributes ──────────────────────────────────────────
        summary.CeVersion = stmt.Attribute("CardinalityEstimationModelVersion")?.Value;
        summary.OptimizationLevel = stmt.Attribute("StatementOptmLevel")?.Value;
        summary.EarlyAbortReason = stmt.Attribute("StatementOptmEarlyAbortReason")?.Value;
        summary.QueryHash = stmt.Attribute("QueryHash")?.Value;
        summary.PlanHash = stmt.Attribute("QueryPlanHash")?.Value;
        summary.Parameterization = stmt.Attribute("StatementParameterizationType")?.Value;

        var compileTimeAttr = stmt.Attribute("CompileTime")?.Value;
        if (compileTimeAttr != null) summary.CompileTimeMs = ParseDouble(compileTimeAttr);

        var compileMemAttr = stmt.Attribute("CompileMemory")?.Value;
        if (compileMemAttr != null) summary.CompileMemoryKB = ParseDouble(compileMemAttr);

        var compileCpuAttr = stmt.Attribute("CompileCPU")?.Value;
        if (compileCpuAttr != null) summary.CompileCpuMs = ParseDouble(compileCpuAttr);

        // ── QueryPlan-level attributes ──────────────────────────────────────────
        var dopAttr = queryPlan.Attribute("DegreeOfParallelism")?.Value;
        if (dopAttr != null && int.TryParse(dopAttr, out var dop)) summary.Dop = dop;

        var nonParallelAttr = queryPlan.Attribute("NonParallelPlanReason")?.Value;
        if (nonParallelAttr != null) summary.NonParallelReason = nonParallelAttr;

        var cachedPlanAttr = queryPlan.Attribute("CachedPlanSize")?.Value;
        if (cachedPlanAttr != null) summary.CachedPlanSizeKB = ParseDouble(cachedPlanAttr);

        // #13 Plan creation time — present in actual plans from DMVs / Query Store
        var creationTime = queryPlan.Attribute("CachedPlanAge")?.Value
                        ?? stmt.Attribute("CreationTime")?.Value;
        if (creationTime != null) summary.PlanCreationTime = creationTime;

        // Memory grant at statement level
        var memGrantEl = queryPlan.Elements().FirstOrDefault(e => e.Name.LocalName == "MemoryGrantInfo");
        if (memGrantEl != null)
        {
            var grantedKB = memGrantEl.Attribute("GrantedMemory")?.Value
                         ?? memGrantEl.Attribute("SerialDesiredMemory")?.Value;
            if (grantedKB != null) summary.MemoryGrantKB = ParseDouble(grantedKB);
        }

        // ── Parameters (compiled vs runtime values) ─────────────────────────────
        var paramList = queryPlan.Descendants()
            .Where(e => e.Name.LocalName == "ColumnReference"
                     && e.Parent?.Name.LocalName == "ParameterList")
            .ToList();
        if (paramList.Count > 0)
        {
            summary.Parameters = paramList.Select(p => new PlanParameter
            {
                Name = p.Attribute("Column")?.Value ?? "",
                DataType = p.Attribute("ParameterDataType")?.Value,
                CompiledValue = p.Attribute("ParameterCompiledValue")?.Value,
                RuntimeValue = p.Attribute("ParameterRuntimeValue")?.Value
            }).ToList();
        }

        // ── Statistics used ─────────────────────────────────────────────────────
        var statsUsed = queryPlan.Descendants()
            .Where(e => e.Name.LocalName == "StatisticsInfo")
            .ToList();
        if (statsUsed.Count > 0)
        {
            summary.StatsUsed = statsUsed.Select(s =>
            {
                var stat = new PlanStatistic
                {
                    Name = s.Attribute("Statistics")?.Value ?? "",
                    Table = s.Attribute("Table")?.Value?.Trim('[', ']'),
                    LastUpdate = s.Attribute("LastUpdate")?.Value
                };
                var modCount = s.Attribute("ModificationCount")?.Value;
                if (modCount != null && long.TryParse(modCount, out var mc)) stat.ModCount = mc;
                var sampling = s.Attribute("SamplingPercent")?.Value;
                if (sampling != null) stat.SamplingPct = ParseDouble(sampling);
                return stat;
            }).ToList();
        }

        // ── Wait stats ──────────────────────────────────────────────────────────
        var waitStats = queryPlan.Descendants()
            .Where(e => e.Name.LocalName == "WaitStats")
            .SelectMany(ws => ws.Elements().Where(e => e.Name.LocalName == "Wait"))
            .ToList();
        if (waitStats.Count > 0)
        {
            summary.WaitStats = waitStats.Select(w => new PlanWaitStat
            {
                WaitType = w.Attribute("WaitType")?.Value ?? "",
                WaitMs = ParseDouble(w.Attribute("WaitTimeMs")?.Value),
                WaitCount = long.TryParse(w.Attribute("WaitCount")?.Value, out var wc) ? wc : 0
            }).ToList();
        }

        // ── Trace flags ─────────────────────────────────────────────────────────
        var traceFlags = queryPlan.Descendants()
            .Where(e => e.Name.LocalName == "TraceFlag")
            .Select(tf =>
            {
                var val = tf.Attribute("Value")?.Value;
                var scope = tf.Attribute("Scope")?.Value;
                return scope != null ? $"TF {val} ({scope})" : $"TF {val}";
            })
            .Distinct()
            .ToList();
        if (traceFlags.Count > 0) summary.TraceFlags = traceFlags;

        // ── SET options ─────────────────────────────────────────────────────────
        var setOpts = queryPlan.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "OptimizerStatsUsage")
            ?.Parent; // Navigate up to find StatementSetOptions
        var setOptsEl = stmt.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "StatementSetOptions");
        if (setOptsEl != null)
        {
            var opts = new Dictionary<string, bool>();
            foreach (var attr in setOptsEl.Attributes())
            {
                opts[attr.Name.LocalName] = attr.Value is "true" or "1";
            }
            if (opts.Count > 0) summary.SetOptions = opts;
        }

        // ── Statement-level warnings ────────────────────────────────────────────
        var warnings = new List<string>();

        if (summary.EarlyAbortReason != null)
            warnings.Add($"Optimization aborted: {summary.EarlyAbortReason}");

        if (summary.NonParallelReason != null)
            warnings.Add($"Serial plan: {summary.NonParallelReason}");

        if (summary.Parameters != null)
        {
            foreach (var p in summary.Parameters)
            {
                if (p.CompiledValue != null && p.RuntimeValue != null
                    && p.CompiledValue != p.RuntimeValue)
                {
                    warnings.Add($"Parameter sniffing: {p.Name} compiled={p.CompiledValue}, runtime={p.RuntimeValue}");
                }
            }
        }

        if (summary.StatsUsed != null)
        {
            foreach (var s in summary.StatsUsed)
            {
                if (s.ModCount > 1000)
                    warnings.Add($"Stale stats: {s.Table}.{s.Name} — {s.ModCount:N0} modifications since last update");
            }
        }

        if (summary.SetOptions != null)
        {
            if (summary.SetOptions.TryGetValue("ARITHABORT", out var aa) && !aa)
                warnings.Add("ARITHABORT is OFF — can cause plan cache issues");
            if (summary.SetOptions.TryGetValue("ANSI_NULLS", out var an) && !an)
                warnings.Add("ANSI_NULLS is OFF — may cause unexpected NULL comparison behavior");
        }

        if (warnings.Count > 0) summary.Warnings = warnings;

        return summary;
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
