using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.Core.Transfer.Copy;

/// <summary>
/// 사전 점검(<see cref="ICopyPrecheckService"/>)이 DBMS 마다 달리 보는 것: 기능 차이와 그 DBMS 고유의 이름·오류 번호가 든 안내 글.
/// 값 해석은 <see cref="IBulkValueCodec"/>, 미리 보기 문장은 <see cref="ICopyDialect"/>, 오류 분류는 <c>IDbErrorClassifier</c> 가 따로 맡는다.
/// 구현은 DBMS 어셈블리에 두고 그 DBMS 의 사전 점검 하위 형식이 생성자에서 넘긴다. 키 서비스가 아니다.
/// </summary>
public interface ICopyPrecheckDialect
{
    /// <summary>조회만 주는 객체 권한 <c>READ</c>·시스템 권한 <c>READ ANY …</c> 가 있어 SELECT 와 같게 보는가(Oracle 12c+).</summary>
    bool SupportsReadPrivilege { get; }

    /// <summary>
    /// 「테이블 다시 만들기」를 이 DBMS 대상에서 아예 막는 이유(예: 휴지통 되살리기가 확인되지 않음). 막지 않으면 null.
    /// 글은 <c>RecreateBlocked</c> 항목의 막는 이유 목록에 그대로 들어간다.
    /// </summary>
    string? RecreateBlockedReason(ISession target);

    /// <summary>
    /// 다시 만들 원본 구조가 대상의 기본 문자열 한도를 넘어 CREATE 가 실패할 이유(0개 또는 몇 개의 글). <paramref name="targetParameter"/> 는
    /// 대상 세션의 초기화 파라미터 값을 글자로 읽는다(<c>copy.parameter_value</c>, 못 읽거나 서버 오류면 null).
    /// </summary>
    Task<IReadOnlyList<string>> ExtendedStringBlockersAsync(TableInfo source, Func<string, CancellationToken, Task<string?>> targetParameter,
                                                            CancellationToken ct);

    /// <summary>
    /// DROP 과 함께 사라지고 되살리지 않는 것을 세는 카탈로그 조회(<c>copy.*_count</c> id, 바인드 <c>:OWN</c>·<c>:TBL</c>)와 안내 이름. 순서대로 묻는다.
    /// </summary>
    IReadOnlyList<(string CatalogId, string Label)> NotRestoredProbes { get; }

    /// <summary>다시 만들기 확인 글의 「지운 표가 어디에 남는가」 문장(앞 공백 포함). <paramref name="tablespace"/> 는 대상 표의 테이블스페이스(모르면 null).</summary>
    string RecycleBinNote(string? tablespace);

    /// <summary>
    /// 같은 시점 읽기가 기대는 원본 초기화 파라미터 이름(<c>copy.parameter_value</c> 의 <c>:PNAME</c>). 값은 초 단위 숫자다.
    /// null 이면 조회를 보내지 않고 「못 읽음」(<see cref="ReadConsistencyHint"/> 의 retention = null)으로 둔다(파라미터 뷰가 없는 DBMS).
    /// </summary>
    string? ReadConsistencyParameter { get; }

    /// <summary>
    /// 같은 시점 읽기 안내. <paramref name="estimatedSeconds"/> = 어림 읽기 시간(초), <paramref name="retention"/> = <see cref="ReadConsistencyParameter"/> 값(못 읽으면 null).
    /// </summary>
    (CheckLevel Level, string Message) ReadConsistencyHint(long estimatedSeconds, long? retention);

    /// <summary>자식 행이 남아 부모 행을 지우지 못할 때 서버가 내는 오류 이름(확인 글에 들어간다, Oracle = <c>ORA-02292</c>).</summary>
    string ChildRecordFoundError { get; }
}
