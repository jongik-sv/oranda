namespace Oranda.App.Shell;

/// <summary>
/// 안에 자기 탭 줄을 가진 문서(SQL 작업 공간: 문서 탭 하나 안에 SQL 탭 여러 개). 셸의 「닫기」(Ctrl+W)는 안쪽 탭부터 닫고,
/// 「다음·이전 문서」(Ctrl+Tab)는 안쪽 탭을 차례로 지난 뒤 끝에서 다음 문서로 넘어간다.
/// </summary>
public interface IInnerTabsDocument : System.ComponentModel.INotifyPropertyChanged
{
    /// <summary>안쪽 활성 탭의 이름(창 제목에 「문서 / 탭」으로 보인다). 바뀌면 이 이름으로 PropertyChanged 를 낸다.</summary>
    string? ActiveInnerTitle { get; }

    /// <summary>안쪽 활성 탭을 닫는다. 닫을 탭이 없으면 false(그러면 셸이 문서 자체를 닫는다).</summary>
    bool CloseActiveInnerTab();

    /// <summary>안쪽 탭을 <paramref name="offset"/> 칸 옮긴다. 끝을 넘으면 옮기지 않고 false.</summary>
    bool CycleInnerTab(int offset);

    /// <summary>다른 문서에서 넘어왔을 때: 앞으로 왔으면 첫 탭, 뒤로 왔으면 마지막 탭을 고른다.</summary>
    void SelectEdgeInnerTab(bool first);
}
