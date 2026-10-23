using Oranda.Core.Shell;

namespace Oranda.App.Features.MyBatisWorkspace.Statements;

/// <summary>
/// 「문장」 창 명령 5개(새 문장·id 바꾸기·지우기·새 SQL 탭으로 펼치기·매퍼 전체 펼치기)의 처리기 자리(docs/oranda1/design.md 4.1). TSK-01-02 는 특성(ID·활성 조건·자리)만 동결한다.
/// 특성 문자열·제목 파일·키맵은 고치지 않고 몸만 TSK-03-01(design.md 11장)가 채운다.
/// </summary>
[OrandaCommand("oranda.mybatis.newStatement", CommandCategory.Edit, Icon = "add", Enablement = "mybatisDocActive", Placements = ["context:mybatisStatements/new@10"])]
[OrandaCommand("oranda.mybatis.renameStatement", CommandCategory.Edit, Icon = "rename", Enablement = "mybatisDocActive && mybatisStatementSelected", Placements = ["context:mybatisStatements/rename@20"])]
[OrandaCommand("oranda.mybatis.deleteStatement", CommandCategory.Edit, Icon = "delete", Enablement = "mybatisDocActive && mybatisStatementSelected", Placements = ["context:mybatisStatements/delete@30"])]
[OrandaCommand("oranda.mybatis.expandToSqlTab", CommandCategory.Sql, Icon = "fileSql", Enablement = "mybatisDocActive && mybatisStatementSelected", Placements = ["context:mybatisStatements/expand@40"])]
[OrandaCommand("oranda.mybatis.expandMapper", CommandCategory.Sql, Icon = "fileSql", Enablement = "mybatisDocActive", Placements = ["context:mybatisStatements/expandAll@50"])]
public sealed class MyBatisStatementCommands : ICommandHandler
{
    // 매개변수 없는 생성자만 둔다(시작 시험 제약, OutlineViewModel 주석 참고). 필요한 서비스는 TSK-03-01 가 정한다.

    public Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // 내용은 TSK-03-01(design.md 11장)
        return Task.CompletedTask;
    }
}
