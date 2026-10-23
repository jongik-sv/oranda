using Oranda.App.Features.SqlEditor;
using Oranda.Core.MyBatis;
using Oranda.Language.MyBatis;

namespace Oranda.App.Features.MyBatisWorkspace.Hooks;

/// <summary>
/// MyBatis 작업 공간 하나의 상태(design.md 2장·9장). 03-01 의 열기 훅이 만들어 <see cref="SqlWorkspaceViewModel.MyBatis"/> 에 붙이고,
/// 붙어 있는 동안 그 작업 공간의 문서 종류가 <see cref="SqlWorkspaceKind.MyBatis"/> 다. 떼는 때는 작업 공간을 닫을 때다.
/// 문장 탭(<see cref="SqlEditorViewModel"/>)과 문장의 대응, 기준 원문(연 때 또는 마지막 저장)·해시, 고친 문장 목록을 들고 있다.
/// 계약(TSK-01-02) — 구현은 03-01. 03-02·03-03·03-04·03-06 은 03-01 파일을 고치지 않고 이 멤버로만 쓴다. UI 스레드에서 쓴다.
/// </summary>
public interface IMyBatisWorkspaceSession
{
    /// <summary>매퍼 파일의 절대 경로.</summary>
    string MapperPath { get; }

    /// <summary>매퍼 namespace(없으면 null).</summary>
    string? MapperNamespace { get; }

    /// <summary>기준 문서(연 때 또는 마지막 저장·병합 뒤의 디스크 원문).</summary>
    IMyBatisMapperDocument Document { get; }

    /// <summary>기준 원문 해시(<see cref="IMyBatisMapperDocument.Hash"/>). 파일 감시는 디스크 해시가 이것과 같으면 무시한다(자기 쓰기).</summary>
    string BaselineHash { get; }

    /// <summary>저장 안 한 문장(고침·새 문장·지움·id 바꾸기)이 있다(문맥 키 <c>mybatisDocDirty</c>).</summary>
    bool IsDirty { get; }

    /// <summary>「문장」 창에서 고른 문장 또는 활성 문장 탭의 문장 id(문맥 키 <c>mybatisStatementSelected</c>). 없으면 null.</summary>
    string? SelectedStatementId { get; }

    /// <summary>
    /// 조각 해석기: 같은 매퍼에서 먼저 찾고, 없으면 등록된 해석기(<see cref="MyBatisHooks.FragmentResolvers"/>, 예 03-07 폴더)를 차례로 묻는다.
    /// 계산(03-02·03-04·03-06)은 늘 이것을 넘긴다. 이 합치기는 세션 구현(03-01)이 한다.
    /// </summary>
    IMyBatisFragmentResolver Fragments { get; }

    /// <summary>이 탭이 보이는 문장의 id. 이 작업 공간의 문장 탭이 아니면 null.</summary>
    string? StatementIdOf(SqlEditorViewModel tab);

    /// <summary>고친 원문이 반영된 요소(문장 탭 글 = <see cref="MyBatisElement.Text"/>). 없는 id 면 null.</summary>
    MyBatisElement? ElementOf(string statementId);

    /// <summary>기준 문서에 대한 저장 안 한 편집(문장 바꾸기·새 문장·지움·id 바꾸기). 고침이 없으면 빈 목록.</summary>
    IReadOnlyList<MapperEdit> PendingEdits();

    /// <summary>
    /// 문장을 고른다(탭을 열지는 않는다). 「문장」 창 우클릭 메뉴는 메뉴를 띄우기 전에 이것을 불러 두고, 명령 처리기는 <see cref="SelectedStatementId"/> 를 쓴다
    /// (셸의 우클릭 메뉴 공급은 매개변수를 넘기지 못한다).
    /// </summary>
    void SelectStatement(string? statementId);

    /// <summary>문장 탭을 열거나(이미 있으면 앞으로) 그 탭을 돌려준다. 없는 id 면 null. 03-04 「편집기에서 보기」·03-06·03-07 이 쓴다.</summary>
    SqlEditorViewModel? OpenStatement(string statementId);

    /// <summary>「+ 새 문장」(종류·id 묻기). 안쪽 탭 줄의 「+」가 MyBatis 문서에서 이것을 부른다.</summary>
    Task BeginNewStatementAsync(CancellationToken ct);

    /// <summary>매퍼 파일 경로가 바뀌었다(03-03 「경로 골라 쓰기」 뒤). 기준은 그대로, 경로만 바꾼다.</summary>
    void Relocate(string newMapperPath);

    /// <summary>
    /// 기준을 <paramref name="newText"/>(방금 쓴 디스크 원문)로 바꾼다. <paramref name="writtenStatementIds"/> 의 고침은 저장됐으므로 지우고,
    /// 나머지 고침(일부만 저장했을 때)은 새 기준 위에 남긴다(03-03 저장 뒤).
    /// </summary>
    void Rebase(string newText, IReadOnlyCollection<string> writtenStatementIds);

    /// <summary>
    /// 밖에서 바뀐 디스크 원문을 새 기준으로 받는다(design.md 5.3). <paramref name="keepMine"/> 의 문장은 내 고침을 남기고,
    /// 그 밖의 문장은 디스크 것으로 바꾼다(03-03 병합 뒤).
    /// </summary>
    void ApplyDiskText(string diskText, IReadOnlyCollection<string> keepMine);

    /// <summary>dirty·문장 목록·고른 문장 등 문맥 키·문서 탭 머리·자동 저장 표지에 쓰이는 상태가 바뀌었다.</summary>
    event EventHandler? StateChanged;
}
