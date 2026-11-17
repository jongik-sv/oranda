using Oranda.Core.Sessions;

namespace Oranda.App.Features.TableEdit;

/// <summary>새 테이블 작업이 끝난 까닭(작업 저장이 남길지 지울지 정한다, 설계 18.7).</summary>
public enum NewTableDraftEnd
{
    /// <summary>실행이 테이블을 만들었다(남긴 작업을 지운다).</summary>
    Created,

    /// <summary>사용자가 버리고 탭을 닫았다(남긴 작업을 지운다).</summary>
    Discarded,

    /// <summary>
    /// 「남겨 두기」·확인 없이 놓기: 써 두고 놓는다(소유자 노드 「만들던 새 테이블…」로 다시 연다, TE-43). 쓰지 못했으면 예외(말없이 잃지 않게 — 부르는 쪽이 알린다).
    /// </summary>
    Kept,
}

/// <summary>
/// 「✎ 새 테이블」 탭 작업 저장(설계 18.7, TE-43)이 붙는 자리. 탭이 새 테이블 편집을 시작하면(빈 탭·「이 구조로」) <see cref="Attach"/> 가 불리고,
/// 돌려준 연결은 탭이 끝날 때 한 번 <see cref="INewTableDraftLink.FinishAsync"/> 를 받는다. 자동 저장(30초마다)은 연결 쪽이 한다.
/// 구현 = <see cref="TableDraftHook"/>(<see cref="TableDraftLibrary"/> 위). 남겨 둔 것·끝난 인스턴스 것을 다시 열 때는 <see cref="TableDraftHook.ResumeAsync"/>.
/// </summary>
public interface INewTableDraftHook
{
    /// <summary>새 테이블 편집을 시작했다. null 이면 저장하지 않는다.</summary>
    INewTableDraftLink? Attach(ISession session, TableEditSession edit);
}

/// <summary>
/// <see cref="INewTableDraftHook.Attach"/> 가 준 연결 하나. <see cref="IAsyncDisposable.DisposeAsync"/> = 끝맺지 않고 한 번 써 둔 뒤 자동 저장을 멈춘다
/// (저장분은 이 인스턴스가 쥔 채 — 앱 종료 작업. 다음 실행의 작업 공간 복원이 되살린다).
/// </summary>
public interface INewTableDraftLink : IAsyncDisposable
{
    /// <summary>
    /// 탭의 새 테이블 편집이 끝났다. 두 번째부터는 아무 일도 없다. 저장소가 실패하거나(「남겨 두기」) 쓸 수 없으면 예외를 그대로 낸다
    /// (탭이 남으면 자동 저장도 계속 돈다 — 닫기 확인은 실패를 알리고 탭을 남긴다).
    /// </summary>
    Task FinishAsync(NewTableDraftEnd reason, CancellationToken ct);

    /// <summary>바뀐 것을 지금 써 둔다(끝맺지 않음, 쥔 채). 다 써 있으면 true, 저장소에 쓸 수 없으면 false. 저장소 실패는 예외.</summary>
    Task<bool> SaveNowAsync(CancellationToken ct);
}
