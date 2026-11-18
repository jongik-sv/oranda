#pragma warning disable CA1861 // 시험 입력용 상수 배열
using Oranda.Core.Connection;
using Oranda.Core.Schema;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Schema.TableEdit.Partitions;

namespace Oranda.Core.Tests.Schema.TableEdit.Partitions;

/// <summary>하위 파티션 템플릿(PA-11)·LOCAL 인덱스(PA-12): 바꾸기 절·한 문장·다시 만들기 종속 DDL. 예시 테이블 = SAMPLE.ORDERS.</summary>
public class PartitionTemplateTests
{
    private static readonly Dialect Oracle = new(hashAsTemplate: false);

    private static readonly Dialect TiberoLike = new(hashAsTemplate: true);

    private static readonly ServerInfo Oracle19 =
        new("19.0.0.0.0", "i", "h", null, "D", null, false, 1, new ServerCapabilities(false, false, true, false, false, Dbms.Oracle), Dbms: Dbms.Oracle);

    private static PartitionTableDraft Converting(SubpartitionTemplate? sub, params string[] local) =>
        new(new PartitionScheme(PartitionMethod.Range, ["ORDER_DATE"]) { Interval = "NUMTOYMINTERVAL(1, 'MONTH')", SubpartitionTemplate = sub },
            [new PartitionDraft("n1", "P_BEFORE_2026_11") { Bound = "DATE '2026-11-01'" }])
        {
            LocalIndexes = local,
        };

    private static readonly SubpartitionTemplate ListSub = new(PartitionMethod.List, ["REGION"])
    {
        Rows = [new SubpartitionTemplateRow("SP_E", "'E'"), new SubpartitionTemplateRow("SP_W", "'W', 'N'"), new SubpartitionTemplateRow("SP_X", "DEFAULT")],
    };

    [Fact]
    public void List_template_goes_after_interval_and_before_the_partitions()
    {
        Assert.Equal(
            "PARTITION BY RANGE (ORDER_DATE) INTERVAL (NUMTOYMINTERVAL(1, 'MONTH')) SUBPARTITION BY LIST (REGION) SUBPARTITION TEMPLATE " +
            "(SUBPARTITION SP_E VALUES ('E'), SUBPARTITION SP_W VALUES ('W', 'N'), SUBPARTITION SP_X VALUES (DEFAULT)) " +
            "(PARTITION P_BEFORE_2026_11 VALUES LESS THAN (DATE '2026-11-01'))",
            PartitionScriptBuilder.ConversionClause(PartitionTableDraft.None, Converting(ListSub), Oracle));
    }

    /// <summary>빈 값 칸은 DEFAULT(나머지 전부) — 깨진 <c>VALUES ()</c> 를 내지 않는다.</summary>
    [Fact]
    public void Empty_list_values_become_default()
    {
        var sub = ListSub with { Rows = [new SubpartitionTemplateRow("SP_A", "'A'"), new SubpartitionTemplateRow("SP_REST", " ")] };
        Assert.Contains("SUBPARTITION SP_REST VALUES (DEFAULT))", PartitionScriptBuilder.ConversionClause(PartitionTableDraft.None, Converting(sub), Oracle));
    }

    [Fact]
    public void Hash_template_is_a_count_on_oracle_and_named_rows_where_the_dialect_needs_it()
    {
        var sub = new SubpartitionTemplate(PartitionMethod.Hash, ["CUSTOMER_ID"]) { HashCount = 4 };

        Assert.Contains(" SUBPARTITION BY HASH (CUSTOMER_ID) SUBPARTITIONS 4 (PARTITION", PartitionScriptBuilder.ConversionClause(PartitionTableDraft.None, Converting(sub), Oracle));
        Assert.Contains(" SUBPARTITION BY HASH (CUSTOMER_ID) SUBPARTITION TEMPLATE (SUBPARTITION SP1, SUBPARTITION SP2, SUBPARTITION SP3, SUBPARTITION SP4) (PARTITION",
            PartitionScriptBuilder.ConversionClause(PartitionTableDraft.None, Converting(sub), TiberoLike));
    }

    [Fact]
    public void Online_conversion_names_local_indexes_and_keeps_the_others_global()
    {
        var statements = PartitionScriptBuilder.Build("SAMPLE", "ORDERS", PartitionTableDraft.None, Converting(ListSub, "ORDERS_DATE_IX"), Oracle, Oracle19,
            ["ORDERS_PK", "ORDERS_DATE_IX"]);

        var text = Assert.Single(statements).Statement.Text;
        Assert.StartsWith("ALTER TABLE SAMPLE.ORDERS MODIFY PARTITION BY RANGE (ORDER_DATE) INTERVAL (NUMTOYMINTERVAL(1, 'MONTH')) SUBPARTITION BY LIST (REGION)", text, StringComparison.Ordinal);
        Assert.EndsWith(" ONLINE UPDATE INDEXES (ORDERS_DATE_IX LOCAL, ORDERS_PK GLOBAL)", text, StringComparison.Ordinal);
    }

    /// <summary>LOCAL 목록이 비면 예전 문장 그대로(서버 기본에 맡김).</summary>
    [Fact]
    public void Online_conversion_without_local_indexes_has_no_update_indexes_clause()
    {
        var text = Assert.Single(PartitionScriptBuilder.Build("SAMPLE", "ORDERS", PartitionTableDraft.None, Converting(null), Oracle, Oracle19, ["ORDERS_PK"])).Statement.Text;
        Assert.EndsWith(") ONLINE", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Range_subpartition_template_or_one_the_dialect_refuses_is_a_problem()
    {
        var range = Converting(new SubpartitionTemplate(PartitionMethod.Range, ["REGION"]));
        Assert.Equal([PartitionDiff.SubpartitionMethodKey], PartitionDiff.Compute(PartitionTableDraft.None, range, Oracle, Oracle19).Problems.Select(p => p.ReasonKey));
        Assert.Empty(PartitionScriptBuilder.Build("SAMPLE", "ORDERS", PartitionTableDraft.None, range, Oracle, Oracle19));

        var refusing = new Dialect(hashAsTemplate: false, refuseSub: true);
        Assert.Equal([Dialect.SubKey], PartitionDiff.Compute(PartitionTableDraft.None, Converting(ListSub), refusing, Oracle19).Problems.Select(p => p.ReasonKey));
    }

    /// <summary>이미 파티션된 테이블에 템플릿을 넣으면 방식 바꾸기와 같다(문장 없음).</summary>
    [Fact]
    public void Template_on_an_already_partitioned_table_is_a_scheme_change()
    {
        var original = new PartitionTableDraft(new PartitionScheme(PartitionMethod.Range, ["ORDER_DATE"]), [new PartitionDraft("p1", "P1") { Bound = "DATE '2026-01-01'" }]);
        var edited = original with { Scheme = original.Scheme! with { SubpartitionTemplate = ListSub } };

        Assert.Equal([PartitionDiff.SchemeChangeKey], PartitionDiff.Compute(original, edited, Oracle).Problems.Select(p => p.ReasonKey));
    }

    /// <summary>Oracle DBMS_METADATA 꼴: PK 인덱스도 CREATE UNIQUE INDEX 로 먼저 나오고, 제약은 USING INDEX 로 그 인덱스를 쓴다 → CREATE 끝에만 LOCAL.</summary>
    [Fact]
    public void Local_indexes_in_oracle_dependent_ddl_get_local_on_their_create_statements()
    {
        var ddl = string.Join('\n',
            "CREATE UNIQUE INDEX \"SAMPLE\".\"ORDERS_PK\" ON \"SAMPLE\".\"ORDERS\" (\"ID\", \"ORDER_DATE\") ",
            "  ;",
            "  CREATE INDEX \"SAMPLE\".\"ORDERS_IX\" ON \"SAMPLE\".\"ORDERS\" (\"NAME\") ",
            "  ;",
            "  CREATE INDEX \"SAMPLE\".\"ORDERS_D\" ON \"SAMPLE\".\"ORDERS\" (\"ORDER_DATE\") ",
            "  ;",
            "  ALTER TABLE \"SAMPLE\".\"ORDERS\" ADD CONSTRAINT \"ORDERS_PK\" PRIMARY KEY (\"ID\", \"ORDER_DATE\")",
            "  USING INDEX  ENABLE;");

        var result = ColumnReorderBuilder.WithLocalIndexes(ddl, ["ORDERS_PK", "ORDERS_D"]);

        Assert.Contains("(\"ID\", \"ORDER_DATE\") LOCAL \n  ;", result, StringComparison.Ordinal);
        Assert.Contains("(\"ORDER_DATE\") LOCAL \n  ;", result, StringComparison.Ordinal);
        Assert.Contains("(\"NAME\") \n  ;", result, StringComparison.Ordinal); // 고르지 않은 인덱스는 그대로
        Assert.EndsWith("  USING INDEX  ENABLE;", result, StringComparison.Ordinal); // 제약은 이미 만든 LOCAL 인덱스를 쓴다
        Assert.DoesNotContain("주의", result, StringComparison.Ordinal);
    }

    /// <summary>Tibero 꼴: PK 인덱스 CREATE 가 없고 제약만 → <c>USING INDEX LOCAL</c>(실측). 못 찾은 이름은 맨 앞 주석.</summary>
    [Fact]
    public void Tibero_key_constraints_get_using_index_local_and_missing_names_are_reported()
    {
        var ddl = "CREATE INDEX \"SAMPLE\".\"ORDERS_IX\" ON \"SAMPLE\".\"ORDERS\" (\"YM\", \"C\");\n" +
                  "ALTER TABLE \"SAMPLE\".\"ORDERS\" ADD CONSTRAINT \"ORDERS_PK\" PRIMARY KEY (\"ID\", \"YM\") ENABLE;";

        var result = ColumnReorderBuilder.WithLocalIndexes(ddl, ["ORDERS_PK", "ORDERS_IX", "GONE_IX"]);

        Assert.Contains("(\"YM\", \"C\") LOCAL;", result, StringComparison.Ordinal);
        Assert.EndsWith("PRIMARY KEY (\"ID\", \"YM\") USING INDEX LOCAL ENABLE;", result, StringComparison.Ordinal);
        Assert.StartsWith("-- 주의: LOCAL 로 만들 인덱스의 DDL 을 찾지 못했다: GONE_IX.", result, StringComparison.Ordinal);
        Assert.Same(ddl, ColumnReorderBuilder.WithLocalIndexes(ddl, []));
    }

    private sealed class Dialect(bool hashAsTemplate, bool refuseSub = false) : ITableEditDialect
    {
        public const string SubKey = "TableEdit.unsupported.test.subpartition";

        public Dbms Dbms => Dbms.Oracle;

        public IReadOnlyList<string> TypeNames => ["NUMBER"];

        public bool SupportsVirtualColumnModify => true;

        public bool PurgesOnDrop => false;

        public bool HashSubpartitionsAsTemplate => hashAsTemplate;

        public int MaxIdentifierBytes(ServerInfo? server) => 128;

        public string? UnsupportedReasonKey(TableEditFeature feature, ServerInfo? server) => null;

        public string? UnsupportedPartitionReasonKey(PartitionOperation operation, PartitionMethod method, ServerInfo? server) =>
            refuseSub && operation == PartitionOperation.Subpartition ? SubKey : null;
    }
}
