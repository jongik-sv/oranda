using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Transfer.Copy;
using Xunit;

namespace Oranda.Core.Tests.Transfer.Copy;

/// <summary>작업 변수: 조건의 :이름 에 값을 바인드하고, 여러 값은 목록으로 펼친다(1000개 넘으면 999개씩 목록 형식으로 묶는다).</summary>
public class CopyVariablesTests
{
    private static readonly ResolvedPeriod Period = new(new DateTime(2026, 10, 7), new DateTime(2026, 11, 7));

    private static CopySqlBuilder B(TableRule rule, params CopyVariable[] variables)
    {
        var job = new CopyJob
        {
            Id = "1", Name = "n", Source = new ConnectionRef("s", "SRC"), Target = new ConnectionRef("t", "TGT"),
            SourceOwner = "SRC", TargetOwner = "TGT", MaskSalt = "AA==", Tables = [rule], Variables = variables,
        };
        var cols = new List<ColumnInfo> { new("ID", 1, "NUMBER") };
        return new CopySqlBuilder(job, Period,
            new Dictionary<string, TableInfo> { [rule.Table] = new(new DbObjectRef("SRC", rule.Table, "TABLE"), cols) },
            new Dictionary<string, TableInfo> { [rule.Table] = new(new DbObjectRef("TGT", rule.Table, "TABLE"), cols) });
    }

    private static TableRule Custom(string where) => new("T", RuleKind.Custom, TargetAction.DeleteRangeInsert) { CustomWhere = where };

    private static CopyVariable Text(string name, params string[] values) => new(name, CopyVariableType.Text, values);

    [Fact]
    public void Single_value_binds_the_name_itself_case_insensitively()
    {
        var s = B(Custom("ORDER_NO = :order_no"), Text("ORDER_NO", "A-100")).SourceSelect("T", ["ID"]);

        Assert.Equal("SELECT \"ID\" FROM \"SRC\".\"T\" WHERE (ORDER_NO = :ORDER_NO)", s.Sql);
        Assert.Equal(new BindValue(BindDataType.Varchar2, "A-100"), Assert.Single(s.Binds).Value);
    }

    [Fact]
    public void Several_values_expand_into_a_bind_list_and_the_target_delete_uses_the_same_values()
    {
        var b = B(Custom("COIL_NO IN (:COILS) AND MO = :MO"), Text("COILS", "C1", "C2", "C3"), new CopyVariable("MO", CopyVariableType.Number, ["42"]));

        var s = b.SourceSelect("T", ["ID"]);
        Assert.EndsWith("WHERE (COIL_NO IN (:COILS_1, :COILS_2, :COILS_3) AND MO = :MO)", s.Sql, StringComparison.Ordinal);
        Assert.Equal(["C1", "C2", "C3"], [s.Binds["COILS_1"].Value, s.Binds["COILS_2"].Value, s.Binds["COILS_3"].Value]);
        Assert.Equal(new BindValue(BindDataType.Number, 42m), s.Binds["MO"]);

        var delete = b.TargetDelete("T")!;
        Assert.EndsWith("WHERE (COIL_NO IN (:COILS_1, :COILS_2, :COILS_3) AND MO = :MO)", delete.Sql, StringComparison.Ordinal);
        Assert.Equal(s.Binds.Count, delete.Binds.Count);
    }

    [Fact]
    public void More_than_a_thousand_values_are_chunked_into_list_type_selects()
    {
        var values = Enumerable.Range(1, 1500).Select(i => "C" + i).ToArray();
        var s = B(Custom("COIL_NO IN (:COILS)"), Text("COILS", values)).SourceSelect("T", ["ID"]);

        Assert.Equal(2, s.Sql.Split("SELECT COLUMN_VALUE FROM TABLE(SYS.ODCIVARCHAR2LIST(").Length - 1); // 999 + 501
        Assert.Contains(":COILS_999)) UNION ALL SELECT COLUMN_VALUE FROM TABLE(SYS.ODCIVARCHAR2LIST(:COILS_1000, ", s.Sql, StringComparison.Ordinal);
        Assert.Equal(1500, s.Binds.Count);

        var dates = Enumerable.Range(0, 1001).Select(i => new DateTime(2026, 1, 1).AddDays(i).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var d = B(Custom("D IN (:DAYS)"), new CopyVariable("DAYS", CopyVariableType.Date, dates)).SourceSelect("T", ["ID"]);
        Assert.Contains("SYS.ODCIDATELIST(", d.Sql, StringComparison.Ordinal);
        Assert.Equal(new DateTime(2026, 1, 1), d.Binds["DAYS_1"].Value);
    }

    [Fact]
    public void Unknown_names_are_refused_but_colons_inside_strings_are_not_binds()
    {
        var unknown = Assert.Throws<InvalidOperationException>(() => B(Custom("X = :NOPE")).SourceSelect("T", ["ID"]));
        Assert.Contains(":NOPE", unknown.Message, StringComparison.Ordinal);

        var s = B(Custom("NOTE = 'a:NOPE' AND TO_CHAR(D, 'HH24:MI') = '10:30'")).SourceSelect("T", ["ID"]);
        Assert.Empty(s.Binds);
        Assert.Equal([], CopyVariables.BindNames("NOTE = 'a:NOPE' AND \"COL:X\" = 1"));
        Assert.Equal(["FROM_DT", "COILS"], CopyVariables.BindNames("D >= :from_dt AND C IN (:Coils) AND E < :FROM_DT"));
    }

    [Fact]
    public void Variable_problems_cover_names_values_and_duplicates()
    {
        Assert.Null(CopyVariables.Problem(Text("COILS", "A")));
        Assert.Contains("기간", CopyVariables.Problem(Text("FROM_DT", "A")), StringComparison.Ordinal);
        Assert.NotNull(CopyVariables.Problem(Text("COILS_1", "A"))); // 펼친 이름과 겹친다
        Assert.NotNull(CopyVariables.Problem(Text("C1", "A")));
        Assert.NotNull(CopyVariables.Problem(Text("1ABC", "A")));
        Assert.NotNull(CopyVariables.Problem(Text(new string('A', 25), "A")));
        Assert.Contains("값이 없습니다", CopyVariables.Problem(Text("EMPTY")), StringComparison.Ordinal);
        Assert.Contains("숫자", CopyVariables.Problem(new CopyVariable("N", CopyVariableType.Number, ["1", "x"])), StringComparison.Ordinal);
        Assert.Contains("날짜", CopyVariables.Problem(new CopyVariable("D", CopyVariableType.Date, ["2026-13-01"])), StringComparison.Ordinal);
        Assert.Null(CopyVariables.Problem(new CopyVariable("D", CopyVariableType.Date, ["2026-11-09", "2026-11-09 07:30:00", "20261109"])));
        Assert.NotNull(CopyVariables.Problem(Text("MANY", [.. Enumerable.Range(0, CopyVariables.MaxValues + 1).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture))])));
        Assert.Contains("두 번", Assert.Single(CopyVariables.Problems([Text("A", "1"), Text("A", "2")])), StringComparison.Ordinal);

        var invalid = Assert.Throws<InvalidOperationException>(() => B(Custom("N = :N"), new CopyVariable("N", CopyVariableType.Number, ["x"])).SourceSelect("T", ["ID"]));
        Assert.Contains("숫자", invalid.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Pasted_values_split_on_lines_commas_and_tabs_without_blanks_or_repeats()
    {
        Assert.Equal(["C1", "C2", "C3", "C4"], CopyVariables.SplitValues("C1\r\nC2, C3\n\nC1\tC4 \n"));
        Assert.Empty(CopyVariables.SplitValues("  "));
        Assert.Equal("COILS", CopyVariables.NormalizeName(" :coils "));
    }
}
