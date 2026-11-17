using Oranda.Core.Schema.TableEdit.Partitions;

namespace Oranda.Core.Tests.Schema.TableEdit.Partitions;

/// <summary>파티션 경계 보통 값 ↔ SQL, 다음 파티션·이름·자동 추가 짐작(설계 18.4 쉬운 편집).</summary>
public class PartitionBoundsTests
{
    [Theory]
    [InlineData("TO_DATE(' 2026-10-01 00:00:00', 'SYYYY-MM-DD HH24:MI:SS', 'NLS_CALENDAR=GREGORIAN')", "2026-10-01")]
    [InlineData("TIMESTAMP' 2026-10-01 00:00:00'", "2026-10-01")]
    [InlineData("TIMESTAMP' 2026-10-01 12:30:00'", "2026-10-01 12:30:00")]
    [InlineData("DATE '2026-12-01'", "2026-12-01")]
    [InlineData("1000", "1000")]
    [InlineData("MAXVALUE", "MAXVALUE")]
    [InlineData("'A', 'B'", "A, B")]
    [InlineData("'A,B'", "'A,B'")]
    [InlineData("SYSDATE + 1", "SYSDATE + 1")]
    [InlineData(null, "")]
    public void Server_bounds_read_as_plain_values(string? bound, string plain) => Assert.Equal(plain, PartitionBounds.ToPlain(bound));

    /// <summary>
    /// Tibero 실측(te-w30-verify): 사전 BOUND 는 만든 문장 글 그대로이고, INTERVAL 로 서버가 만든 파티션은 <c>DD-MM-YYYY HH24:mi:ss</c> 꼴이다.
    /// TO_DATE 는 날짜 형식 글을 읽어 보통 값으로 바꾼다(모르는 형식 요소면 그대로).
    /// </summary>
    [Theory]
    [InlineData("TO_DATE('01-02-2027 00:00:00', 'DD-MM-YYYY HH24:mi:ss')", "2027-02-01")]
    [InlineData("TO_DATE('01-02-2027 13:30:05', 'DD-MM-YYYY HH24:mi:ss')", "2027-02-01 13:30:05")]
    [InlineData("TO_DATE('2026-11-01', 'YYYY-MM-DD')", "2026-11-01")]
    [InlineData("TO_DATE('2026-12-01 13:30:00', 'YYYY-MM-DD HH24:MI:SS')", "2026-12-01 13:30:00")]
    [InlineData("TO_DATE('2026/11/01', 'YYYY/MM/DD')", "2026-11-01")]
    [InlineData("to_date('20261101', 'yyyymmdd')", "2026-11-01")]
    [InlineData("TO_DATE('01-NOV-2026', 'DD-MON-YYYY')", "TO_DATE('01-NOV-2026', 'DD-MON-YYYY')")]
    [InlineData("TO_DATE('31-02-2027', 'DD-MM-YYYY')", "TO_DATE('31-02-2027', 'DD-MM-YYYY')")]
    [InlineData("TO_DATE('01-02-2027', 'DD-MM-YYYY'), 5", "2027-02-01, 5")]
    [InlineData("TO_DATE('2026-11-01', 'YYYY-MM-DD HH24:MI:SS')", "2026-11-01")] // 값이 형식보다 짧다(시각 생략) — 서버는 받는다
    [InlineData("TO_DATE('2027', 'YYYY')", "TO_DATE('2027', 'YYYY')")] // 빠진 월 = 서버는 이번 달, .NET 은 1월 → 읽지 않는다
    [InlineData("TO_DATE('13:00:00', 'HH24:MI:SS')", "TO_DATE('13:00:00', 'HH24:MI:SS')")]
    [InlineData("TO_DATE('11-2026', 'MM-YYYY')", "TO_DATE('11-2026', 'MM-YYYY')")]
    public void To_date_bounds_read_with_their_format_mask(string bound, string plain) => Assert.Equal(plain, PartitionBounds.ToPlain(bound));

    [Fact]
    public void Server_made_interval_bound_continues_as_a_date()
    {
        string[] bounds = ["DATE '2026-11-01'", "TO_DATE('01-12-2026 00:00:00', 'DD-MM-YYYY HH24:mi:ss')"];
        Assert.Equal(new DateTime(2026, 12, 1), PartitionBounds.AsDate(bounds[1]));
        Assert.Equal(("P_2026_12", "DATE '2027-01-01'"), PartitionBounds.NextRange(bounds));
    }

    [Theory]
    [InlineData("2026-12-01", PartitionKeyKind.Date, "DATE '2026-12-01'")]
    [InlineData("2026-12", PartitionKeyKind.Date, "DATE '2026-12-01'")]
    [InlineData("2026-12-01 13:00:00", PartitionKeyKind.Date, "TO_DATE('2026-12-01 13:00:00', 'YYYY-MM-DD HH24:MI:SS')")]
    [InlineData("DATE '2026-12-01'", PartitionKeyKind.Date, "DATE '2026-12-01'")]
    [InlineData("MAXVALUE", PartitionKeyKind.Date, "MAXVALUE")]
    [InlineData("A, B", PartitionKeyKind.Text, "'A', 'B'")]
    [InlineData("O'NEIL", PartitionKeyKind.Text, "'O''NEIL'")]
    [InlineData("'A'", PartitionKeyKind.Text, "'A'")]
    [InlineData("1000", PartitionKeyKind.Number, "1000")]
    [InlineData("  ", PartitionKeyKind.Number, null)]
    public void Plain_values_become_sql(string plain, PartitionKeyKind kind, string? sql) => Assert.Equal(sql, PartitionBounds.ToSql(plain, kind));

    [Fact]
    public void Plain_and_sql_round_trip_for_dates_and_text()
    {
        Assert.Equal("2026-12-01", PartitionBounds.ToPlain(PartitionBounds.ToSql("2026-12-01", PartitionKeyKind.Date)));
        Assert.Equal("A, B", PartitionBounds.ToPlain(PartitionBounds.ToSql("A, B", PartitionKeyKind.Text)));
    }

    [Theory]
    [InlineData(PartitionKeyKind.Date, "DATE")]
    [InlineData(PartitionKeyKind.Date, "TIMESTAMP(6)")]
    [InlineData(PartitionKeyKind.Number, "NUMBER")]
    [InlineData(PartitionKeyKind.Text, "VARCHAR2")]
    [InlineData(PartitionKeyKind.Other, "CLOB")]
    public void Key_kind_follows_the_column_type(PartitionKeyKind kind, string type) => Assert.Equal(kind, PartitionBounds.KindOf(type));

    [Fact]
    public void Next_range_continues_the_monthly_step()
    {
        var next = PartitionBounds.NextRange(["TO_DATE(' 2026-10-01 00:00:00', 'SYYYY-MM-DD HH24:MI:SS', 'NLS_CALENDAR=GREGORIAN')", "DATE '2026-11-01'"]);
        Assert.Equal(("P_2026_11", "DATE '2026-12-01'"), next);
    }

    [Fact]
    public void Next_range_continues_yearly_daily_and_numeric_steps()
    {
        Assert.Equal(("P_2026", "DATE '2027-01-01'"), PartitionBounds.NextRange(["DATE '2025-01-01'", "DATE '2026-01-01'"]));
        Assert.Equal(("P_2026_11_02", "DATE '2026-11-03'"), PartitionBounds.NextRange(["DATE '2026-11-01'", "DATE '2026-11-02'"]));
        Assert.Equal(("P_2000", "3000"), PartitionBounds.NextRange(["1000", "2000"]));
        Assert.Equal(("P_2026_11", "DATE '2026-12-01'"), PartitionBounds.NextRange(["DATE '2026-11-01'"]));
        Assert.Null(PartitionBounds.NextRange(["MAXVALUE"]));
        Assert.Null(PartitionBounds.NextRange([]));
    }

    [Fact]
    public void Names_come_from_the_period_start()
    {
        Assert.Equal("P_2026_11", PartitionBounds.SuggestName("DATE '2026-11-01'", "DATE '2026-12-01'", PartitionMethod.Range));
        Assert.Equal("P_BEFORE_2026_11", PartitionBounds.SuggestName(null, "DATE '2026-11-01'", PartitionMethod.Range));
        Assert.Equal("P_1000", PartitionBounds.SuggestName("1000", "2000", PartitionMethod.Range));
        Assert.Equal("P_SEOUL", PartitionBounds.SuggestName(null, "'Seoul', 'Busan'", PartitionMethod.List));
        Assert.Null(PartitionBounds.SuggestName(null, "MAXVALUE", PartitionMethod.Range));
    }

    [Theory]
    [InlineData(null, IntervalUnit.Off)]
    [InlineData("NUMTOYMINTERVAL(1,'MONTH')", IntervalUnit.Month)]
    [InlineData("NUMTOYMINTERVAL(1, 'YEAR')", IntervalUnit.Year)]
    [InlineData("numtodsinterval(1,'day')", IntervalUnit.Day)]
    [InlineData("1000", IntervalUnit.Custom)]
    public void Interval_text_maps_to_units(string? interval, IntervalUnit unit) => Assert.Equal(unit, PartitionBounds.UnitOf(interval));

    [Fact]
    public void Interval_unit_is_guessed_from_the_bounds()
    {
        Assert.Equal(IntervalUnit.Month, PartitionBounds.GuessUnit(["DATE '2026-10-01'", "DATE '2026-11-01'"]));
        Assert.Equal(IntervalUnit.Year, PartitionBounds.GuessUnit(["DATE '2025-01-01'", "DATE '2026-01-01'"]));
        Assert.Equal(IntervalUnit.Day, PartitionBounds.GuessUnit(["DATE '2026-11-01'", "DATE '2026-11-02'"]));
        Assert.Equal(IntervalUnit.Month, PartitionBounds.GuessUnit([]));
    }

    [Fact]
    public void Period_start_is_the_start_of_today_s_period()
    {
        var today = new DateTime(2026, 11, 18, 9, 30, 0, DateTimeKind.Unspecified);
        Assert.Equal(new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Unspecified), PartitionBounds.PeriodStart(today, IntervalUnit.Month));
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified), PartitionBounds.PeriodStart(today, IntervalUnit.Year));
        Assert.Equal(new DateTime(2026, 11, 18, 0, 0, 0, DateTimeKind.Unspecified), PartitionBounds.PeriodStart(today, IntervalUnit.Day));
    }
}
