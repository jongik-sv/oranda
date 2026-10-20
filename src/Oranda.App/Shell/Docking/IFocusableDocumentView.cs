namespace Oranda.App.Shell.Docking;

/// <summary>
/// 문서 탭의 보기(View)가 「문서로 포커스 옮기기」(<see cref="DockWorkspace.FocusDocument"/>) 때 어디에 포커스를 줄지 정한다.
/// 없으면 도킹 영역이 보기 안의 첫 포커스 가능한 컨트롤에 준다.
/// </summary>
public interface IFocusableDocumentView
{
    /// <summary>내용으로 포커스를 옮긴다. 아직 화면에 붙지 않아 못 옮겼으면 false(잠깐 뒤 다시 부른다).</summary>
    bool FocusContent();
}
