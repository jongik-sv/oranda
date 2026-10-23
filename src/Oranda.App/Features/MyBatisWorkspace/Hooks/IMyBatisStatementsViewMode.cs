using Avalonia.Controls;

namespace Oranda.App.Features.MyBatisWorkspace.Hooks;

/// <summary>
/// 「문장」 창의 다른 보기 모드(R5, design.md 9장). 계약(TSK-01-02) — 구현은 03-07 폴더 트리(<c>Folder/FolderTree*</c>).
/// 부르는 곳: 03-01 「문장」 창 도구 막대의 보기 전환(파일 하나 = 기본 / 등록된 모드).
/// </summary>
public interface IMyBatisStatementsViewMode
{
    /// <summary>모드 ID(<c>folder</c> 등).</summary>
    string Id { get; }

    /// <summary>도구 막대 단추 툴팁(한국어 화면 글, literals-en.json 에 영어 짝).</summary>
    string Title { get; }

    /// <summary>아이콘 키(기존 아이콘 리소스).</summary>
    string IconKey { get; }

    /// <summary>지금 쓸 수 있다(예 매퍼 폴더가 열려 있음).</summary>
    bool IsAvailable { get; }

    event EventHandler? AvailabilityChanged;

    /// <summary>모드의 View. 「문장」 창이 한 번 만들어 들고 있다.</summary>
    Control CreateView();
}
