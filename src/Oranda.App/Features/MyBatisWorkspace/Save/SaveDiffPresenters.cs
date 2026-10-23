using Oranda.Language.MyBatis;

namespace Oranda.App.Features.MyBatisWorkspace.Save;

/// <summary>
/// 「바뀌는 부분」 창에 넘기는 것(design.md 5.1 의 5번, 시안 SaveDiff).
/// <see cref="Check"/> 는 체크한 항목만 적용한 새 원문의 검사 결과를 돌려준다(체크를 바꿀 때마다 다시 부른다).
/// </summary>
/// <param name="FileName">매퍼 파일 이름(머리 「EmpMapper.xml 에 저장」).</param>
/// <param name="Items">바뀐 문장(모두 체크된 채로 시작).</param>
/// <param name="UnchangedCount">바뀌지 않은 최상위 요소 수(「바뀌지 않은 문장 n개」).</param>
/// <param name="Check">체크한 항목 → 검사 결과.</param>
/// <param name="ShowEveryTime">「저장할 때마다 이 창 보기」의 지금 값.</param>
/// <param name="CombosChecked">조합 검사 표시(design.md 5.4: 검사가 아니라 표시만). null = 「조합 검사 안 함 (선택)」.</param>
public sealed record SaveDiffRequest(
    string FileName,
    IReadOnlyList<MapperSaveItem> Items,
    int UnchangedCount,
    Func<IReadOnlyList<MapperSaveItem>, MapperWriteCheck> Check,
    bool ShowEveryTime,
    string? CombosChecked = null);

/// <summary>「저장」의 답: 체크한 항목(1개 이상)과 「저장할 때마다 이 창 보기」 값.</summary>
public sealed record SaveDiffResult(IReadOnlyList<MapperSaveItem> Checked, bool ShowEveryTime);

/// <summary>「바뀌는 부분」 창. 취소하면 null.</summary>
public interface ISaveDiffPresenter
{
    Task<SaveDiffResult?> ShowAsync(SaveDiffRequest request, CancellationToken ct);
}

/// <summary>알림 띠 「차이 보고 고르기」: 양쪽이 고친 문장마다 파일 것 / 내 것. 취소하면 null.</summary>
public interface IMergeChoicePresenter
{
    Task<IReadOnlyDictionary<string, MapperMergeChoice>?> ChooseAsync(string fileName, IReadOnlyList<MapperMergeConflict> conflicts, CancellationToken ct);
}
