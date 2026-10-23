using Oranda.Core.Shell;

namespace Oranda.App.Features.MyBatisWorkspace.Conversion;

/// <summary>
/// 「MyBatis 작업 공간으로 바꾸기」 명령의 처리기 자리(docs/oranda1/design.md 4.1). TSK-01-02 는 특성(ID·활성 조건·자리)만 동결한다.
/// 특성 문자열·제목 파일·키맵은 고치지 않고 몸만 TSK-03-05(design.md 11장)가 채운다.
/// </summary>
[OrandaCommand("oranda.mybatis.convertWorkspace", CommandCategory.File, Icon = "swap", Enablement = "sqlWorkspaceActive && !mybatisDocActive && mybatisConvertReady", Placements = ["menu:file/mybatisConvert@97", "context:documentTab/mybatisConvert@40"])]
public sealed class MyBatisConvertCommands : ICommandHandler
{
    // 매개변수 없는 생성자만 둔다(시작 시험 제약, OutlineViewModel 주석 참고). 필요한 서비스는 TSK-03-05 가 정한다.

    public Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // 내용은 TSK-03-05(design.md 11장)
        return Task.CompletedTask;
    }
}
