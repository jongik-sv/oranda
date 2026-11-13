namespace Oranda.Core.Rollback;

/// <summary>
/// 묶음 저장소(설계 12.1·12.3): <c>oranda.db</c> 의 <c>rollback_group</c>·<c>rollback_statement</c> 행과 <c>{data}/rollback</c> 의 암호화 파일.
/// 구현: Oranda.Platform <c>SqliteRollbackStore</c>(RB-21). 요약과 이유는 저장소가 <c>ISecretRedactor</c> 로 가린 뒤 쓴다.
/// </summary>
public interface IRollbackStore
{
    /// <summary>= vault.IsDurable 이고 DB 를 쓸 수 있음.</summary>
    bool CanStoreScripts { get; }

    /// <summary>Id = 0 이면 새로 만들고 Id 를 돌려준다. 있으면 덮어쓴다. document 가 null 이면 파일을 만들지 않는다.</summary>
    Task<long> SaveAsync(RollbackGroup group, RollbackScriptDocument? document, CancellationToken ct);

    /// <summary>최근 순: <c>EndedAt ?? StartedAt</c> 내림차순으로 정렬한 뒤 <see cref="RollbackQuery.Limit"/> 개까지 자른다.</summary>
    Task<IReadOnlyList<RollbackGroup>> SearchAsync(RollbackQuery query, CancellationToken ct);

    Task<RollbackGroup?> GetAsync(long id, CancellationToken ct);

    /// <summary>파일이 없으면 null. 키가 없거나 변조되면 <see cref="System.Security.Cryptography.CryptographicException"/>.</summary>
    Task<RollbackScriptDocument?> LoadDocumentAsync(long id, CancellationToken ct);

    /// <summary>시작 정리: 「열림」 묶음을 「확인 필요」(AppRestart)로 바꾸고 바꾼 수를 돌려준다(RD-7).</summary>
    Task<int> MarkOrphansAsync(CancellationToken ct);

    /// <summary>
    /// 보관 기간이 지난 묶음의 행과 파일을 지운다(12.3). 기준 시각은 <c>EndedAt ?? StartedAt</c>.
    /// 「열림」(<see cref="RollbackScriptState.Open"/>) 묶음은 지우지 않는다(끝나지 않은 트랜잭션, 시작 정리가 「확인 필요」로 바꾼 뒤 기간을 센다).
    /// </summary>
    Task PruneAsync(int retentionDays, CancellationToken ct);
}
