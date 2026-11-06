using Oranda.Core.Connection;
using Oranda.Core.Transfer.Copy;
using Oranda.Oracle.Transfer.Copy;

namespace Oranda.Oracle.Tests.Transfer.Copy;

// 소유: 데이터 복사 Task 9. 실행기의 순수 판정: 오류 문구에서 행 값 지우기, 사전 점검 뒤 규칙 변경 감지.
public class CopyRunnerRulesTests
{
    [Fact]
    public void Safe_error_drops_row_values_but_keeps_names()
    {
        Assert.Equal(
            "ORA-01722: unable to convert string value containing '…' to a number: \"CP_DST\".\"T\".\"AMT\"",
            CopyRunner.SafeError("ORA-01722: unable to convert string value containing 'secret-123' to a number: \"CP_DST\".\"T\".\"AMT\"\nHelp: https://docs.oracle.com/error-help/db/ora-01722/"));
        Assert.Equal(
            "ORA-00001: unique constraint (\"CP_DST\".\"PK_T\") violated on table \"CP_DST\".\"T\" columns (\"ID\") row with column values (…)",
            CopyRunner.SafeError("ORA-00001: unique constraint (\"CP_DST\".\"PK_T\") violated on table \"CP_DST\".\"T\" columns (\"ID\") row with column values (ID:42, NAME:'kim')"));
        Assert.Equal("ORA-02290: check constraint (CP_DST.CK_T) violated", CopyRunner.SafeError("ORA-02290: check constraint (CP_DST.CK_T) violated\r\nmore"));
    }

    private static ServerInfo Server(string? host, string? instance, bool rac = false) =>
        new("19.0.0.0.0", instance, host, "PDB1", "ORCL", "PDB1", true, 1,
            new ServerCapabilities(false, true, true, false, false), DbUniqueName: "ORCL", IsRac: rac);

    [Theory]
    [InlineData("db1.example.com", "ORCL", "db1.example.com", "ORCL", "APP", "APP", false, true)]
    [InlineData("db1.example.com", "ORCL", "db1.example.com", "ORCL", "APP", "app", false, true)]
    [InlineData("db1.example.com", "ORCL", "db1.example.com", "ORCL", "APP", "APP_DEV", false, false)]
    [InlineData("db1.example.com", "ORCL", "db2.example.com", "ORCL", "APP", "APP", false, false)]
    [InlineData("db1.example.com", "ORCL1", "db2.example.com", "ORCL2", "APP", "APP", true, true)]
    [InlineData(null, null, "db2.example.com", "ORCL", "APP", "APP", false, true)]
    public void Same_database_and_owner_uses_names_host_and_instance(string? sourceHost, string? sourceInstance, string? targetHost,
                                                                     string? targetInstance, string sourceOwner, string targetOwner,
                                                                     bool rac, bool expected)
    {
        // 이름이 같은 복제 DB·기본 이름 컨테이너(호스트가 다름)는 다른 DB 로 본다. RAC 는 노드마다 호스트가 달라 이름만 본다.
        var same = CopyRunner.IsSameDatabaseAndOwner("ORCL/PDB1/APP_R", Server(sourceHost, sourceInstance, rac),
                                                     "orcl/pdb1/APP_W", Server(targetHost, targetInstance, rac), sourceOwner, targetOwner);
        Assert.Equal(expected, same);
        Assert.False(CopyRunner.IsSameDatabaseAndOwner("ORCL/PDB1/APP_R", Server("h", "i"), "ORCL/PDB2/APP_W", Server("h", "i"), "APP", "APP"));
    }

    [Fact]
    public void Same_rule_compares_lists_by_content()
    {
        var a = new TableRule("T", RuleKind.Period, TargetAction.DeleteRangeInsert)
        {
            PeriodColumns = [new PeriodColumn("ORDER_DT")],
            Masks = [new MaskRule("NAME", MaskKind.Hash)],
            SkipColumns = ["X"],
            Parent = new ParentLink("P", ["P_ID"], ["ID"]),
        };
        var copy = a with
        {
            PeriodColumns = [new PeriodColumn("ORDER_DT")],
            Masks = [new MaskRule("NAME", MaskKind.Hash)],
            SkipColumns = ["X"],
            Parent = new ParentLink("P", ["P_ID"], ["ID"]),
        };

        Assert.True(CopyRunner.SameRule(a, copy));
        Assert.False(CopyRunner.SameRule(a, a with { Kind = RuleKind.Exclude }));
        Assert.False(CopyRunner.SameRule(a, a with { Masks = [] }));
        Assert.False(CopyRunner.SameRule(a, a with { Parent = new ParentLink("P", ["P_ID2"], ["ID"]) }));
    }
}
