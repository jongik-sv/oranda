using Avalonia.Controls;
using Oranda.App.Features.SqlEditor;

namespace Oranda.App.Features.MyBatisWorkspace.Hooks;

/// <summary>키보드 포커스를 편집기 글자 영역으로 옮길 수 있는 View(<see cref="SqlWorkspaceView"/> 가 탭을 고를 때 부른다).</summary>
public interface IEditorFocusTarget
{
    void FocusEditor();
}

/// <summary>
/// MyBatis 문장 탭의 편집기 View 와 탭 머리 내용(design.md 1장·2.3·9장). 계약(TSK-01-02) — 구현은 03-01.
/// 부르는 곳: <see cref="SqlWorkspaceView"/> 가 MyBatis 작업 공간의 탭 View 를 만들 때(아니면 지금처럼 <c>SqlEditorView</c>).
/// 편집기 View 는 <see cref="IEditorFocusTarget"/> 를 구현하고, 감싼 <c>SqlEditorView</c> 의 포커스 자리를 <c>mybatisText</c> 로 덮어쓴다.
/// </summary>
public interface IMyBatisEditorViewFactory
{
    /// <summary>문장 탭 하나의 편집기 View(머리 줄 + 원문 편집기). 탭마다 한 번 만들고 작업 공간 View 가 들고 있다.</summary>
    Control CreateEditor(SqlEditorViewModel tab, IMyBatisWorkspaceSession session);

    /// <summary>탭 머리 앞에 붙일 내용(종류 표시 SEL·INS·UPD·DEL). 없으면 null.</summary>
    Control? CreateTabBadge(SqlEditorViewModel tab, IMyBatisWorkspaceSession session);
}
