/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.PerformanceReport;
using Xunit;

namespace SQLTriage.Tests.Private;

/// <summary>
/// reports-r1-04 (honesty-hunt lane 6, 2026-08-27): a collection that THREW rendered identically to
/// a collection that measured nothing.
///
/// <para>⚠⚠ THIS FILE IS NOT IN EITHER SHIPPED SUITE, and nothing here may be counted inside a
/// "suite green" claim. <c>Data\Services\PerformanceReport\**</c> is Compile-Removed from the
/// default AND the community build (buildprofile.targets), so the composer, the snapshot model and
/// the report's PDF builder exist only under <c>-p:SQLTriagePrivate=true</c>. The test project
/// mirrors that condition on this whole folder, so CI compiles neither the code under test nor this
/// file. That gate is exactly why this finding sat at "untested" through the hunt: nobody could run
/// the path at all. Same standing as the sibling <see cref="PerformanceReportScopeLiveHarness"/>.</para>
///
/// <para>THE DEFECT, as filed. <c>PerformanceReportComposer.ComposeAsync</c> guarded the Disk I/O
/// and Code Hotspots collections with a catch that only logged a warning, leaving each section at
/// its all-default state. The PDF then printed "No disk telemetry returned for this instance" plus
/// a "0s sample window" header. Both are claims about the SERVER. Neither was earned: the reader
/// was told the instance is quiet when the truth was that SQLTriage could not look. The sibling
/// <c>IndexSection.RankingFailure</c> was added for this precise defect on 2026-08-14 and the two
/// point-in-time sections beside it were missed.</para>
///
/// <para>UNLIKE the live harness in this folder, these are offline. A real collection fault is not
/// reliably inducible against a healthy local instance, so the fault is induced at the collaborator:
/// the composer's own guards are called with absent services, the real section builders throw inside
/// the real catches, and the section that comes back is then rendered by the real PDF builder. The
/// render-only tests above are driven from constructed sections; the wiring claim they rest on —
/// that the composer produces exactly this shape, carrying exactly the exception's own message — is
/// made by <see cref="ADiskReadThatThrows_ComesBackAsTheExceptionsOwnMessage_AndThatMessageIsWhatThePdfPrints"/>
/// and its two siblings.</para>
///
/// <para>INVOCATION:</para>
/// <code>
///   dotnet test Tests/SQLTriage.Tests -c Debug -p:SQLTriagePrivate=true `
///       --filter "FullyQualifiedName~PerformanceReportCollectionFailureTests"
/// </code>
/// </summary>
public sealed class PerformanceReportCollectionFailureTests
{
    static PerformanceReportCollectionFailureTests()
    {
        // App.xaml.cs sets this at startup; tests bypass startup, so set it here.
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
    }

    /// <summary>Whitespace-stripped PDF text. Skia writes kerned glyph runs, so "0s sample" comes
    /// back split; the same basis <c>ReportBundleServiceTests.PdfText</c> uses.</summary>
    private static string Text(byte[] pdf) =>
        Regex.Replace(SQLTriage.Tests.PdfTextExtractor.Extract(pdf), @"\s+", "");

    private static PerformanceReportModel BaseModel() => new()
    {
        ConnectionAlias = "probe",
        CanonicalServerName = "PROBE01",
        AppVersion = "test",
        GeneratedUtc = "2026-08-27T00:00Z",
        TimezoneId = "UTC",
        RunId = "r104test",
    };

    // ── The defect arm: a read that threw ────────────────────────────────────────────────────

    [Fact]
    public void A_disk_read_that_threw_says_so_and_prints_no_sample_window()
    {
        var m = BaseModel();
        m.DiskIo = new DiskIoSection { CollectionFailure = "Timeout expired before the drive read completed." };

        var text = Text(AssessmentPdf.BuildPerformanceAnalysisReport(m));

        // The extractor is genuinely reading this document, or every assertion below is vacuous.
        Assert.Contains("DiskI/O", text);

        Assert.Contains("Disktelemetrycouldnotbecollected", text);
        Assert.Contains("Timeoutexpiredbeforethedrivereadcompleted", text);
        Assert.Contains("Treatitasunknown,notasclear", text);

        // The two fabrications this closes: the server claim, and the measured-looking window.
        Assert.DoesNotContain("Nodisktelemetryreturnedforthisinstance", text);
        Assert.DoesNotContain("0ssamplewindow", text);
        Assert.Contains("Nosamplewindowwasmeasured", text);
    }

    [Fact]
    public void A_plan_cache_read_that_threw_says_so()
    {
        var m = BaseModel();
        m.Hotspots = new HotspotSection { CollectionFailure = "The SELECT permission was denied on sys.dm_exec_query_stats." };

        var text = Text(AssessmentPdf.BuildPerformanceAnalysisReport(m));

        Assert.Contains("CodeHotspots", text);
        Assert.Contains("Plan-cacheactivitycouldnotbecollected", text);
        Assert.Contains("SELECTpermissionwasdenied", text);
        Assert.DoesNotContain("Noplan-cacheactivityreturnedforthisinstance", text);
    }

    [Fact]
    public void A_maintenance_advisory_that_threw_says_so()
    {
        var m = BaseModel();
        m.Maintenance = new MaintenanceSection { CollectionFailure = "The maintenance script service is not available." };

        var text = Text(AssessmentPdf.BuildPerformanceAnalysisReport(m));

        Assert.Contains("Themaintenanceadvisorycouldnotbecollected", text);
        Assert.DoesNotContain("Nomaintenancerecommendationgeneratedforthisinstance", text);
    }

    // ── The control arm: nothing measured, nothing thrown ────────────────────────────────────
    //
    // Without this the three tests above would pass on a builder that had simply stopped rendering
    // the empty states at all. The honest "we looked and found nothing" sentence must survive, and
    // must NOT wear the failure wording.

    [Fact]
    public void A_genuinely_empty_collection_still_reads_as_empty_and_not_as_a_failure()
    {
        var m = BaseModel();
        m.DiskIo = new DiskIoSection { SampleWindowSeconds = 30 };   // sampled, found nothing
        m.Hotspots = new HotspotSection();
        m.Maintenance = new MaintenanceSection { Generated = false };

        var text = Text(AssessmentPdf.BuildPerformanceAnalysisReport(m));

        Assert.Contains("Nodisktelemetryreturnedforthisinstance", text);
        Assert.Contains("Noplan-cacheactivityreturnedforthisinstance", text);
        Assert.Contains("Nomaintenancerecommendationgeneratedforthisinstance", text);
        Assert.Contains("30ssamplewindow", text);

        Assert.DoesNotContain("couldnotbecollected", text);
        Assert.DoesNotContain("Treatitasunknown,notasclear", text);
    }

    // ── The DTO's own rule, on its own ───────────────────────────────────────────────────────

    [Fact]
    public void AFailedCollectionHasNoSampleWindowToPrint()
    {
        // Narrow by design: this is the DiskIoSection RULE, nothing more. It used to be titled
        // "TheComposersCatchProducesTheShapeRenderedHere" and its comment claimed to assert the
        // composer's own assignment; the body constructs three literals and never mentions
        // PerformanceReportComposer. The wiring claim is now made by the test below, which calls it.
        Assert.False(new DiskIoSection { CollectionFailure = "boom" }.SampleWindowMeasured);
        Assert.True(new DiskIoSection { SampleWindowSeconds = 12 }.SampleWindowMeasured);

        var neverRan = new DiskIoSection();
        Assert.False(neverRan.SampleWindowMeasured,
            "a zero-second window on a section nothing populated is a fabricated measurement, not a "
            + "short sample, and it printed as '0s sample window' for the whole life of this defect");
    }

    // ── The seam: the composer's OWN catch, driven ───────────────────────────────────────────
    //
    // These call PerformanceReportComposer.ComposeAsync's three point-in-time guards directly (the
    // named internal methods ComposeAsync now calls) with collaborators that are absent, so the real
    // section builder really throws inside the real guard. What comes back is the section the
    // composer produces on a collection fault, and it is then rendered by the real PDF builder — so
    // one test spans throw → section → delivered bytes.
    //
    // Reverting any of those three catches to the pre-fix log-and-continue form fails these: the
    // section comes back all-default, CollectionFailure is null, and the PDF prints the server claim
    // again. That is the mutation the previous version of this file did not catch.
    //
    // A hard connect failure returns early from ComposeAsync (Succeeded=false) and never reaches
    // these guards, which is why they are driven directly rather than through ComposeAsync: the
    // live-collection phase would need a reachable instance, and this file is offline.

    /// <summary>A composer whose collaborators are absent, so the first read in each section builder
    /// throws. The constructor only assigns fields, so this is the real production object.</summary>
    private static PerformanceReportComposer ComposerWhoseReadsThrow() =>
        new(null!, null!, null!, null!, null!, null!, log: null);

    private static ServerConnection Conn() => new()
    {
        Id = "probe", ServerNames = "probe-target", UseWindowsAuthentication = true,
    };

    /// <summary>The runtime's own message for the throw these guards catch, taken from the runtime
    /// rather than hard-coded, so the exact-match assertions below cannot be satisfied by a guard
    /// that invents its own text and cannot fail on a machine whose CLR resources differ.</summary>
    private static string ThrownMessage()
    {
        try { object? nothing = null; _ = nothing!.ToString(); }
        catch (Exception ex) { return ex.Message; }
        return "unreachable";
    }

    [Fact]
    public async Task ADiskReadThatThrows_ComesBackAsTheExceptionsOwnMessage_AndThatMessageIsWhatThePdfPrints()
    {
        var section = await ComposerWhoseReadsThrow()
            .DiskIoSectionOrFailureAsync(Conn(), "probe", CancellationToken.None);

        Assert.Equal(ThrownMessage(), section.CollectionFailure);
        Assert.False(section.SampleWindowMeasured, "a failed collection has no sample window");

        var m = BaseModel();
        m.DiskIo = section;
        var text = Text(AssessmentPdf.BuildPerformanceAnalysisReport(m));

        Assert.Contains("Disktelemetrycouldnotbecollected", text);
        Assert.Contains(Squash(section.CollectionFailure!), text);
        Assert.DoesNotContain("Nodisktelemetryreturnedforthisinstance", text);
        Assert.DoesNotContain("0ssamplewindow", text);
    }

    [Fact]
    public async Task APlanCacheReadThatThrows_ComesBackAsTheExceptionsOwnMessage()
    {
        var section = await ComposerWhoseReadsThrow()
            .HotspotSectionOrFailureAsync(Conn(), "probe", CancellationToken.None);

        Assert.Equal(ThrownMessage(), section.CollectionFailure);

        var m = BaseModel();
        m.Hotspots = section;
        var text = Text(AssessmentPdf.BuildPerformanceAnalysisReport(m));

        Assert.Contains("Plan-cacheactivitycouldnotbecollected", text);
        Assert.Contains(Squash(section.CollectionFailure!), text);
        Assert.DoesNotContain("Noplan-cacheactivityreturnedforthisinstance", text);
    }

    [Fact]
    public async Task AMaintenanceAdvisoryThatThrows_ComesBackAsTheExceptionsOwnMessage()
    {
        var section = await ComposerWhoseReadsThrow()
            .MaintenanceSectionOrFailureAsync("probe", CancellationToken.None);

        Assert.Equal(ThrownMessage(), section.CollectionFailure);

        var m = BaseModel();
        m.Maintenance = section;
        var text = Text(AssessmentPdf.BuildPerformanceAnalysisReport(m));

        Assert.Contains("Themaintenanceadvisorycouldnotbecollected", text);
        Assert.DoesNotContain("Nomaintenancerecommendationgeneratedforthisinstance", text);
    }

    /// <summary>Whitespace-stripped, to the same basis <see cref="Text"/> puts the PDF on.</summary>
    private static string Squash(string s) => Regex.Replace(s, @"\s+", "");
}
