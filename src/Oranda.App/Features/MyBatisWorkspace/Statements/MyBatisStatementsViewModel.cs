using Oranda.App.Shell;
using Oranda.Core.Shell;

namespace Oranda.App.Features.MyBatisWorkspace.Statements;

/// <summary>
/// 「문장」 도구 창 자리(docs/oranda1/design.md 3.1). TSK-01-02 는 등록과 빈 화면만 둔다. 내용은 TSK-03-01 가 이 파일과 View 를 채운다.
/// </summary>
[OrandaToolPanel("oranda.panel.mybatisStatements", ToolPanelKind.Tool, DockLocation.Left, Order = 30)]
public sealed class MyBatisStatementsViewModel : ToolPanelViewModelBase
{
    // 매개변수 없는 생성자만 둔다(OutlineViewModel 주석의 시작 시험 제약).
}
