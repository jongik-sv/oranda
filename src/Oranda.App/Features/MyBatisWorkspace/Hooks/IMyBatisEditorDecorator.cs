using AvaloniaEdit;
using Oranda.App.Features.SqlEditor;

namespace Oranda.App.Features.MyBatisWorkspace.Hooks;

/// <summary>꾸밈을 붙일 문장 편집기 하나의 문맥.</summary>
/// <param name="Session">그 문장이 든 MyBatis 작업 공간.</param>
/// <param name="Tab">문장 탭.</param>
/// <param name="Editor">원문 편집기(<c>TextView</c> 의 LineTransformer·BackgroundRenderer·여백을 붙인다).</param>
public sealed record MyBatisEditorContext(IMyBatisWorkspaceSession Session, SqlEditorViewModel Tab, TextEditor Editor);

/// <summary>
/// 문장 편집기 꾸밈(R3, design.md 9장). 계약(TSK-01-02) — 구현은 Task 마다 파일 하나:
/// 03-01 강조(<c>Editor/Highlighting/</c>), 03-02 흐림·켬끔 점·물결(<c>Editor/EvalDecorator*</c>), 03-04 자동 고침 띠(<c>Combos/AutoFixDecorator*</c>).
/// 부르는 곳: 03-01 문장 편집기 View 가 열 때 등록된 꾸밈을 <see cref="Order"/> 순서로 모두 붙이고, 닫을 때 돌려받은 것을 Dispose 한다.
/// </summary>
public interface IMyBatisEditorDecorator
{
    /// <summary>붙이는 순서(작은 것 먼저). 강조 0, 계산 꾸밈 100, 자동 고침 200.</summary>
    int Order { get; }

    IDisposable Attach(MyBatisEditorContext context);
}
