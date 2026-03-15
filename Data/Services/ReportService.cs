/* In the name of God, the Merciful, the Compassionate */

using Microsoft.Extensions.Logging;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Elements.Table;
using QuestPDF.Infrastructure;
using SqlHealthAssessment.Data.Models;
using System.Globalization;
using System.IO;

namespace SqlHealthAssessment.Data.Services;

/// <summary>
/// Generates Executive Summary PDF reports from Vulnerability Assessment CSV data using QuestPDF.
/// </summary>
public class ReportService
{
    private readonly ILogger<ReportService> _logger;

    public ReportService(ILogger<ReportService> logger)
    {
        _logger = logger;
        QuestPDF.Settings.License = LicenseType.Community;
    }

    /// <summary>
    /// Returns all CSV files in the output directory matching the given pattern.
    /// </summary>
    public List<string> GetAvailableCsvFiles(string pattern = "VulnerabilityAssessment_*.csv")
    {
        var outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output");
        if (!Directory.Exists(outputDir))
            return new List<string>();

        return Directory.GetFiles(outputDir, pattern)
            .OrderByDescending(File.GetLastWriteTime)
            .ToList();
    }

    /// <summary>
    /// Parses a Vulnerability Assessment CSV file into typed records.
    /// </summary>
    public List<AssessmentRow> ParseCsv(string filePath)
    {
        var rows = new List<AssessmentRow>();
        if (!File.Exists(filePath)) return rows;

        using var reader = new StreamReader(filePath);
        var headerLine = reader.ReadLine();
        if (headerLine == null) return rows;

        while (!reader.EndOfStream)
        {
            var line = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(line)) continue;

            var fields = ParseCsvLine(line);
            if (fields.Count < 12) continue;

            rows.Add(new AssessmentRow
            {
                CheckId     = fields[0],
                DisplayName = fields[1],
                Message     = fields[2],
                Severity    = fields[3],
                RawSeverity = fields[4],
                TargetName  = fields[5],
                TargetType  = fields[6],
                Category    = fields[7],
                Description = fields[8],
                Remediation = fields[9],
                HelpLink    = fields[10],
                Status      = fields[11]
            });
        }

        _logger.LogInformation("Parsed {Count} rows from {File}", rows.Count, filePath);
        return rows;
    }

    /// <summary>
    /// Generates the Executive Summary PDF and saves to the output directory.
    /// Returns the full file path on success.
    /// </summary>
    public string GenerateReport(List<AssessmentRow> data, ReportConfig config)
    {
        var filtered = ApplyFilters(data, config);
        var serverName = ExtractServerName(data);
        var outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output");
        Directory.CreateDirectory(outputDir);
        var fileName = $"ExecutiveSummary_{serverName}_{DateTime.Now:yyyyMMdd_HHmmss}.pdf";
        var filePath = Path.Combine(outputDir, fileName);

        var document = Document.Create(container =>
        {
            // --- Cover Page ---
            if (config.ShowCoverPage)
            {
                container.Page(page =>
                {
                    ConfigurePage(page, config);
                    page.Content().Column(col =>
                    {
                        col.Item().PaddingTop(80).AlignCenter().Text(config.CompanyName)
                            .FontSize(28).Bold().FontColor(config.PrimaryColor);

                        col.Item().PaddingTop(20).AlignCenter().Text(config.ReportTitle)
                            .FontSize(22).FontColor(config.AccentColor);

                        col.Item().PaddingTop(8).AlignCenter().Text(config.ReportSubtitle)
                            .FontSize(16).FontColor(Colors.Grey.Medium);

                        col.Item().PaddingTop(40).AlignCenter()
                            .LineHorizontal(2).LineColor(config.AccentColor);

                        col.Item().PaddingTop(30).AlignCenter().Text($"Server: {serverName}")
                            .FontSize(14).FontColor(Colors.Grey.Darken1);

                        col.Item().PaddingTop(10).AlignCenter().Text($"Date: {DateTime.Now:dd MMMM yyyy}")
                            .FontSize(12).FontColor(Colors.Grey.Darken1);

                        col.Item().PaddingTop(10).AlignCenter().Text($"Prepared by: {config.PreparedBy}")
                            .FontSize(12).FontColor(Colors.Grey.Darken1);

                        col.Item().PaddingTop(10).AlignCenter().Text($"Total Checks: {data.Count}  |  Findings: {data.Count(r => r.Status == "Failed")}")
                            .FontSize(11).FontColor(Colors.Grey.Darken1);
                    });
                });
            }

            // --- Executive Summary Page ---
            if (config.ShowExecutiveSummary)
            {
                container.Page(page =>
                {
                    ConfigurePage(page, config);
                    page.Header().Element(h => RenderHeader(h, config, serverName));
                    page.Footer().Element(RenderFooter);

                    page.Content().PaddingTop(10).Column(col =>
                    {
                        col.Item().Text("Executive Summary").FontSize(18).Bold().FontColor(config.PrimaryColor);
                        col.Item().PaddingTop(6).Text($"This report summarises the findings of a Microsoft SQL Vulnerability Assessment performed against {serverName} on {DateTime.Now:dd MMMM yyyy}. " +
                            $"A total of {data.Count} checks were evaluated, of which {data.Count(r => r.Status == "Failed")} reported findings requiring attention.")
                            .FontSize(10).LineHeight(1.4f);

                        // Severity summary boxes
                        if (config.ShowSeverityBreakdown)
                        {
                            col.Item().PaddingTop(16).Text("Severity Breakdown").FontSize(14).Bold().FontColor(config.PrimaryColor);
                            col.Item().PaddingTop(8).Row(row =>
                            {
                                var critical = data.Count(r => r.Severity == "Error" && r.Status == "Failed");
                                var warnings = data.Count(r => r.Severity == "Warning" && r.Status == "Failed");
                                var info     = data.Count(r => r.Severity == "Information" && r.Status == "Failed");
                                var passed   = data.Count(r => r.Status == "Passed");

                                SeverityBox(row.RelativeItem(), "CRITICAL", critical, config.CriticalColor);
                                row.ConstantItem(8);
                                SeverityBox(row.RelativeItem(), "WARNING", warnings, config.WarningColor);
                                row.ConstantItem(8);
                                SeverityBox(row.RelativeItem(), "INFO", info, config.InfoColor);
                                row.ConstantItem(8);
                                SeverityBox(row.RelativeItem(), "PASSED", passed, config.PassColor);
                            });
                        }

                        // Category breakdown table
                        if (config.ShowCategoryBreakdown)
                        {
                            var categories = data
                                .Where(r => r.Status == "Failed")
                                .SelectMany(r => r.Category.Split(", ", StringSplitOptions.RemoveEmptyEntries)
                                    .Select(t => t.Trim())
                                    .Where(t => t.Length > 0 && t != "DefaultRuleset"))
                                .GroupBy(t => t)
                                .Select(g => (Name: g.Key, Count: g.Count()))
                                .OrderByDescending(c => c.Count)
                                .Take(15)
                                .ToList();

                            if (categories.Any())
                            {
                                col.Item().PaddingTop(16).Text("Findings by Category").FontSize(14).Bold().FontColor(config.PrimaryColor);
                                col.Item().PaddingTop(8).Table(table =>
                                {
                                    table.ColumnsDefinition(cols =>
                                    {
                                        cols.RelativeColumn(3);
                                        cols.RelativeColumn(1);
                                        cols.RelativeColumn(6);
                                    });

                                    // Header
                                    table.Header(header =>
                                    {
                                        TableHeaderCell(header.Cell(), "Category");
                                        TableHeaderCell(header.Cell(), "Count");
                                        TableHeaderCell(header.Cell(), "Bar");
                                    });

                                    var maxCount = categories.Max(c => c.Count);
                                    foreach (var cat in categories)
                                    {
                                        var barPct = maxCount > 0 ? (float)cat.Count / maxCount : 0;
                                        TableCell(table.Cell(), cat.Name);
                                        TableCell(table.Cell(), cat.Count.ToString());
                                        table.Cell().PaddingVertical(3).PaddingHorizontal(4)
                                            .Column(c =>
                                            {
                                                c.Item().Height(14)
                                                    .Width(barPct * 200)
                                                    .Background(config.AccentColor);
                                            });
                                    }
                                });
                            }
                        }
                    });
                });
            }

            // --- Detailed Findings Pages ---
            if (config.ShowDetailedFindings && filtered.Any(r => r.Status == "Failed"))
            {
                var failedRows = filtered.Where(r => r.Status == "Failed").ToList();

                // Group by severity for ordered output
                var severityOrder = new[] { "Error", "Warning", "Information" };
                foreach (var severity in severityOrder)
                {
                    var group = failedRows.Where(r => r.Severity == severity).ToList();
                    if (!group.Any()) continue;

                    container.Page(page =>
                    {
                        ConfigurePage(page, config);
                        page.Header().Element(h => RenderHeader(h, config, serverName));
                        page.Footer().Element(RenderFooter);

                        page.Content().PaddingTop(10).Column(col =>
                        {
                            var label = severity == "Error" ? "Critical" : severity;
                            var color = severity == "Error" ? config.CriticalColor
                                      : severity == "Warning" ? config.WarningColor
                                      : config.InfoColor;

                            col.Item().Text($"{label} Findings ({group.Count})")
                                .FontSize(16).Bold().FontColor(color);

                            col.Item().PaddingTop(8).Table(table =>
                            {
                                table.ColumnsDefinition(cols =>
                                {
                                    cols.ConstantColumn(70);  // CheckId
                                    cols.RelativeColumn(2);   // DisplayName
                                    cols.RelativeColumn(3);   // Message
                                    cols.RelativeColumn(1);   // Target Type
                                    cols.RelativeColumn(1.5f);// Category
                                });

                                table.Header(header =>
                                {
                                    TableHeaderCell(header.Cell(), "Check ID");
                                    TableHeaderCell(header.Cell(), "Finding");
                                    TableHeaderCell(header.Cell(), "Details");
                                    TableHeaderCell(header.Cell(), "Scope");
                                    TableHeaderCell(header.Cell(), "Category");
                                });

                                foreach (var row in group)
                                {
                                    var cats = row.Category
                                        .Split(", ", StringSplitOptions.RemoveEmptyEntries)
                                        .Where(t => t.Trim() != "DefaultRuleset")
                                        .Select(t => t.Trim());

                                    TableCell(table.Cell(), row.CheckId, 8);
                                    TableCell(table.Cell(), row.DisplayName, 8);
                                    TableCell(table.Cell(), Truncate(row.Message, 120), 8);
                                    TableCell(table.Cell(), row.TargetType, 8);
                                    TableCell(table.Cell(), string.Join(", ", cats), 8);
                                }
                            });
                        });
                    });
                }
            }

            // --- Passed Checks Page (optional) ---
            if (config.ShowPassedChecks)
            {
                var passed = filtered.Where(r => r.Status == "Passed").ToList();
                if (passed.Any())
                {
                    container.Page(page =>
                    {
                        ConfigurePage(page, config);
                        page.Header().Element(h => RenderHeader(h, config, serverName));
                        page.Footer().Element(RenderFooter);

                        page.Content().PaddingTop(10).Column(col =>
                        {
                            col.Item().Text($"Passed Checks ({passed.Count})")
                                .FontSize(16).Bold().FontColor(config.PassColor);

                            col.Item().PaddingTop(8).Table(table =>
                            {
                                table.ColumnsDefinition(cols =>
                                {
                                    cols.ConstantColumn(70);
                                    cols.RelativeColumn(2);
                                    cols.RelativeColumn(3);
                                    cols.RelativeColumn(1);
                                });

                                table.Header(header =>
                                {
                                    TableHeaderCell(header.Cell(), "Check ID");
                                    TableHeaderCell(header.Cell(), "Check Name");
                                    TableHeaderCell(header.Cell(), "Details");
                                    TableHeaderCell(header.Cell(), "Scope");
                                });

                                foreach (var row in passed)
                                {
                                    TableCell(table.Cell(), row.CheckId, 8);
                                    TableCell(table.Cell(), row.DisplayName, 8);
                                    TableCell(table.Cell(), Truncate(row.Message, 140), 8);
                                    TableCell(table.Cell(), row.TargetType, 8);
                                }
                            });
                        });
                    });
                }
            }

            // --- Recommendations Page ---
            if (config.ShowRecommendations)
            {
                var remediations = filtered
                    .Where(r => r.Status == "Failed" && !string.IsNullOrWhiteSpace(r.Remediation))
                    .GroupBy(r => r.CheckId)
                    .Select(g => g.First())
                    .OrderBy(r => r.Severity == "Error" ? 0 : r.Severity == "Warning" ? 1 : 2)
                    .Take(20)
                    .ToList();

                if (remediations.Any())
                {
                    container.Page(page =>
                    {
                        ConfigurePage(page, config);
                        page.Header().Element(h => RenderHeader(h, config, serverName));
                        page.Footer().Element(RenderFooter);

                        page.Content().PaddingTop(10).Column(col =>
                        {
                            col.Item().Text("Recommendations").FontSize(16).Bold().FontColor(config.PrimaryColor);
                            col.Item().PaddingTop(4).Text("The following remediation actions are recommended based on the assessment findings.")
                                .FontSize(9).FontColor(Colors.Grey.Darken1).LineHeight(1.3f);

                            foreach (var rec in remediations)
                            {
                                col.Item().PaddingTop(10).BorderBottom(1).BorderColor(Colors.Grey.Lighten2).Column(inner =>
                                {
                                    var severityColor = rec.Severity == "Error" ? config.CriticalColor
                                                      : rec.Severity == "Warning" ? config.WarningColor
                                                      : config.InfoColor;

                                    inner.Item().Row(r =>
                                    {
                                        r.AutoItem().MinWidth(50).Background(severityColor)
                                            .Padding(3).AlignCenter()
                                            .Text(rec.Severity == "Error" ? "HIGH" : rec.Severity == "Warning" ? "MED" : "LOW")
                                            .FontSize(7).Bold().FontColor(Colors.White);
                                        r.ConstantItem(8);
                                        r.RelativeItem().Text($"{rec.CheckId}: {rec.DisplayName}")
                                            .FontSize(10).Bold();
                                    });

                                    inner.Item().PaddingTop(3).Text(rec.Remediation)
                                        .FontSize(9).LineHeight(1.3f);
                                });
                            }
                        });
                    });
                }
            }
        });

        document.GeneratePdf(filePath);
        _logger.LogInformation("Executive Summary report generated: {Path}", filePath);
        return filePath;
    }

    // --- Private helpers ---

    private static void ConfigurePage(PageDescriptor page, ReportConfig config)
    {
        if (config.LandscapeOrientation)
            page.Size(PageSizes.A4.Landscape());
        else
            page.Size(PageSizes.A4);

        page.Margin(config.MarginMm, Unit.Millimetre);
        page.DefaultTextStyle(x => x.FontSize(10).FontFamily("Segoe UI", "Arial", "Helvetica"));
    }

    private static void RenderHeader(IContainer container, ReportConfig config, string serverName)
    {
        container.BorderBottom(1).BorderColor(config.AccentColor).PaddingBottom(5).Row(row =>
        {
            row.RelativeItem().Text($"{config.CompanyName} — {config.ReportSubtitle}")
                .FontSize(9).FontColor(config.AccentColor);
            row.RelativeItem().AlignRight().Text(serverName)
                .FontSize(9).FontColor(Colors.Grey.Darken1);
        });
    }

    private static void RenderFooter(IContainer container)
    {
        container.BorderTop(1).BorderColor(Colors.Grey.Lighten2).PaddingTop(5).Row(row =>
        {
            row.RelativeItem().Text($"Generated {DateTime.Now:dd MMM yyyy HH:mm}")
                .FontSize(8).FontColor(Colors.Grey.Medium);
            row.RelativeItem().AlignRight().Text(text =>
            {
                text.Span("Page ").FontSize(8).FontColor(Colors.Grey.Medium);
                text.CurrentPageNumber().FontSize(8).FontColor(Colors.Grey.Medium);
                text.Span(" of ").FontSize(8).FontColor(Colors.Grey.Medium);
                text.TotalPages().FontSize(8).FontColor(Colors.Grey.Medium);
            });
        });
    }

    private static void SeverityBox(IContainer container, string label, int count, string color)
    {
        container.Border(1).BorderColor(color).Padding(10).Column(col =>
        {
            col.Item().AlignCenter().Text(count.ToString()).FontSize(28).Bold().FontColor(color);
            col.Item().AlignCenter().Text(label).FontSize(9).Bold().FontColor(color);
        });
    }

    private static void TableHeaderCell(ITableCellContainer cell, string text)
    {
        cell.Background("#e8eaf6").Padding(4)
            .Text(text).FontSize(8).Bold().FontColor("#1a237e");
    }

    private static void TableCell(ITableCellContainer cell, string text, float fontSize = 9)
    {
        cell.BorderBottom(1).BorderColor(Colors.Grey.Lighten3)
            .PaddingVertical(3).PaddingHorizontal(4)
            .Text(text ?? "").FontSize(fontSize).FontColor(Colors.Black);
    }

    private static string Truncate(string text, int maxLen)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length <= maxLen ? text : text[..(maxLen - 3)] + "...";
    }

    private static string ExtractServerName(List<AssessmentRow> data)
    {
        var first = data.FirstOrDefault();
        if (first == null) return "Unknown";

        // TargetName format: Server[@Name='MSI'] or Server[@Name='MSI']/Database[@Name='X']
        var target = first.TargetName;
        var start = target.IndexOf("'", StringComparison.Ordinal);
        var end = target.IndexOf("'", start + 1, StringComparison.Ordinal);
        if (start >= 0 && end > start)
            return target[(start + 1)..end];

        return "Unknown";
    }

    private static List<AssessmentRow> ApplyFilters(List<AssessmentRow> data, ReportConfig config)
    {
        var filtered = data.AsEnumerable();

        if (config.SeverityFilter != "All")
            filtered = filtered.Where(r => r.Severity.Equals(config.SeverityFilter, StringComparison.OrdinalIgnoreCase));

        if (config.StatusFilter != "All")
            filtered = filtered.Where(r => r.Status.Equals(config.StatusFilter, StringComparison.OrdinalIgnoreCase));

        if (config.CategoryFilter != "All")
            filtered = filtered.Where(r =>
                r.Category.Split(", ", StringSplitOptions.RemoveEmptyEntries)
                    .Any(t => t.Trim().Equals(config.CategoryFilter, StringComparison.OrdinalIgnoreCase)));

        return filtered.ToList();
    }

    /// <summary>
    /// RFC 4180 compliant CSV line parser — handles quoted fields with embedded commas and escaped quotes.
    /// </summary>
    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var i = 0;

        while (i < line.Length)
        {
            if (line[i] == '"')
            {
                // Quoted field
                i++; // skip opening quote
                var sb = new System.Text.StringBuilder();
                while (i < line.Length)
                {
                    if (line[i] == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            sb.Append('"');
                            i += 2;
                        }
                        else
                        {
                            i++; // skip closing quote
                            break;
                        }
                    }
                    else
                    {
                        sb.Append(line[i]);
                        i++;
                    }
                }
                fields.Add(sb.ToString());
                if (i < line.Length && line[i] == ',') i++; // skip comma
            }
            else
            {
                // Unquoted field
                var end = line.IndexOf(',', i);
                if (end < 0) end = line.Length;
                fields.Add(line[i..end]);
                i = end + 1;
            }
        }

        return fields;
    }
}

/// <summary>
/// Represents a single row from a Vulnerability Assessment CSV export.
/// </summary>
public class AssessmentRow
{
    public string CheckId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Message { get; set; } = "";
    public string Severity { get; set; } = "";
    public string RawSeverity { get; set; } = "";
    public string TargetName { get; set; } = "";
    public string TargetType { get; set; } = "";
    public string Category { get; set; } = "";
    public string Description { get; set; } = "";
    public string Remediation { get; set; } = "";
    public string HelpLink { get; set; } = "";
    public string Status { get; set; } = "";
}
