using Oranda.App.Features.TableEdit;
using Oranda.Core.Schema.TableEdit.Partitions;
using static Oranda.App.Tests.Features.TableEdit.TableEditPartitionSessionTests;
using static Oranda.App.Tests.Features.TableEdit.TableEditTestSupport;

namespace Oranda.App.Tests.Features.TableEdit;

/// <summary>
/// 파티션 쉬운 편집(설계 18.4, 2026-11-18 「쉽게 바꾸고 추가는 자동으로」): 쉬운 설정(일반 → 범위 + 자동 추가), 자동 추가 칸·켜기 안내,
/// 보통 값 경계와 자동 이름, 자동 추가가 켜진 테이블의 「＋」. 실제 Core 엔진·Oracle 기본 규칙.
/// </summary>
public class TableEditPartitionEasyTests
{
    private static readonly DateTime Today = new(2026, 11, 18, 0, 0, 0, DateTimeKind.Unspecified);

    private static List<string> Texts(TableEditSession s) => [.. s.Script.Statements.Select(x => x.Statement.Text)];

    private static TableEditSession Normal(OracleRules? rules = null)
    {
        var s = Create(WithOrderDate(OrdersTable()), rules);
        s.Today = () => Today;
        return s;
    }

    [Fact]
    public void Easy_setup_offers_the_date_columns_and_makes_a_monthly_interval_table()
    {
        var s = Normal();
        Assert.True(s.ShowsEasyPartitioning);
        Assert.Equal(["ORDER_DATE"], s.EasyDateColumns);
        Assert.Equal("ORDER_DATE", s.EasyPartitionColumn);
        Assert.Contains("P_BEFORE_2026_11", s.EasyPartitionSummary, StringComparison.Ordinal);

        Assert.True(s.ApplyEasyPartitioning());

        Assert.False(s.ShowsEasyPartitioning);
        Assert.True(s.IsPartitionConversion);
        var row = Assert.Single(s.PartitionRows);
        Assert.Equal("P_BEFORE_2026_11", row.Name);
        Assert.Equal("2026-11-01", row.BoundText);
        Assert.Equal("NUMTOYMINTERVAL(1, 'MONTH')", s.PartitionDraft!.Scheme!.Interval);
        var text = Assert.Single(Texts(s));
        Assert.Contains("PARTITION BY RANGE (ORDER_DATE) INTERVAL (NUMTOYMINTERVAL(1, 'MONTH'))", text, StringComparison.Ordinal);
        Assert.Contains("PARTITION P_BEFORE_2026_11 VALUES LESS THAN (DATE '2026-11-01')", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Easy_setup_by_year_starts_at_new_year()
    {
        var s = Normal();
        s.EasyPartitionUnitIndex = 2;

        Assert.True(s.ApplyEasyPartitioning());

        Assert.Equal("P_BEFORE_2026", s.PartitionRows[0].Name);
        Assert.Equal("2026-01-01", s.PartitionRows[0].BoundText);
        Assert.Equal("NUMTOYMINTERVAL(1, 'YEAR')", s.PartitionDraft!.Scheme!.Interval);
    }

    [Fact]
    public void Without_automatic_add_the_easy_setup_creates_twelve_periods_ahead()
    {
        var s = Normal(new OracleRules { UnsupportedPartition = op => op == PartitionOperation.SetInterval ? "x" : null });

        Assert.Contains("12", s.EasyPartitionSummary, StringComparison.Ordinal);
        Assert.True(s.ApplyEasyPartitioning());

        Assert.Null(s.PartitionDraft!.Scheme!.Interval);
        Assert.Equal(13, s.PartitionRows.Count);
        Assert.Equal("P_2026_11", s.PartitionRows[1].Name);
        Assert.Equal("2026-12-01", s.PartitionRows[1].BoundText);
    }

    [Fact]
    public void A_table_without_date_columns_gets_no_easy_setup_button_but_says_why()
    {
        var s = Create(OrdersTable());
        Assert.True(s.ShowsEasyPartitioning);
        Assert.False(s.HasEasyDateColumns);
        Assert.False(s.ApplyEasyPartitioning());
        Assert.Contains("날짜 열이 없어", s.EasyPartitionSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void Monthly_table_without_interval_offers_automatic_add_in_one_click()
    {
        var s = Create(PartitionedOrders());
        Assert.True(s.ShowsAutoAddOffer);
        Assert.Equal("매월 자동 추가 켜기", s.AutoAddOfferButtonText);
        Assert.Equal("끔", s.PartitionIntervalChoice);

        s.EnableAutoAdd();

        Assert.False(s.ShowsAutoAddOffer);
        Assert.Equal("매월", s.PartitionIntervalChoice);
        Assert.Equal(["ALTER TABLE SAMPLE.ORDERS SET INTERVAL (NUMTOYMINTERVAL(1, 'MONTH'))"], Texts(s));
        Assert.False(s.CanAddPartition); // 자동 추가가 켜지면 DB 가 만든다(ADD 는 ORA-14760)
    }

    [Fact]
    public void Interval_choice_turns_automatic_add_on_and_off_and_custom_shows_the_expression()
    {
        var s = Create(PartitionedOrders(interval: "NUMTOYMINTERVAL(1,'MONTH')"));
        Assert.Equal("매월", s.PartitionIntervalChoice);
        Assert.False(s.ShowsIntervalText);
        Assert.False(s.CanAddPartition);
        Assert.Contains("자동 추가가 켜져", s.AddPartitionTip, StringComparison.Ordinal);

        s.PartitionIntervalChoice = "끔";
        Assert.Equal(["ALTER TABLE SAMPLE.ORDERS SET INTERVAL ()"], Texts(s));
        Assert.True(s.CanAddPartition);

        s.PartitionIntervalChoice = "직접 입력…";
        Assert.True(s.ShowsIntervalText);
        s.PartitionIntervalText = "NUMTOYMINTERVAL(3, 'MONTH')";
        Assert.Equal("직접 입력…", s.PartitionIntervalChoice);
        Assert.Equal(["ALTER TABLE SAMPLE.ORDERS SET INTERVAL (NUMTOYMINTERVAL(3, 'MONTH'))"], Texts(s));
    }

    [Fact]
    public void Bounds_show_as_plain_dates_and_typed_dates_become_sql_with_a_name()
    {
        var s = Create(PartitionedOrders(lastBound: "TO_DATE(' 2026-11-01 00:00:00', 'SYYYY-MM-DD HH24:MI:SS', 'NLS_CALENDAR=GREGORIAN')"));
        Assert.Equal("2026-11-01", s.PartitionRows[^1].BoundText);
        Assert.Equal("예: 2026-12-01 (이 날짜 미만)", s.BoundPlaceholder);

        var row = s.AddPartition()!;
        Assert.Equal("P_2026_11", row.Row.Name);

        row.BoundText = "2027-01-01"; // 두 달짜리로 바꿔도 이름은 구간 시작
        Assert.Equal("P_2026_11", row.Row.Name);
        Assert.Equal("DATE '2027-01-01'", row.Row.Bound);

        row.Name = "P_LAST";
        row.BoundText = "2027-02-01"; // 사람이 지은 이름은 그대로
        Assert.Equal("P_LAST", row.Row.Name);
        Assert.Equal(["ALTER TABLE SAMPLE.ORDERS ADD PARTITION P_LAST VALUES LESS THAN (DATE '2027-02-01')"], Texts(s));
    }

    [Fact]
    public void Add_label_says_next_partition_for_ranges()
    {
        Assert.Equal("＋ 다음 파티션", Create(PartitionedOrders()).AddPartitionText);
        Assert.Equal("＋ 더하기", Create(PartitionedOrders("LIST")).AddPartitionText);
    }
}
