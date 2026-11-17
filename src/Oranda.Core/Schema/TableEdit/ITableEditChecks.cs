using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.Core.Schema.TableEdit;

/// <summary>
/// 실행 전 검사·가상 식 미리보기의 읽기(설계 5장·7.6·10장, TE-33). 사용자 문장이 아니므로 실행 엔진 밖에서 메타데이터 공급자처럼 세션 읽기 경로로 읽고,
/// 실행 이력·변경 일지·반영 묶음에 남기지 않는다. 읽기만 한다(잠금 없음).
/// 구현은 Oracle·Tibero 공급자가 <c>[OrandaService(typeof(ITableEditChecks), Dbms = …)] internal sealed</c> 로 둔다.
/// 시간 제한은 부르는 쪽이 <paramref name="ct"/> 로 건다(기본 10초) — 구현은 취소를 서버 명령 취소로 옮기고 <see cref="OperationCanceledException"/> 을 낸다.
/// 이름은 모두 사전 표기(따옴표 없는 대문자 그대로)로 받고, 구현이 <c>SqlIdentifier</c> 로 감싼다.
/// </summary>
public interface ITableEditChecks
{
    /// <summary>이 구현이 맡는 DBMS.</summary>
    Dbms Dbms { get; }

    /// <summary><c>SELECT COUNT(*)</c>.</summary>
    Task<long> CountRowsAsync(ISession s, DbObjectRef table, CancellationToken ct);

    /// <summary>통계 행 수(<c>NUM_ROWS</c>, 제목줄·큰 테이블 판정). 통계가 없으면 null.</summary>
    Task<long?> NumRowsAsync(ISession s, DbObjectRef table, CancellationToken ct);

    /// <summary>
    /// 열 <paramref name="column"/> 의 값 가운데 새 정의 <paramref name="target"/>(길이·길이 기준·정밀도·소수)에 담기지 않는 행 수.
    /// 문자 = <c>LENGTH</c>/<c>LENGTHB</c> &gt; n, 숫자 = 정수 자리 넘침(소수 자리를 줄이는 것은 반올림이라 넘침이 아니다).
    /// </summary>
    Task<long> OverflowCountAsync(ISession s, DbObjectRef table, string column, ColumnDraft target, CancellationToken ct);

    /// <summary>열이 NULL 인 행 수.</summary>
    Task<long> NullCountAsync(ISession s, DbObjectRef table, string column, CancellationToken ct);

    /// <summary>열이 NULL 이 아닌 행 수(데이터 있는 열의 형식 변경 판정).</summary>
    Task<long> NonNullCountAsync(ISession s, DbObjectRef table, string column, CancellationToken ct);

    /// <summary>키 <paramref name="columns"/> 가 겹치는 행 수(겹치는 묶음의 행을 모두 센다. 모두 NULL 인 키는 빼고 — UK 규칙).</summary>
    Task<long> DuplicateKeyCountAsync(ISession s, DbObjectRef table, IReadOnlyList<string> columns, CancellationToken ct);

    /// <summary>FK 를 더하면 짝이 없는 행 수(키 열이 하나라도 NULL 인 행은 빼고).</summary>
    Task<long> OrphanCountAsync(
        ISession s, DbObjectRef table, IReadOnlyList<string> columns, DbObjectRef referencedTable, IReadOnlyList<string> referencedColumns, CancellationToken ct);

    /// <summary>CHECK 조건을 어기는 행 수(<c>WHERE NOT (조건)</c>, 조건이 UNKNOWN 인 행은 어기지 않음).</summary>
    Task<long> CheckViolationCountAsync(ISession s, DbObjectRef table, string condition, CancellationToken ct);

    /// <summary>
    /// 가상 식 미리보기(설계 7.6): <c>SELECT 식, 쓰인 열 FROM 테이블 WHERE ROWNUM &lt;= rows</c>. 계산이 실패하면 오류를 <see cref="ExpressionPreview.Error"/> 에 담는다(던지지 않음).
    /// </summary>
    /// <param name="columns">식에 쓰인 열(같이 보인다)</param>
    Task<ExpressionPreview> PreviewExpressionAsync(
        ISession s, DbObjectRef table, string expression, IReadOnlyList<string> columns, int rows, CancellationToken ct);

    /// <summary>
    /// 새 테이블의 식 문법 확인(데이터 없음): <paramref name="columns"/> 형식의 빈 행 위에서 식을 해석만 한다. 맞으면 null, 아니면 서버 오류 메시지.
    /// </summary>
    Task<string?> ValidateExpressionAsync(ISession s, string owner, IReadOnlyList<ColumnDraft> columns, string expression, CancellationToken ct);

    /// <summary>
    /// 이름이 이미 있나. <see cref="TableEditNameKind.Column"/> 은 <paramref name="table"/> 의 열, 나머지는 <paramref name="owner"/> 스키마 안
    /// (테이블 = 테이블·뷰·시노님 등 같은 이름 공간, 인덱스, 제약).
    /// </summary>
    Task<bool> NameExistsAsync(ISession s, string owner, string name, TableEditNameKind kind, DbObjectRef? table, CancellationToken ct);

    /// <summary>이 테이블을 쓰는 객체(뷰·트리거·PL/SQL·시노님, <c>ALL_DEPENDENCIES</c>).</summary>
    Task<IReadOnlyList<DbObjectRef>> DependentsAsync(ISession s, DbObjectRef table, CancellationToken ct);
}

/// <summary>이름 겹침 검사 대상.</summary>
public enum TableEditNameKind
{
    Table,
    Column,
    Constraint,
    Index,
}

/// <summary>가상 식 미리보기 결과. <see cref="Error"/> 가 있으면 계산 실패(<see cref="Rows"/> 는 빔).</summary>
/// <param name="Columns">결과 열 이름(식 + 쓰인 열)</param>
/// <param name="Rows">값 문자열(NULL = null)</param>
/// <param name="Error">서버 오류 메시지</param>
public sealed record ExpressionPreview(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string?>> Rows, string? Error = null);
