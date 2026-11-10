using System.Reflection;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Schema;
using Oranda.Tibero.Schema;

namespace Oranda.Tibero.Tests.Schema;

public class TiberoColumnReorderScriptsTests
{
    private static readonly DbObjectRef Emp = new("HR", "EMP", "TABLE");
    private static readonly ColumnReorderTraits Plain = new(false, false, null);

    private static readonly ColumnInfo[] Columns =
    [
        new("ID", 1, "NUMBER") { IsNullable = false },
        new("NAME", 2, "VARCHAR2") { Length = 20, DefaultValue = "'x'" },
        new("MEMO", 3, "VARCHAR2") { Length = 100 },
    ];

    private static readonly TiberoColumnReorderScripts Scripts = new();

    private static SqlStatement Alter(string text) => new(text, default, StatementKind.Ddl);

    [Fact]
    public void The_service_is_registered_for_tibero()
    {
        var attribute = Assert.Single(typeof(TiberoColumnReorderScripts).GetCustomAttributes<OrandaServiceAttribute>(inherit: false));

        Assert.Equal(Dbms.Tibero, Scripts.Dbms);
        Assert.Equal(typeof(IColumnReorderScripts), attribute.ServiceType);
        Assert.Equal(Dbms.Tibero, attribute.Dbms);
    }

    [Fact]
    public void The_invisible_method_is_blocked_with_a_reason_even_on_a_plain_table()
    {
        var reasons = Scripts.BlockReasons(ColumnReorderMethod.InvisibleVisible, Columns, Plain, supportsInvisibleColumns: true);

        Assert.Equal([TiberoColumnReorderScripts.InvisibleNotSupported], reasons);
        Assert.Throws<NotSupportedException>(() => Scripts.InvisibleVisible(Emp, ["ID", "NAME", "MEMO"], ["MEMO", "ID", "NAME"]));
    }

    [Fact]
    public void Rebuild_is_blocked_until_the_dependent_ddl_is_ready_and_open_after()
    {
        var before = TiberoColumnReorderScripts.BlockReasons(ColumnReorderMethod.Rebuild, Columns, Plain, false, dependentDdlReady: false);
        var after = TiberoColumnReorderScripts.BlockReasons(ColumnReorderMethod.Rebuild, Columns, Plain, false, dependentDdlReady: true);

        Assert.Equal([TiberoColumnReorderScripts.DependentDdlNotReady], before);
        Assert.Empty(after);
        Assert.Equal(TiberoColumnReorderScripts.DependentDdlReady, Scripts.BlockReasons(ColumnReorderMethod.Rebuild, Columns, Plain, false).Count == 0);
    }

    [Fact]
    public void Rebuild_still_blocks_external_and_temporary_tables_with_the_shared_reasons()
    {
        var reasons = TiberoColumnReorderScripts.BlockReasons(
            ColumnReorderMethod.Rebuild, Columns, new ColumnReorderTraits(true, true, null), false, dependentDdlReady: true);

        Assert.Equal(2, reasons.Count);
        Assert.Contains(reasons, r => r.StartsWith("외부 테이블", StringComparison.Ordinal));
        Assert.Contains(reasons, r => r.StartsWith("임시 테이블", StringComparison.Ordinal));
    }

    [Fact]
    public void Rebuild_always_makes_the_ctas_script_even_when_the_caller_says_enterprise()
    {
        var script = Scripts.Rebuild(Emp, Columns, ["MEMO", "ID", "NAME"], enterprise: true, usePrimaryKey: true,
            dependentDdl: "CREATE INDEX \"HR\".\"IX\" ON \"HR\".\"EMP\" (\"ID\");");

        Assert.DoesNotContain("DBMS_REDEFINITION", script, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE \"HR\".\"EMP$NEW\" AS SELECT \"MEMO\", \"ID\", \"NAME\" FROM \"HR\".\"EMP\";", script, StringComparison.Ordinal);
        Assert.Contains("RENAME TO \"EMP$OLD\"", script, StringComparison.Ordinal);
        Assert.Contains("CREATE INDEX \"HR\".\"IX\"", script, StringComparison.Ordinal);
        Assert.Contains(Environment.NewLine + "DROP TABLE \"HR\".\"EMP$OLD\";" + Environment.NewLine, script, StringComparison.Ordinal);
        Assert.DoesNotContain(TiberoColumnReorderScripts.OriginalKept, script, StringComparison.Ordinal);

        // 4) 머리 줄은 Tibero 글(휴지통 기본 꺼짐 = 영구 삭제), 줄 끝은 한 가지.
        Assert.DoesNotContain(TiberoColumnReorderScripts.CoreDropHeading, script, StringComparison.Ordinal);
        Assert.Contains(TiberoColumnReorderScripts.DropHeading, script, StringComparison.Ordinal);
        Assert.Equal(script.ReplaceLineEndings(), script);
    }

    /// <summary>D4c: 종속 DDL 을 읽지 못했으면(null) 원본을 지우지 않는다 — 지우면 제약·인덱스·트리거를 되살릴 길이 없다.</summary>
    [Fact]
    public void Rebuild_keeps_the_original_table_when_the_dependent_ddl_could_not_be_read()
    {
        var script = Scripts.Rebuild(Emp, Columns, ["MEMO", "ID", "NAME"], enterprise: false, usePrimaryKey: true, dependentDdl: null);
        var lines = script.ReplaceLineEndings("\n").Split('\n');

        Assert.DoesNotContain("DROP TABLE \"HR\".\"EMP$OLD\";", lines);
        Assert.Contains("-- DROP TABLE \"HR\".\"EMP$OLD\";", lines);
        Assert.Contains(TiberoColumnReorderScripts.OriginalKept, lines);
        Assert.Contains(TiberoColumnReorderScripts.KeptHeading, lines);
        Assert.DoesNotContain(lines, l => l.StartsWith(TiberoColumnReorderScripts.CoreDropHeading, StringComparison.Ordinal));
        Assert.Contains("ALTER TABLE \"HR\".\"EMP$NEW\" RENAME TO \"EMP\";", lines); // 나머지 단계는 그대로
        Assert.DoesNotContain(lines, l => l.StartsWith("DROP ", StringComparison.Ordinal));
        Assert.Equal(script.ReplaceLineEndings(), script); // 줄 끝 섞지 않음
    }

    /// <summary>읽었는데 종속 객체가 없으면(빈 글) 보통 스크립트: 원본을 지운다(단순한 표에 $OLD 를 남기지 않는다).</summary>
    [Theory]
    [InlineData("")]
    [InlineData("  \n")]
    public void Rebuild_drops_the_original_when_the_table_has_no_dependents(string dependentDdl)
    {
        var lines = Scripts.Rebuild(Emp, Columns, ["MEMO", "ID", "NAME"], enterprise: false, usePrimaryKey: true, dependentDdl)
            .ReplaceLineEndings("\n").Split('\n');

        Assert.Contains("DROP TABLE \"HR\".\"EMP$OLD\";", lines);
        Assert.DoesNotContain(TiberoColumnReorderScripts.OriginalKept, lines);
        Assert.Contains(TiberoColumnReorderScripts.DropHeading, lines);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void For_tibero_throws_when_the_builder_no_longer_emits_the_drop_line(bool keepOriginal) =>
        Assert.Throws<InvalidOperationException>(() => TiberoColumnReorderScripts.ForTibero("SELECT 1 FROM DUAL;", Emp, keepOriginal));

    [Fact]
    public void Helpers_delegate_to_the_shared_text_builders()
    {
        var statements = new[] { Alter("ALTER TABLE T MODIFY (A INVISIBLE)") };
        var block = Scripts.AsSingleBlock(Emp, statements);
        var hits = Scripts.FilterRiskHits([new SourceSearchHit(new DbObjectRef("HR", "P", "PROCEDURE"), 5, "  SELECT * FROM emp;")], Emp);

        Assert.Equal(1, Scripts.FirstDifference(["A", "B", "C"], ["A", "C", "B"]));
        Assert.Equal("CREATE INDEX i;", Scripts.DependentPart("CREATE TABLE t;", "CREATE TABLE t;\nCREATE INDEX i;"));
        Assert.Equal("ALTER TABLE T MODIFY (A INVISIBLE);", Scripts.ToText(statements));
        Assert.StartsWith("-- 열 순서 바꾸기 HR.EMP", block.Text, StringComparison.Ordinal);
        Assert.Equal(ColumnReorderRisk.SelectStar, Assert.Single(hits).Risk);
    }
}
