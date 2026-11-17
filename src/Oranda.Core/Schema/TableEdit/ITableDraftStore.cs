using Oranda.Core.Connection;

namespace Oranda.Core.Schema.TableEdit;

/// <summary>
/// 새 테이블 작업 저장소(<c>oranda.db</c> 마이그레이션 <c>0007_table_draft.sql</c>, 설계 18.7·TE-43). 「✎ 새 테이블」 탭의 편집 모델을
/// 30초마다·탭을 닫을 때 덮어써 두고, 작업 공간 복원·소유자 노드 「만들던 새 테이블…」로 되살린다. 실행 성공·「버리기」 때 지운다.
/// 저장분마다 그것을 탭에 연 앱 인스턴스(<see cref="TableDraftEntry.OpenBy"/>)를 적어, 앱 두 개가 같은 파일을 써도 한 저장분은 한 인스턴스만 쥔다 —
/// 덮어쓰기·놓기·지우기는 그 값을 조건으로 한다.
/// 파일을 쓸 수 없으면(마이그레이션 실패, 더 새로운 버전) 쓰기는 조용히 건너뛰고 기본값을 돌려준다(<see cref="IsWritable"/>).
/// </summary>
public interface ITableDraftStore
{
    /// <summary>쓸 수 있나. 처음 열기 전에는 false 일 수 있다.</summary>
    bool IsWritable { get; }

    /// <summary>
    /// 저장분 목록(고친 때 최근 순). <paramref name="connectionName"/>·<paramref name="owner"/> 가 null 이면 거르지 않는다(대소문자 그대로 비교).
    /// 모델 JSON 은 읽지 않는다.
    /// </summary>
    Task<IReadOnlyList<TableDraftEntry>> ListAsync(string? connectionName, string? owner, CancellationToken ct);

    /// <summary>저장분 하나. 없거나 모델 JSON 이 깨졌으면 null.</summary>
    Task<StoredTableDraft?> GetAsync(long id, CancellationToken ct);

    /// <summary>
    /// 저장. <paramref name="id"/> 가 이 인스턴스(<paramref name="instance"/>)가 쥔 행이면 덮어쓰고, null 이거나 그 행이 지워졌거나 다른 인스턴스가 쥐었으면
    /// 새 행(새 id)으로 넣는다 — 탭의 작업은 잃지 않고 남의 저장분은 덮지 않는다. <paramref name="keepOpen"/> 이 거짓이면(닫기 「남겨 두기」) 쓰고 놓는다
    /// (<see cref="TableDraftEntry.OpenBy"/> = null). 소유자·이름은 <paramref name="draft"/> 에서 온다. 쓸 수 없으면 null.
    /// </summary>
    Task<TableDraftEntry?> SaveAsync(long? id, string connectionName, Dbms dbms, TableDraft draft, string instance, bool keepOpen, CancellationToken ct);

    /// <summary>
    /// 저장분을 이 인스턴스가 쥔다(다시 열기). 지금 쥔 값이 <paramref name="expectedOpenBy"/>(놓인 것 = null, 끝난 인스턴스 값)와 같을 때만 바꾸고 참.
    /// 그 사이 다른 인스턴스가 쥐었거나 지워졌으면 거짓. 고친 때는 바꾸지 않는다.
    /// </summary>
    Task<bool> ClaimAsync(long id, string? expectedOpenBy, string instance, CancellationToken ct);

    /// <summary>이 인스턴스가 쥔 저장분을 놓는다(모델은 그대로). 남이 쥐었으면 아무 일도 없다.</summary>
    Task ReleaseAsync(long id, string instance, CancellationToken ct);

    /// <summary>쥔 값이 <paramref name="expectedOpenBy"/> 와 같을 때만 지우고 참(탭의 「버리기」 = 이 인스턴스, 목록에서 지우기 = 놓인 것·끝난 인스턴스).</summary>
    Task<bool> DeleteAsync(long id, string? expectedOpenBy, CancellationToken ct);
}

/// <summary>
/// 저장분 한 줄(목록용, 모델 없음). <see cref="Name"/> 은 사용자가 아직 이름을 안 썼으면 빈 문자열.
/// <see cref="OpenBy"/> = 이것을 탭에 연 앱 인스턴스. null = 닫기에서 「남겨 두기」한 것(소유자 노드 메뉴로만 다시 엶).
/// </summary>
public sealed record TableDraftEntry(long Id, string ConnectionName, Dbms Dbms, string Owner, string Name, DateTimeOffset UpdatedAt, string? OpenBy);

/// <summary>저장분 한 줄과 그 편집 모델.</summary>
public sealed record StoredTableDraft(TableDraftEntry Entry, TableDraft Draft);
