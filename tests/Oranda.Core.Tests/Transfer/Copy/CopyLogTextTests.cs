using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Transfer.Copy;
using Xunit;

namespace Oranda.Core.Tests.Transfer.Copy;

/// <summary>작업 로그 글: 실제 문장 글 그대로, 바인드는 기간 경계만 값·나머지는 개수만, 마스킹 값은 들어가지 않는다.</summary>
public class CopyLogTextTests
{
    private static readonly ResolvedPeriod Period = new(new DateTime(2026, 10, 1), new DateTime(2026, 11, 1));

    private static CopySqlBuilder B(TableRule rule, params CopyVariable[] variables)
    {
        var job = new CopyJob
        {
            Id = "1", Name = "n", Source = new ConnectionRef("s", "SRC"), Target = new ConnectionRef("t", "TGT"),
            SourceOwner = "SRC", TargetOwner = "TGT", MaskSalt = "AA==", Tables = [rule], Variables = variables,
        };
        var cols = new List<ColumnInfo> { new("ID", 1, "NUMBER"), new("DOC_NO", 2, "VARCHAR2"), new("ORDER_DT", 3, "DATE"), new("CUST_NAME", 4, "VARCHAR2") };
        return new CopySqlBuilder(job, Period,
            new Dictionary<string, TableInfo> { [rule.Table] = new(new DbObjectRef("SRC", rule.Table, "TABLE"), cols) },
            new Dictionary<string, TableInfo> { [rule.Table] = new(new DbObjectRef("TGT", rule.Table, "TABLE"), cols) });
    }

    [Fact]
    public void Variable_list_binds_are_summarized_as_a_count_without_values()
    {
        var docs = Enumerable.Range(1, 23).Select(i => "SECRET-DOC-" + i).ToArray();
        var rule = new TableRule("T", RuleKind.Custom, TargetAction.DeleteRangeInsert) { CustomWhere = "DOC_NO IN (:CSM_DOC_NOS)" };
        var select = B(rule, new CopyVariable("CSM_DOC_NOS", CopyVariableType.Text, docs)).SourceSelect("T", ["ID", "DOC_NO"]);

        var line = CopyLogText.Statement("원본", select.Sql, select.Binds);

        Assert.StartsWith("원본: " + select.Sql, line, StringComparison.Ordinal); // 실제 SELECT 글 그대로
        Assert.Contains(":CSM_DOC_NOS = 23개", line, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-DOC", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Period_bounds_show_their_values_and_single_variables_only_a_count()
    {
        var rule = new TableRule("T", RuleKind.Period, TargetAction.DeleteRangeInsert)
        {
            PeriodColumns = [new PeriodColumn("ORDER_DT")],
            ExtraWhere = "DOC_NO = :ONE",
        };
        var delete = B(rule, new CopyVariable("ONE", CopyVariableType.Text, ["HIDDEN-VALUE"])).TargetDelete("T")!;

        var summary = CopyLogText.Binds(delete.Binds);

        Assert.Contains(":B1_FROM = 2026-10-01 00:00:00", summary, StringComparison.Ordinal);
        Assert.Contains(":B1_TO = 2026-11-01 00:00:00", summary, StringComparison.Ordinal);
        Assert.Contains(":ONE = 1개", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("HIDDEN-VALUE", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Masked_values_never_reach_the_statement_text()
    {
        // 마스킹은 앱에서 행 값을 바꿔 바인드 배열로 보낸다: SELECT·INSERT 글과 바인드 요약에 고정값이 들어가지 않는다.
        var rule = new TableRule("T", RuleKind.All, TargetAction.DeleteAllInsert)
        {
            Masks = [new MaskRule("CUST_NAME", MaskKind.Fixed, "MASK-FIXED-VALUE")],
        };
        var b = B(rule);
        var select = b.SourceSelect("T", ["ID", "CUST_NAME"]);
        var insert = b.Insert("T", ["ID", "CUST_NAME"]);

        var text = CopyLogText.Statement("원본", select.Sql, select.Binds) + CopyLogText.Statement("대상", insert);

        Assert.DoesNotContain("MASK-FIXED-VALUE", text, StringComparison.Ordinal);
        Assert.Contains("INSERT", text, StringComparison.Ordinal);
    }

    [Fact]
    public void No_binds_give_the_plain_statement()
    {
        Assert.Equal("대상: COMMIT", CopyLogText.Statement("대상", "COMMIT", new Dictionary<string, BindValue>()));
        Assert.Equal(string.Empty, CopyLogText.Binds(null));
    }

    [Fact]
    public void Format_puts_time_phase_table_and_kind()
    {
        var at = new DateTimeOffset(2026, 11, 9, 14, 3, 21, TimeSpan.Zero).ToLocalTime();
        Assert.Equal(at.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + " [적재] ORDERS · 배치 2 · 5,000행 보냄",
            CopyLogText.Format(new CopyLogEntry(at, "적재", "ORDERS", "배치 2 · 5,000행 보냄")));
        Assert.EndsWith("[마무리] 오류: 커밋하지 못함",
            CopyLogText.Format(new CopyLogEntry(at, "마무리", null, "커밋하지 못함", CopyLogKind.Error)), StringComparison.Ordinal);
    }

    [Fact]
    public void Elapsed_reads_as_seconds_then_minutes()
    {
        Assert.Equal("0.42초", CopyLogText.Elapsed(TimeSpan.FromMilliseconds(420)));
        Assert.Equal("3분 05초", CopyLogText.Elapsed(TimeSpan.FromSeconds(185)));
    }

    [Fact]
    public void Log_progress_carries_the_entry_and_no_rows()
    {
        var entry = new CopyLogEntry(DateTimeOffset.Now, "적재", "T", "x");
        var p = CopyProgress.ForLog(entry);
        Assert.Same(entry, p.Log);
        Assert.Equal(0, p.Rows);
        Assert.Null(new CopyProgress("T", 1, null, "적재").Log);
    }
}
