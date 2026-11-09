using Oranda.Core.Transfer.Copy;
using Oranda.Oracle.IntegrationTests.Integration;

namespace Oranda.Oracle.IntegrationTests.Transfer.Copy;

// 작업 변수: 조건의 :이름 이 실제 서버에서 바인드되는지. 값 1000개를 넘는 목록(999개씩 SYS.ODCI*LIST 로 묶음)은 11g 의 IN 상한(ORA-01795)과
// 목록 생성자 인자 상한(ORA-00939)을 피해야 한다.

/// <summary>26ai: 여러 값·한 값·날짜 변수로 원본·대상 건수를 센다(사전 점검 예상 건수).</summary>
[Trait("Category", "Integration")]
[Collection(OracleFreeShared.Name)]
public sealed class CopyVariablesDbTests
{
    private readonly OracleFreeFixture _db;

    public CopyVariablesDbTests(OracleFreeFixture db) => _db = db;

    [Fact]
    public async Task Variables_bind_on_the_server_including_more_than_a_thousand_values()
        => await CopyVariableCases.CountWithVariablesAsync(await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag()));
}

/// <summary>11.2: 같은 경우(IN 목록 1000개 상한이 있는 서버).</summary>
[Trait("Category", "Integration")]
[Collection(Oracle11gShared.Name)]
public sealed class CopyVariables11gDbTests
{
    private readonly Oracle11gFixture _db;

    public CopyVariables11gDbTests(Oracle11gFixture db) => _db = db;

    [Fact]
    public async Task Variables_bind_on_the_server_including_more_than_a_thousand_values()
        => await CopyVariableCases.CountWithVariablesAsync(await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag()));
}

internal static class CopyVariableCases
{
    public static async Task CountWithVariablesAsync(CopyDbScope scope)
    {
        await using var _ = scope;
        const string columns = "ID NUMBER PRIMARY KEY, COIL VARCHAR2(20), MO VARCHAR2(20), DT DATE";
        await scope.CreateSourceTableAsync("COILS", columns);
        await scope.CreateTargetTableAsync("COILS", columns);
        await scope.ExecSourceAsync(
            "INSERT INTO COILS SELECT LEVEL, 'C' || LEVEL, CASE WHEN MOD(LEVEL, 2) = 0 THEN 'MO-A' ELSE 'MO-B' END, DATE '2026-11-01' + MOD(LEVEL, 3) FROM DUAL CONNECT BY LEVEL <= 2000");
        await scope.ExecSourceAsync("COMMIT");

        // 코일 1~1500(문자 1500개 → 999 + 501 로 묶음) 가운데 MO-A(짝수) = 750, 그중 날짜가 11-01·11-02 인 것.
        var coils = Enumerable.Range(1, 1500).Select(i => "C" + i).ToArray();
        var rule = new TableRule("COILS", RuleKind.Custom, TargetAction.DeleteRangeInsert)
        {
            CustomWhere = "COIL IN (:COILS) AND MO = :MO AND DT IN (:DAYS)",
        };
        var job = scope.Job(rule) with
        {
            Variables =
            [
                new CopyVariable("COILS", CopyVariableType.Text, coils),
                new CopyVariable("MO", CopyVariableType.Text, ["MO-A"]),
                new CopyVariable("DAYS", CopyVariableType.Date, ["2026-11-01", "2026-11-02"]),
            ],
        };
        long expected = Enumerable.Range(1, 1500).Count(i => i % 2 == 0 && i % 3 != 2);
        var sessions = await scope.OpenSessionsAsync();

        var report = await scope.Precheck().RunAsync(sessions, job, new ResolvedPeriod(null, null), null, default);

        Assert.False(report.HasErrors, string.Join(Environment.NewLine, report.Items.Where(i => i.Level == CheckLevel.Error).Select(i => i.Code + ": " + i.Message)));
        Assert.Equal(expected, report.EstimatedRows["COILS"]);
        var delete = Assert.Single(report.Preview, p => p.Side == PreviewSide.Target && p.Sql.StartsWith("DELETE FROM", StringComparison.Ordinal));
        Assert.Contains("SYS.ODCIVARCHAR2LIST(", delete.Sql, StringComparison.Ordinal);
    }
}
