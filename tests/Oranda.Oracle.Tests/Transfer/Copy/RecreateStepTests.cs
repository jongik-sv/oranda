using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Metadata;
using Oranda.Core.Transfer.Copy;
using Oranda.Oracle.Transfer.Copy;

namespace Oranda.Oracle.Tests.Transfer.Copy;

// 소유: 데이터 복사 Task 10. GET_DDL('TRIGGER') 글에서 생성 문장만 남긴다(상태 문장은 카탈로그 STATUS 로 따로 정한다).
public class RecreateStepTests
{
    [Theory]
    [InlineData("\n  CREATE OR REPLACE EDITIONABLE TRIGGER \"CP_DST\".\"T_TRG\" BEFORE INSERT ON \"CP_DST\".\"T\" FOR EACH ROW BEGIN :NEW.NAME := 'x'; END;\nALTER TRIGGER \"CP_DST\".\"T_TRG\" ENABLE")]
    [InlineData("\n  CREATE OR REPLACE EDITIONABLE TRIGGER \"CP_DST\".\"T_TRG\" BEFORE INSERT ON \"CP_DST\".\"T\" FOR EACH ROW BEGIN :NEW.NAME := 'x'; END;\n/\nALTER TRIGGER \"CP_DST\".\"T_TRG\" ENABLE;\n")]
    public void Trigger_ddl_keeps_only_create_statement(string ddl)
    {
        Assert.Equal(
            "CREATE OR REPLACE EDITIONABLE TRIGGER \"CP_DST\".\"T_TRG\" BEFORE INSERT ON \"CP_DST\".\"T\" FOR EACH ROW BEGIN :NEW.NAME := 'x'; END;",
            RecreateStep.TriggerCreate(ddl));
    }

    [Fact]
    public async Task Drop_statement_has_no_purge_and_asks_table_name()
    {
        var drop = CopyStatements.DropTable("CP_DST", "T");

        Assert.Equal("DROP TABLE \"CP_DST\".\"T\"", drop.Text); // CASCADE CONSTRAINTS 없음(안 보이는 FK 는 ORA-02449 로 막힌다).
        Assert.DoesNotContain("PURGE", drop.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(StatementKind.Ddl, drop.Kind);
        Assert.Equal("DROP", drop.Verb);
        Assert.Equal("T", drop.DangerousTarget);
        var server = new ServerInfo("23.26.0.0.0", null, null, null, null, null, false, 1, new ServerCapabilities(false, false, false, false, false));
        var profile = new ConnectionProfile("dev", "CP_DST", "db.example.com/ORCL", Environment: ConnectionEnvironment.Development);
        var decision = await new ExecutionPolicy().EvaluateAsync(new PolicyContext(profile, server, HistorySource.DataCopy), drop, CancellationToken.None);
        Assert.Equal(PolicyDecisionKind.Confirm, decision.Kind);
        Assert.Equal("T", decision.RetypeText);
    }

    [Fact]
    public void Recreated_target_uses_source_structure_under_target_owner()
    {
        var source = new TableInfo(new DbObjectRef("CP_SRC", "T", "TABLE"),
            [new ColumnInfo("ID", 1, "NUMBER") { IsIdentity = true }, new ColumnInfo("NAME", 2, "VARCHAR2") { Length = 20 }])
        {
            Constraints = [new ConstraintInfo("T_PK", ConstraintKind.PrimaryKey, ["ID"])],
        };

        var target = PrecheckRules.AsRecreated(source, "CP_DST");

        Assert.Equal(new DbObjectRef("CP_DST", "T", "TABLE"), target.Table);
        Assert.False(target.Columns[0].IsIdentity); // CreateTableDdl 이 BY DEFAULT 로 만든다: 값을 넣을 수 있다.
        Assert.Equal(["ID"], PrecheckRules.KeyColumns(target, PrecheckRules.SelectColumns(source, target, new TableRule("T", RuleKind.All, TargetAction.Recreate))));
    }
}
