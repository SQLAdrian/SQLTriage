/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using SQLTriage.Data.Services.Licensing;
using Xunit;

namespace SQLTriage.Tests.Licensing;

/// <summary>
/// The EXCLUDED signal that pairs with the report filter. The whole point of this type is that a
/// seat-gated read is never a SILENT drop: whatever the filter removes, it also names, so every
/// surface can render an honest banner.
/// </summary>
public sealed class SeatFilterTests
{
    [Fact]
    public void NothingExcluded_IsComplete_AndHasNoNotice()
    {
        var f = new SeatFilter(new[] { "A", "B" }, Array.Empty<string>());
        Assert.True(f.IsComplete);
        Assert.Null(f.ExclusionNotice);
    }

    [Fact]
    public void OneExcluded_UsesSingularCopy_AndNamesIt()
    {
        var f = new SeatFilter(new[] { "A" }, new[] { "GHOST" });
        Assert.False(f.IsComplete);
        Assert.Equal("1 instance excluded: not covered by your licence (GHOST).", f.ExclusionNotice);
    }

    [Fact]
    public void ManyExcluded_UsesPluralCopy_AndStatesTheCount()
    {
        // The brief's example shape: "3 instances excluded: not covered by your licence".
        var f = new SeatFilter(new[] { "A" }, new[] { "X", "Y", "Z" });
        Assert.StartsWith("3 instances excluded: not covered by your licence", f.ExclusionNotice);
    }

    [Fact]
    public void LongExclusionList_IsTruncated_ButTheCountStaysTrue()
    {
        // The count must remain exact even though the names are elided — a truncated list that
        // implied only 5 were dropped would be a lie.
        var excluded = Enumerable.Range(1, 12).Select(i => "SRV" + i).ToArray();
        var f = new SeatFilter(Array.Empty<string>(), excluded);

        Assert.StartsWith("12 instances excluded", f.ExclusionNotice);
        Assert.Contains("…", f.ExclusionNotice);
        Assert.Contains("SRV1", f.ExclusionNotice);
        Assert.DoesNotContain("SRV12", f.ExclusionNotice);   // beyond the first 5
    }

    [Fact]
    public void ExclusionNotice_IsNonBlaming_AndNamesTheLicenceAsTheReason()
    {
        // Copy guard: honest and neutral. It states a licence boundary — it does not accuse the
        // operator of anything, and it does not upsell.
        var notice = new SeatFilter(Array.Empty<string>(), new[] { "A" }).ExclusionNotice!;
        Assert.Contains("not covered by your licence", notice);
        foreach (var banned in new[] { "unauthorised", "unauthorized", "illegal", "violation", "exceeded your" })
            Assert.DoesNotContain(banned, notice, StringComparison.OrdinalIgnoreCase);
    }
}
