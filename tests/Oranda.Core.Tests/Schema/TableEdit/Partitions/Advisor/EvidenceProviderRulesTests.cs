using Oranda.Core.Schema.TableEdit.Partitions.Advisor;

namespace Oranda.Core.Tests.Schema.TableEdit.Partitions.Advisor;

// 근거 공급자 공용 순수 규칙(설계 2026-11-18 5장): 조건 글에서 열 찾기, 날짜형 판정, 분포 만들기, 표본 비율.
public sealed class EvidenceProviderRulesTests
{
    private static readonly string[] Columns = ["ORDER_DT", "STATUS", "Mixed Case", "ID"];

    [Theory]
    [InlineData("\"T\".\"ORDER_DT\">=TO_DATE(' 2026-01-01 00:00:00', 'syyyy-mm-dd hh24:mi:ss')", "ORDER_DT")]
    [InlineData("\"ORDER_DT\">=:B1 AND \"STATUS\"='X'", "ORDER_DT,STATUS")]
    [InlineData("T.STATUS=:1", "STATUS")]
    [InlineData("TO_CHAR(\"T\".\"ORDER_DT\",'yyyymm')='202601'", "ORDER_DT")]
    [InlineData("INTERNAL_FUNCTION(\"T\".\"ORDER_DT\")>SYSDATE@!", "ORDER_DT")]
    [InlineData("\"Mixed Case\"=1", "Mixed Case")]
    public void Finds_table_columns_in_predicate_text(string predicate, string expected)
    {
        var found = EvidenceProviderRules.ColumnsIn(predicate, Columns);

        Assert.Equal(expected.Split(',').Order(StringComparer.Ordinal), found.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("\"T\".\"MEMO\"='ORDER_DT'")]            // 문자열 리터럴 안 이름
    [InlineData("\"X\".\"OTHER\"=:STATUS")]               // 바인드 이름
    [InlineData("\"MIXED CASE\"=1")]                      // 따옴표 이름은 대소문자 그대로
    [InlineData("\"A\".\"B\" IS NOT NULL AND ID_X = 1")] // 비슷한 이름
    [InlineData("")]
    [InlineData(null)]
    public void Ignores_literals_bind_names_and_near_misses(string? predicate)
    {
        Assert.Empty(EvidenceProviderRules.ColumnsIn(predicate, Columns));
    }

    [Fact]
    public void An_unquoted_keyword_is_not_a_column_unless_qualified()
    {
        string[] columns = ["DATE", "NULL"];

        Assert.Empty(EvidenceProviderRules.ColumnsIn("X IS NULL AND Y > DATE '2026-01-01'", columns));
        Assert.Equal(["DATE"], EvidenceProviderRules.ColumnsIn("T.DATE > 1", columns));
    }

    [Fact]
    public void An_escaped_quote_in_a_literal_does_not_end_the_literal()
    {
        Assert.Empty(EvidenceProviderRules.ColumnsIn("\"MEMO\"='it''s STATUS' AND 1=1", Columns));
    }

    [Fact]
    public void Weights_columns_by_statement_executions_and_counts_each_statement_once()
    {
        PlanStatement[] statements =
        [
            new("a", 900, ["\"ORDER_DT\">=:1", "\"T\".\"ORDER_DT\"<:2"]), // 같은 문장에서 두 번 나와도 한 번
            new("b", 100, ["\"STATUS\"='X'"]),
            new("c", 0, ["\"ID\"=1"]),
        ];

        var use = EvidenceProviderRules.BuildPredicateUse(statements, Columns);

        Assert.Equal(["ORDER_DT", "STATUS", "ID"], use.Select(u => u.Column));
        var order = use[0];
        Assert.Equal(900, order.Executions);
        Assert.Equal(1, order.Statements);
        Assert.Equal(0.9, order.Share, 6);
        Assert.Equal(0.1, use[1].Share, 6);
        Assert.Equal(0, use[2].Share);
    }

    [Fact]
    public void No_statements_means_no_use()
    {
        Assert.Empty(EvidenceProviderRules.BuildPredicateUse([], Columns));
        Assert.Empty(EvidenceProviderRules.BuildPredicateUse([new("a", 5, ["\"X\"=1"])], Columns));
    }

    [Theory]
    [InlineData(DateLikeFormat.Yyyymmdd, "20260131", "20241231", "19990228")]
    [InlineData(DateLikeFormat.Yyyymm, "202601", "202412", "199902")]
    [InlineData(DateLikeFormat.YyyyDashMmDashDd, "2026-01-31", "2024-12-31", "1999-02-28")]
    public void Detects_each_date_like_format(DateLikeFormat format, params string[] values)
    {
        var found = EvidenceProviderRules.DetectDateLike(values.Concat(Enumerable.Repeat(values[0], 20)));

        Assert.NotNull(found);
        Assert.Equal(format, found.Format);
        Assert.Equal(1.0, found.MatchRatio);
        Assert.Equal(values.Length + 20, found.SampleSize);
    }

    [Fact]
    public void Dates_must_exist_on_the_calendar()
    {
        // 2월 30일·13월은 형식은 맞아도 날짜가 아니다 → 비율이 95% 아래로 떨어져 날짜형이 아니다.
        var values = Enumerable.Repeat("20260115", 90).Concat(Enumerable.Repeat("20260230", 10));

        Assert.Null(EvidenceProviderRules.DetectDateLike(values));
        Assert.False(EvidenceProviderRules.MatchesFormat("202613", DateLikeFormat.Yyyymm));
        Assert.False(EvidenceProviderRules.MatchesFormat("2026-02-30", DateLikeFormat.YyyyDashMmDashDd));
    }

    [Fact]
    public void Ninety_five_percent_is_the_line()
    {
        var ok = Enumerable.Repeat("20260115", 95).Concat(Enumerable.Repeat("garbage", 5));
        var low = Enumerable.Repeat("20260115", 94).Concat(Enumerable.Repeat("garbage", 6));

        Assert.Equal(0.95, EvidenceProviderRules.DetectDateLike(ok)!.MatchRatio, 6);
        Assert.Null(EvidenceProviderRules.DetectDateLike(low));
    }

    [Fact]
    public void Blank_and_null_samples_are_not_counted_and_all_blank_is_not_date_like()
    {
        var found = EvidenceProviderRules.DetectDateLike([null, "", "  ", "20260115 ", "20260116"]);

        Assert.Equal(2, found!.SampleSize);
        Assert.Null(EvidenceProviderRules.DetectDateLike([null, " "]));
        Assert.Null(EvidenceProviderRules.DetectDateLike([]));
    }

    [Fact]
    public void Plain_numbers_such_as_ids_are_not_date_like()
    {
        Assert.Null(EvidenceProviderRules.DetectDateLike(Enumerable.Range(1, 200).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture))));
    }

    [Fact]
    public void Monthly_keeps_36_months_back_and_12_ahead_and_the_rest_goes_to_rest()
    {
        var now = new DateTime(2026, 11, 18);
        (string?, long)[] groups =
        [
            ("2023-12", 5),   // 지금 달 포함 36개월째 → 버킷
            ("2023-11", 7),   // 그보다 이름 → 나머지
            ("2026-11", 10),
            ("2027-11", 3),   // 12개월 앞 → 버킷
            ("2027-12", 2),   // 그 뒤 → 나머지
            ("9999-12", 4),   // 끝 날짜 → 나머지
            ("2026-13", 1),   // 달이 아님 → 나머지
            (null, 6),        // NULL
        ];

        var d = EvidenceProviderRules.BuildMonthly("D", groups, 1.5, now);

        Assert.Equal(DistributionKind.Monthly, d.Kind);
        Assert.Equal(["2023-12", "2026-11", "2027-11"], d.Buckets.Select(b => b.Label));
        Assert.Equal(7 + 2 + 4 + 1, d.RestCount);
        Assert.Equal(6, d.NullCount);
        Assert.Equal(5 + 7 + 10 + 3 + 2 + 4 + 1 + 6, d.SampledRows);
        Assert.Equal(1.5, d.SamplePercent);
    }

    [Fact]
    public void Monthly_of_an_empty_table_is_empty()
    {
        var d = EvidenceProviderRules.BuildMonthly("D", [], null, new DateTime(2026, 11, 18));

        Assert.Empty(d.Buckets);
        Assert.Equal(0, d.SampledRows);
        Assert.Equal(0, d.RestCount);
        Assert.Null(d.SamplePercent);
    }

    [Fact]
    public void Top_values_take_the_largest_and_put_the_remainder_in_rest()
    {
        (string?, long)[] groups = [("B", 30), ("A", 50), ("C", 10), ("D", 5), (null, 5)];

        var d = EvidenceProviderRules.BuildTopValues("S", groups, 100, 5, 2, null);

        Assert.Equal(DistributionKind.TopValues, d.Kind);
        Assert.Equal([("A", 50L), ("B", 30L)], d.Buckets.Select(b => (b.Label, b.Count)));
        Assert.Equal(15, d.RestCount);
        Assert.Equal(5, d.NullCount);
        Assert.Equal(100, d.SampledRows);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(200_000L)]
    public void Small_or_unknown_tables_are_read_in_full(long? rows)
    {
        Assert.Null(EvidenceProviderRules.SamplePercentFor(rows));
    }

    [Theory]
    [InlineData(1_000_000L, 5.0)]
    [InlineData(200_001L, 10.0)]     // 25% 가 나오지만 10 으로 자른다
    [InlineData(1_000_000_000L, 0.01)] // 0.005% 가 나오지만 0.01 로 올린다
    public void Large_tables_are_sampled_to_about_fifty_thousand_rows(long rows, double expected)
    {
        Assert.Equal(expected, EvidenceProviderRules.SamplePercentFor(rows)!.Value, 6);
    }

    [Fact]
    public void Sample_clause_uses_a_dot_and_is_empty_without_a_sample()
    {
        Assert.Equal(string.Empty, EvidenceProviderRules.SampleClause(null));
        Assert.Equal(" SAMPLE (0.01)", EvidenceProviderRules.SampleClause(0.01));
        Assert.Equal(" SAMPLE (5)", EvidenceProviderRules.SampleClause(5));
    }

    [Theory]
    [InlineData(null, "TO_CHAR(\"C\", 'YYYY-MM')")]
    [InlineData(DateLikeFormat.YyyyDashMmDashDd, "SUBSTR(TO_CHAR(\"C\"), 1, 7)")]
    [InlineData(DateLikeFormat.Yyyymm, "CASE WHEN \"C\" IS NULL THEN NULL ELSE SUBSTR(TO_CHAR(\"C\"), 1, 4) || '-' || SUBSTR(TO_CHAR(\"C\"), 5, 2) END")]
    [InlineData(DateLikeFormat.Yyyymmdd, "CASE WHEN \"C\" IS NULL THEN NULL ELSE SUBSTR(TO_CHAR(\"C\"), 1, 4) || '-' || SUBSTR(TO_CHAR(\"C\"), 5, 2) END")]
    public void Month_label_expression_per_format(DateLikeFormat? format, string expected)
    {
        Assert.Equal(expected, EvidenceProviderRules.MonthLabelExpression(format, "\"C\""));
    }
}
