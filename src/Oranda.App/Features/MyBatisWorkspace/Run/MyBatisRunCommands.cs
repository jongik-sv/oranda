using Oranda.Core.Shell;

namespace Oranda.App.Features.MyBatisWorkspace.Run;

/// <summary>
/// 시나리오 실행·실행 계획·펼친 SQL 복사 명령 4개의 처리기 자리(docs/oranda1/design.md 4.1). TSK-01-02 는 특성(ID·활성 조건·자리)만 동결한다.
/// 특성 문자열·제목 파일·키맵은 고치지 않고 몸만 TSK-03-02(design.md 11장)가 채운다.
/// </summary>
[OrandaCommand("oranda.mybatis.runScenario", CommandCategory.Sql, Icon = "run", When = "mybatisDocFocus", Enablement = "mybatisDocActive && mybatisStatementSelected && (hasSession || dedicatedSession || tabConnectionMissing) && !sessionBusy", Placements = ["toolbar:mybatis/run@10", "menu:sql/mybatisRun@15"])]
[OrandaCommand("oranda.mybatis.explainScenario", CommandCategory.Sql, Icon = "explainPlan", Enablement = "mybatisDocActive && mybatisStatementSelected && (hasSession || dedicatedSession || tabConnectionMissing) && !sessionBusy", Placements = ["toolbar:mybatis/explain@20", "menu:sql/mybatisExplain@52"])]
[OrandaCommand("oranda.mybatis.copyExpanded", CommandCategory.Edit, Icon = "copy", Enablement = "mybatisDocActive && mybatisStatementSelected", Placements = ["toolbar:mybatis/copy@50", "context:mybatisExpanded/copy@10"])]
[OrandaCommand("oranda.mybatis.copyExpandedWithValues", CommandCategory.Edit, Icon = "copy", Enablement = "mybatisDocActive && mybatisStatementSelected", Placements = ["toolbar:mybatis/copy@51", "context:mybatisExpanded/copyValues@20"])]
public sealed class MyBatisRunCommands : ICommandHandler
{
    // 매개변수 없는 생성자만 둔다(시작 시험 제약, OutlineViewModel 주석 참고). 필요한 서비스는 TSK-03-02 가 정한다.

    public Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // 내용은 TSK-03-02(design.md 11장)
        return Task.CompletedTask;
    }
}
