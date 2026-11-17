using Oranda.Core.Schema.TableEdit.Partitions;

namespace Oranda.Core.Tests.Schema.TableEdit.Partitions;

/// <summary>「경계 채우기…」(설계 18.4, 시안 ⑥).</summary>
public class BoundFillerTests
{
    [Fact]
    public void Monthly_from_2026_01_makes_twelve_rows_named_by_month_with_next_month_bounds()
    {
        var rows = BoundFiller.Fill(new BoundFill(BoundUnit.Month, 12) { StartDate = new DateOnly(2026, 1, 1) });

        Assert.Equal(12, rows.Count);
        Assert.Equal(("P_2026_01", "DATE '2026-02-01'"), (rows[0].Name, rows[0].Bound));
        Assert.Equal(("P_2026_12", "DATE '2027-01-01'"), (rows[11].Name, rows[11].Bound));
        Assert.Equal(12, rows.Select(r => r.RowKey).Distinct().Count());
    }

    [Fact]
    public void Day_year_and_quarter_steps()
    {
        var days = BoundFiller.Fill(new BoundFill(BoundUnit.Day, 2) { StartDate = new DateOnly(2026, 2, 28) });
        Assert.Equal([("P_2026_02_28", "DATE '2026-03-01'"), ("P_2026_03_01", "DATE '2026-03-02'")], days.Select(r => (r.Name, r.Bound)));

        var years = BoundFiller.Fill(new BoundFill(BoundUnit.Year, 2) { StartDate = new DateOnly(2025, 1, 1), Prefix = "Y", Tablespace = "TS" });
        Assert.Equal([("Y2025", "DATE '2026-01-01'", "TS"), ("Y2026", "DATE '2027-01-01'", "TS")], years.Select(r => (r.Name, r.Bound, r.Tablespace)));

        var quarters = BoundFiller.Fill(new BoundFill(BoundUnit.Month, 2, Step: 3) { StartDate = new DateOnly(2026, 1, 1) });
        Assert.Equal([("P_2026_01", "DATE '2026-04-01'"), ("P_2026_04", "DATE '2026-07-01'")], quarters.Select(r => (r.Name, r.Bound)));
    }

    [Fact]
    public void Numbers_use_the_start_in_the_name_and_the_next_start_as_bound()
    {
        var rows = BoundFiller.Fill(new BoundFill(BoundUnit.Number, 3, Step: 1000) { StartNumber = -1000 });

        Assert.Equal([("P_M1000", "0"), ("P_0", "1000"), ("P_1000", "2000")], rows.Select(r => (r.Name, r.Bound)));
        Assert.Equal([("P_0_5", "1.5")], BoundFiller.Fill(new BoundFill(BoundUnit.Number, 1) { StartNumber = 0.5m }).Select(r => (r.Name, r.Bound)));
    }

    [Fact]
    public void Existing_names_are_skipped()
    {
        var rows = BoundFiller.Fill(new BoundFill(BoundUnit.Month, 3) { StartDate = new DateOnly(2026, 9, 1) }, ["P_2026_10"]);

        Assert.Equal(["P_2026_09", "P_2026_11"], rows.Select(r => r.Name));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public void Count_and_step_must_be_positive(int count, int step) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => BoundFiller.Fill(new BoundFill(BoundUnit.Month, count, step)));
}
