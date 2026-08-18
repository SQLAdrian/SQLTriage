/* In the name of God, the Merciful, the Compassionate */
/*
 * AssessmentPdf — shared QuestPDF kit for the assessment "vectors" (corpus audit, Microsoft
 * assessment, …). One deterministic, margin-foot­ered, colour-blind-aware findings report so every
 * vector exports with a consistent look (matching the Diagnostics Maturity Roadmap).
 *
 * BuildFindingsReport renders: a title band, summary stat chips, and a findings table (status ·
 * severity · check · category · server · detail), findings-first then passes. Colour-blind mode
 * swaps pass/fail/severity to a Wong-derived palette; the brand accent (#e2583e) is decorative.
 */

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SkiaSharp;

namespace SQLTriage.Data.Services;

public sealed class AssessmentMeta
{
    public string Title = "";
    public string Company = "";       // optional org branding on the report
    public string Subtitle = "";      // scope line, e.g. "3 servers · localhost, .\\NEW2022"
    public string Engine = "";        // e.g. "Corpus audit checks" / "Microsoft SQL Assessment API"
    public string GeneratedUtc = "";  // 2026-06-09T12:56Z
    public string TimezoneId = "";
    public string RunId = "";
    public bool   ColorBlind;
    public bool   Watermark;
    public string WatermarkText = "DRAFT — non-production data";
    public string FooterMeta = "";    // centre footer line
}

public enum FindingState { Pass, Fail, Error, Skipped, Info }

public sealed class FindingRow
{
    public FindingState State;
    public string Name = "";
    public string Category = "";
    public string Severity = "";
    public string Server = "";
    public string Detail = "";
    public string BusinessImpact = "";
    public string ConsultingLink = "";
    /// <summary>Plain-English remediation/recommendation text. Used by the Executive Briefing;
    /// falls back to <see cref="Detail"/> when empty. The detailed findings table ignores it.</summary>
    public string Recommendation = "";
}

public sealed class StatChip
{
    public string Label = "";
    public string Value = "";
    public string Color = "#888888";
}

public sealed class FindingsReport
{
    public AssessmentMeta Meta = new();
    public List<StatChip> Stats = new();
    public List<FindingRow> Findings = new();
}

public static class AssessmentPdf
{
    public const string Brand = "#e2583e";
    public const string Ink   = "#333333";
    public const string Muted = "#888888";
    public const string Faint = "#aaaaaa";
    public const string Line  = "#e5e5e5";

    public static string Pass(bool cb) => cb ? "#009E73" : "#16a34a";
    public static string Fail(bool cb) => cb ? "#D55E00" : "#dc2626";
    public static string Warn(bool cb) => cb ? "#E69F00" : "#f59e0b";
    public const  string Info = "#3b82f6";

    public static string Sev(string? sev, bool cb) => (sev ?? "").Trim().ToLowerInvariant() switch
    {
        "critical" or "high"                          => Fail(cb),
        "warning" or "medium" or "moderate"           => Warn(cb),
        "low" or "info" or "information" or "informational" => Info,
        "pass" or "passed" or "ok"                    => Pass(cb),
        _                                              => Muted,
    };

    private static (string glyph, string color) StateGlyph(FindingState s, bool cb) => s switch
    {
        FindingState.Pass    => ("✓", Pass(cb)),
        FindingState.Fail    => ("✗", Fail(cb)),
        FindingState.Error   => ("⛒", Fail(cb)),
        FindingState.Skipped => ("⤼", Muted),
        _                    => ("ⓘ", Info),
    };

    // Order: errors, failures (by severity weight), info, skipped, then passes.
    private static int StateRank(FindingState s) => s switch
    {
        FindingState.Error => 0, FindingState.Fail => 1, FindingState.Info => 2,
        FindingState.Skipped => 3, _ => 4,
    };
    private static int SevRank(string? sev) => (sev ?? "").Trim().ToLowerInvariant() switch
    {
        "critical" => 0, "high" => 1, "warning" => 2, "medium" or "moderate" => 3,
        "low" => 4, "info" or "information" or "informational" => 5, _ => 6,
    };

    public static byte[] BuildFindingsReport(FindingsReport r)
    {
        var cb = r.Meta.ColorBlind;
        var ordered = r.Findings
            .OrderBy(f => StateRank(f.State))
            .ThenBy(f => SevRank(f.Severity))
            .ThenBy(f => f.Server, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(26);
                page.DefaultTextStyle(s => s.FontSize(8.5f).FontFamily("Arial").FontColor(Ink));
                if (r.Meta.Watermark) page.Foreground().Element(c => Watermark(c, r.Meta));

                page.Footer().Element(c => StandardFooter(c, r.Meta.FooterMeta));

                // Donut summary (pass% with a priority breakdown) — the executive hero.
                var passed = ordered.Count(f => f.State == FindingState.Pass);
                var highN  = ordered.Count(f => f.State != FindingState.Pass && SevRank(f.Severity) <= 1);
                var lowN   = ordered.Count(f => f.State != FindingState.Pass) - highN;
                var totalN = ordered.Count;
                var pct    = totalN > 0 ? 100.0 * passed / totalN : 0;
                var segs = new List<DonutSegment>
                {
                    new() { Value = passed, Color = Pass(cb) },
                    new() { Value = lowN,   Color = Info },
                    new() { Value = highN,  Color = Fail(cb) },
                };
                var legend = new List<(string, string)>
                {
                    ($"{highN} High Priority", Fail(cb)),
                    ($"{lowN} Low Priority",   Info),
                    ($"{passed} Passed Checks", Pass(cb)),
                };

                // By-area breakdown (worst pass-rate first) — executive "where are the problems".
                var byArea = ordered
                    .GroupBy(f => string.IsNullOrWhiteSpace(f.Category) ? "Uncategorised" : f.Category)
                    .Select(g =>
                    {
                        var tot = g.Count();
                        var pas = g.Count(x => x.State == FindingState.Pass);
                        return (Area: g.Key, Passed: pas, Total: tot, Pct: tot > 0 ? 100.0 * pas / tot : 0);
                    })
                    .OrderBy(x => x.Pct).ThenByDescending(x => x.Total - x.Passed)
                    .ToList();

                page.Content().PaddingVertical(6).Column(col =>
                {
                    col.Spacing(10);
                    col.Item().Element(c => TitleBand(c, r));
                    col.Item().Row(row =>
                    {
                        row.Spacing(14);
                        row.ConstantItem(140).AlignMiddle().Element(d => Donut(d, segs, $"{pct:F0}%", "Passed", Pass(cb)));
                        row.ConstantItem(150).AlignMiddle().Element(l => DonutLegend(l, legend));
                        if (r.Stats.Count > 0) row.RelativeItem().AlignMiddle().Element(c => Stats(c, r.Stats));
                    });
                    if (byArea.Count > 1)
                        col.Item().Element(c => AreaBreakdown(c, byArea.Take(10).ToList(), cb));
                    col.Item().Element(c => Table(c, ordered, cb));
                });
            });
        }).GeneratePdf();
    }

    private static void TitleBand(IContainer c, FindingsReport r) =>
        c.Border(1).BorderColor("#dddddd").Padding(10).Row(row =>
        {
            row.RelativeItem().Column(cc =>
            {
                if (!string.IsNullOrWhiteSpace(r.Meta.Company))
                    cc.Item().Text(r.Meta.Company.ToUpperInvariant()).FontSize(8).Bold().FontColor(Muted);
                cc.Item().Text(r.Meta.Title).FontSize(18).Bold().FontColor("#222222");
                if (!string.IsNullOrEmpty(r.Meta.Subtitle))
                    cc.Item().Text(r.Meta.Subtitle).FontSize(9).FontColor("#666666");
                cc.Item().Text(t =>
                {
                    if (!string.IsNullOrEmpty(r.Meta.Engine)) { t.Span(r.Meta.Engine).FontColor(Muted).FontSize(8); t.Span("  ·  ").FontColor(Faint).FontSize(8); }
                    t.Span($"Generated {r.Meta.GeneratedUtc} ({r.Meta.TimezoneId})  ·  Run {r.Meta.RunId}").FontColor(Faint).FontSize(8);
                });
            });
            row.ConstantItem(120).AlignRight().AlignMiddle().Text("⛁ SQLTriage").Bold().FontColor(Brand).FontSize(13);
        });

    private static void Stats(IContainer c, List<StatChip> stats) =>
        c.Row(row =>
        {
            row.Spacing(8);
            foreach (var s in stats)
                row.RelativeItem().Border(1).BorderColor(Line).Padding(8).Column(cc =>
                {
                    cc.Item().AlignCenter().Text(s.Value).FontSize(18).Bold().FontColor(s.Color);
                    cc.Item().AlignCenter().Text(s.Label).FontSize(7.5f).FontColor(Muted);
                });
        });

    private static void Table(IContainer c, List<FindingRow> rows, bool cb) =>
        c.Table(table =>
        {
            table.ColumnsDefinition(d =>
            {
                d.ConstantColumn(58);   // status
                d.ConstantColumn(66);   // severity
                d.RelativeColumn(3);    // check + detail
                d.ConstantColumn(96);   // category
                d.RelativeColumn(2);    // server
            });

            table.Header(h =>
            {
                foreach (var head in new[] { "Status", "Severity", "Check", "Category", "Server" })
                    h.Cell().BorderBottom(1).BorderColor(Muted).PaddingVertical(3).PaddingRight(4)
                        .Text(head).FontSize(7.5f).Bold().FontColor(Muted);
            });

            var alt = false;
            foreach (var f in rows)
            {
                var (glyph, scol) = StateGlyph(f.State, cb);
                var bg = alt ? "#fafafa" : "#ffffff"; alt = !alt;
                string label = f.State switch
                {
                    FindingState.Pass => "Pass", FindingState.Fail => "Finding",
                    FindingState.Error => "Error", FindingState.Skipped => "Skip", _ => "Info",
                };

                table.Cell().Background(bg).PaddingVertical(3).PaddingLeft(2).Text(t =>
                { t.Span(glyph + " ").FontColor(scol).FontSize(8.5f); t.Span(label).FontColor(scol).FontSize(7.5f); });

                table.Cell().Background(bg).PaddingVertical(3).Text(string.IsNullOrWhiteSpace(f.Severity) ? "—" : f.Severity)
                    .FontSize(7.5f).Bold().FontColor(Sev(f.Severity, cb));

                table.Cell().Background(bg).PaddingVertical(3).PaddingRight(6).Column(cc =>
                {
                    cc.Item().Text(f.Name).FontSize(8).FontColor("#333333");
                    if (!string.IsNullOrWhiteSpace(f.Detail))
                        cc.Item().Text(f.Detail).FontSize(7).Italic().FontColor(Muted);
                    if (!string.IsNullOrWhiteSpace(f.BusinessImpact))
                        cc.Item().PaddingTop(1).Text("Impact: " + f.BusinessImpact).FontSize(7).Bold().FontColor("#d97706");
                    if (!string.IsNullOrWhiteSpace(f.ConsultingLink))
                        cc.Item().PaddingTop(2).Hyperlink(f.ConsultingLink).Text("Need Expert Help? [Talk to Adrian]").FontSize(7).Bold().FontColor("#2563eb").Underline();
                });

                table.Cell().Background(bg).PaddingVertical(3).PaddingRight(4).Text(string.IsNullOrWhiteSpace(f.Category) ? "—" : f.Category)
                    .FontSize(7.5f).FontColor("#555555");

                table.Cell().Background(bg).PaddingVertical(3).Text(string.IsNullOrWhiteSpace(f.Server) ? "—" : f.Server)
                    .FontSize(7.5f).FontColor("#555555");
            }
        });

    private static void Watermark(IContainer c, AssessmentMeta m) =>
        c.AlignCenter().AlignMiddle().Rotate(-30).Text(m.WatermarkText)
            .FontSize(58).Bold().FontColor(m.ColorBlind ? "#f2dcc6" : "#f6d4cc");

    // Shared page footer: brand mark · centre meta line · page x of y.
    private static void StandardFooter(IContainer c, string footerMeta) =>
        c.BorderTop(1.5f).BorderColor(Brand).PaddingTop(4).Row(row =>
        {
            row.RelativeItem().Text(t =>
            {
                t.Span("⛁ SQLTriage").Bold().FontColor(Brand).FontSize(7.5f);
                t.Span("  ·  Compliments of sqldba.org").FontColor(Muted).FontSize(7);
            });
            row.RelativeItem(3).AlignCenter().Text(footerMeta).FontSize(7).FontColor("#666666");
            row.RelativeItem().AlignRight().Text(t =>
            {
                t.DefaultTextStyle(x => x.FontSize(7).FontColor("#666666"));
                t.Span("Page "); t.CurrentPageNumber(); t.Span(" of "); t.TotalPages();
            });
        });

    // Standard page chrome (size/margin/font/watermark/footer) for the briefing + bundle documents.
    private static void SetupPage(PageDescriptor page, AssessmentMeta meta, bool landscape)
    {
        page.Size(landscape ? PageSizes.A4.Landscape() : PageSizes.A4);
        page.Margin(28);
        page.DefaultTextStyle(s => s.FontSize(9).FontFamily("Arial").FontColor(Ink));
        if (meta.Watermark) page.Foreground().Element(c => Watermark(c, meta));
        page.Footer().Element(c => StandardFooter(c, meta.FooterMeta));
    }

    private static string Clamp(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max - 1).TrimEnd() + "…";

    // ── Donut / ring chart ─────────────────────────────────────────────────
    // Ring arcs drawn in SkiaSharp; the centre label is overlaid as QuestPDF text (font/version-safe).
    public sealed class DonutSegment { public double Value; public string Color = "#888888"; }

    /// <summary>Public donut-PNG accessor so sibling report builders (e.g. RiskAssessmentPdf)
    /// reuse the exact same ring rendering without duplicating the SkiaSharp code.</summary>
    public static byte[] DonutPngPublic(IReadOnlyList<DonutSegment> segments) => DonutPng(segments);

    private static byte[] DonutPng(IReadOnlyList<DonutSegment> segments, int px = 360)
    {
        var info = new SKImageInfo(px, px, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        float stroke = px * 0.17f;
        float radius = (px - stroke) / 2f - 1f;
        var rect = new SKRect(px / 2f - radius, px / 2f - radius, px / 2f + radius, px / 2f + radius);

        using (var track = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = stroke, IsAntialias = true, Color = new SKColor(0xE5, 0xE5, 0xE5) })
            canvas.DrawOval(rect, track);

        var total = segments.Sum(s => s.Value);
        if (total > 0)
        {
            using var paint = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = stroke, IsAntialias = true, StrokeCap = SKStrokeCap.Butt };
            float start = -90f;
            foreach (var s in segments)
            {
                if (s.Value <= 0) continue;
                float sweep = (float)(s.Value / total * 360.0);
                paint.Color = SKColor.TryParse(s.Color, out var col) ? col : SKColors.Gray;
                using var path = new SKPath();
                path.AddArc(rect, start, sweep);
                canvas.DrawPath(path, paint);
                start += sweep;
            }
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static void Donut(IContainer c, IReadOnlyList<DonutSegment> segments, string centreBig, string centreSmall, string bigColor, float sizePt = 132)
    {
        var png = DonutPng(segments);
        c.Width(sizePt).Height(sizePt).Layers(layers =>
        {
            layers.Layer().Image(png).FitArea();
            layers.PrimaryLayer().AlignCenter().AlignMiddle().Column(col =>
            {
                col.Item().AlignCenter().Text(centreBig).FontSize(sizePt * 0.16f).Bold().FontColor(bigColor);
                if (!string.IsNullOrEmpty(centreSmall))
                    col.Item().AlignCenter().Text(centreSmall).FontSize(sizePt * 0.075f).FontColor(Muted);
            });
        });
    }

    private static void AreaBreakdown(IContainer c, List<(string Area, int Passed, int Total, double Pct)> rows, bool cb) =>
        c.Column(col =>
        {
            col.Item().PaddingBottom(3).Text("Findings by area — lowest pass-rate first").FontSize(9.5f).Bold().FontColor("#444444");
            col.Item().Table(table =>
            {
                table.ColumnsDefinition(d => { d.RelativeColumn(2); d.RelativeColumn(3); d.ConstantColumn(96); });
                foreach (var r in rows)
                {
                    var barCol = ScoreColor(r.Pct, cb);
                    var p = (int)System.Math.Round(r.Pct);
                    table.Cell().PaddingVertical(2).Text(r.Area).FontSize(8).FontColor("#444444");
                    table.Cell().PaddingVertical(3).PaddingRight(10).AlignMiddle().Row(bar =>
                    {
                        if (p > 0)   bar.RelativeItem(p).Height(7).Background(barCol);
                        if (p < 100) bar.RelativeItem(100 - p).Height(7).Background("#eeeeee");
                    });
                    table.Cell().PaddingVertical(2).Text($"{r.Pct:F0}%  ·  {r.Total - r.Passed} open").FontSize(7.5f).FontColor("#666666");
                }
            });
        });

    private static void DonutLegend(IContainer c, IReadOnlyList<(string Label, string Color)> items) =>
        c.Column(col =>
        {
            col.Spacing(3);
            foreach (var (label, color) in items)
                col.Item().Row(row =>
                {
                    row.ConstantItem(12).AlignMiddle().Height(8).Background(color);
                    row.RelativeItem().PaddingLeft(5).Text(label).FontSize(8).FontColor("#555555");
                });
        });

    // ── Compliance scorecard report ────────────────────────────────────────
    public static byte[] BuildComplianceReport(ComplianceReport r)
    {
        var cb = r.Meta.ColorBlind;
        var scoreCol = ScoreColor(r.OverallPercent, cb);

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(26);
                page.DefaultTextStyle(s => s.FontSize(8.5f).FontFamily("Arial").FontColor(Ink));
                if (r.Meta.Watermark) page.Foreground().Element(c => Watermark(c, r.Meta));

                page.Footer().Element(c => StandardFooter(c, r.Meta.FooterMeta));

                page.Content().PaddingVertical(6).Column(col =>
                {
                    col.Spacing(10);

                    // Title band with the big framework score on the right.
                    col.Item().Border(1).BorderColor("#dddddd").Padding(10).Row(row =>
                    {
                        row.RelativeItem().Column(cc =>
                        {
                            if (!string.IsNullOrWhiteSpace(r.Meta.Company))
                                cc.Item().Text(r.Meta.Company.ToUpperInvariant()).FontSize(8).Bold().FontColor(Muted);
                            cc.Item().Text(r.Meta.Title).FontSize(18).Bold().FontColor("#222222");
                            if (!string.IsNullOrEmpty(r.Meta.Subtitle))
                                cc.Item().Text(r.Meta.Subtitle).FontSize(9).FontColor("#666666");
                            cc.Item().Text($"{r.Meta.Engine}  ·  Generated {r.Meta.GeneratedUtc} ({r.Meta.TimezoneId})  ·  Run {r.Meta.RunId}")
                                .FontSize(8).FontColor(Faint);
                        });
                        row.ConstantItem(130).AlignRight().AlignMiddle().Column(cc =>
                        {
                            cc.Item().AlignRight().Text(r.OverallPercent >= 0 ? $"{r.OverallPercent:F0}%" : "—").FontSize(28).Bold().FontColor(scoreCol);
                            cc.Item().AlignRight().Text("COMPLIANCE").FontSize(7).FontColor(Faint);
                        });
                    });

                    var compSegs = new List<DonutSegment>
                    {
                        new() { Value = r.Compliant,    Color = Pass(cb) },
                        new() { Value = r.Partial,      Color = Warn(cb) },
                        new() { Value = r.NonCompliant, Color = Fail(cb) },
                        new() { Value = r.OutsideScope, Color = "#cccccc" },
                    };
                    col.Item().Row(row =>
                    {
                        row.Spacing(14);
                        row.ConstantItem(124).AlignMiddle().Element(d =>
                            Donut(d, compSegs, r.OverallPercent >= 0 ? $"{r.OverallPercent:F0}%" : "—", "Compliant", scoreCol, 120));
                        row.RelativeItem().AlignMiddle().Row(inner =>
                        {
                            inner.Spacing(8);
                            ComplianceStat(inner, r.Compliant.ToString(),    "COMPLIANT",     Pass(cb));
                            ComplianceStat(inner, r.Partial.ToString(),      "PARTIAL",       Warn(cb));
                            ComplianceStat(inner, r.NonCompliant.ToString(), "NON-COMPLIANT", Fail(cb));
                            ComplianceStat(inner, r.OutsideScope.ToString(), "OUTSIDE SCOPE", Muted);
                        });
                    });

                    col.Item().Table(table =>
                    {
                        table.ColumnsDefinition(d => { d.ConstantColumn(90); d.RelativeColumn(); d.ConstantColumn(56); d.ConstantColumn(108); });
                        table.Header(h =>
                        {
                            foreach (var head in new[] { "Control ID", "Control", "Score", "Status" })
                                h.Cell().BorderBottom(1).BorderColor(Muted).PaddingVertical(3).PaddingRight(4)
                                    .Text(head).FontSize(7.5f).Bold().FontColor(Muted);
                        });
                        var alt = false;
                        foreach (var f in r.Families)
                        {
                            var bg = alt ? "#fafafa" : "#ffffff"; alt = !alt;
                            var (label, scol) = StatusLabel(f.Status, cb);
                            table.Cell().Background(bg).PaddingVertical(3).PaddingLeft(2).Text(f.Id).FontSize(7.5f).FontColor("#555555");
                            table.Cell().Background(bg).PaddingVertical(3).PaddingRight(6).Text(f.Name).FontSize(8).FontColor("#333333");
                            table.Cell().Background(bg).PaddingVertical(3).Text(f.Status == "NoData" ? "—" : $"{f.Percent:F0}%").FontSize(8).FontColor("#444444");
                            table.Cell().Background(bg).PaddingVertical(3).Text(label).FontSize(7.5f).Bold().FontColor(scol);
                        }
                    });
                });
            });
        }).GeneratePdf();
    }

    private static void ComplianceStat(RowDescriptor row, string value, string label, string colour) =>
        row.RelativeItem().Border(1).BorderColor(Line).Padding(8).Column(cc =>
        {
            cc.Item().AlignCenter().Text(value).FontSize(18).Bold().FontColor(colour);
            cc.Item().AlignCenter().Text(label).FontSize(7.5f).FontColor(Muted);
        });

    private static (string, string) StatusLabel(string status, bool cb) => status switch
    {
        "Compliant"            => ("Compliant", Pass(cb)),
        "PartiallyCompliant"   => ("Partial", Warn(cb)),
        "NonCompliant"         => ("Non-compliant", Fail(cb)),
        _                       => ("Outside scope", Muted),
    };

    /// <summary>Score → colour ramp (≥80 pass, ≥50 warn, ≥0 fail, &lt;0 outside-scope grey). Public for tab tinting.</summary>
    public static string ScoreColor(double percent, bool cb) =>
        percent < 0   ? Muted :
        percent >= 80 ? Pass(cb) :
        percent >= 50 ? Warn(cb) :
                        Fail(cb);

    // ══════════════════════════════════════════════════════════════════════
    //  Executive Briefing — Microsoft-style 5-focus-area narrative
    // ══════════════════════════════════════════════════════════════════════
    // Microsoft's SQL Assessment groups findings into five focus areas, each with
    // a pass% donut + "Highest Priority Recommendations". We map our corpus/VA
    // categories onto those five areas (one tunable place) and render a slide-style
    // briefing: dark cover → narrative summary → one page per focus area.

    public static readonly IReadOnlyList<string> FocusAreas = new[]
    {
        "Security & Compliance",
        "Availability & Business Continuity",
        "Performance & Scalability",
        "Operations & Monitoring",
        "Upgrade, Migration & Deployment",
    };

    /// <summary>Map a corpus/VA category to one of Microsoft's five focus areas. Keyword-based so it
    /// tolerates naming drift across engines; Operations &amp; Monitoring is the catch-all. Tunable here.</summary>
    public static string FocusAreaFor(string? category)
    {
        var c = (category ?? "").ToLowerInvariant();
        bool Has(params string[] keys) => keys.Any(k => c.Contains(k));

        if (Has("secur", "encrypt", "audit", "surface", "compliance", "permission", "login",
                "authentic", "vulnerab", "network", "firewall", "certificate", "tde", "credential"))
            return "Security & Compliance";
        if (Has("backup", "restore", "recover", "availab", "alwayson", "always on", "cluster",
                "replicat", "log shipping", "mirror", "reliab", "corruption", "dbcc", "failover", "dr"))
            return "Availability & Business Continuity";
        if (Has("perf", "index", "tempdb", "memory", "cpu", "io", "i/o", "wait", "statistic",
                "query", "tuning", "scal", "parallel", "fragment", "page life"))
            return "Performance & Scalability";
        if (Has("upgrade", "migrat", "deploy", "build", "patch", "version", "compat", "edition",
                "install", "servicing", "cumulative", "end of support", "eol", "lifecycle"))
            return "Upgrade, Migration & Deployment";
        return "Operations & Monitoring";
    }

    private sealed record AreaStat(string Area, List<FindingRow> Findings, int Passed, int Total, double Pct);

    // Best plain-English recommendation, skipping trivially short/placeholder values
    // (some corpus checks carry a bare "#"/"." as RecommendedAction) in favour of the
    // richer Detail/Description, then the check name as a last resort.
    private static string RecText(FindingRow f)
    {
        var rec = (f.Recommendation ?? "").Trim();
        if (rec.Length >= 5) return rec;
        var det = (f.Detail ?? "").Trim();
        if (det.Length >= 5) return det;
        return f.Name;
    }

    private static List<DonutSegment> PrioritySegments(IReadOnlyCollection<FindingRow> rows, int passed, bool cb)
    {
        var highN = rows.Count(f => f.State != FindingState.Pass && SevRank(f.Severity) <= 1);
        var lowN  = rows.Count(f => f.State != FindingState.Pass) - highN;
        return new List<DonutSegment>
        {
            new() { Value = passed, Color = Pass(cb) },
            new() { Value = lowN,   Color = Info },
            new() { Value = highN,  Color = Fail(cb) },
        };
    }

    public static byte[] BuildExecutiveBriefing(BriefingReport r)
    {
        var cb = r.Meta.ColorBlind;

        var grouped = FocusAreas.ToDictionary(a => a, _ => new List<FindingRow>());
        foreach (var f in r.Findings)
            grouped[FocusAreaFor(f.Category)].Add(f);

        var areaStats = FocusAreas
            .Select(a =>
            {
                var list   = grouped[a];
                var passed = list.Count(x => x.State == FindingState.Pass);
                return new AreaStat(a, list, passed, list.Count, list.Count > 0 ? 100.0 * passed / list.Count : -1);
            })
            .Where(s => s.Total > 0)
            .ToList();

        var overallTotal = r.Findings.Count;
        var overallPass  = r.Findings.Count(f => f.State == FindingState.Pass);
        var overallPct   = overallTotal > 0 ? 100.0 * overallPass / overallTotal : 0;
        var overallSegs  = PrioritySegments(r.Findings, overallPass, cb);

        var ranked = areaStats.OrderByDescending(s => s.Pct).ToList();
        var best   = ranked.FirstOrDefault();
        var worst  = ranked.LastOrDefault();
        var topFinding = r.Findings
            .Where(f => f.State != FindingState.Pass)
            .OrderBy(f => SevRank(f.Severity)).ThenBy(f => f.Name)
            .FirstOrDefault();

        var bestText = best is null
            ? "No checks were evaluated."
            : $"{best.Area} is in the strongest shape — {best.Passed} of {best.Total} checks passed ({best.Pct:F0}%).";
        var worstText = worst is null || worst.Pct < 0
            ? "No open findings to report."
            : $"{worst.Area} has the most room to improve — {worst.Pct:F0}% of checks passed, with {worst.Total - worst.Passed} open finding(s).";
        string improveText;
        if (topFinding is null)
            improveText = "All evaluated checks passed — maintain current configuration and re-assess periodically.";
        else
        {
            var rec = RecText(topFinding);
            improveText = string.Equals(rec, topFinding.Name, StringComparison.OrdinalIgnoreCase)
                ? $"Start with the highest-severity finding: “{Clamp(topFinding.Name, 110)}”."
                : $"Start with “{Clamp(topFinding.Name, 90)}”: {Clamp(rec, 240)}";
        }

        return Document.Create(container =>
        {
            // 1) Dark cover with the overall pass% donut.
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(0);
                page.DefaultTextStyle(s => s.FontFamily("Arial").FontColor("#f5f5f5"));
                page.Content().Background("#0d141a").PaddingVertical(48).PaddingHorizontal(60).Column(col =>
                {
                    col.Spacing(8);
                    col.Item().AlignCenter().Text("⛁").FontColor(Brand).FontSize(32);
                    col.Item().AlignCenter().Text("SQLTRIAGE").FontColor("#9aa4ad").FontSize(11).Bold();
                    col.Item().AlignCenter().Text(r.Meta.Title).FontColor("#f5f5f5").FontSize(28).Bold();
                    col.Item().AlignCenter().Text("Executive Briefing").FontColor(Brand).FontSize(15).Bold();
                    if (!string.IsNullOrWhiteSpace(r.Meta.Subtitle))
                        col.Item().AlignCenter().Text(r.Meta.Subtitle).FontColor("#c5ccd2").FontSize(12);
                    if (!string.IsNullOrWhiteSpace(r.Meta.Company))
                        col.Item().AlignCenter().Text($"Prepared for {r.Meta.Company}").FontColor("#9aa4ad").FontSize(12);
                    col.Item().PaddingTop(14).AlignCenter().Element(d =>
                        Donut(d, overallSegs, $"{overallPct:F0}%", "Passed", Pass(cb), 150));
                    col.Item().PaddingTop(8).AlignCenter().Text(
                        $"{overallPass} of {overallTotal} checks passed across {areaStats.Count} focus area(s)")
                        .FontColor("#c5ccd2").FontSize(11);
                    col.Item().PaddingTop(16).AlignCenter().Text(
                        $"Generated {r.Meta.GeneratedUtc} ({r.Meta.TimezoneId})  ·  Run {r.Meta.RunId}")
                        .FontColor("#6b757d").FontSize(9);
                    col.Item().AlignCenter().Text("Compliments of sqldba.org").FontColor("#6b757d").FontSize(9);
                });
            });

            // 2) Narrative summary: donut + what-went-well/poorly/improve + per-area strip.
            container.Page(page =>
            {
                SetupPage(page, r.Meta, true);
                page.Content().PaddingVertical(8).Column(col =>
                {
                    col.Spacing(14);
                    col.Item().Element(c => BriefingTitleBand(c, r.Meta, "Executive Summary"));
                    col.Item().Row(row =>
                    {
                        row.Spacing(22);
                        row.ConstantItem(150).AlignMiddle().Element(d =>
                            Donut(d, overallSegs, $"{overallPct:F0}%", "Passed", Pass(cb), 140));
                        row.RelativeItem().AlignMiddle().Column(n =>
                        {
                            n.Spacing(10);
                            NarrativeBlock(n, "What went well", bestText, Pass(cb));
                            NarrativeBlock(n, "What needs attention", worstText, Fail(cb));
                            NarrativeBlock(n, "How to improve", improveText, Info);
                        });
                    });
                    if (areaStats.Count > 0)
                        col.Item().PaddingTop(2).Element(c => AreaBreakdown(c,
                            areaStats.Select(s => (s.Area, s.Passed, s.Total, s.Pct)).OrderBy(x => x.Pct).ToList(), cb));
                });
            });

            // 3) One page per focus area (worst pass-rate first).
            foreach (var s in areaStats.OrderBy(x => x.Pct))
            {
                var recs = s.Findings
                    .Where(f => f.State != FindingState.Pass)
                    .OrderBy(f => SevRank(f.Severity)).ThenBy(f => f.Name)
                    .ToList();

                container.Page(page =>
                {
                    SetupPage(page, r.Meta, true);
                    page.Content().PaddingVertical(8).Column(col =>
                    {
                        col.Spacing(12);
                        col.Item().Element(c => BriefingTitleBand(c, r.Meta, s.Area));
                        col.Item().Row(row =>
                        {
                            row.Spacing(22);
                            row.ConstantItem(140).AlignMiddle().Element(d =>
                                Donut(d, PrioritySegments(s.Findings, s.Passed, cb), $"{s.Pct:F0}%", "Passed", ScoreColor(s.Pct, cb), 132));
                            row.RelativeItem().AlignMiddle().Column(cc =>
                            {
                                cc.Spacing(3);
                                cc.Item().Text($"{s.Total} checks evaluated").FontSize(13).Bold().FontColor("#333333");
                                cc.Item().Text($"{s.Passed} passed  ·  {s.Total - s.Passed} open").FontSize(10).FontColor("#666666");
                                var highN = recs.Count(f => SevRank(f.Severity) <= 1);
                                if (highN > 0)
                                    cc.Item().PaddingTop(2).Text($"{highN} high-priority finding(s) need attention").FontSize(9).FontColor(Fail(cb));
                            });
                        });
                        col.Item().PaddingTop(2).Text("Highest Priority Recommendations").FontSize(12).Bold().FontColor(Brand);
                        if (recs.Count == 0)
                            col.Item().Text("No open findings — this area passed all evaluated checks.").FontSize(10).Italic().FontColor(Pass(cb));
                        else
                        {
                            foreach (var f in recs.Take(7))
                                col.Item().Element(c => RecommendationCard(c, f, cb));
                            if (recs.Count > 7)
                                col.Item().PaddingTop(2).Text($"+ {recs.Count - 7} more finding(s) in the detailed assessment report.")
                                    .FontSize(8.5f).Italic().FontColor(Muted);
                        }
                    });
                });
            }
        }).GeneratePdf();
    }

    private static void NarrativeBlock(ColumnDescriptor col, string heading, string body, string accent) =>
        col.Item().BorderLeft(3).BorderColor(accent).PaddingLeft(10).Column(cc =>
        {
            cc.Item().Text(heading).FontSize(10.5f).Bold().FontColor(accent);
            cc.Item().Text(body).FontSize(9.5f).FontColor("#444444");
        });

    private static void RecommendationCard(IContainer c, FindingRow f, bool cb) =>
        c.Border(1).BorderColor(Line).MinHeight(20).Row(row =>
        {
            row.ConstantItem(4).Background(Sev(f.Severity, cb));
            row.RelativeItem().Padding(8).Column(cc =>
            {
                cc.Item().Row(h =>
                {
                    h.RelativeItem().Text(f.Name).FontSize(9.5f).Bold().FontColor("#222222");
                    if (!string.IsNullOrWhiteSpace(f.Severity))
                        h.ConstantItem(74).AlignRight().Text(f.Severity.ToUpperInvariant()).FontSize(7.5f).Bold().FontColor(Sev(f.Severity, cb));
                });
                var rec = RecText(f);
                if (!string.IsNullOrWhiteSpace(rec) && !string.Equals(rec, f.Name, StringComparison.OrdinalIgnoreCase))
                    cc.Item().PaddingTop(1).Text(Clamp(rec, 320)).FontSize(8.5f).FontColor("#555555");
                if (!string.IsNullOrWhiteSpace(f.BusinessImpact))
                    cc.Item().PaddingTop(2).Text("Impact: " + Clamp(f.BusinessImpact, 200)).FontSize(8.5f).Bold().FontColor("#d97706");
                if (!string.IsNullOrWhiteSpace(f.ConsultingLink))
                    cc.Item().PaddingTop(3).Hyperlink(f.ConsultingLink).Text("Need Expert Help? [Book a consultation]").FontSize(8.5f).Bold().FontColor("#2563eb").Underline();
                if (!string.IsNullOrWhiteSpace(f.Server))
                    cc.Item().PaddingTop(1).Text(f.Server).FontSize(7.5f).FontColor(Faint);
            });
        });

    private static void BriefingTitleBand(IContainer c, AssessmentMeta m, string section) =>
        c.BorderBottom(2).BorderColor(Brand).PaddingBottom(8).Row(row =>
        {
            row.RelativeItem().Column(cc =>
            {
                if (!string.IsNullOrWhiteSpace(m.Company))
                    cc.Item().Text(m.Company.ToUpperInvariant()).FontSize(8).Bold().FontColor(Muted);
                cc.Item().Text(section).FontSize(17).Bold().FontColor("#222222");
                cc.Item().Text(t =>
                {
                    t.Span(m.Title).FontColor(Muted).FontSize(8.5f);
                    if (!string.IsNullOrWhiteSpace(m.Subtitle)) { t.Span("  ·  ").FontColor(Faint).FontSize(8.5f); t.Span(m.Subtitle).FontColor(Faint).FontSize(8.5f); }
                });
            });
            row.ConstantItem(120).AlignRight().AlignMiddle().Text("⛁ SQLTriage").Bold().FontColor(Brand).FontSize(13);
        });

    // ══════════════════════════════════════════════════════════════════════
    //  Report Bundles — QuestPDF rebuild of the 3 diagnostic packages
    // ══════════════════════════════════════════════════════════════════════
    // Replaces the crashing browser-print path. Portrait, document-style reports
    // that reuse the shared palette / donut / footer.

    public static byte[] BuildExecutiveSummaryBundle(ExecutiveSummaryBundle b)
    {
        var cb = b.Meta.ColorBlind;
        var execEstate = b.TopRisks.Any(r => r.ServersTotal > 0);
        var scoreCol = ScoreColor(b.Score, cb);
        var segs = new List<DonutSegment>
        {
            new() { Value = b.Score,                 Color = scoreCol },
            new() { Value = Math.Max(0, 100 - b.Score), Color = "#eeeeee" },
        };
        return Document.Create(c => c.Page(page =>
        {
            SetupPage(page, b.Meta, false);
            page.Content().PaddingVertical(6).Column(col =>
            {
                col.Spacing(16);
                col.Item().Element(x => BundleTitleBand(x, b.Meta, "For non-technical stakeholders"));
                // A single estate has no one health score — show a scope summary instead of a
                // misleading "0 / 100" donut. Single-server reports keep the donut.
                if (execEstate)
                {
                    var serverTotal = b.TopRisks.Select(r => r.ServersTotal).DefaultIfEmpty(0).Max();
                    col.Item().Background("#f7f7fb").Border(1).BorderColor(Line).Padding(10).Column(cc =>
                    {
                        cc.Item().Text("Estate Overview").FontSize(13).Bold().FontColor("#333333");
                        cc.Item().PaddingTop(4).Text($"{serverTotal} server{(serverTotal == 1 ? "" : "s")} scanned · top risks ranked by how many servers each affects.")
                            .FontSize(10).FontColor("#555555");
                    });
                }
                else
                {
                    col.Item().Row(row =>
                    {
                        row.Spacing(18);
                        row.ConstantItem(150).AlignMiddle().Element(d => Donut(d, segs, $"{b.Score}", "/ 100", scoreCol, 140));
                        row.RelativeItem().AlignMiddle().Column(cc =>
                        {
                            cc.Item().Text("Overall Health Score").FontSize(13).Bold().FontColor("#333333");
                            if (!string.IsNullOrWhiteSpace(b.ScoreMessage))
                                cc.Item().PaddingTop(4).Text(b.ScoreMessage).FontSize(10).FontColor("#555555");
                        });
                    });
                }
                col.Item().Text(execEstate ? "Top 5 Risks Across the Estate" : "Top 5 Risks").FontSize(13).Bold().FontColor(Brand);
                if (b.TopRisks.Count == 0)
                    col.Item().Text("No cached vulnerability findings — run a Vulnerability Assessment first.").FontSize(10).Italic().FontColor(Muted);
                else
                    col.Item().Element(x => BundleFindingsTable(x, b.TopRisks, cb, framework: false, showRemediation: false, serversImpacted: execEstate, businessVoice: true));
                col.Item().PaddingTop(4).Text("Report period: last 30 days. Suitable for board reports and management briefings.")
                    .FontSize(8.5f).Italic().FontColor(Muted);
            });
        })).GeneratePdf();
    }

    public static byte[] BuildDbaHandoffBundle(DbaHandoffBundle b)
    {
        var cb = b.Meta.ColorBlind;
        return Document.Create(c => c.Page(page =>
        {
            SetupPage(page, b.Meta, false);
            page.Content().PaddingVertical(6).Column(col =>
            {
                col.Spacing(16);
                col.Item().Element(x => BundleTitleBand(x, b.Meta, "Full diagnostic baseline"));

                col.Item().Text("Server Inventory").FontSize(13).Bold().FontColor(Brand);
                if (b.Inventory.Count == 0)
                    col.Item().Text("No health data cached. Visit the Health page first.").FontSize(10).Italic().FontColor(Muted);
                else
                    col.Item().Element(x => KeyValueTable(x, b.Inventory));

                var dbaEstate = b.EstateAppendix.Count > 0;
                col.Item().Text(dbaEstate ? "Findings Across the Estate (grouped by check)" : "All Vulnerability Assessment Findings").FontSize(13).Bold().FontColor(Brand);
                if (b.AllFindings.Count == 0)
                    col.Item().Text("No cached findings. Run a Vulnerability Assessment first.").FontSize(10).Italic().FontColor(Muted);
                else
                    col.Item().Element(x => BundleFindingsTable(x, b.AllFindings, cb, framework: false, showRemediation: false, serversImpacted: dbaEstate));

                if (dbaEstate)
                {
                    col.Item().Text("Appendix — Failed Checks by Server").FontSize(13).Bold().FontColor(Brand);
                    col.Item().Element(x => EstateAppendixTable(x, b.EstateAppendix));
                }
                else
                {
                    col.Item().Text("Known Issues (Failed Checks)").FontSize(13).Bold().FontColor(Brand);
                    if (b.KnownIssues.Count == 0)
                        col.Item().Text("No failed checks in cached findings.").FontSize(10).Italic().FontColor(Pass(cb));
                    else
                        col.Item().Element(x => BundleFindingsTable(x, b.KnownIssues, cb, framework: false, showRemediation: true));
                }
            });
        })).GeneratePdf();
    }

    public static byte[] BuildAuditEvidenceBundle(AuditEvidenceBundle b)
    {
        var cb = b.Meta.ColorBlind;
        return Document.Create(c => c.Page(page =>
        {
            SetupPage(page, b.Meta, false);
            page.Content().PaddingVertical(6).Column(col =>
            {
                col.Spacing(16);
                col.Item().Element(x => BundleTitleBand(x, b.Meta, "For compliance review"));
                col.Item().Background("#f7f7fb").Border(1).BorderColor(Line).Padding(8).Column(hashCol =>
                {
                    hashCol.Item().Text(t =>
                    {
                        t.Span("Document SHA-256 (rendered findings): ").FontSize(8).Bold().FontColor(Muted);
                        t.Span(b.Sha256).FontSize(8).FontFamily("Consolas").FontColor("#444444");
                    });
                    if (!string.IsNullOrEmpty(b.ScanDataSha256))
                    {
                        hashCol.Item().Text(t =>
                        {
                            t.Span("Scan-data SHA-256 (raw results): ").FontSize(8).Bold().FontColor(Muted);
                            t.Span(b.ScanDataSha256).FontSize(8).FontFamily("Consolas").FontColor("#444444");
                        });
                    }
                });

                col.Item().Text("Vulnerability Assessment Findings").FontSize(13).Bold().FontColor(Brand);
                if (b.Findings.Count == 0)
                    col.Item().Text("No cached findings. Run a Vulnerability Assessment first.").FontSize(10).Italic().FontColor(Muted);
                else
                    col.Item().Element(x => BundleFindingsTable(x, b.Findings, cb, framework: true, showRemediation: false));

                col.Item().Text("Audit Log Summary (Last 30 Days)").FontSize(13).Bold().FontColor(Brand);
                col.Item().Element(x => KeyValueTable(x, new List<(string, string)>
                {
                    ("Total Audit Events", b.AuditEventCount.ToString("N0")),
                    ("HMAC Chain Status",  b.ChainStatus),
                }));

                col.Item().Text("Report Integrity").FontSize(13).Bold().FontColor(Brand);
                col.Item().Element(x => KeyValueTable(x, new List<(string, string)>
                {
                    ("Generated By", "SQLTriage Diagnostic Report Packages"),
                    ("Generated",    $"{b.Meta.GeneratedUtc} ({b.Meta.TimezoneId})"),
                    ("Report Period","Last 30 days"),
                    ("Document SHA-256",  b.Sha256),
                    ("Scan-data SHA-256", b.ScanDataSha256),
                }));
            });
        })).GeneratePdf();
    }

    public static byte[] BuildRiskRegisterBundle(RiskRegisterBundle b)
    {
        var cb = b.Meta.ColorBlind;
        var estate = b.Rows.Any(r => r.ServersTotal > 0);
        var tagline = b.Acknowledgement ? "For management review & sign-off" : "Living risk ledger";
        return Document.Create(c => c.Page(page =>
        {
            SetupPage(page, b.Meta, false);
            page.Content().PaddingVertical(6).Column(col =>
            {
                col.Spacing(14);
                col.Item().Element(x => BundleTitleBand(x, b.Meta, tagline));

                col.Item().Element(x => KeyValueTable(x, new List<(string, string)>
                {
                    ("Critical risks", b.CriticalCount.ToString()),
                    ("High risks",     b.HighCount.ToString()),
                    ("Other tracked",  b.OtherCount.ToString()),
                    ("Total",          b.Rows.Count.ToString()),
                }));

                col.Item().Text("Risk Register").FontSize(13).Bold().FontColor(Brand);
                if (b.Rows.Count == 0)
                    col.Item().Text("No outstanding bad-state risks. Run a Vulnerability Assessment first, or the estate is clean.")
                        .FontSize(10).Italic().FontColor(Pass(cb));
                else
                    col.Item().Element(x => RiskRegisterTable(x, b.Rows, cb, estate));

                if (b.Acknowledgement)
                {
                    col.Item().PaddingTop(6).Text("Risk Acknowledgement & Approval").FontSize(13).Bold().FontColor(Brand);
                    col.Item().Background("#f7f7fb").Border(1).BorderColor(Line).Padding(10)
                        .Text(ReportBundleService.AcknowledgementStatement(b.FormalTone)).FontSize(9).FontColor("#333333");
                    col.Item().PaddingTop(4).Element(x => KeyValueTable(x, new List<(string, string)>
                    {
                        ("Prepared by",  string.IsNullOrEmpty(b.PreparedBy) ? "______________________________" : b.PreparedBy),
                        ("Decision",     "[  ] Remediation approved        [  ] Risk accepted (deferred)"),
                        ("Name & title", "______________________________"),
                        ("Signature",    "______________________________"),
                        ("Date",         "______________________________"),
                    }));
                }
            });
        })).GeneratePdf();
    }

    private static void RiskRegisterTable(IContainer c, List<RiskRegisterRow> rows, bool cb, bool estate) =>
        c.Table(table =>
        {
            table.ColumnsDefinition(d =>
            {
                d.ConstantColumn(74);                 // id
                d.ConstantColumn(58);                 // severity
                d.RelativeColumn(2);                  // risk
                d.ConstantColumn(74);                 // category
                d.ConstantColumn(60);                 // owner
                d.ConstantColumn(58);                 // review by
                if (estate) d.ConstantColumn(54);     // servers
                d.RelativeColumn(3);                  // business impact
            });

            var heads = estate
                ? new[] { "ID", "Severity", "Risk", "Category", "Owner", "Review by", "Servers", "Business Impact" }
                : new[] { "ID", "Severity", "Risk", "Category", "Owner", "Review by", "Business Impact" };
            table.Header(h =>
            {
                foreach (var head in heads)
                    h.Cell().BorderBottom(1).BorderColor(Muted).PaddingVertical(3).PaddingRight(4)
                        .Text(head).FontSize(7.5f).Bold().FontColor(Muted);
            });

            var alt = false;
            foreach (var r in rows)
            {
                var bg = alt ? "#fafafa" : "#ffffff"; alt = !alt;
                table.Cell().Background(bg).PaddingVertical(3).PaddingLeft(2).PaddingRight(4).Text(Clamp(r.Id, 28)).FontSize(7.5f).FontColor("#555555");
                table.Cell().Background(bg).PaddingVertical(3).Text(string.IsNullOrWhiteSpace(r.Severity) ? "—" : r.Severity)
                    .FontSize(7.5f).Bold().FontColor(Sev(r.Severity, cb));
                table.Cell().Background(bg).PaddingVertical(3).PaddingRight(6).Text(r.Risk).FontSize(8).FontColor("#333333");
                table.Cell().Background(bg).PaddingVertical(3).PaddingRight(4).Text(string.IsNullOrWhiteSpace(r.Category) ? "—" : r.Category)
                    .FontSize(7.5f).FontColor("#555555");
                table.Cell().Background(bg).PaddingVertical(3).PaddingRight(4).Text(string.IsNullOrWhiteSpace(r.Owner) ? "—" : r.Owner)
                    .FontSize(7.5f).FontColor("#555555");
                table.Cell().Background(bg).PaddingVertical(3).PaddingRight(4).Text(r.ReviewByUtc.HasValue ? r.ReviewByUtc.Value.ToString("yyyy-MM-dd") : "—")
                    .FontSize(7.5f).FontColor("#555555");
                if (estate)
                    table.Cell().Background(bg).PaddingVertical(3).Text($"{r.ServersImpacted} of {r.ServersTotal}").FontSize(7.5f).FontColor("#555555");
                table.Cell().Background(bg).PaddingVertical(3).PaddingRight(2).Text(Clamp(r.BusinessImpact, 500)).FontSize(7.5f).FontColor("#555555");
            }
        });

    private static void BundleTitleBand(IContainer c, AssessmentMeta m, string tagline) =>
        c.BorderBottom(2).BorderColor(Brand).PaddingBottom(8).Row(row =>
        {
            row.RelativeItem().Column(cc =>
            {
                cc.Item().Text(tagline.ToUpperInvariant()).FontSize(8).Bold().FontColor(Muted);
                if (!string.IsNullOrWhiteSpace(m.Company))
                    cc.Item().Text(m.Company).FontSize(9).Bold().FontColor("#444444");
                cc.Item().Text(m.Title).FontSize(20).Bold().FontColor("#222222");
                if (!string.IsNullOrWhiteSpace(m.Subtitle))
                    cc.Item().Text(m.Subtitle).FontSize(9).FontColor("#666666");
                cc.Item().Text($"Generated {m.GeneratedUtc} ({m.TimezoneId})  ·  Run {m.RunId}").FontSize(8).FontColor(Faint);
            });
            row.ConstantItem(120).AlignRight().AlignMiddle().Text("⛁ SQLTriage").Bold().FontColor(Brand).FontSize(14);
        });

    private static void KeyValueTable(IContainer c, List<(string Label, string Value)> rows) =>
        c.Table(table =>
        {
            table.ColumnsDefinition(d => { d.ConstantColumn(170); d.RelativeColumn(); });
            var alt = false;
            foreach (var (label, value) in rows)
            {
                var bg = alt ? "#fafafa" : "#ffffff"; alt = !alt;
                table.Cell().Background("#f0f0f8").BorderColor(Line).Border(0.5f).PaddingVertical(4).PaddingHorizontal(8)
                    .Text(label).FontSize(8.5f).Bold().FontColor("#444444");
                table.Cell().Background(bg).BorderColor(Line).Border(0.5f).PaddingVertical(4).PaddingHorizontal(8)
                    .Text(string.IsNullOrWhiteSpace(value) ? "—" : value).FontSize(8.5f).FontColor("#333333");
            }
        });

    private static void BundleFindingsTable(IContainer c, List<BundleFinding> rows, bool cb, bool framework, bool showRemediation, bool serversImpacted = false, bool businessVoice = false) =>
        c.Table(table =>
        {
            table.ColumnsDefinition(d =>
            {
                d.ConstantColumn(74);                 // id
                d.ConstantColumn(58);                 // severity
                d.RelativeColumn(2);                  // check
                d.ConstantColumn(82);                 // category
                if (framework) d.ConstantColumn(86);  // framework
                if (serversImpacted) d.ConstantColumn(96); // servers impacted
                else d.RelativeColumn(3);             // message / remediation
            });

            var lastHead = serversImpacted ? "Servers Impacted" : (showRemediation ? "Remediation" : "Message");
            var heads = framework
                ? new[] { "ID", "Severity", "Check", "Category", "Framework", lastHead }
                : new[] { "ID", "Severity", "Check", "Category", lastHead };
            table.Header(h =>
            {
                foreach (var head in heads)
                    h.Cell().BorderBottom(1).BorderColor(Muted).PaddingVertical(3).PaddingRight(4)
                        .Text(head).FontSize(7.5f).Bold().FontColor(Muted);
            });

            var alt = false;
            foreach (var f in rows)
            {
                var bg = alt ? "#fafafa" : "#ffffff"; alt = !alt;
                table.Cell().Background(bg).PaddingVertical(3).PaddingLeft(2).PaddingRight(4).Text(Clamp(f.Id, 28)).FontSize(7.5f).FontColor("#555555");
                table.Cell().Background(bg).PaddingVertical(3).Text(string.IsNullOrWhiteSpace(f.Severity) ? "—" : f.Severity)
                    .FontSize(7.5f).Bold().FontColor(Sev(f.Severity, cb));
                table.Cell().Background(bg).PaddingVertical(3).PaddingRight(6).Text(f.Name).FontSize(8).FontColor("#333333");
                table.Cell().Background(bg).PaddingVertical(3).PaddingRight(4).Text(string.IsNullOrWhiteSpace(f.Category) ? "—" : f.Category)
                    .FontSize(7.5f).FontColor("#555555");
                if (framework)
                    table.Cell().Background(bg).PaddingVertical(3).PaddingRight(4).Text(string.IsNullOrWhiteSpace(f.Framework) ? "—" : f.Framework)
                        .FontSize(7.5f).FontColor("#555555");
                if (serversImpacted)
                    table.Cell().Background(bg).PaddingVertical(3).PaddingRight(2).Text($"{f.ServersImpacted} of {f.ServersTotal}").FontSize(7.5f).FontColor("#555555");
                else
                {
                    var body = showRemediation
                        ? (string.IsNullOrWhiteSpace(f.Remediation) ? f.Message : f.Remediation)
                        : (businessVoice && !string.IsNullOrWhiteSpace(f.BusinessImpact) ? f.BusinessImpact : f.Message);
                    table.Cell().Background(bg).PaddingVertical(3).PaddingRight(2).Text(Clamp(body ?? "", 400)).FontSize(7.5f).FontColor("#555555");
                }
            }
        });

    /// <summary>Estate appendix table: one row per server with its failed-check ids.</summary>
    private static void EstateAppendixTable(IContainer c, List<EstateServerEntry> rows) =>
        c.Table(table =>
        {
            table.ColumnsDefinition(d =>
            {
                d.RelativeColumn(2);   // server
                d.ConstantColumn(48);  // failed count
                d.RelativeColumn(6);   // check ids
            });
            table.Header(h =>
            {
                foreach (var head in new[] { "Server", "Failed", "Check IDs" })
                    h.Cell().BorderBottom(1).BorderColor(Muted).PaddingVertical(3).PaddingRight(4)
                        .Text(head).FontSize(7.5f).Bold().FontColor(Muted);
            });
            var alt = false;
            foreach (var s in rows)
            {
                var bg = alt ? "#fafafa" : "#ffffff"; alt = !alt;
                table.Cell().Background(bg).PaddingVertical(3).PaddingLeft(2).PaddingRight(4).Text(s.Server).FontSize(7.5f).FontColor("#333333");
                table.Cell().Background(bg).PaddingVertical(3).Text(s.FindingCount.ToString()).FontSize(7.5f).FontColor("#555555");
                table.Cell().Background(bg).PaddingVertical(3).PaddingRight(2).Text(Clamp(string.Join(", ", s.CheckIds), 600)).FontSize(7.5f).FontColor("#555555");
            }
        });
}

public sealed class ComplianceFamilyRow
{
    public string Id = "";
    public string Name = "";
    public double Percent;
    public string Status = "";   // Compliant | PartiallyCompliant | NonCompliant | NoData
}

public sealed class ComplianceReport
{
    public AssessmentMeta Meta = new();
    public double OverallPercent;   // -1 = outside scope
    public int Compliant, Partial, NonCompliant, OutsideScope;
    public List<ComplianceFamilyRow> Families = new();
}

// ── Executive Briefing DTO ──────────────────────────────────────────────
public sealed class BriefingReport
{
    public AssessmentMeta Meta = new();
    public List<FindingRow> Findings = new();
}

// ── Report Bundle DTOs ──────────────────────────────────────────────────
public sealed class BundleFinding
{
    public string Id = "";
    public string Severity = "";
    public string Name = "";
    public string Category = "";
    public string Message = "";
    /// <summary>Client-facing business-impact prose (corpus '## Business Impact'). Empty when unmapped.
    /// Business-audience bundles render this; technical bundles render <see cref="Message"/>.</summary>
    public string BusinessImpact = "";
    public string Remediation = "";
    public string Framework = "";
    public string Status = "";
    /// <summary>Finding provenance: "Microsoft VA" or "Corpus". Lets a merged report show source per row.</summary>
    public string Source = "Microsoft VA";
    /// <summary>Estate roll-up only: distinct servers this check impacts. 0 for single-server reports.</summary>
    public int ServersImpacted;
    /// <summary>Estate roll-up only: total distinct servers in the scanned set (the denominator).</summary>
    public int ServersTotal;
}

/// <summary>One server's contribution to an estate roll-up appendix: server name + its impacted check ids.</summary>
public sealed class EstateServerEntry
{
    public string Server = "";
    public int FindingCount;
    public List<string> CheckIds = new();
}

public sealed class ExecutiveSummaryBundle
{
    public AssessmentMeta Meta = new();
    public int Score;               // 0-100 health score
    public string ScoreMessage = "";
    public List<BundleFinding> TopRisks = new();
}

public sealed class DbaHandoffBundle
{
    public AssessmentMeta Meta = new();
    public List<(string Label, string Value)> Inventory = new();
    public List<BundleFinding> AllFindings = new();
    public List<BundleFinding> KnownIssues = new();
    /// <summary>Non-empty only for an estate (All-Servers) roll-up; drives the per-server appendix.</summary>
    public List<EstateServerEntry> EstateAppendix = new();
}

public sealed class AuditEvidenceBundle
{
    public AssessmentMeta Meta = new();
    public List<BundleFinding> Findings = new();
    public int AuditEventCount;
    public string ChainStatus = "";
    /// <summary>Document-integrity hash: over the rendered (enriched) findings. Re-derivable from the visible report.</summary>
    public string Sha256 = "";
    /// <summary>Scan-data hash: over the raw assessment results, independent of corpus enrichment / build profile.</summary>
    public string ScanDataSha256 = "";
}

/// <summary>One risk in the register: a bad=1 failing check, with business-impact framing for a manager/exec.</summary>
public sealed class RiskRegisterRow
{
    public string Id = "";
    public string Severity = "";
    public string Risk = "";          // check display name — "what is wrong"
    public string Category = "";
    public string BusinessImpact = ""; // plain-English "why it matters" (flavour-aware)
    public string Remediation = "";    // "what to do about it"
    public string Status = "Open";     // Open by default; future: Accepted / Remediated
    public string Owner = "";          // accountable owner — report operator name (falls back to OS user)
    public DateTime? ReviewByUtc;      // review-by date = report-generation date + review cadence
    public int ServersImpacted;        // estate mode; 0 for single server
    public int ServersTotal;
}

/// <summary>
/// Risk Register / Risk Acknowledgement bundle. The Register is the living ledger; the
/// Acknowledgement Sheet is a point-in-time snapshot of it plus the accountability-transfer
/// block (manager signs to fund the work OR to formally accept the risk, absolving the DBA).
/// </summary>
public sealed class RiskRegisterBundle
{
    public AssessmentMeta Meta = new();
    public List<RiskRegisterRow> Rows = new();
    public int CriticalCount;
    public int HighCount;
    public int OtherCount;
    /// <summary>True = render the acknowledgement/signature instrument (snapshot). False = living register only.</summary>
    public bool Acknowledgement;
    /// <summary>Acknowledgement tone: true = formal ISO/NIST risk-acceptance language; false = plain "cover the DBA".</summary>
    public bool FormalTone = true;
    /// <summary>Prepared-by line (the DBA). Free text from settings/user; blank renders a fill-in line.</summary>
    public string PreparedBy = "";
}
