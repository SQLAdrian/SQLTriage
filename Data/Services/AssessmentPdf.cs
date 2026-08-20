/* In the name of God, the Merciful, the Compassionate */
/*
 * AssessmentPdf — shared QuestPDF kit for the assessment "vectors" (corpus audit, Microsoft
 * assessment, …). One deterministic, margin-foot­ered, colour-blind-aware findings report so every
 * vector exports with a consistent look (matching the Diagnostics Maturity Roadmap).
 *
 * BuildFindingsReport renders: a title band, summary stat chips, and a findings table (status ·
 * severity · check · category · server · detail), findings-first then passes. Colour-blind mode
 * swaps pass/fail/severity to a Wong-derived palette; the brand accent (#1FA85E green) is decorative.
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

    /// <summary>Optional one-sentence coverage qualifier, printed beside the headline numbers on
    /// every report built from this meta. It exists so a report whose run did NOT cover the whole
    /// catalog cannot read like one that did — the audit page sets it from
    /// <see cref="CategoryRunFilter"/>'s measurement of the run it just executed.
    /// <para>Set it only from a MEASUREMENT of the run being reported, never from an operator's
    /// intent or a default. Empty is the normal case and prints nothing at all, so an unfiltered
    /// report renders exactly as it always has.</para></summary>
    public string CoverageNote = "";
}

// Accepted (F6): a FAIL/WARN the client signed off as acceptable-by-design on that instance —
// neither an open finding nor a pass. Rendered distinctly so the PDF deliverable matches the app.
//
// Warn (ruling #4, 2026-07-20): the check RAN but could not fully assess the target — typically an
// under-privileged caller that could not read every database. Neither a pass (no assertion was
// made) nor an open finding (nothing about the SERVER is wrong). Appended last so existing ordinals
// are unchanged. See CheckClassification.IsWarn for the scoring treatment.
public enum FindingState { Pass, Fail, Error, Skipped, Info, Accepted, Warn }

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

public static partial class AssessmentPdf
{
    // Canonical S-mark brand (DECISIONS 2026-07-09; retired the old coral accent).
    //  Brand     — decorative green for fills/borders/marks and text on a DARK field.
    //  BrandText — a deeper green for coloured TEXT on WHITE (print contrast).
    public const string Brand     = "#1FA85E";
    public const string BrandText = "#15703F";
    public const string Ink   = "#333333";
    public const string Muted = "#888888";
    public const string Faint = "#aaaaaa";
    public const string Line  = "#e5e5e5";

    // ── Brand mark ──────────────────────────────────────────────────────────
    // The real S-mark tile (green-on-ink, 512px), shipped next to the exe via csproj
    // (Assets/brand/sqltriage-mark.png, PreserveNewest) and loaded once. Null when the file is
    // absent → callers fall back to the ⛁ glyph. A missing logo NEVER throws over a report.
    private static readonly byte[]? MarkPng = LoadMarkPng();
    private static byte[]? LoadMarkPng()
    {
        try
        {
            var p = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "brand", "sqltriage-mark.png");
            return System.IO.File.Exists(p) ? System.IO.File.ReadAllBytes(p) : null;
        }
        catch { return null; }
    }

    /// <summary>Square brand tile sized in points: the real S-mark PNG when present, else the ⛁ glyph
    /// (green) as a runtime fallback. Shared by every report builder so the mark stays consistent.</summary>
    public static void MarkTile(IContainer c, float pt)
    {
        if (MarkPng is not null)
            c.Width(pt).Height(pt).Image(MarkPng).FitArea();
        else
            // No fixed height on the glyph: its ~1.2× line box would overflow a rigid pt-tall
            // container and throw. Point size ≈ tile height keeps the visual weight in step.
            c.Text("⛁").FontColor(Brand).FontSize(pt * 0.95f);
    }

    /// <summary>Right-aligned header brand lockup: S-mark tile + "SQLTriage" wordmark (BrandText green
    /// for print contrast on white). Drop-in for the old right-hand "⛁ SQLTriage" header text.</summary>
    public static void HeaderMark(IContainer c, float markPt, float textPt) =>
        c.Row(row =>
        {
            row.RelativeItem();   // spacer → lockup hugs the right edge
            row.AutoItem().AlignMiddle().Element(m => MarkTile(m, markPt));
            row.AutoItem().AlignMiddle().PaddingLeft(4).Text("SQLTriage").Bold().FontColor(BrandText).FontSize(textPt);
        });

    /// <summary>Left footer brand lockup: S-mark tile + "SQLTriage · Compliments of sqldba.org".
    /// The wordmark is BrandText (print contrast); the compliments tail stays muted.</summary>
    public static void FooterMark(IContainer c) =>
        c.Row(row =>
        {
            row.AutoItem().AlignMiddle().Element(m => MarkTile(m, 9));
            row.RelativeItem().AlignMiddle().PaddingLeft(3).Text(t =>
            {
                t.Span("SQLTriage").Bold().FontColor(BrandText).FontSize(7.5f);
                t.Span("  ·  Compliments of sqldba.org").FontColor(Muted).FontSize(7);
            });
        });

    public static string Pass(bool cb) => cb ? "#009E73" : "#16a34a";
    public static string Fail(bool cb) => cb ? "#D55E00" : "#dc2626";
    public static string Warn(bool cb) => cb ? "#E69F00" : "#f59e0b";
    public const  string Info = "#3b82f6";
    /// <summary>F6 accepted-by-design findings — teal, distinct from Pass green / Info blue in both palettes.</summary>
    public const  string AcceptedCol = "#0e7490";

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
        FindingState.Pass     => ("✓", Pass(cb)),
        FindingState.Fail     => ("✗", Fail(cb)),
        FindingState.Error    => ("⛒", Fail(cb)),
        FindingState.Skipped  => ("⤼", Muted),
        FindingState.Accepted => ("✓", AcceptedCol),
        FindingState.Warn     => ("⚠", Warn(cb)),
        _                     => ("ⓘ", Info),
    };

    // Order: errors, failures (by severity weight), warn, info, accepted, skipped, then passes.
    // Warn sits directly under Fail: it is not an open finding, but "we could not assess this"
    // is the most actionable thing on the page after an actual finding, and burying it next to
    // the passes is how a degraded run gets mistaken for a clean one.
    private static int StateRank(FindingState s) => s switch
    {
        FindingState.Error => 0, FindingState.Fail => 1, FindingState.Warn => 2,
        FindingState.Info => 3, FindingState.Accepted => 4, FindingState.Skipped => 5, _ => 6,
    };

    /// <summary>An OPEN finding: not passed, not client-accepted (F6), not Skipped/Info. 2026-07-16:
    /// added the Skipped/Info exclusion — without it a SKIP or INFO row (State never used to be set
    /// to Info before that date; see the FindingState.Info doc comment) counted as "open" and could
    /// show up as a "top finding"/"highest priority recommendation" card, exactly the never-a-finding
    /// invariant this state exists to avoid. Accepted findings are excluded from high/low-priority
    /// counts and "top finding" lists but keep their own slice/label.
    /// Ruling #4 (2026-07-20): Warn excluded too — a check that could not fully assess the target
    /// says nothing about the server's configuration, so it must never surface as a "top finding"
    /// or "highest priority recommendation" in a client deliverable. Mirrors
    /// <see cref="CheckClassification.IsScorable"/> upstream.</summary>
    private static bool IsOpen(FindingRow f) =>
        f.State != FindingState.Pass && f.State != FindingState.Accepted &&
        f.State != FindingState.Skipped && f.State != FindingState.Info &&
        f.State != FindingState.Warn;

    /// <summary>
    /// True for a state that belongs in a pass-rate/"N of M checks" denominator. Excludes
    /// Skipped/Info the same way <see cref="CheckClassification.IsScorable"/> does upstream —
    /// folding either into a rate's denominator (without also counting it in the numerator)
    /// understates the rate for reports that legitimately contain informational/not-applicable
    /// results. Accepted and Error stay in the rate (Accepted rides its own visible slice — see
    /// PrioritySegments; Error stays a visible, distinct outcome, not silently absorbed here).
    /// </summary>
    /// Warn joins that exclusion (ruling #4, 2026-07-20) for the same reason and no other: the
    /// check made no assertion, so folding it into a denominator it can never appear in the
    /// numerator of would understate the rate — reporting a permissions gap as a lower score.
    private static bool IsRated(FindingRow f) =>
        f.State != FindingState.Skipped && f.State != FindingState.Info &&
        f.State != FindingState.Warn;
    private static int SevRank(string? sev) => (sev ?? "").Trim().ToLowerInvariant() switch
    {
        "critical" => 0, "high" => 1, "warning" => 2, "medium" or "moderate" => 3,
        "low" => 4, "info" or "information" or "informational" => 5, _ => 6,
    };

#if !SQLT_NO_REPORT_FINDINGS_PDF
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
                // F6: accepted findings get their own slice/legend line — excluded from the
                // open-priority counts (they're signed off) but never silently folded into pass.
                // 2026-07-16: passed/totalN now come from IsRated (excludes Skipped/Info) — a
                // report containing SKIP/INFO rows used to keep them in totalN's denominator
                // without ever counting them in the numerator, silently deflating "Passed %".
                var rated     = ordered.Where(IsRated).ToList();
                var passed    = rated.Count(f => f.State == FindingState.Pass);
                var acceptedN = rated.Count(f => f.State == FindingState.Accepted);
                var highN  = ordered.Count(f => IsOpen(f) && SevRank(f.Severity) <= 1);
                var lowN   = ordered.Count(IsOpen) - highN;
                var totalN = rated.Count;
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
                if (acceptedN > 0)
                {
                    segs.Add(new DonutSegment { Value = acceptedN, Color = AcceptedCol });
                    legend.Insert(2, ($"{acceptedN} Accepted", AcceptedCol));
                }

                // By-area breakdown (worst pass-rate first) — executive "where are the problems".
                // 2026-07-16: grouped from `rated` (excludes Skipped/Info) — Total used to include
                // them without Passed ever counting them, so "Total − Passed open" overstated the
                // area's open-finding count by however many SKIP/INFO rows it had.
                var byArea = rated
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
#endif

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
                // Coverage qualifier, directly above the pass-rate donut it qualifies. Rendered
                // only when the caller measured a narrower run; empty prints nothing, so an
                // unfiltered report's title band is unchanged.
                if (!string.IsNullOrWhiteSpace(r.Meta.CoverageNote))
                    cc.Item().PaddingTop(3).Text(r.Meta.CoverageNote)
                        .FontSize(8).Italic().FontColor(Warn(r.Meta.ColorBlind));
            });
            row.ConstantItem(120).AlignMiddle().Element(m => HeaderMark(m, 15, 13));
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
                    FindingState.Error => "Error", FindingState.Skipped => "Skip",
                    FindingState.Accepted => "Accepted",
                    // "Partial", not "Warning": the check ran and found nothing wrong with the
                    // server — it just could not see all of it. "Warning" would read to a client
                    // as a mild finding, which is precisely the misreading ruling #4 exists to stop.
                    FindingState.Warn => "Partial",
                    _ => "Info",
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
            .FontSize(58).Bold().FontColor(m.ColorBlind ? "#f2dcc6" : "#CBEAD8");

    // Shared page footer: brand mark · centre meta line · page x of y.
    private static void StandardFooter(IContainer c, string footerMeta) =>
        c.BorderTop(1.5f).BorderColor(Brand).PaddingTop(4).Row(row =>
        {
            row.RelativeItem().Element(FooterMark);
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

    /// <summary>
    /// Render a corpus Markdown body as plain text for a PDF cell, then clamp it.
    /// </summary>
    /// <remarks>
    /// 2026-07-22. Corpus bodies are Markdown and were passed straight to QuestPDF, which has no
    /// Markdown support, so client deliverables showed literal "**823, 824, and 825**", backticked
    /// identifiers and a stray "### Why this matters". Markdig's plain-text renderer is the same one
    /// the Blazor surfaces already use (Playbooks.razor:341, CioDashboard.razor:1649).
    /// Corpus source is hard-wrapped at ~100 columns, so the wrap is collapsed inside each paragraph
    /// and the cell reflows to its own width; blank-line paragraph breaks are kept.
    /// </remarks>
    private static string PlainClamp(string? s, int max) => Clamp(MarkdownToPlain(s), max);

    /// <summary>Flatten corpus Markdown to plain text, keeping sub-headings on their own line.</summary>
    internal static string MarkdownToPlain(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";

        // Markdig flattens a sub-heading inline, which glues it onto the sentence that follows:
        // "...Oracle: sp_Blitz CheckID 158. Why this matters A 1 MB growth increment...". 189 corpus
        // Intents carry a "### " heading (215 occurrences in all) -- 186 "Why this matters", 14 "Cost", and a handful that are load-bearing
        // ("Caveat", "Heuristic limits (read before acting)"), so they are promoted rather than dropped.
        var normalised = System.Text.RegularExpressions.Regex.Replace(
            s, @"(?m)^[ \t]*#{1,6}[ \t]+(.+?)[ \t]*$",
            m =>
            {
                var head = m.Groups[1].Value.TrimEnd();
                var suffix = head.EndsWith(":") || head.EndsWith(".") ? "" : ":";
                return "\n" + head + suffix + "\n";
            });

        var plain = Markdig.Markdown.ToPlainText(normalised);

        // Corpus source is hard-wrapped at ~100 columns. Collapse the wrap inside each paragraph so the
        // cell reflows to its own width; keep blank-line paragraph breaks.
        var paragraphs = System.Text.RegularExpressions.Regex.Split(plain, @"\r?\n\s*\r?\n")
            .Select(p => System.Text.RegularExpressions.Regex.Replace(p, @"\s+", " ").Trim())
            .Where(p => p.Length > 0);
        return string.Join("\n", paragraphs);
    }

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
    // Gated per Adrian's 2026-07-21 ruling ("three keepers only") — was briefly a fourth keeper.
    // NB: ComplianceStat is OUTSIDE this fence on purpose: the HaDr posture report shares it,
    // and a profile with hadr-posture "on" + compliance-report "off" must still compile.
#if !SQLT_NO_REPORT_COMPLIANCE_REPORT
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
                        // Not-tested controls (#87): un-scored, kept out of the % but shown so the
                        // donut still sums to the full control count. Distinct grey (lightness, not hue).
                        new() { Value = r.NotTested,    Color = "#9aa0a6" },
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
                            if (r.NotTested > 0)
                                ComplianceStat(inner, r.NotTested.ToString(), "NOT TESTED", "#9aa0a6");
                        });
                    });

                    // Methodology note (#gaps-3): rendered UNCONDITIONALLY on every compliance report so an
                    // exported "X% Compliant" headline never travels without its mapping basis and exclusion
                    // rule. Mirrors the web caveat at ComplianceMap.razor:31; previously the only such note was
                    // gated behind `notTested.Count > 0`, so a report with no not-tested controls disclosed nothing.
                    col.Item().PaddingTop(2).Text("Findings are scored against industry frameworks via category-level control mapping. The compliance score is computed over tested controls only — Outside-Scope and Not-Tested controls are excluded from the percentage, so the score never claims coverage SQLTriage does not have.")
                        .FontSize(8).Italic().FontColor(Muted);

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
                            table.Cell().Background(bg).PaddingVertical(3).Text(f.Percent < 0 ? "—" : $"{f.Percent:F0}%").FontSize(8).FontColor("#444444");
                            table.Cell().Background(bg).PaddingVertical(3).Text(label).FontSize(7.5f).Bold().FontColor(scol);
                        }
                    });

                    // ── Not-tested controls (#87): explicit honesty note so an auditor can see the
                    // score deliberately excludes controls SQLTriage does not test. ──
                    var notTested = r.Families.Where(f => f.NotTested).ToList();
                    if (notTested.Count > 0)
                    {
                        col.Item().PaddingTop(4).Text("Controls Not Tested by SQLTriage").FontSize(12).Bold().FontColor(BrandText);
                        col.Item().Text("Shown for completeness only. SQLTriage does not test these controls, so they are un-scored and excluded from the compliance score above — the score never claims coverage the tool does not have.")
                            .FontSize(8).Italic().FontColor(Muted);
                        foreach (var f in notTested)
                        {
                            var reason = string.IsNullOrWhiteSpace(f.NotTestedReason) ? "Not tested by SQLTriage." : f.NotTestedReason;
                            col.Item().Text(t =>
                            {
                                t.Span($"{f.Id} — {f.Name}: ").FontSize(8).Bold().FontColor("#555555");
                                t.Span(reason).FontSize(8).FontColor("#555555");
                            });
                        }
                    }

                    // ── Complete failing findings by control (#87): the exported evidence carries EVERY
                    // failed finding per control family, not a capped sample. ──
                    var failingFamilies = r.Families.Where(f => f.Findings.Count > 0).OrderBy(f => f.Id, StringComparer.OrdinalIgnoreCase).ToList();
                    if (failingFamilies.Count > 0)
                    {
                        col.Item().PaddingTop(4).Text("Failing Findings by Control").FontSize(12).Bold().FontColor(BrandText);
                        foreach (var f in failingFamilies)
                        {
                            col.Item().PaddingTop(2).Text($"{f.Id} — {f.Name}  ({f.Findings.Count} finding{(f.Findings.Count == 1 ? "" : "s")})")
                                .FontSize(9).Bold().FontColor("#333333");
                            col.Item().Element(x => ComplianceFindingsTable(x, f.Findings, cb));
                        }
                    }
                });
            });
        }).GeneratePdf();
    }

    /// <summary>Complete (uncapped) failing-findings table for one compliance control family (#87).</summary>
    private static void ComplianceFindingsTable(IContainer c, List<ComplianceFindingRow> rows, bool cb) =>
        c.Table(table =>
        {
            table.ColumnsDefinition(d =>
            {
                d.ConstantColumn(74);   // check id
                d.ConstantColumn(58);   // severity
                d.RelativeColumn(2);    // check name
                d.RelativeColumn(3);    // message
                d.ConstantColumn(96);   // server
            });
            table.Header(h =>
            {
                foreach (var head in new[] { "Check ID", "Severity", "Check", "Message", "Server" })
                    h.Cell().BorderBottom(1).BorderColor(Muted).PaddingVertical(3).PaddingRight(4)
                        .Text(head).FontSize(7.5f).Bold().FontColor(Muted);
            });
            var alt = false;
            foreach (var f in rows)
            {
                var bg = alt ? "#fafafa" : "#ffffff"; alt = !alt;
                table.Cell().Background(bg).PaddingVertical(3).PaddingLeft(2).PaddingRight(4).Text(Clamp(f.CheckId, 28)).FontSize(7.5f).FontColor("#555555");
                table.Cell().Background(bg).PaddingVertical(3).Text(string.IsNullOrWhiteSpace(f.Severity) ? "—" : f.Severity)
                    .FontSize(7.5f).Bold().FontColor(Sev(f.Severity, cb));
                table.Cell().Background(bg).PaddingVertical(3).PaddingRight(6).Text(f.Name).FontSize(8).FontColor("#333333");
                table.Cell().Background(bg).PaddingVertical(3).PaddingRight(4).Text(Clamp(f.Message ?? "", 400)).FontSize(7.5f).FontColor("#555555");
                table.Cell().Background(bg).PaddingVertical(3).PaddingRight(2).Text(string.IsNullOrWhiteSpace(f.Server) ? "—" : f.Server).FontSize(7.5f).FontColor("#555555");
            }
        });
#endif

    private static void ComplianceStat(RowDescriptor row, string value, string label, string colour) =>
        row.RelativeItem().Border(1).BorderColor(Line).Padding(8).Column(cc =>
        {
            cc.Item().AlignCenter().Text(value).FontSize(18).Bold().FontColor(colour);
            cc.Item().AlignCenter().Text(label).FontSize(7.5f).FontColor(Muted);
        });

#if !SQLT_NO_REPORT_COMPLIANCE_REPORT
    private static (string, string) StatusLabel(string status, bool cb) => status switch
    {
        "Compliant"            => ("Compliant", Pass(cb)),
        "PartiallyCompliant"   => ("Partial", Warn(cb)),
        "NonCompliant"         => ("Non-compliant", Fail(cb)),
        "NotTested"            => ("Not currently tested", Muted),
        _                       => ("Outside scope", Muted),
    };
#endif

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

    /// <summary>
    /// Ruling #4 (2026-07-20), Adrian: SURFACE IT. WARN is correctly out of every rate here
    /// (<see cref="IsRated"/>) and out of every open-finding list (<see cref="IsOpen"/>) — but the
    /// briefing renderer is aggregate-only, so a WARN row that is in neither simply vanished. The
    /// cold gate reproduced it: three checks ran, the document said "2 checks evaluated" and named
    /// the warn check nowhere. A run that could not see the server then reads identically to a run
    /// that could — a false clean assembled out of individually-correct exclusions.
    ///
    /// So the count gets its own explicit line. Returns null when nothing was unassessable, so a
    /// clean run carries no noise; callers render only on non-null.
    ///
    /// Wording note: "could not be fully assessed", never "warnings". A client reading "2 warnings"
    /// hears two mild findings; the whole point of this state is that NO finding was established.
    /// </summary>
    internal static string? UnassessedNote(IEnumerable<FindingRow> rows)
    {
        var n = rows.Count(f => f.State == FindingState.Warn);
        if (n == 0) return null;
        return n == 1
            ? "1 check could not be fully assessed — it is excluded from the pass rate above."
            : $"{n} checks could not be fully assessed — they are excluded from the pass rate above.";
    }

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
        var highN = rows.Count(f => IsOpen(f) && SevRank(f.Severity) <= 1);
        var lowN  = rows.Count(IsOpen) - highN;
        var segs = new List<DonutSegment>
        {
            new() { Value = passed, Color = Pass(cb) },
            new() { Value = lowN,   Color = Info },
            new() { Value = highN,  Color = Fail(cb) },
        };
        // F6: accepted slice keeps the donut summing to the row count without inflating pass.
        var acceptedN = rows.Count(f => f.State == FindingState.Accepted);
        if (acceptedN > 0) segs.Add(new DonutSegment { Value = acceptedN, Color = AcceptedCol });
        return segs;
    }

#if !SQLT_NO_REPORT_EXEC_BRIEFING
    public static byte[] BuildExecutiveBriefing(BriefingReport r)
    {
        var cb = r.Meta.ColorBlind;

        var grouped = FocusAreas.ToDictionary(a => a, _ => new List<FindingRow>());
        foreach (var f in r.Findings)
            grouped[FocusAreaFor(f.Category)].Add(f);

        // 2026-07-16: Total/Passed/Pct now computed over IsRated (excludes Skipped/Info) — the
        // raw `list`/`r.Findings` (all states) is kept on AreaStat.Findings and for PrioritySegments/
        // IsOpen below, which already exclude Skipped/Info themselves; only the numeric summary
        // (used for "N of M checks passed" narrative text) needs the rated subset, or a SKIP/INFO
        // row sat in Total's denominator forever without ever landing in Passed's numerator.
        var areaStats = FocusAreas
            .Select(a =>
            {
                var list  = grouped[a];
                var rated = list.Where(IsRated).ToList();
                var passed = rated.Count(x => x.State == FindingState.Pass);
                return new AreaStat(a, list, passed, rated.Count, rated.Count > 0 ? 100.0 * passed / rated.Count : -1);
            })
            .Where(s => s.Total > 0)
            .ToList();

        var ratedFindings = r.Findings.Where(IsRated).ToList();
        var overallTotal = ratedFindings.Count;
        var overallPass  = ratedFindings.Count(f => f.State == FindingState.Pass);
        var overallPct   = overallTotal > 0 ? 100.0 * overallPass / overallTotal : 0;
        var overallSegs  = PrioritySegments(r.Findings, overallPass, cb);
        var overallUnassessed = UnassessedNote(r.Findings);

        var ranked = areaStats.OrderByDescending(s => s.Pct).ToList();
        var best   = ranked.FirstOrDefault();
        var worst  = ranked.LastOrDefault();
        var topFinding = r.Findings
            .Where(IsOpen)   // F6: an accepted finding is never the headline "top finding"
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
                page.Content().Background("#0F1513").PaddingVertical(48).PaddingHorizontal(60).Column(col =>
                {
                    col.Spacing(8);
                    col.Item().AlignCenter().Element(m => MarkTile(m, 40));
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
                    // Same argument as the degraded-run line below: the cover donut and the "N of M
                    // checks passed" line above it are what every reader takes away, so a run that
                    // deliberately skipped part of the catalog says so HERE, not on an interior page.
                    if (!string.IsNullOrWhiteSpace(r.Meta.CoverageNote))
                        col.Item().PaddingTop(4).AlignCenter().Text(r.Meta.CoverageNote)
                            .FontColor(Warn(cb)).FontSize(10);
                    // Ruling #4: the degraded-run line rides the COVER, not just an interior page —
                    // the cover donut is the one thing every reader sees, and it is the number that
                    // would otherwise be mistaken for a complete assessment.
                    if (overallUnassessed != null)
                        col.Item().PaddingTop(4).AlignCenter().Text(overallUnassessed)
                            .FontColor(Warn(cb)).FontSize(10);
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
                    // The summary page repeats the pass-rate donut, so it repeats the qualifier.
                    if (!string.IsNullOrWhiteSpace(r.Meta.CoverageNote))
                        col.Item().Text(r.Meta.CoverageNote).FontSize(9).Italic().FontColor(Warn(cb));
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
                            // Ruling #4: its own titled block, at the same weight as the other
                            // three. Folding it into a footnote is how the gate's "2 checks
                            // evaluated" for a 3-check run happened in the first place.
                            if (overallUnassessed != null)
                                NarrativeBlock(n, "What could not be assessed",
                                    overallUnassessed + " Re-run with an account that can read the "
                                    + "whole instance to close the gap.", Warn(cb));
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
                    .Where(IsOpen)   // F6: accepted findings are not open recommendations
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
                                // Ruling #4: per-area too. "N checks evaluated" is a count of the
                                // RATED subset, so on this page specifically the number is smaller
                                // than the number of checks that actually ran — state the shortfall
                                // beside it rather than leaving the reader to assume there was none.
                                var areaUnassessed = UnassessedNote(s.Findings);
                                if (areaUnassessed != null)
                                    cc.Item().PaddingTop(2).Text(areaUnassessed).FontSize(9).FontColor(Warn(cb));
                                var highN = recs.Count(f => SevRank(f.Severity) <= 1);
                                if (highN > 0)
                                    cc.Item().PaddingTop(2).Text($"{highN} high-priority finding(s) need attention").FontSize(9).FontColor(Fail(cb));
                            });
                        });
                        col.Item().PaddingTop(2).Text("Highest Priority Recommendations").FontSize(12).Bold().FontColor(BrandText);
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
#endif

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
            row.ConstantItem(120).AlignMiddle().Element(m => HeaderMark(m, 15, 13));
        });

    // ══════════════════════════════════════════════════════════════════════
    //  CIO Executive Report — QuestPDF replacement for the WebView2 DOM-screenshot
    //  export off /cio (2026-07-13). Same palette/Donut/StandardFooter as every other
    //  report; every field on the DTO is a pass-through of what CioDashboard.razor
    //  already computed and rendered — see CioDashboard.razor's BuildCioReportDto().
    // ══════════════════════════════════════════════════════════════════════
    // Adrian's rulings (2026-07-13): (1) Governance is a subordinate TEXT ROW + swatch
    // under Executive Health, never a second donut; (2) the trend renders as a compact
    // table, not a bar chart; (3) the report title is fixed to "CIO Executive Report"
    // (set by the caller's AssessmentMeta.Title).
#if !SQLT_NO_REPORT_CIO_EXEC
    public static byte[] BuildCioExecutiveReport(CioExecutiveReport r)
    {
        var cb = r.Meta.ColorBlind;

        return Document.Create(c => c.Page(page =>
        {
            SetupPage(page, r.Meta, false);
            page.Content().PaddingVertical(6).Column(col =>
            {
                col.Spacing(14);
                col.Item().Element(x => BundleTitleBand(x, r.Meta, "Executive overview"));

                // Executive Health — the SOLE headline (ruling 2026-07-11 + Adrian's
                // dual-band ruling: EH never shares top billing with Governance).
                if (r.EhAssessed)
                {
                    var ehColor = EhSeverityColor(r.EhBand, cb);
                    var ehSegs = new List<DonutSegment>
                    {
                        new() { Value = r.EhScore, Color = ehColor },
                        new() { Value = Math.Max(0, 100 - r.EhScore), Color = "#eeeeee" },
                    };
                    col.Item().Row(row =>
                    {
                        row.Spacing(18);
                        row.ConstantItem(150).AlignMiddle().Element(d => Donut(d, ehSegs, r.EhScore.ToString(), r.EhBand, ehColor, 140));
                        row.RelativeItem().AlignMiddle().Column(cc =>
                        {
                            cc.Item().Text("Estate Executive Health").FontSize(13).Bold().FontColor("#333333");
                            if (!string.IsNullOrWhiteSpace(r.EhBasisLine))
                                cc.Item().PaddingTop(4).Text(r.EhBasisLine).FontSize(8.5f).FontColor("#666666");
                            if (!string.IsNullOrWhiteSpace(r.EhFeedLine))
                                cc.Item().PaddingTop(4).Text(r.EhFeedLine).FontSize(8.5f).FontColor("#666666");
                            if (!string.IsNullOrWhiteSpace(r.EhDriverLine))
                                cc.Item().PaddingTop(1).Text(r.EhDriverLine).FontSize(8.5f).Italic().FontColor("#777777");
                        });
                    });
                }
                else
                {
                    // 2026-08-05: this used to print one fixed sentence for every state that is
                    // not "assessed", so a client whose servers were all unreachable read "not yet
                    // assessed" and a remedy naming Vulnerability Assessment, which has not fed
                    // Executive Health since the 2026-07-16 ruling. The sentence now comes from the
                    // same policy the screen uses, conditioned on the states actually counted.
                    col.Item().Background("#f7f7fb").Border(1).BorderColor(Line).Padding(10)
                        .Text("Estate Executive Health — " + (string.IsNullOrWhiteSpace(r.EhBasisLine)
                            ? "nothing has been assessed yet, so no score is shown."
                            : r.EhBasisLine))
                        .FontSize(9.5f).Italic().FontColor(Muted);
                }

                // Governance — a SUBORDINATE text row + colour swatch, never a second donut.
                if (r.GovAssessed)
                {
                    var govColor = CioMedalColor(r.GovBand);
                    col.Item().Row(row =>
                    {
                        row.ConstantItem(14).AlignMiddle().Height(10).Background(govColor);
                        row.RelativeItem().PaddingLeft(6).Column(cc =>
                        {
                            cc.Item().Text($"Governance — a component of Executive Health: {r.GovScore:F0} / {r.GovBand}")
                                .FontSize(10.5f).Bold().FontColor("#333333");
                            if (!string.IsNullOrWhiteSpace(r.GovSubline))
                                cc.Item().Text(r.GovSubline).FontSize(8).FontColor(Muted);
                            if (!string.IsNullOrWhiteSpace(r.GovCheckCountLabel))
                                cc.Item().Text(r.GovCheckCountLabel).FontSize(7.5f).FontColor(Faint);
                        });
                    });
                }

                // Per-server heatmap — same six columns as the on-screen Server Risk Heatmap.
                if (r.ServerRows.Count > 0)
                {
                    col.Item().Text("Server Risk Heatmap").FontSize(13).Bold().FontColor(BrandText);
                    col.Item().Element(x => CioHeatmapTable(x, r.ServerRows, cb));
                }

                // Top high-priority findings — reuses the Executive Briefing's own
                // finding-card renderer (RecommendationCard) so the visual language matches.
                col.Item().Text("Top High-Priority Findings").FontSize(13).Bold().FontColor(BrandText);
                if (r.TopFindings.Count == 0)
                    col.Item().Text("No open high-priority findings.").FontSize(9.5f).Italic().FontColor(Pass(cb));
                else
                    foreach (var f in r.TopFindings)
                        col.Item().Element(x => RecommendationCard(x, new FindingRow
                        {
                            State = FindingState.Fail,
                            Name = f.Name,
                            Severity = f.Severity,
                            Server = f.Server,
                            Recommendation = f.Recommendation,
                        }, cb));

                // Power Savings section REMOVED 2026-07-22 (Adrian). It emitted "Annual cost, potentially
                // avoidable: $X-$Y" and a CO2e figure into a CIO deliverable. The code labelled it a
                // modelled estimate, but on the page it read as a measured saving a CIO could budget
                // against -- and it is derived, not observed. The report no longer carries it.
                // r.Power is still populated upstream and still drives the on-screen CIO dashboard card.

                // Governance Trend — compact table (Adrian's ruling: table, not a bar chart).
                if (r.TrendRows.Count > 1)
                {
                    col.Item().Text("Governance Trend").FontSize(13).Bold().FontColor(BrandText);
                    col.Item().Element(x => CioTrendTable(x, r.TrendRows));
                }
            });
        })).GeneratePdf();
    }
#endif

    private static string EhSeverityColor(string? severity, bool cb) => (severity ?? "").Trim().ToLowerInvariant() switch
    {
        "healthy"  => Pass(cb),
        "warning"  => Warn(cb),
        "critical" => Fail(cb),
        _          => Muted,
    };

    /// <summary>Governance medal-band colour ramp — mirrors CioDashboard.razor's own
    /// BandColor() so the PDF swatch/chips match the on-screen heatmap exactly.</summary>
    private static string CioMedalColor(string? band) => (band ?? "").Trim().ToLowerInvariant() switch
    {
        "platinum" => "#e3e9f2",
        "gold"     => "#e7b53c",
        "silver"   => "#c4ccd8",
        "bronze"   => "#c87f43",
        _          => "#9aa1ad",
    };

    private static void CioChip(IContainer c, string text, string color) =>
        c.Background(color).PaddingVertical(2).PaddingHorizontal(4).AlignCenter()
            .Text(text).FontSize(7.5f).Bold().FontColor("#0b0e14");

    private static void CioHeatmapTable(IContainer c, List<CioServerRow> rows, bool cb) =>
        c.Table(table =>
        {
            table.ColumnsDefinition(d =>
            {
                d.RelativeColumn(2);   // server
                d.ConstantColumn(64);  // connection
                d.ConstantColumn(84);  // exec health
                d.ConstantColumn(84);  // governance
                d.ConstantColumn(46);  // open
                d.ConstantColumn(46);  // critical
            });

            table.Header(h =>
            {
                foreach (var head in new[] { "Server", "Connection", "Exec Health", "Governance", "Open", "Critical" })
                    h.Cell().BorderBottom(1).BorderColor(Muted).PaddingVertical(3).PaddingRight(4)
                        .Text(head).FontSize(7.5f).Bold().FontColor(Muted);
            });

            var alt = false;
            foreach (var r in rows)
            {
                var bg = alt ? "#fafafa" : "#ffffff"; alt = !alt;
                table.Cell().Background(bg).PaddingVertical(3).PaddingLeft(2).PaddingRight(4).Text(r.Server).FontSize(8).FontColor("#333333");
                table.Cell().Background(bg).PaddingVertical(3).AlignCenter().Text(r.Connection)
                    .FontSize(7.5f).Bold().FontColor(r.Connection == "Online" ? Pass(cb) : r.Connection == "Offline" ? Fail(cb) : Muted);
                if (r.EhAssessed)
                    table.Cell().Background(bg).PaddingVertical(2).PaddingRight(2).Element(x => CioChip(x, $"{r.EhScore} · {r.EhBand}", EhSeverityColor(r.EhBand, cb)));
                else
                    table.Cell().Background(bg).PaddingVertical(3).AlignCenter().Text("—").FontSize(8).FontColor(Muted);
                table.Cell().Background(bg).PaddingVertical(2).PaddingRight(2).Element(x => CioChip(x, $"{r.GovScore:F0} · {r.GovBand}", CioMedalColor(r.GovBand)));
                table.Cell().Background(bg).PaddingVertical(2).PaddingRight(2).Element(x => CioChip(x, r.Open.ToString(), r.Open == 0 ? Pass(cb) : Warn(cb)));
                table.Cell().Background(bg).PaddingVertical(2).PaddingRight(2).Element(x => CioChip(x, r.Critical.ToString(), r.Critical == 0 ? Pass(cb) : Fail(cb)));
            }
        });

    // Bucketing fix (2026-07-13): the trend rows are now compact day/week buckets (Adrian's
    // ruling: table, not a bar chart) — Period | Min | Max | Band, matching the on-screen
    // chart's bucketing exactly so the PDF and the page can never disagree.
    private static void CioTrendTable(IContainer c, List<CioTrendRow> rows) =>
        c.Table(table =>
        {
            table.ColumnsDefinition(d => { d.RelativeColumn(2); d.ConstantColumn(50); d.ConstantColumn(50); d.ConstantColumn(84); });
            table.Header(h =>
            {
                foreach (var head in new[] { "Period", "Min", "Max", "Band" })
                    h.Cell().BorderBottom(1).BorderColor(Muted).PaddingVertical(3).PaddingRight(4)
                        .Text(head).FontSize(7.5f).Bold().FontColor(Muted);
            });
            var alt = false;
            foreach (var r in rows)
            {
                var bg = alt ? "#fafafa" : "#ffffff"; alt = !alt;
                table.Cell().Background(bg).PaddingVertical(3).PaddingLeft(2).Text(r.Period).FontSize(8).FontColor("#333333");
                table.Cell().Background(bg).PaddingVertical(3).Text(r.Min.ToString("F1")).FontSize(8).FontColor("#555555");
                // Medal-band colour (matches the heatmap's Governance chips and CioDashboard.razor's
                // own trend-bar colouring, which is band-based — NOT this class's health-percentage
                // Pass/Warn/Fail ramp, which would misclassify a 74/Gold row as "amber warning".
                table.Cell().Background(bg).PaddingVertical(3).Text(r.Max.ToString("F1")).FontSize(8).Bold().FontColor(CioMedalColor(r.Band));
                table.Cell().Background(bg).PaddingVertical(3).Text(r.Band).FontSize(8).FontColor("#555555");
            }
        });

    // ══════════════════════════════════════════════════════════════════════
    //  Report Bundles — QuestPDF rebuild of the 3 diagnostic packages
    // ══════════════════════════════════════════════════════════════════════
    // Replaces the crashing browser-print path. Portrait, document-style reports
    // that reuse the shared palette / donut / footer.

#if !SQLT_NO_REPORT_EXEC_SUMMARY
    public static byte[] BuildExecutiveSummaryBundle(ExecutiveSummaryBundle b)
    {
        var cb = b.Meta.ColorBlind;
        var execEstate = b.TopRisks.Any(r => r.ServersTotal > 0);
        // C4 (2026-08-05): the donut used to render whatever int the DTO carried, and the health
        // model holds 0 in every state where nothing was measured, so a client whose server had
        // gone silent got a full red 0/100 ring on the cover of a deliverable. A ring is a
        // verdict. It is drawn only when a measurement produced it, and the colour and the
        // segments are computed inside that branch so no expression here touches the placeholder.
        var scoreAssessed = b.ScoreAssessed;
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
                else if (!scoreAssessed)
                {
                    col.Item().Background("#f7f7fb").Border(1).BorderColor(Line).Padding(10).Column(cc =>
                    {
                        cc.Item().Text("Overall Health Score").FontSize(13).Bold().FontColor("#333333");
                        cc.Item().PaddingTop(4).Text(string.IsNullOrWhiteSpace(b.ScoreBasis)
                            ? "No score is shown because nothing has been measured for this server."
                            : b.ScoreBasis).FontSize(10).Italic().FontColor(Muted);
                    });
                }
                else
                {
                    var scoreCol = ScoreColor(b.Score, cb);
                    var segs = new List<DonutSegment>
                    {
                        new() { Value = b.Score,                 Color = scoreCol },
                        new() { Value = Math.Max(0, 100 - b.Score), Color = "#eeeeee" },
                    };
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
                col.Item().Text(execEstate ? "Top 5 Risks Across the Estate" : "Top 5 Risks").FontSize(13).Bold().FontColor(BrandText);
                if (b.TopRisks.Count == 0)
                    col.Item().Text("No cached vulnerability findings — run a Vulnerability Assessment first.").FontSize(10).Italic().FontColor(Muted);
                else
                    col.Item().Element(x => BundleFindingsTable(x, b.TopRisks, cb, framework: false, showRemediation: false, serversImpacted: execEstate, businessVoice: true));
                // 2026-07-22: dropped "Report period: last 30 days." No time filter is applied anywhere on
                // this path -- the findings are the latest cached assessment -- so the sentence asserted a
                // reporting window the code does not implement.
                col.Item().PaddingTop(4).Text("Reflects the most recent completed assessment. Suitable for board reports and management briefings.")
                    .FontSize(8.5f).Italic().FontColor(Muted);
            });
        })).GeneratePdf();
    }
#endif

#if !SQLT_NO_REPORT_DBA_HANDOFF
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

                col.Item().Text("Server Inventory").FontSize(13).Bold().FontColor(BrandText);
                if (b.Inventory.Count == 0)
                    col.Item().Text("No health data cached. Visit the Health page first.").FontSize(10).Italic().FontColor(Muted);
                else
                    col.Item().Element(x => KeyValueTable(x, b.Inventory));

                var dbaEstate = b.EstateAppendix.Count > 0;
                // 2026-07-22: was "All Vulnerability Assessment Findings". The 2026-07-16 honesty ruling made
                // this bundle corpus-only; the HTML twin was updated to "All Diagnostic Findings" and this PDF
                // heading was missed, so the deliverable still claimed a VA scope it no longer has.
                col.Item().Text(dbaEstate ? "Findings Across the Estate (grouped by check)" : "All Diagnostic Findings").FontSize(13).Bold().FontColor(BrandText);
                if (b.AllFindings.Count == 0)
                    col.Item().Text("No cached findings. Run a Vulnerability Assessment first.").FontSize(10).Italic().FontColor(Muted);
                else
                    col.Item().Element(x => BundleFindingsTable(x, b.AllFindings, cb, framework: false, showRemediation: false, serversImpacted: dbaEstate));

                if (dbaEstate)
                {
                    col.Item().Text("Appendix — Failed Checks by Server").FontSize(13).Bold().FontColor(BrandText);
                    col.Item().Element(x => EstateAppendixTable(x, b.EstateAppendix));
                }
                else
                {
                    col.Item().Text("Known Issues (Failed Checks)").FontSize(13).Bold().FontColor(BrandText);
                    if (b.KnownIssues.Count == 0)
                        col.Item().Text("No failed checks in cached findings.").FontSize(10).Italic().FontColor(Pass(cb));
                    else
                        col.Item().Element(x => BundleFindingsTable(x, b.KnownIssues, cb, framework: false, showRemediation: true));
                }
            });
        })).GeneratePdf();
    }
#endif

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

                col.Item().Text("Vulnerability Assessment Findings").FontSize(13).Bold().FontColor(BrandText);
                if (b.Findings.Count == 0)
                    col.Item().Text("No cached findings. Run a Vulnerability Assessment first.").FontSize(10).Italic().FontColor(Muted);
                else
                    col.Item().Element(x => BundleFindingsTable(x, b.Findings, cb, framework: true, showRemediation: false));

                col.Item().Text("Audit Log Summary (Last 30 Days)").FontSize(13).Bold().FontColor(BrandText);
                col.Item().Element(x => KeyValueTable(x, new List<(string, string)>
                {
                    ("Total Audit Events", b.AuditEventCount.ToString("N0")),
                    ("HMAC Chain Status",  b.ChainStatus),
                }));

                col.Item().Text("Report Integrity").FontSize(13).Bold().FontColor(BrandText);
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

                col.Item().Text("Risk Register").FontSize(13).Bold().FontColor(BrandText);
                if (b.Rows.Count == 0)
                    col.Item().Text("No outstanding bad-state risks. Run a Vulnerability Assessment first, or the estate is clean.")
                        .FontSize(10).Italic().FontColor(Pass(cb));
                else
                    col.Item().Element(x => RiskRegisterTable(x, b.Rows, cb, estate));

                if (b.Acknowledgement)
                {
                    col.Item().PaddingTop(6).Text("Risk Acknowledgement & Approval").FontSize(13).Bold().FontColor(BrandText);
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
            row.ConstantItem(120).AlignMiddle().Element(m => HeaderMark(m, 16, 14));
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
                    // 2026-07-22: was Clamp(body, 400) on raw Markdown -- 498 of 576 corpus Intents
                    // exceed 400 chars on that raw basis (median 680, mean 796), so ~86% of rows truncated
                    // mid-sentence in a handover document. Now strip the Markdown (PlainClamp) and raise the
                    // ceiling to 1500; on the stripped basis only 31 of 576 still exceed it. Deliberately
                    // NOT unlimited: a QuestPDF table cell cannot split across a page break, and the longest
                    // Intent is 6350 chars raw / 6049 stripped, which would break the layout. (Figures
                    // re-measured live vs corpus-v2 e0fa11ea; the original 496/6278 did not reproduce --
                    // see sqltriage-meta worklist f540989.)
                    table.Cell().Background(bg).PaddingVertical(3).PaddingRight(2).Text(PlainClamp(body, 1500)).FontSize(7.5f).FontColor("#555555");
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

    // ── HA/DR & Backup Posture report (2026-07-16) ─────────────────────────
    // Pure recomposition — see HaDrPostureReportService for where every number comes from.
    // This method only renders the DTO; it reuses the same title band / donut / bar-breakdown /
    // findings-table primitives every other AssessmentPdf report already uses.
#if !SQLT_NO_REPORT_HADR_POSTURE
    public static byte[] BuildHaDrPostureReport(HaDrPostureReport r)
    {
        var cb = r.Meta.ColorBlind;

        // Overall pass% is computed only over sections that actually have data — a section with
        // no scorable checks (Total == 0, e.g. no AGs configured) must never drag the headline
        // number down as if it had failed. The donut/bar breakdown must also express ONE unit:
        // corpus CHECK pass-rate. Restore Verification is measured in DATABASES restored, not
        // checks, so folding its rate into the same percentage would compare unlike things — it is
        // excluded from the composite and renders as its own stat + section block below.
        static bool IsCorpusCheckSection(HaDrSectionRow s) => s.Id != "restore-verify";
        var scored        = r.Sections.Where(s => IsCorpusCheckSection(s) && s.Total > 0).ToList();
        var overallTotal  = scored.Sum(s => s.Total);
        var overallPassed = scored.Sum(s => s.Passed);
        var overallPct    = overallTotal > 0 ? 100.0 * overallPassed / overallTotal : -1;
        var overallColor  = overallPct < 0 ? Muted : ScoreColor(overallPct, cb);

        var overallSegs = new List<DonutSegment>
        {
            new() { Value = overallPassed, Color = Pass(cb) },
            new() { Value = Math.Max(0, overallTotal - overallPassed), Color = Fail(cb) },
        };

        var byArea = scored
            .Select(s => (Area: s.Name, Passed: s.Passed, Total: s.Total, Pct: s.Percent))
            .OrderBy(x => x.Pct)
            .ToList();

        return Document.Create(c => c.Page(page =>
        {
            SetupPage(page, r.Meta, true);
            page.Content().PaddingVertical(6).Column(col =>
            {
                col.Spacing(12);
                col.Item().Element(x => BundleTitleBand(x, r.Meta, "HA/DR & Backup Diagnostic"));

                col.Item().Row(row =>
                {
                    row.Spacing(14);
                    row.ConstantItem(124).AlignMiddle().Element(d =>
                        Donut(d, overallSegs, overallPct >= 0 ? $"{overallPct:F0}%" : "—", "Passed", overallColor, 120));
                    row.RelativeItem().AlignMiddle().Row(inner =>
                    {
                        inner.Spacing(8);
                        foreach (var s in r.Sections)
                            ComplianceStat(inner, s.Total > 0 ? $"{s.Percent:F0}%" : "—", s.Name.ToUpperInvariant(),
                                s.Total > 0 ? ScoreColor(s.Percent, cb) : Muted);
                    });
                });

                if (byArea.Count > 1)
                    col.Item().Element(x => AreaBreakdown(x, byArea, cb));

                foreach (var s in r.Sections)
                    col.Item().Element(x => HaDrSectionBlock(x, s, cb));
            });
        })).GeneratePdf();
    }
#endif

    private static void HaDrSectionBlock(IContainer c, HaDrSectionRow s, bool cb) =>
        c.Border(1).BorderColor(Line).Padding(9).Column(cc =>
        {
            cc.Item().Row(row =>
            {
                row.RelativeItem().Text(s.Name).FontSize(12).Bold().FontColor("#222222");
                row.ConstantItem(90).AlignRight().Text(s.Total > 0 ? $"{s.Percent:F0}%" : "—")
                    .FontSize(16).Bold().FontColor(s.Total > 0 ? ScoreColor(s.Percent, cb) : Muted);
            });
            if (!string.IsNullOrWhiteSpace(s.ContextLine))
                cc.Item().PaddingTop(1).Text(s.ContextLine).FontSize(7.5f).FontColor(Muted);

            if (s.Total == 0)
            {
                cc.Item().PaddingTop(5).Text(string.IsNullOrWhiteSpace(s.EmptyStateText)
                        ? "Not currently assessed for this server."
                        : s.EmptyStateText)
                    .FontSize(8.5f).Italic().FontColor(Muted);
                return;
            }

            cc.Item().PaddingTop(2).Text($"{s.Passed} of {s.Total} checks passed").FontSize(8).FontColor("#555555");
            if (s.OpenFindings.Count > 0)
                cc.Item().PaddingTop(5).Element(x => Table(x, s.OpenFindings, cb));
            else
                cc.Item().PaddingTop(5).Text("No open findings.").FontSize(8.5f).Italic().FontColor(Pass(cb));
        });
}

public sealed class ComplianceFamilyRow
{
    public string Id = "";
    public string Name = "";
    public double Percent;
    public string Status = "";   // Compliant | PartiallyCompliant | NonCompliant | NoData | NotTested
    /// <summary>True when SQLTriage does not test this control — rendered un-scored and excluded from the aggregate (#87).</summary>
    public bool NotTested;
    /// <summary>Honest explanation shown for a NotTested control.</summary>
    public string NotTestedReason = "";
    /// <summary>ALL failing findings mapped to this control — NOT capped (#87). Exported evidence must be complete.</summary>
    public List<ComplianceFindingRow> Findings = new();
}

/// <summary>One failing finding attached to a compliance control family, for the evidence PDF.</summary>
public sealed class ComplianceFindingRow
{
    public string CheckId = "";
    public string Name = "";
    public string Severity = "";
    public string Message = "";
    public string Server = "";
}

public sealed class ComplianceReport
{
    public AssessmentMeta Meta = new();
    public double OverallPercent;   // -1 = outside scope
    public int Compliant, Partial, NonCompliant, OutsideScope, NotTested;
    public List<ComplianceFamilyRow> Families = new();
}

// ── HA/DR & Backup Posture DTOs (2026-07-16, pure recomposition) ───────
// Every field here is read from checks/results that already ran (corpus via
// CheckExecutionService, the RestoreVerify/MSP#9 results artifact, or the cached ServerDocs
// HA/DR snapshot) — see HaDrPostureReportService, which composes this DTO and never runs a
// new probe.
/// <summary>One section of the report — a pass-% verdict recomposed from an existing source.
/// Total == 0 is the honest "not applicable / not yet assessed" state (e.g. no AGs configured
/// on this server) — never rendered as a failing 0%.</summary>
public sealed class HaDrSectionRow
{
    public string Id = "";     // "backups" | "ag-failover" | "quorum" | "restore-verify"
    public string Name = "";   // display title, e.g. "Availability Groups & Failover"
    public int Passed;
    public int Total;          // scorable denominator; 0 = no data for this section
    public double Percent;     // -1 = no data
    /// <summary>Shown instead of a findings table when Total == 0 — the real corpus SKIP/INFO
    /// message when one exists (e.g. "No AGs configured."), else a generic not-yet-assessed
    /// line. Never a fabricated line pretending to know something that wasn't checked.</summary>
    public string EmptyStateText = "";
    /// <summary>Optional one-line context pulled from a raw source (ServerDocs snapshot summary,
    /// restore-verify run timestamp) — informational only, not part of the pass-% math.</summary>
    public string ContextLine = "";
    public List<FindingRow> OpenFindings = new();
}

public sealed class HaDrPostureReport
{
    public AssessmentMeta Meta = new();
    public List<HaDrSectionRow> Sections = new();
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
    public int Score;               // 0-100 health score. Meaningless unless ScoreAssessed.
    /// <summary>C4 (2026-08-05): false when nothing measured the server. The donut is not drawn.</summary>
    public bool ScoreAssessed = true;
    /// <summary>EstateHealthPolicy.ServerBasis() pass-through: which kind of unmeasured it is.</summary>
    public string ScoreBasis = "";
    public string ScoreMessage = "";
    public List<BundleFinding> TopRisks = new();
}

// ── CIO Executive Report DTO (2026-07-13) ───────────────────────────────
// Every field is a straight pass-through of what CioDashboard.razor already
// computed and rendered on screen — see CioDashboard.razor's BuildCioReportDto().
// Governance never gets its own donut (Adrian's ruling): it renders as a
// subordinate text row + swatch under the Executive Health headline.
public sealed class CioExecutiveReport
{
    public AssessmentMeta Meta = new();

    // Estate Executive Health — the sole headline (ruling 2026-07-11).
    public bool EhAssessed;
    public int EhScore;
    public string EhBand = "";        // "Healthy" | "Warning" | "Critical"
    public string EhFeedLine = "";    // EstateFeedSubLine() pass-through
    public string EhDriverLine = "";  // EstateDriverLine() pass-through
    // EstateHealthPolicy.EstateBasis() pass-through (2026-08-05). Says what the headline number
    // is a mean OF, and names every server it does not cover and why. Printed in BOTH branches:
    // beside the donut when there is a number, and INSTEAD of the donut when there is not.
    public string EhBasisLine = "";

    // Governance — subordinate text row + swatch, never a second donut.
    public bool GovAssessed;
    public double GovScore;
    public string GovBand = "";              // ScoreBand.ToString(), e.g. "Gold"
    public string GovSubline = "";           // fixed governance card-role honesty text
    public string GovCheckCountLabel = "";   // "N of M checks scored (...)" — same formula as the on-screen strip

    public List<CioServerRow> ServerRows = new();
    public List<CioTopFinding> TopFindings = new();
    public CioPowerBlock? Power;             // null => section omitted (never a fabricated zero)
    public List<CioTrendRow> TrendRows = new();
    public int TotalChecks;                  // GovernanceScore.TotalFindings pass-through
}

public sealed class CioServerRow
{
    public string Server = "";
    public string Connection = "";  // "Online" | "Offline" | "Unknown"
    public bool EhAssessed;
    public int EhScore;
    public string EhBand = "";
    public double GovScore;
    public string GovBand = "";
    public int Open;
    public int Critical;
}

public sealed class CioTopFinding
{
    public string Name = "";
    public string Severity = "";
    public string Server = "";
    public string Recommendation = "";
}

public sealed class CioPowerBlock
{
    public int ServersWithEstimate;
    public bool HasSignal;
    public int CutLowPct, CutHighPct;
    public string DriverClause = "";
    public int ServersQuantified;
    public int KWhLow, KWhHigh, CostLow, CostHigh, Co2Low, Co2High;
}

public sealed class CioTrendRow
{
    // Bucketing fix (2026-07-13): this is a bucket period label (e.g. "13 Jul" or
    // "w/c 07 Jul"), not a single audit's date — renamed from AuditDate accordingly.
    // Score/Band are kept as the bucket's representative (last-in-bucket) values;
    // Min/Max are the new bucketed range shown alongside them.
    public string Period = "";
    public double Score;
    public double Min;
    public double Max;
    public string Band = "";
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
