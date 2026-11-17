using Oranda.Core.Connection;

namespace Oranda.Core.ChangeBundle;

/// <summary>
/// 반영 묶음 저장소(<c>oranda.db</c> 마이그레이션 <c>0006_change_bundle.sql</c>, 설계 9.1). 묶음은 이 PC 에만 있다.
/// 파일을 쓸 수 없으면(마이그레이션 실패, 더 새로운 버전) 쓰기는 조용히 건너뛰고 기본값을 돌려준다(<see cref="IsWritable"/>).
/// </summary>
public interface IChangeBundleStore
{
    /// <summary>쓸 수 있나. 처음 열기 전에는 false 일 수 있다.</summary>
    bool IsWritable { get; }

    /// <summary>모든 묶음(만든 순서).</summary>
    Task<IReadOnlyList<ChangeBundleInfo>> ListBundlesAsync(CancellationToken ct);

    Task<ChangeBundleInfo?> GetBundleAsync(long id, CancellationToken ct);

    /// <summary>「지금 묶음」. 없으면 null.</summary>
    Task<ChangeBundleInfo?> GetCurrentAsync(CancellationToken ct);

    /// <summary>새 묶음. <paramref name="makeCurrent"/> 면 이것이 「지금 묶음」이 되고 다른 묶음의 표시는 내려간다. 쓸 수 없으면 null.</summary>
    Task<ChangeBundleInfo?> CreateAsync(string name, Dbms? dbms, IReadOnlyList<string> targetOwners, bool makeCurrent, CancellationToken ct);

    Task RenameAsync(long id, string name, CancellationToken ct);

    /// <summary>대상 DBMS(빈 묶음이 첫 문장을 받을 때)·대상 소유자 목록을 바꾼다.</summary>
    Task UpdateTargetAsync(long id, Dbms? dbms, IReadOnlyList<string> targetOwners, CancellationToken ct);

    /// <summary>「지금 묶음」을 바꾼다. null 이면 「지금 묶음」 없음.</summary>
    Task SetCurrentAsync(long? id, CancellationToken ct);

    /// <summary>묶음과 그 문장·실행 기록을 지운다. 「지금 묶음」이었으면 없어진다.</summary>
    Task DeleteAsync(long id, CancellationToken ct);

    /// <summary>묶음 문장(화면 순서 = <see cref="ChangeBundleItem.Position"/> 오름차순).</summary>
    Task<IReadOnlyList<ChangeBundleItem>> ListItemsAsync(long bundleId, CancellationToken ct);

    /// <summary>같은 문장(비밀을 가린 뒤 <see cref="StatementHash"/> 가 같은 것). 없으면 null.</summary>
    Task<ChangeBundleItem?> FindSameAsync(long bundleId, string text, CancellationToken ct);

    /// <summary>끝에 넣는다(비밀 가림·해시는 저장소가 한다). 쓸 수 없으면 null.</summary>
    Task<ChangeBundleItem?> AddItemAsync(long bundleId, NewChangeBundleItem item, CancellationToken ct);

    /// <summary>
    /// 문장 전문을 고친다(묶음 창 오타 고치기). 그 문장의 실행 기록은 지운다(TE-14). 해시도 새 글로 바뀐다. 새 글에서도 비밀을 가리면 그 ⚠ 를 더하고, 이미 있던 ⚠ 는 끄지 않는다(가린 <c>***</c> 를 고쳐도 다시 적어야 함이 남는다).
    /// </summary>
    Task UpdateItemTextAsync(long itemId, string text, CancellationToken ct);

    Task SetCheckedAsync(long itemId, bool isChecked, CancellationToken ct);

    /// <summary>순서를 이 목록대로 바꾼다. 묶음에 없는 id 는 무시하고, 목록에 빠진 문장은 뒤에 원래 순서로 둔다.</summary>
    Task ReorderAsync(long bundleId, IReadOnlyList<long> itemIds, CancellationToken ct);

    Task DeleteItemAsync(long itemId, CancellationToken ct);

    /// <summary>실행 한 번을 남긴다(성공·실패 모두).</summary>
    Task AddRunAsync(ChangeBundleRun run, CancellationToken ct);

    /// <summary>묶음 문장마다 환경별 마지막 성공 때. 성공 기록이 없는 문장은 빠진다.</summary>
    Task<IReadOnlyDictionary<long, ChangeBundleRunMarks>> GetRunMarksAsync(long bundleId, CancellationToken ct);
}
