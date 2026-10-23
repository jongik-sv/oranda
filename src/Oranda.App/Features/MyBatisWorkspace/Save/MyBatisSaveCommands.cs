using Oranda.App.Features.SqlEditor;
using Oranda.Core.Shell;

namespace Oranda.App.Features.MyBatisWorkspace.Save;

/// <summary>
/// 「xml 에 저장」 명령(Ctrl+S 는 file.save 의 저장 훅을 거쳐 같은 처리기)의 처리기 자리(docs/oranda1/design.md 4.1). TSK-01-02 는 특성(ID·활성 조건·자리)만 동결한다.
/// 특성 문자열·제목 파일·키맵은 고치지 않고 몸만 TSK-03-03(design.md 11장)가 채운다: 활성 MyBatis 작업 공간을 저장 훅(<see cref="MyBatisSaveHandler"/>)에 넘긴다.
/// </summary>
[OrandaCommand("oranda.mybatis.save", CommandCategory.File, Icon = "save", Enablement = "mybatisDocActive && mybatisDocDirty && !mybatisFormatAllBusy", Placements = ["toolbar:mybatis/save@40", "menu:file/mybatisSave@32"])]
public sealed class MyBatisSaveCommands : ICommandHandler
{
    // 매개변수 없는 생성자만 둔다(시작 시험 제약, OutlineViewModel 주석 참고).

    /// <summary>다루는 호스트. 기본은 <see cref="SqlEditorHost.Current"/>(시험이 바꾼다).</summary>
    internal SqlEditorHost? Host { get; set; }

    public async Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var host = Host ?? SqlEditorHost.Current;
        if (host.ActiveWorkspace is { MyBatis: not null } workspace && host.MyBatisHooks.SaveHandler is { } handler)
        {
            await handler.SaveAsync(workspace, cancellationToken).ConfigureAwait(true);
        }
    }
}
