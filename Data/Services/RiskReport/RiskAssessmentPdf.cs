/* In the name of God, the Merciful, the Compassionate */

// QuestPDF builder for the Risk Assessment report (SSRS replacement). Self-contained
// so the whole RiskReport folder stays Compile-Removable for community builds without
// dragging AssessmentPdf's privates public. Reuses AssessmentPdf's public palette +
// ScoreColor, and its DonutSegment/donut approach, for visual consistency.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace SQLTriage.Data.Services.RiskReport
{
    public sealed class RiskAssessmentMeta
    {
        public string Customer = "";
        public string GeneratedUtc = "";
        public string TimezoneId = "";
        public string RunId = "";
        public bool ColorBlind;
    }

    public static class RiskAssessmentPdf
    {
        // Reuse the shared palette so this matches the rest of the report suite.
        private const string Brand = AssessmentPdf.Brand;
        private const string Ink   = AssessmentPdf.Ink;
        private const string Muted = AssessmentPdf.Muted;
        private const string Faint = AssessmentPdf.Faint;
        private const string Line  = AssessmentPdf.Line;

        public static byte[] Build(RiskAssessmentReport report, RiskAssessmentMeta meta)
        {
            var cb = meta.ColorBlind;
            var h = report.Header;
            var f = report.Filters;

            // Render-time filters (source already applied SupportedOnly):
            //  - ShowAllChecks off  → only rows with something to report (Bad=1 or any failures)
            //  - IncludeYellowChecks off → drop medium-only (yellow) findings
            IEnumerable<RiskCheckRow> rows = report.Rows;
            if (!f.ShowAllChecks)
                rows = rows.Where(x => x.Bad == 1 || x.FailedItems > 0 || x.FailedHigh > 0 || x.FailedMedium > 0);
            if (!f.IncludeYellowChecks)
                rows = rows.Where(x => !(x.FailedHigh == 0 && x.FailedItems == 0 && x.FailedMedium > 0));
            var shown = rows.ToList();

            var scoreCol = AssessmentPdf.ScoreColor(h.PassedPercent, cb);
            var segs = new List<AssessmentPdf.DonutSegment>
            {
                new() { Value = h.PassedPercent,                 Color = scoreCol },
                new() { Value = Math.Max(0, 100 - h.PassedPercent), Color = "#eeeeee" },
            };

            var byCategory = shown
                .GroupBy(x => string.IsNullOrWhiteSpace(x.Category) ? "Uncategorised" : x.Category)
                .OrderByDescending(g => g.Sum(x => x.FailedHigh))
                .ThenByDescending(g => g.Sum(x => x.FailedItems))
                .ToList();

            var footerMeta = $"SQLTriage — SQL Risk Assessment — {h.Customer} — {meta.GeneratedUtc} ({meta.TimezoneId}) — Run {meta.RunId}";

            return Document.Create(container => container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(28);
                page.DefaultTextStyle(s => s.FontSize(9).FontFamily("Arial").FontColor(Ink));
                page.Footer().Element(c => Footer(c, footerMeta));

                page.Content().PaddingVertical(6).Column(col =>
                {
                    col.Spacing(14);

                    // Title band
                    col.Item().Border(1).BorderColor("#dddddd").Padding(10).Row(row =>
                    {
                        row.RelativeItem().Column(cc =>
                        {
                            cc.Item().Text(string.IsNullOrWhiteSpace(h.Customer) ? "—" : h.Customer.ToUpperInvariant())
                                .FontSize(8).Bold().FontColor(Muted);
                            cc.Item().Text("SQL Risk Assessment").FontSize(18).Bold().FontColor("#222222");
                            cc.Item().Text(t =>
                            {
                                if (!string.IsNullOrWhiteSpace(h.ContractType)) { t.Span(h.ContractType).FontColor(Muted).FontSize(8); t.Span("  ·  ").FontColor(Faint).FontSize(8); }
                                t.Span($"{h.ServerCount} server(s)  ·  Last check {h.LastDomainCheck}").FontColor(Muted).FontSize(8);
                                t.Span($"  ·  Generated {meta.GeneratedUtc} ({meta.TimezoneId})  ·  Run {meta.RunId}").FontColor(Faint).FontSize(8);
                            });
                        });
                        row.ConstantItem(120).AlignRight().AlignMiddle().Text("⛁ SQLTriage").Bold().FontColor(Brand).FontSize(13);
                    });

                    // Passed% donut + scope summary
                    col.Item().Row(row =>
                    {
                        row.Spacing(18);
                        row.ConstantItem(140).AlignMiddle().Element(d =>
                            Donut(d, segs, $"{h.PassedPercent:F0}%", "Passed", scoreCol, 132));
                        row.RelativeItem().AlignMiddle().Column(cc =>
                        {
                            cc.Item().Text("Overall Pass Rate").FontSize(13).Bold().FontColor("#333333");
                            cc.Item().PaddingTop(4).Text($"{shown.Count} finding row(s) shown across {byCategory.Count} categor(y/ies).")
                                .FontSize(10).FontColor("#555555");
                            var hi = shown.Sum(x => x.FailedHigh);
                            if (hi > 0)
                                cc.Item().PaddingTop(2).Text($"{hi} high-severity item(s) need attention.").FontSize(9.5f).FontColor(AssessmentPdf.Fail(cb));
                        });
                    });

                    // Findings grouped by category
                    if (shown.Count == 0)
                        col.Item().Text("No findings match the selected filters.").FontSize(10).Italic().FontColor(Muted);
                    else
                        foreach (var grp in byCategory)
                        {
                            col.Item().PaddingTop(4).Text(grp.Key).FontSize(12).Bold().FontColor(Brand);
                            col.Item().Element(c => CategoryTable(c, grp.ToList(), f.Detailed, cb));
                        }
                });
            })).GeneratePdf();
        }

        private static void CategoryTable(IContainer c, List<RiskCheckRow> rows, bool detailed, bool cb) =>
            c.Table(table =>
            {
                table.ColumnsDefinition(d =>
                {
                    d.RelativeColumn(3);    // section / summary
                    d.ConstantColumn(54);   // impact
                    d.ConstantColumn(66);   // complexity
                    d.ConstantColumn(48);   // failed
                    d.ConstantColumn(44);   // hours
                });
                table.Header(h =>
                {
                    foreach (var head in new[] { "Finding", "Impact", "Complexity", "Failed", "Hours" })
                        h.Cell().BorderBottom(1).BorderColor(Muted).PaddingVertical(3).PaddingRight(4)
                            .Text(head).FontSize(7.5f).Bold().FontColor(Muted);
                });

                var alt = false;
                foreach (var x in rows)
                {
                    var bg = alt ? "#fafafa" : "#ffffff"; alt = !alt;
                    table.Cell().Background(bg).PaddingVertical(3).PaddingRight(6).Column(cc =>
                    {
                        var title = string.IsNullOrWhiteSpace(x.Summary) ? x.Section : x.Summary;
                        cc.Item().Text(t =>
                        {
                            if (!string.IsNullOrWhiteSpace(x.OutcomeImage)) t.Span(x.OutcomeImage + " ").FontSize(9);
                            t.Span(string.IsNullOrWhiteSpace(title) ? "—" : title).FontSize(8).FontColor("#333333");
                        });
                        if (detailed && !string.IsNullOrWhiteSpace(x.Details))
                            cc.Item().Text(x.Details).FontSize(7).Italic().FontColor(Muted);
                        if (!string.IsNullOrWhiteSpace(x.SqlInstance))
                            cc.Item().Text(x.SqlInstance).FontSize(7).FontColor(Faint);
                    });
                    table.Cell().Background(bg).PaddingVertical(3).Text(string.IsNullOrWhiteSpace(x.ImpactDescription) ? "—" : x.ImpactDescription)
                        .FontSize(7.5f).FontColor("#555555");
                    table.Cell().Background(bg).PaddingVertical(3).Text(string.IsNullOrWhiteSpace(x.ComplexityDescription) ? "—" : x.ComplexityDescription)
                        .FontSize(7.5f).FontColor("#555555");
                    table.Cell().Background(bg).PaddingVertical(3).Text(t =>
                    {
                        t.Span(x.FailedItems.ToString()).FontSize(8).FontColor(x.FailedHigh > 0 ? AssessmentPdf.Fail(cb) : "#555555");
                    });
                    table.Cell().Background(bg).PaddingVertical(3).Text(x.Hours > 0 ? x.Hours.ToString("0.#") : "—").FontSize(7.5f).FontColor("#555555");
                }
            });

        private static void Donut(IContainer c, IReadOnlyList<AssessmentPdf.DonutSegment> segs, string big, string small, string bigColor, float sizePt)
        {
            // Reuse AssessmentPdf's PNG donut via its public DonutSegment type would need a public
            // renderer; instead draw the same ring inline using the shared segment list + colours.
            // Centre label overlaid as text (font/version-safe), matching AssessmentPdf.
            c.Width(sizePt).Height(sizePt).Layers(layers =>
            {
                layers.Layer().Image(AssessmentPdf.DonutPngPublic(segs)).FitArea();
                layers.PrimaryLayer().AlignCenter().AlignMiddle().Column(col =>
                {
                    col.Item().AlignCenter().Text(big).FontSize(sizePt * 0.16f).Bold().FontColor(bigColor);
                    if (!string.IsNullOrEmpty(small))
                        col.Item().AlignCenter().Text(small).FontSize(sizePt * 0.075f).FontColor(Muted);
                });
            });
        }

        private static void Footer(IContainer c, string footerMeta) =>
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
    }
}
