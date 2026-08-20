/* In the name of God, the Merciful, the Compassionate */
/*
 * RoadmapPdfBuilder — generates the Diagnostics Maturity Roadmap PDF with QuestPDF (server-side,
 * from the data model) instead of mutating the live DOM and printing via WebView2.
 *
 * Deterministic pagination, margin-reserved header/footer that never overlap content, watermark and
 * cover/attestation pages, and colour-blind-aware colours. This REPLACES the retired browser-print
 * export (the old body.printing-active DOM path is gone); it is not a mirror of it, so anything the
 * screen shows has to be carried by RoadmapReport to appear here.
 *
 * COLOUR RULE EXEMPTION: the app-wide "never hard-code status colours, use CSS vars" rule exists so
 * body.colorblind-mode can remap them. QuestPDF has no CSS and no variables, so the palette below is
 * literal hex — and carries the colour-blind ramp itself, switched by RoadmapReport.ColorBlind, which
 * is the same guarantee reached a different way. Values are the Wong (2011) safe pairs, kept in step
 * with wwwroot/css/Accessibility.css.
 *
 * Layout notes:
 *  - Cover + attestation are separate page sections (cover is full-bleed, no footer).
 *  - Category cards render in a 3-up grid; a card with many findings spans full width so it can
 *    page-break cleanly (QuestPDF rows can't split a cell across pages).
 *  - Colour-blind mode swaps the maturity ramp + pass/fail/severity to a Wong-derived palette; the
 *    brand accent (#1FA85E green) is decorative and left alone.
 */

#nullable enable

using System.Collections.Generic;
using System.Linq;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace SQLTriage.Data.Services;

// ── DTOs: a render-only projection of the roadmap, decoupled from the page's private types ──
public sealed class RoadmapReport
{
    // footer / header
    public string ServerLabel = "";
    public string GeneratedUtc = "";   // short, e.g. 2026-06-09T12:56Z
    public string TimezoneId = "";
    public string RunId = "";           // short run id (footer)
    public bool   ColorBlind;

    // cover
    public string Company = "";          // optional org branding
    public string CoverSubtitle = "";
    public string PreparedForDate = ""; // e.g. 09 June 2026

    // executive summary
    public int    OverallLevel;
    public string OverallLevelName = "";
    public double OverallScore;
    public double RiskWeightedScore;
    public int    ItemsImpacted;
    public int    FindingsEvaluated;
    public int    FindingsPassed;
    public int    ServersSelected;
    public string Headline = "";
    public int    Critical, High, Medium;
    public List<string> TopActions = new();
    public List<RoadmapLevel> Levels = new();

    // attestation
    public string RunIdFull = "";
    public string GeneratedUtcIso = "";
    public string ToolVersion = "";
    public string Operator = "";
    public string FrameworkVersion = "";
    public List<string> ServersInScope = new();

    // watermark
    public bool   Watermark;
    public string WatermarkText = "DRAFT — non-production data";
}

public sealed class RoadmapLevel
{
    public int    Number;
    public string Name = "";
    public string Description = "";
    public int    PassedCount, TotalCount;
    public double PassRate;
    public int    Target = 80;
    public List<RoadmapCategory> Categories = new();
}

public sealed class RoadmapCategory
{
    public string Name = "";
    public int    PassedCount, TotalCount;
    public List<RoadmapFinding> Failed = new();
    public List<RoadmapFinding> Info = new();
    public List<RoadmapFinding> PassedItems = new();
    public int ItemCount => Failed.Count + Info.Count + PassedItems.Count;
}

public sealed class RoadmapFinding
{
    public string  Name = "";
    public string  Tag = "";
    public string? Remediation;
    public string? Effort;
    public int[]?  Related;
}

public static class RoadmapPdfBuilder
{
    // Canonical S-mark brand (DECISIONS 2026-07-09; retired the old coral accent). Decorative green for
    // borders/marks; coloured TEXT on white uses AssessmentPdf.BrandText for print contrast.
    private const string Brand = "#1FA85E";
    private const string Ink   = "#333333";
    private const string Muted = "#888888";
    private const string Faint = "#aaaaaa";
    private const string Line  = "#e5e5e5";
    private const string CoverBg = "#0F1513";

    private static string Mat(int level, bool cb) => cb
        ? level switch { 1 => "#D55E00", 2 => "#E69F00", 3 => "#B8860B", 4 => "#009E73", 5 => "#CC79A7", _ => "#666666" }
        : level switch { 1 => "#dc2626", 2 => "#f59e0b", 3 => "#ca8a04", 4 => "#16a34a", 5 => "#9333ea", _ => "#666666" };

    private static string Pass(bool cb) => cb ? "#009E73" : "#16a34a";
    private static string Fail(bool cb) => cb ? "#D55E00" : "#dc2626";
    private static string High(bool cb) => cb ? "#E69F00" : "#f59e0b";

    // Card / tile fills. Deliberately near-white: the body stays print-friendly (an operator
    // prints this on a mono laser as often as they read it on screen), and the colour carries
    // in the accent borders and chips rather than in large flooded areas of ink.
    private const string Tile = "#fafbfc";
    private const string ChipBg = "#f4f5f6";

    /// <summary>
    /// A pass-rate bar — the print echo of the page's progress bars. Two weighted cells rather
    /// than a fixed-width fill, so it scales with whatever column it lands in.
    /// <para>
    /// Both ends are guarded on purpose: QuestPDF treats a zero relative weight as "unconstrained",
    /// so a naive 0%% bar renders as a FULL bar — a score of nothing painted as a score of
    /// everything. That is the one failure mode a compliance report must never have.
    /// </para>
    /// </summary>
    private static void Bar(IContainer c, double pct, string colour, float height = 5)
    {
        var p = pct < 0 ? 0 : (pct > 100 ? 100 : pct);
        c.Height(height).Background(Line).Row(row =>
        {
            if (p > 0) row.RelativeItem((float)p).Background(colour);
            if (p < 100) row.RelativeItem((float)(100 - p));
        });
    }

    /// <summary>A severity pill: tinted body with a solid colour spine, echoing the page badges.</summary>
    private static void Chip(RowDescriptor row, string text, string colour) =>
        row.AutoItem()
           .Background(ChipBg).BorderLeft(2).BorderColor(colour)
           .PaddingVertical(2).PaddingHorizontal(6)
           .Text(text).FontSize(8).Bold().FontColor(colour);

    public static byte[] Build(RoadmapReport r)
    {
        var cb = r.ColorBlind;
        var accent = Mat(r.OverallLevel, cb);

        return Document.Create(container =>
        {
            // ── Cover (full-bleed, no footer) ──
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(0);
                page.DefaultTextStyle(s => s.FontFamily("Arial"));
                if (r.Watermark) page.Foreground().Element(c => Watermark(c, r, cb));
                page.Content().Element(c => Cover(c, r, accent));
            });

            // ── Body + attestation (margins + footer) ──
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(26);
                page.DefaultTextStyle(s => s.FontSize(8.5f).FontFamily("Arial").FontColor(Ink));
                if (r.Watermark) page.Foreground().Element(c => Watermark(c, r, cb));

                page.Footer().BorderTop(1.5f).BorderColor(Brand).PaddingTop(4).Row(row =>
                {
                    row.RelativeItem().Element(AssessmentPdf.FooterMark);
                    row.RelativeItem(3).AlignCenter().Text(
                        $"SQLTriage — Diagnostics Maturity Roadmap — {r.ServerLabel} — {r.GeneratedUtc} ({r.TimezoneId}) — Run {r.RunId}")
                        .FontSize(7).FontColor("#666666");
                    row.RelativeItem().AlignRight().Text(t =>
                    {
                        t.DefaultTextStyle(x => x.FontSize(7).FontColor("#666666"));
                        t.Span("Page "); t.CurrentPageNumber(); t.Span(" of "); t.TotalPages();
                    });
                });

                page.Content().PaddingVertical(6).Column(col =>
                {
                    col.Spacing(11);
                    col.Item().Element(c => Header(c, r, accent));
                    col.Item().Element(c => ExecSummary(c, r, cb));
                    // Each maturity level starts on its own page.
                    foreach (var lvl in r.Levels)
                    {
                        col.Item().PageBreak();
                        col.Item().Element(c => Level(c, lvl, cb));
                    }
                    if (!string.IsNullOrEmpty(r.RunIdFull))
                    {
                        col.Item().PageBreak();
                        col.Item().Element(c => Attestation(c, r));
                    }
                });
            });
        }).GeneratePdf();
    }

    // ───────────────────────────────── Cover ─────────────────────────────────
    // No AlignMiddle: a centred (non-splittable) block throws DocumentLayoutException if the stacked
    // content is even slightly taller than the page. Top-aligned with padding paginates safely and
    // the trimmed content comfortably fills one landscape page.
    private static void Cover(IContainer c, RoadmapReport r, string accent) =>
        c.Background(CoverBg).PaddingVertical(34).PaddingHorizontal(40).Column(col =>
        {
            col.Spacing(7);
            col.Item().AlignCenter().Element(m => AssessmentPdf.MarkTile(m, 38));
            col.Item().AlignCenter().Text("SQLTRIAGE").FontColor("#9aa4ad").FontSize(10).Bold();
            col.Item().AlignCenter().Text("Diagnostics Maturity Roadmap").FontColor("#f5f5f5").FontSize(26).Bold();
            col.Item().AlignCenter().Text(r.CoverSubtitle).FontColor("#c5ccd2").FontSize(12);
            if (!string.IsNullOrWhiteSpace(r.Company))
                col.Item().AlignCenter().Text($"Prepared for {r.Company}").FontColor("#9aa4ad").FontSize(12);

            col.Item().PaddingTop(6).AlignCenter().Border(3).BorderColor(accent).Padding(12).Width(140).Column(b =>
            {
                b.Item().AlignCenter().Text($"L{r.OverallLevel}").FontColor(accent).FontSize(36).Bold();
                b.Item().AlignCenter().Text(r.OverallLevelName).FontColor(accent).FontSize(11).Bold();
            });

            // L1-L5 progress dots (centred via flexible side spacers)
            col.Item().PaddingTop(4).Row(row =>
            {
                row.RelativeItem();
                for (var lv = 1; lv <= 5; lv++)
                {
                    var reached = lv <= r.OverallLevel;
                    var lvName = lv switch { 1 => "Foundation", 2 => "Hardened", 3 => "Performant", 4 => "Observable", 5 => "Governed", _ => "" };
                    row.ConstantItem(100).Column(cc =>
                    {
                        cc.Item().AlignCenter().Text(reached ? "●" : "○").FontColor(reached ? Mat(lv, r.ColorBlind) : "#3a444d").FontSize(15);
                        cc.Item().AlignCenter().Text(lvName).FontColor(reached ? "#c5ccd2" : "#6b757d").FontSize(8);
                    });
                }
                row.RelativeItem();
            });

            col.Item().PaddingTop(8).Row(row =>
            {
                CoverStat(row, $"{r.OverallScore:N0}%", "OVERALL SCORE");
                CoverStat(row, $"{r.ServersSelected}", "SERVERS");
                CoverStat(row, $"{r.FindingsPassed} / {r.FindingsEvaluated}", "CHECKS PASSED");
                CoverStat(row, $"{r.ItemsImpacted}", "ITEMS IMPACTED");
            });

            col.Item().PaddingTop(6).AlignCenter().Text($"Prepared for {r.CoverSubtitle} — {r.PreparedForDate}")
                .Bold().FontColor("#e8ebed").FontSize(10);

            if (!string.IsNullOrEmpty(r.RunIdFull))
                col.Item().PaddingTop(8).AlignCenter().MaxWidth(720).Border(1).BorderColor("#2a343d").Background("#161D1B").Padding(10).Table(table =>
                {
                    table.ColumnsDefinition(d => { d.ConstantColumn(80); d.RelativeColumn(); d.ConstantColumn(80); d.RelativeColumn(); });
                    CoverMeta(table, "Run ID", r.RunIdFull); CoverMeta(table, "Generated", $"{r.GeneratedUtcIso} ({r.TimezoneId})");
                    CoverMeta(table, "Tool", $"SQLTriage v{r.ToolVersion}"); CoverMeta(table, "Operator", r.Operator);
                    CoverMeta(table, "Framework", r.FrameworkVersion); CoverMeta(table, "", "");
                });

            col.Item().PaddingTop(7).AlignCenter().Text("Compliments of sqldba.org").FontColor("#6b757d").FontSize(9);
        });

    private static void CoverStat(RowDescriptor row, string value, string label) =>
        row.RelativeItem().Column(cc =>
        {
            cc.Item().AlignCenter().Text(value).FontColor("#f5f5f5").FontSize(24).Bold();
            cc.Item().AlignCenter().Text(label).FontColor("#9aa4ad").FontSize(9);
        });

    private static void CoverMeta(TableDescriptor table, string label, string value)
    {
        table.Cell().PaddingVertical(2).Text(label).FontColor("#7d8893").FontSize(8).Bold();
        table.Cell().PaddingVertical(2).Text(value).FontColor("#dfe3e6").FontSize(8.5f);
    }

    // ───────────────────────────── Body header band ─────────────────────────────
    private static void Header(IContainer c, RoadmapReport r, string accent) =>
        c.Border(1).BorderColor("#dddddd").Padding(10).Row(row =>
        {
            row.ConstantItem(86).AlignMiddle().Column(cc =>
            {
                cc.Item().AlignCenter().Text($"L{r.OverallLevel}").FontSize(30).Bold().FontColor(accent);
                cc.Item().AlignCenter().Text(r.OverallLevelName).FontSize(9).Bold().FontColor(accent);
            });
            row.RelativeItem().PaddingLeft(14).AlignMiddle().Column(cc =>
            {
                cc.Item().Text("Diagnostics Maturity Roadmap").FontSize(18).Bold().FontColor("#222222");
                cc.Item().Text(r.ServerLabel).FontSize(9).FontColor("#666666");
                cc.Item().Text($"Generated {r.GeneratedUtc} ({r.TimezoneId})  ·  Run {r.RunId}").FontSize(8).FontColor(Faint);
            });
            row.ConstantItem(150).AlignMiddle().Column(cc =>
            {
                cc.Item().AlignRight().Text($"{r.OverallScore:N0}%").FontSize(26).Bold().FontColor(accent);
                cc.Item().AlignRight().Text("OVERALL SCORE").FontSize(7).FontColor(Faint);
                cc.Item().AlignRight().Text($"{r.ServersSelected} servers  ·  {r.FindingsPassed}/{r.FindingsEvaluated} passed").FontSize(7.5f).FontColor(Muted);
            });
        });

    private static void ExecSummary(IContainer c, RoadmapReport r, bool cb) =>
        c.Column(col =>
        {
            col.Spacing(7);
            col.Item().Text("Executive Summary").FontSize(11).Bold().FontColor("#444444");

            if (!string.IsNullOrWhiteSpace(r.Headline))
                col.Item().Background("#fafafa").Border(1).BorderColor("#eeeeee").Padding(8).Text(t =>
                {
                    t.Span("Headline Finding   ").Bold().FontColor(Mat(r.OverallLevel, cb));
                    t.Span(r.Headline).FontColor("#444444");
                });

            col.Item().Row(row =>
            {
                row.Spacing(8);
                Stat(row, $"L{r.OverallLevel}", "MATURITY LEVEL", Mat(r.OverallLevel, cb));
                Stat(row, $"{r.OverallScore:N0}%", "OVERALL SCORE", "#2563eb");
                Stat(row, $"{r.RiskWeightedScore:N0}%", "RISK-WEIGHTED", Pass(cb));
                Stat(row, $"{r.ItemsImpacted}", "ITEMS IMPACTED", Fail(cb));
            });

            col.Item().Row(row =>
            {
                row.Spacing(6);
                row.AutoItem().PaddingTop(2).Text("Finding severity").FontSize(8).FontColor("#555555");
                Chip(row, $"Critical {r.Critical}", Fail(cb));
                Chip(row, $"High {r.High}", High(cb));
                Chip(row, $"Medium {r.Medium}", Ink);
                row.RelativeItem();
            });

            if (r.TopActions.Count > 0)
                col.Item().Column(cc =>
                {
                    cc.Item().Text("Top 3 Actionable Items").Bold().FontSize(9).FontColor("#444444");
                    var i = 1;
                    foreach (var a in r.TopActions)
                        cc.Item().Text($"{i++}.  {a}").FontSize(8).FontColor("#555555");
                });

            col.Item().PaddingTop(2).Table(table =>
            {
                table.ColumnsDefinition(d => { d.ConstantColumn(26); d.RelativeColumn(); d.ConstantColumn(120); d.ConstantColumn(80); });
                foreach (var h in new[] { "", "Maturity Stage", "Pass Rate", "Status" })
                    table.Cell().BorderBottom(1).BorderColor(Line).PaddingBottom(2).Text(h).FontSize(7.5f).Bold().FontColor(Muted);
                foreach (var lvl in r.Levels)
                {
                    var done = lvl.PassRate >= lvl.Target;
                    var hex = Mat(lvl.Number, cb);
                    table.Cell().PaddingVertical(3).Text($"L{lvl.Number}").FontSize(8).Bold().FontColor(hex);
                    table.Cell().PaddingVertical(3).Text($"{lvl.Name} — {lvl.Description}").FontSize(8).FontColor("#444444");
                    // Gauge + number, so the summary scans visually the way the page does.
                    table.Cell().PaddingVertical(3).PaddingRight(8).Column(cc =>
                    {
                        cc.Item().Text($"{lvl.PassRate:N0}% ({lvl.PassedCount}/{lvl.TotalCount})").FontSize(8).FontColor("#444444");
                        cc.Item().PaddingTop(2).Element(x => Bar(x, lvl.PassRate, done ? Pass(cb) : hex, 4));
                    });
                    table.Cell().PaddingVertical(3).Text(done ? "COMPLETE" : "IN PROGRESS").FontSize(8).Bold().FontColor(done ? Pass(cb) : High(cb));
                }
            });
        });

    // Tinted tile with a coloured top spine — the print translation of the page's dark
    // accent-topped stat cards.
    // Tinted tile with a coloured top spine — the print translation of the page's accent-topped
    // stat cards. The spine is a real 3pt element rather than a BorderTop, because chaining
    // Border/BorderColor twice on one container leaves which colour wins up to QuestPDF's
    // decorator order; an explicit filled band is unambiguous.
    private static void Stat(RowDescriptor row, string value, string label, string colour) =>
        row.RelativeItem().Border(1).BorderColor(Line).Background(Tile).Column(cc =>
        {
            cc.Item().Height(3).Background(colour);
            cc.Item().Padding(8).Column(inner =>
            {
                inner.Item().AlignCenter().Text(value).FontSize(20).Bold().FontColor(colour);
                inner.Item().AlignCenter().Text(label).FontSize(7).FontColor(Muted);
            });
        });

    // ───────────────────────────── Maturity level ─────────────────────────────
    private static void Level(IContainer c, RoadmapLevel lvl, bool cb) =>
        c.Column(col =>
        {
            col.Spacing(6);
            var hex = Mat(lvl.Number, cb);
            var done = lvl.PassRate >= lvl.Target;

            col.Item().BorderBottom(2).BorderColor(hex).PaddingBottom(3).Row(row =>
            {
                row.RelativeItem().Text(t =>
                {
                    t.Span($"L{lvl.Number}  {lvl.Name}").FontSize(12).Bold().FontColor(hex);
                    t.Span($"   {lvl.Description}").FontSize(8).FontColor(Muted);
                });
                row.ConstantItem(190).Column(cc =>
                {
                    cc.Item().AlignRight().Text(t =>
                    {
                        t.Span($"{lvl.PassRate:N0}%  ").FontSize(12).Bold().FontColor(done ? Pass(cb) : hex);
                        t.Span($"({lvl.PassedCount}/{lvl.TotalCount})   ").FontSize(8).FontColor(Muted);
                        t.Span($"Target {lvl.Target}%").FontSize(8).FontColor(Faint);
                    });
                    cc.Item().PaddingTop(3).Element(x => Bar(x, lvl.PassRate, done ? Pass(cb) : hex));
                });
            });

            // Full-width stacked cards. A 3-up *grid* of cards isn't safe here: QuestPDF Rows can't
            // page-break, and in a 1/3-width column verbose remediation wraps tall enough to exceed a
            // page → DocumentLayoutException. Full-width cards give remediation room and break cleanly.
            foreach (var cat in lvl.Categories)
                col.Item().Element(x => Category(x, cat, cb, hex));
        });

    // Card with a coloured left spine in its level's colour, so a category is visually tied to
    // the maturity level it belongs to even when the page break separates it from the header.
    private static void Category(IContainer c, RoadmapCategory cat, bool cb, string levelHex) =>
        c.Border(1).BorderColor(Line).Row(outer =>
        {
            outer.ConstantItem(3).Background(levelHex);
            outer.RelativeItem().Padding(7).Column(col =>
        {
            col.Spacing(3);
            col.Item().Row(row =>
            {
                row.RelativeItem().Text(cat.Name).Bold().FontSize(9.5f).FontColor("#555555");
                // Per-category pass rate as a small gauge — the page shows one, the PDF didn't.
                var pct = cat.TotalCount > 0 ? cat.PassedCount * 100.0 / cat.TotalCount : -1;
                if (pct >= 0)
                {
                    row.ConstantItem(56).PaddingRight(6).PaddingTop(4)
                       .Element(x => Bar(x, pct, pct >= 80 ? Pass(cb) : (pct >= 50 ? High(cb) : Fail(cb)), 4));
                }
                row.AutoItem().Text($"{cat.PassedCount} / {cat.TotalCount}").FontSize(8).FontColor(Muted);
            });
            col.Item().LineHorizontal(0.5f).LineColor("#eeeeee");

            foreach (var f in cat.Failed)
                col.Item().Element(x => Finding(x, f, FindingKind.Fail, cb));
            foreach (var f in cat.Info)
                col.Item().Element(x => Finding(x, f, FindingKind.Info, cb));

            // Passed items are short — flow them 3-up across the full card width to save vertical space.
            if (cat.PassedItems.Count > 0)
                for (var i = 0; i < cat.PassedItems.Count; i += 3)
                {
                    var slice = cat.PassedItems.Skip(i).Take(3).ToList();
                    col.Item().Row(row =>
                    {
                        row.Spacing(8);
                        foreach (var f in slice) row.RelativeItem().Element(x => Finding(x, f, FindingKind.Pass, cb));
                        for (var k = slice.Count; k < 3; k++) row.RelativeItem();
                    });
                }
        });
        });

    private enum FindingKind { Fail, Info, Pass }

    private static void Finding(IContainer c, RoadmapFinding f, FindingKind kind, bool cb)
    {
        var (glyph, colour) = kind switch
        {
            FindingKind.Fail => ("✗", Fail(cb)),
            FindingKind.Info => ("ⓘ", "#3b82f6"),
            _                => ("✓", Pass(cb)),
        };

        c.Row(row =>
        {
            row.ConstantItem(11).Text(glyph).FontColor(colour).FontSize(8.5f);
            row.RelativeItem().Column(cc =>
            {
                cc.Item().Row(r2 =>
                {
                    r2.RelativeItem().Text(f.Name).FontSize(8).FontColor(kind == FindingKind.Pass ? "#555555" : "#333333");
                    if (!string.IsNullOrEmpty(f.Effort))
                        r2.AutoItem().PaddingLeft(5).Text(f.Effort).FontSize(6.5f).FontColor(AssessmentPdf.BrandText);
                    if (!string.IsNullOrEmpty(f.Tag))
                        r2.AutoItem().PaddingLeft(5).Text(f.Tag).FontSize(6.5f).FontColor(Faint);
                });
                if (kind == FindingKind.Fail && !string.IsNullOrWhiteSpace(f.Remediation))
                    cc.Item().Text(f.Remediation).FontSize(7).Italic().FontColor(Muted);
                if (kind == FindingKind.Fail && f.Related is { Length: > 0 })
                    cc.Item().Text("Related: " + string.Join(", ", f.Related.Select(x => "#" + x))).FontSize(6.5f).FontColor(Faint);
            });
        });
    }

    // ───────────────────────────── Attestation ─────────────────────────────
    private static void Attestation(IContainer c, RoadmapReport r) =>
        c.Column(col =>
        {
            col.Spacing(12);
            col.Item().BorderBottom(2).BorderColor(Brand).PaddingBottom(6).Text("Operator Attestation").FontSize(18).Bold().FontColor("#222222");
            col.Item().Text(
                // Provenance corrected 2026-07-22: this run is driven by SQLTriage's own check corpus, not
                // by sp_triage / sp_Blitz output. Individual checks cite their upstream oracle where one
                // exists, but the report is not produced FROM those tools — and a false provenance claim
                // inside the section headed "Operator Attestation" is the worst possible place for one.
                "This report was produced by SQLTriage from the results of its own diagnostic check corpus. The metadata " +
                "below identifies the run, the operator, and the tool version that produced it. The SHA-256 hash " +
                "(recorded post-export in the sibling .manifest.json) lets a recipient verify the PDF they hold is " +
                "byte-for-byte identical to the one this operator generated.")
                .FontSize(9.5f).FontColor("#444444").LineHeight(1.5f);
            col.Item().PaddingTop(4).Table(table =>
            {
                table.ColumnsDefinition(d => { d.ConstantColumn(170); d.RelativeColumn(); });
                if (!string.IsNullOrWhiteSpace(r.Company)) AttRow(table, "Company", r.Company);
                AttRow(table, "Run ID", r.RunIdFull);
                AttRow(table, "Generated (UTC)", r.GeneratedUtcIso);
                AttRow(table, "Operator timezone", r.TimezoneId);
                AttRow(table, "Tool version", $"SQLTriage v{r.ToolVersion}");
                AttRow(table, "Operator", r.Operator);
                AttRow(table, "Compliance frameworks", r.FrameworkVersion);
                AttRow(table, "Servers in scope", string.Join("  ·  ", r.ServersInScope));
            });
        });

    private static void AttRow(TableDescriptor table, string label, string value)
    {
        table.Cell().BorderBottom(1).BorderColor("#dddddd").PaddingVertical(5).Text(label).FontSize(9).FontColor("#666666");
        table.Cell().BorderBottom(1).BorderColor("#dddddd").PaddingVertical(5).Text(value).FontSize(9).FontColor("#222222");
    }

    // ───────────────────────────── Watermark ─────────────────────────────
    private static void Watermark(IContainer c, RoadmapReport r, bool cb) =>
        c.AlignCenter().AlignMiddle().Rotate(-30).Text(r.WatermarkText)
            .FontSize(58).Bold().FontColor(cb ? "#f2dcc6" : "#CBEAD8");
}
