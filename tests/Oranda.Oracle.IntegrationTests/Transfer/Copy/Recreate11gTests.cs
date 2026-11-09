using Oranda.Core.Connection;
using Oranda.Core.Transfer.Copy;
using Oranda.Oracle.IntegrationTests.Integration;
using Oranda.Oracle.Transfer.Copy;

namespace Oranda.Oracle.IntegrationTests.Transfer.Copy;

// 11.2: 「테이블 다시 만들기」 준비(트리거·권한·참조 FK 받아 두기)와 사전 점검이 12c 전용 열(ALL_TAB_PRIVS.TYPE)을 쓰지 않는다(ORA-00904 회귀).

/// <summary>11.2 에서 다시 만들기 준비와 사전 점검이 오류 없이 권한을 받아 둔다.</summary>
[Trait("Category", "Integration")]
[Collection(Oracle11gShared.Name)]
public sealed class Recreate11gTests
{
    private const string Columns = "ID NUMBER PRIMARY KEY, NAME VARCHAR2(20)";

    private readonly Oracle11gFixture _db;

    public Recreate11gTests(Oracle11gFixture db) => _db = db;

    [Fact]
    public async Task Capture_and_precheck_read_grants_without_12c_columns()
    {
        await using var scope = await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag());
        await scope.CreateSourceTableAsync("RC", Columns);
        await scope.CreateTargetTableAsync("RC", Columns);
        await scope.GrantWriterAsync("RC", "SELECT");
        var sessions = await scope.OpenSessionsAsync(env: ConnectionEnvironment.Development);

        var preserved = await new RecreateStep(scope.Bulk()).CaptureAsync(sessions.Target, scope.DstUser, "RC", default);
        Assert.Contains(preserved.Grants, g => g.Contains("SELECT", StringComparison.Ordinal) && g.Contains(scope.WriterUser, StringComparison.Ordinal));

        var job = scope.Job(new TableRule("RC", RuleKind.All, TargetAction.Recreate));
        var report = await scope.Precheck().RunAsync(sessions, job, new ResolvedPeriod(null, null), null, default);
        Assert.False(report.HasErrors, string.Join(Environment.NewLine, report.Items.Where(i => i.Level == CheckLevel.Error).Select(i => i.Code + ": " + i.Message)));
    }
}
