/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Linq;
using SQLTriage.Components.Shared;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Pins the DataGrid sort key against the crash found live on 2026-07-30: the old key
    /// returned decimal OR string per cell, so a numeric column containing one DBNull produced
    /// mixed key types and OrderBy threw "Object must be of type Decimal" — unmounting the whole
    /// page. The compliance grid's Compliance % column (un-scored rows are DBNull by design) was
    /// the first real data to hit it, but ANY grid column mixing numbers and blanks would have.
    /// </summary>
    public class DataGridSortKeyTests
    {
        [Fact]
        public void Mixed_numbers_text_and_dbnull_sort_without_throwing()
        {
            var cells = new object?[] { 45.0, DBNull.Value, "n/a", 100, null, 9.5, "", "zeta" };

            // The regression: this exact OrderBy is what the grid runs. It must not throw.
            var sorted = cells.OrderBy(DataGrid.ToSortKey).ToArray();

            // Numbers first (numeric order), then text (ordinal), then blanks last.
            Assert.Equal(9.5, sorted[0]);
            Assert.Equal(45.0, sorted[1]);
            Assert.Equal(100, sorted[2]);
            Assert.Equal("n/a", sorted[3]);
            Assert.Equal("zeta", sorted[4]);
            Assert.All(sorted.Skip(5), v => Assert.True(v is null || v is DBNull || (v as string)?.Length == 0));
        }

        [Fact]
        public void Numeric_order_is_numeric_not_lexical()
        {
            var sorted = new object?[] { "10", "9", "2" }.OrderBy(DataGrid.ToSortKey).ToArray();
            Assert.Equal(new object?[] { "2", "9", "10" }, sorted);
        }

        [Fact]
        public void Descending_puts_blanks_first_and_that_is_the_accepted_tradeoff()
        {
            // A composite key cannot pin blanks to the bottom in BOTH directions without a
            // custom comparer; blanks-first on a descending sort is acceptable, crashing is not.
            var sorted = new object?[] { 45.0, DBNull.Value, 9.5 }
                .OrderByDescending(DataGrid.ToSortKey).ToArray();
            Assert.Equal(DBNull.Value, sorted[0]);
            Assert.Equal(45.0, sorted[1]);
            Assert.Equal(9.5, sorted[2]);
        }
    }
}
