using Oranda.Core.Metadata;

namespace Oranda.Core.Schema.TableEdit;

/// <summary>실행 전 검사 판정(설계 10장): ✔ / ⚠ 알림 / ✖ 막음.</summary>
public enum CheckSeverity
{
    Ok,
    Warning,
    Block,
}

/// <summary>검사 항목 종류(설계 10장 표의 줄). 화면은 이 값으로 문구 키를 고른다. 값을 더할 때는 끝에 덧붙인다.</summary>
public enum CheckCode
{
    /// <summary>✔ 항목별 한 줄(위 어느 것에도 걸리지 않음).</summary>
    Passed,

    /// <summary>✖ 길이·정밀도를 줄이는데 넘치는 행이 있다.</summary>
    Overflow,

    /// <summary>✖ 데이터가 있으면 안 되는 형식 변경(예: VARCHAR2 → NUMBER, 데이터 있는 열).</summary>
    IncompatibleTypeChange,

    /// <summary>✖ NOT NULL 추가인데 NULL 행이 있다(TE-18).</summary>
    NullRows,

    /// <summary>✖ PK·UK 추가인데 중복 키가 있다.</summary>
    DuplicateKeys,

    /// <summary>✖ FK 추가인데 짝 없는 행이 있다.</summary>
    OrphanRows,

    /// <summary>✖ CHECK 추가인데 어기는 행이 있다.</summary>
    CheckViolations,

    /// <summary>✖ 가상 식(또는 인덱스 식) 계산이 실패한다.</summary>
    ExpressionFails,

    /// <summary>✖ 이름이 이미 있다(열·제약·인덱스·테이블).</summary>
    NameExists,

    /// <summary>⚠ 운영 접속 + 큰 테이블 + 잠그는 변경(MODIFY·NOT NULL·제약 추가·인덱스 만들기). 의뢰용 스크립트 저장을 권함.</summary>
    LongLock,

    /// <summary>⚠ DROP COLUMN·CONSTRAINT·INDEX: 되돌릴 수 없음(<see cref="CheckFinding.Dependents"/> = 의존 객체).</summary>
    Irreversible,

    /// <summary>⚠ 서버 이름(SYS_C…) 제약을 다룬다 — 다른 DB 에서 실패(TE-8).</summary>
    ServerName,

    /// <summary>⚠ 검사 질의가 시간 제한을 넘었거나 읽지 못했다 — 「확인 못 함」.</summary>
    NotChecked,

    /// <summary>⚠ 가상 식 변경을 DROP + ADD 로 한다(방언이 MODIFY 를 못 함, 설계 8.2).</summary>
    VirtualRecreated,

    /// <summary>✖ 방언·서버가 지원하지 않는 편집(설계 18.8). <see cref="CheckFinding.Detail"/> = 이유 문자열 키.</summary>
    Unsupported,

    /// <summary>✖ 서버가 받지 않는 정의(지우는 열을 쓰는 식, 유일 비트맵, PK 없는 IOT 등). <see cref="CheckFinding.Detail"/> = 까닭(예 <c>droppedColumn:B</c>).</summary>
    InvalidDefinition,

    /// <summary>⚠ 테이블 이름 바꾸기로 이 테이블을 쓰는 뷰·PL/SQL·시노님이 무효가 된다(설계 18.1).</summary>
    DependentsInvalidated,

    /// <summary>⚠ 숨김 ↔ 보임으로 열 순서가 바뀐다(맨 뒤로, 설계 18.5).</summary>
    ColumnOrderChanges,

    /// <summary>⚠ 쓴 설정을 문장에 넣지 않는다(예: 새 GTT 의 테이블스페이스 — 임시 테이블스페이스를 쓴다). <see cref="CheckFinding.Detail"/> = 까닭(<c>temporaryTablespace</c>).</summary>
    SettingIgnored,

    /// <summary>⚠ MOVE 가 테이블 전체를 다시 쓴다(설계 18.2). <see cref="CheckFinding.Count"/> = 통계 행 수(모르면 null, Detail <c>unknown</c>).</summary>
    TableRewrite,

    /// <summary>⚠ ONLINE 이 안 되는 MOVE — 그동안 DML 이 잠긴다(설계 18.2).</summary>
    MoveLocksDml,

    /// <summary>✖ 형식 변환식으로 바꿀 수 없는 행이 있다(설계 18.9, TE-49). <see cref="CheckFinding.Detail"/> = <c>atLeast</c>(다 세지 못함) 또는 처음 본 서버 오류.</summary>
    ConversionFails,

    /// <summary>
    /// ⚠ 형식 변환 바꿔치기: 2단계 UPDATE 부터 3단계 DROP 까지 이 테이블에 쓰기를 멈춰야 한다(3단계 앞 확인 블록이 바뀐 행을 보면 멈춤). 행 트리거는 UPDATE 로 모든 행에서 발화한다
    /// (<see cref="CheckFinding.Dependents"/> = 트리거).
    /// </summary>
    ConversionNeedsQuiet,

    /// <summary>
    /// ⚠ Tibero 서버 결함(2026-11-18 실측, win30): 가상 열이 있는 테이블에서 지운 열의 값이 저장소에 남아, 나중에 더한 열(이름·형식이 달라도)에 보인다.
    /// 같은 이름을 다른 형식으로 다시 더하면 읽기가 깨진다(ODP-590750 corrupted DATE). 가상 열이 없으면 생기지 않는다.
    /// </summary>
    TiberoDroppedColumnData,
}

/// <summary>
/// 검사 결과 한 줄. <see cref="CheckSeverity.Ok"/> 줄은 그 항목(<see cref="Code"/>)을 검사해 걸리지 않았다는 뜻이다(설계 10장 ✔). <see cref="RowKey"/> 는 편집 줄(<see cref="TableDraft.TableRowKey"/> = 테이블 전체).
/// 문구는 화면이 <see cref="Code"/>·<see cref="Subject"/>·<see cref="Count"/>·<see cref="Detail"/> 로 만든다.
/// </summary>
public sealed record CheckFinding(CheckSeverity Severity, CheckCode Code, string RowKey)
{
    /// <summary>대상 이름(열·제약·인덱스).</summary>
    public string? Subject { get; init; }

    /// <summary>건수(넘치는 행·NULL 행·중복·짝 없는 행·어기는 행).</summary>
    public long? Count { get; init; }

    /// <summary>덧붙일 글(서버 오류 메시지, 겹친 이름, 미지원 이유 키 등).</summary>
    public string? Detail { get; init; }

    /// <summary>「그 행 보기」로 SQL 편집기에 열 SELECT(소유자 붙인 꼴). 없으면 null.</summary>
    public string? ViewRowsSql { get; init; }

    /// <summary>DROP 때 이 테이블·열을 쓰는 뷰·트리거·PL/SQL(의존 정보).</summary>
    public IReadOnlyList<DbObjectRef> Dependents { get; init; } = [];
}
