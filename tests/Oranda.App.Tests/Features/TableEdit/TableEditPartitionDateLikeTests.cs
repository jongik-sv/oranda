using Oranda.App.Features.TableEdit;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit.Partitions;
using Oranda.Core.Schema.TableEdit.Partitions.Advisor;
using static Oranda.App.Tests.Features.TableEdit.TableEditPartitionSessionTests;
using static Oranda.App.Tests.Features.TableEdit.TableEditTestSupport;

namespace Oranda.App.Tests.Features.TableEdit;

/// <summary>
/// 쉬운 설정의 날짜형 문자열·숫자 열(파티션 추천 설계 PA-10): 세션에 넣은 열 → 목록에 더함, 고르면 INTERVAL 없이 「시작 앞 한 줄 + 앞으로 12개월 + P_MAX」,
/// 경계 글은 꼴대로. 그 뒤 월 추가 스크립트(PA-5)로 이어 간다.
/// </summary>
public class TableEditPartitionDateLikeTests
{
    private static readonly DateTime Today = new(2026, 11, 18, 0, 0, 0, DateTimeKind.Unspecified);

    /// <summary>SAMPLE.ORDERS + ORDER_DATE(DATE) + ORDER_YMD VARCHAR2(8) + ORDER_YM NUMBER(6).</summary>
    private static TableEditSession Normal()
    {
        var table = WithOrderDate(OrdersTable());
        table = table with
        {
            Columns =
            [
                .. table.Columns,
                new ColumnInfo("ORDER_YMD", table.Columns.Count + 1, "VARCHAR2") { Length = 8 },
                new ColumnInfo("ORDER_YM", table.Columns.Count + 2, "NUMBER") { Precision = 6 },
            ],
        };
        var s = Create(table);
        s.Today = () => Today;
        return s;
    }

    [Fact]
    public void Date_like_columns_join_the_easy_list_after_real_date_columns()
    {
        var s = Normal();
        Assert.Equal(["ORDER_DATE"], s.EasyDateColumns);

        s.SetDateLikeColumns(new Dictionary<string, DateLikeFormat>
        {
            ["ORDER_YMD"] = DateLikeFormat.Yyyymmdd,
            ["ORDER_YM"] = DateLikeFormat.Yyyymm,
            ["STATUS_GONE"] = DateLikeFormat.Yyyymm, // 표에 없는 열은 무시
        });

        Assert.Equal(["ORDER_DATE", "ORDER_YMD", "ORDER_YM"], s.EasyDateColumns);
        Assert.False(s.IsEasyDateLike); // 고른 열은 그대로 ORDER_DATE
        s.EasyPartitionColumn = "ORDER_YMD";
        Assert.True(s.IsEasyDateLike);
        Assert.Contains("YYYYMMDD", s.EasyPartitionSummary, StringComparison.Ordinal);
        Assert.Contains("월 추가 스크립트", s.EasyPartitionSummary, StringComparison.Ordinal);

        s.SetDateLikeColumns(null);
        Assert.Equal(["ORDER_DATE"], s.EasyDateColumns);
        Assert.Equal("ORDER_DATE", s.EasyPartitionColumn); // 사라진 열 대신 첫 열
    }

    [Fact]
    public void Text_yyyymmdd_column_makes_a_before_row_twelve_months_and_p_max_without_interval()
    {
        var s = Normal();
        s.SetDateLikeColumns(new Dictionary<string, DateLikeFormat> { ["ORDER_YMD"] = DateLikeFormat.Yyyymmdd });
        s.EasyPartitionColumn = "ORDER_YMD";

        Assert.True(s.ApplyEasyPartitioning());

        Assert.Null(s.PartitionDraft!.Scheme!.Interval);
        var rows = s.PartitionDraft.Partitions;
        Assert.Equal(14, rows.Count);
        Assert.Equal(("P_BEFORE_2026_11", "'20261101'"), (rows[0].Name, rows[0].Bound));
        Assert.Equal(("P_2026_11", "'20261201'"), (rows[1].Name, rows[1].Bound));
        Assert.Equal(("P_2027_10", "'20271101'"), (rows[12].Name, rows[12].Bound));
        Assert.Equal(("P_MAX", "MAXVALUE"), (rows[13].Name, rows[13].Bound));
        var text = Assert.Single(s.Script.Statements).Statement.Text;
        Assert.Contains("MODIFY PARTITION BY RANGE (ORDER_YMD) (PARTITION P_BEFORE_2026_11 VALUES LESS THAN ('20261101'), ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("INTERVAL", text, StringComparison.Ordinal);

        // 만든 모양은 월 추가 스크립트의 조건(MAXVALUE 끝, INTERVAL 없음, 달 첫날 경계)에 맞는다.
        var plan = PartitionMonthScript.Plan(s.PartitionDraft, 1)!;
        Assert.Equal(new MonthSplit("P_2027_11", "'20271201'"), Assert.Single(plan.Months));
    }

    /// <summary>숫자 YYYYMM 은 따옴표 없이, 하루 단위를 골라도 달로 만든다.</summary>
    [Fact]
    public void Number_yyyymm_column_uses_bare_numbers_and_months_even_when_days_are_chosen()
    {
        var s = Normal();
        s.SetDateLikeColumns(new Dictionary<string, DateLikeFormat> { ["ORDER_YM"] = DateLikeFormat.Yyyymm });
        s.EasyPartitionColumn = "ORDER_YM";
        s.EasyPartitionUnitIndex = 0;

        Assert.True(s.ApplyEasyPartitioning());

        var rows = s.PartitionDraft!.Partitions;
        Assert.Equal(("P_BEFORE_2026_11", "202611"), (rows[0].Name, rows[0].Bound));
        Assert.Equal(("P_2026_12", "202701"), (rows[2].Name, rows[2].Bound));
        Assert.Equal("P_MAX", rows[^1].Name);
    }
}
