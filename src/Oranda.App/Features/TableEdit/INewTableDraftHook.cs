using Oranda.Core.Sessions;

namespace Oranda.App.Features.TableEdit;

/// <summary>새 테이블 작업이 끝난 까닭(작업 저장이 남길지 지울지 정한다, 설계 18.7).</summary>
public enum NewTableDraftEnd
{
    /// <summary>실행이 테이블을 만들었다(남긴 작업을 지운다).</summary>
    Created,

    /// <summary>사용자가 버리고 탭을 닫았다(남긴 작업을 지운다).</summary>
    Discarded,
}

/// <summary>
/// 「✎ 새 테이블」 탭 작업 저장(설계 18.7, TE-43)이 붙는 자리. 탭이 새 테이블 편집을 시작하면(빈 탭·「이 구조로」·되살리기) <see cref="Attach"/> 가 불리고,
/// 돌려준 연결은 탭이 끝날 때 한 번 <see cref="INewTableDraftLink.Finish"/> 를 받는다. 자동 저장(30초마다·닫을 때)은 연결 쪽이 <see cref="TableEditSession.Recomputed"/> 를 보고 한다.
/// 이 빌드는 구현을 등록하지 않는다(작업 저장 저장소·서비스가 머지되면 그쪽이 서비스로 등록한다).
/// </summary>
public interface INewTableDraftHook
{
    /// <summary>새 테이블 편집을 시작했다. null 이면 저장하지 않는다.</summary>
    INewTableDraftLink? Attach(ISession session, TableEditSession edit);
}

/// <summary><see cref="INewTableDraftHook.Attach"/> 가 준 연결 하나.</summary>
public interface INewTableDraftLink
{
    /// <summary>탭의 새 테이블 편집이 끝났다(한 번만 불린다).</summary>
    void Finish(NewTableDraftEnd reason);
}
