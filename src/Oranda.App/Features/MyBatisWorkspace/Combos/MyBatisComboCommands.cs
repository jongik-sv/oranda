using Oranda.Core.Shell;

namespace Oranda.App.Features.MyBatisWorkspace.Combos;

/// <summary>
/// 조합 검사·자동 고침 명령 2개의 처리기 자리(docs/oranda1/design.md 4.1). TSK-01-02 는 특성(ID·활성 조건·자리)만 동결한다.
/// 특성 문자열·제목 파일·키맵은 고치지 않고 몸만 TSK-03-04(design.md 11장)가 채운다.
/// </summary>
[OrandaCommand("oranda.mybatis.checkCombinations", CommandCategory.Sql, Icon = "compile", Enablement = "mybatisDocActive && mybatisStatementSelected", Placements = ["toolbar:mybatis/combos@30", "menu:sql/mybatisCombos@25"])]
[OrandaCommand("oranda.mybatis.applyAutoFix", CommandCategory.Edit, Icon = "docEdit", Enablement = "mybatisDocActive && mybatisStatementSelected && !readOnlyDoc", Placements = ["context:mybatisEditor/autoFix@10"])]
public sealed class MyBatisComboCommands : ICommandHandler
{
    // 매개변수 없는 생성자만 둔다(시작 시험 제약, OutlineViewModel 주석 참고). 필요한 서비스는 TSK-03-04 가 정한다.

    public Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // 내용은 TSK-03-04(design.md 11장)
        return Task.CompletedTask;
    }
}
