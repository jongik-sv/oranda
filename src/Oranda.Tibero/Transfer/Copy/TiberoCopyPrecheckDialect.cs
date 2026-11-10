using System.Globalization;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Transfer.Copy;

namespace Oranda.Tibero.Transfer.Copy;

/// <summary>
/// Tibero 사전 점검 방언(<see cref="ICopyPrecheckDialect"/>, T5 결정 65 TB3a). 근거는 docs/design/tibero-driver-transfer.md 3장(TM0b 실측, 6.2.15).
/// 이 형식은 키 서비스가 아니고 등록하지 않는다: Tibero 복사는 복사 키 등록(두 번째 계정 복사 IT 뒤, TI1b 결정65) 전까지 꺼져 있고, 사전 점검 하위 형식
/// <c>TiberoCopyPrecheckService</c>(TI1b)가 Tibero 코덱(TB1)·실행 방언(TB3b)과 함께 이 방언을 넘긴다. 카탈로그 SELECT 는 <c>Catalog/copy/*.sql</c>(<c>dbms: tibero</c>).
/// </summary>
internal sealed class TiberoCopyPrecheckDialect : ICopyPrecheckDialect
{
    public static readonly TiberoCopyPrecheckDialect Instance = new();

    /// <summary>지운 표가 남는 곳을 알리는 문장에서 영구 삭제로 보는 테이블스페이스.</summary>
    private const string SystemTablespace = "SYSTEM";

    /// <summary>
    /// 복사 뒤 되살리지 않는 것을 세는 조회. Oracle 의 FGA 정책(<c>copy.fga_policy_count</c>)은 없다: Tibero 에 <c>ALL_AUDIT_POLICIES</c> 가 없어(-8033)
    /// 같은 개념을 읽을 사전 뷰를 찾지 못했다. 거짓 「0개」 를 내지 않으려고 0 을 돌려주는 조회를 만들지 않고 목록에서 뺀다.
    /// </summary>
    private static readonly (string CatalogId, string Label)[] Probes =
    [
        ("copy.col_priv_count", "열 권한"),
        ("copy.other_grantor_count", "다른 계정이 준 권한"),
        ("copy.mview_log_count", "MV 로그"),
        ("copy.vpd_policy_count", "VPD 정책"),
    ];

    /// <summary>Tibero 에는 조회만 주는 READ 객체 권한·READ ANY TABLE 이 없다(확인된 것이 없어 SELECT 권한만 SELECT 로 본다).</summary>
    public bool SupportsReadPrivilege => false;

    /// <summary>
    /// 「테이블 다시 만들기」는 아직 막는다(TB4 결정). 받아 두기 부품(<see cref="TiberoRecreateStep"/>: 트리거 DDL = T3 <c>*_SOURCE</c> 조립, 휴지통 BIN 이름,
    /// <c>FLASHBACK TABLE … TO BEFORE DROP [RENAME TO]</c>)은 <c>TiberoRecreateStepIt</c> 가 잰다. 남은 위험(TB4 리뷰)은 실제 실행기로 도는 두 번째 계정 복사 IT(TI1b 결정65 - 지금 TI1b IT 는 같은 계정 거부만 본다)가 재고 나서 푼다:
    /// 컴파일 오류 트리거(번호 미분류 → 끄지 못함), 바이트 길이를 CHAR 로 다시 쓸 때 큰 열, 부모·자식을 함께 다시 만들 때 휴지통의 자식 FK,
    /// DEFERRABLE FK 를 카탈로그로 못 읽어 즉시 검사로 돌아옴, 서버 이름 제약(<c>&lt;소유자&gt;_CON&lt;숫자&gt;</c>)을 Core 가 명시 이름으로 다시 만듦.
    /// </summary>
    public string? RecreateBlockedReason(ISession target) => RecreateBlocked;

    internal const string RecreateBlocked =
        "Tibero 의 테이블 다시 만들기는 아직 지원하지 않습니다(트리거 컴파일 오류·지연 FK·큰 바이트 길이 열을 다시 만드는 경로가 서버에서 확인되지 않음)";

    /// <summary>
    /// 「없으면 만들기」는 막지 않는다(null, xdb-copy XC8b 2026-11-10, D11 변경). Tibero→Tibero 와 Oracle→Tibero 모두 대상이 Tibero 면 새 표를 만든다.
    /// 근거는 XC5 DDL 실측(D19): CHAR/BYTE 길이·DEFAULT·CHECK·일반/UNIQUE 인덱스·COMMENT 가 모두 만들어지고, <c>DROP TABLE … PURGE</c> 는 휴지통에 남기지 않으며,
    /// CREATE TABLE 은 앞 트랜잭션을 묵시 커밋하므로 대상 쓰기 트랜잭션을 열기 전에 만든다(<c>CopyRunner.CreateMissingTablesAsync</c>).
    /// 「테이블 다시 만들기」는 그대로 막는다(<see cref="RecreateBlockedReason"/>): 지우고 되살리는 경로는 아직 서버에서 확인되지 않았다.
    /// </summary>
    public string? CreateBlockedReason(ISession target) => null;

    /// <summary>
    /// 문자열 길이 한도가 같은 Tibero 끼리뿐 아니라 Oracle 원본도 막을 것이 없다: 교차 복사는 이 함수에 원본을 Tibero 로 옮긴 구조(<c>CrossTableMap</c>)를 넘기고,
    /// 대상 Tibero VARCHAR2 한도(65532바이트) 안이다. Oracle 의 MAX_STRING_SIZE 같은 문자열 한도 파라미터가 Tibero 에는 확인되지 않았다.
    /// 다시 만들기 DDL 은 길이를 CHAR 단위로 쓴다: <c>VARCHAR2(4000 BYTE)</c> 를 CHAR 길이로 다시 만들어 4000바이트 값을 다시 넣는 것은 <c>TiberoRecreateStepIt</c> 가 단언하고,
    /// 최대 바이트 길이(65532 BYTE) 열은 같은 IT 가 결과만 남긴다(다시 만들기는 지금 막혀 있다 — 풀 때 그 결과로 여기서 막을지 정한다).
    /// </summary>
    public Task<IReadOnlyList<string>> ExtendedStringBlockersAsync(TableInfo source, Func<string, CancellationToken, Task<string?>> targetParameter,
                                                                   CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(targetParameter);
        return Task.FromResult<IReadOnlyList<string>>([]);
    }

    public IReadOnlyList<(string CatalogId, string Label)> NotRestoredProbes => Probes;

    public string RecycleBinNote(string? tablespace)
        => string.Equals(tablespace, SystemTablespace, StringComparison.OrdinalIgnoreCase)
            ? " 이 표는 SYSTEM 테이블스페이스에 있어 휴지통에 남지 않고 영구 삭제됩니다."
            : " 휴지통(USE_RECYCLEBIN)이 꺼져 있으면 지운 표는 영구 삭제되고, 켜져 있어도 휴지통 이름으로는 읽을 수 없어 FLASHBACK TABLE 로만 되살릴 수 있습니다.";

    /// <summary>Tibero 의 초기화 파라미터 뷰는 <c>V$PARAMETERS</c>(<c>copy.parameter_value</c>, 이름은 대소문자 무관으로 비교).</summary>
    public string ReadConsistencyParameter => "undo_retention";

    public (CheckLevel Level, string Message) ReadConsistencyHint(long estimatedSeconds, long? retention)
    {
        if (retention is not { } r)
        {
            return (CheckLevel.Pass, "원본 UNDO_RETENTION 을 읽을 수 없어 같은 시점 읽기가 끝까지 유지되는지는 알 수 없습니다.");
        }

        return estimatedSeconds > r
            ? (CheckLevel.Confirm,
                $"예상 읽기 시간 약 {Number(estimatedSeconds)}초가 원본 UNDO_RETENTION {Number(r)}초보다 깁니다. 읽는 도중 같은 시점의 데이터를 잃어 멈출 수 있으니 기간을 줄이거나 나눠 실행하세요.")
            : (CheckLevel.Pass, $"예상 읽기 시간 약 {Number(estimatedSeconds)}초, 원본 UNDO_RETENTION {Number(r)}초.");
    }

    /// <summary>TBR-10009: 자식 행이 남아 있어 부모 행을 지울 수 없음(<c>TiberoErrorClassifier</c> 의 FK_EXIST).</summary>
    public string ChildRecordFoundError => "TBR-10009";

    /// <summary>
    /// 다시 만들 때 원본에서 빠지거나 바뀌는 것(Oracle 글의 DEFAULT ON NULL·IDENTITY·COLLATE·도메인은 Tibero 에서 확인하지 않아 말하지 않는다).
    /// 실측: <c>ALL_CONSTRAINTS</c> 에 <c>DEFERRABLE</c>·<c>DEFERRED</c> 열이 없어(handover) 지연 FK 도 읽지 못한 채 즉시 검사로 돌아온다 — Oracle 글의 「DEFERRABLE PK/UK」 에 FK 가 더해진다.
    /// 문자 길이는 CHAR 단위(<c>TiberoRecreateStepIt</c> 가 BYTE → CHAR 를 확인). 파티션·IOT·임시 표는 Core DDL 이 일반 표로 만든다.
    /// </summary>
    public string RecreateDifferenceNote(bool hasDomain) =>
        " 원본과 다르게 만들어지는 것: 문자 길이는 글자 단위(CHAR), DEFERRABLE 제약(PK/UK 와, Tibero 카탈로그가 지연 여부를 알려 주지 않아 FK 도)은 즉시 검사로 바뀌고,"
        + " 파티션·IOT·임시 표는 일반 표가 됩니다.";

    /// <summary>서버가 지은 이름 = <c>&lt;소유자&gt;_CON&lt;숫자&gt;</c>(#76 실측)·<c>_SYS_…</c>·Oracle 모양. 실행기의 <see cref="TiberoCopyDialect.IsServerName"/> 과 같은 판정.</summary>
    public bool IsServerName(string name, string owner) => TiberoRecreateStep.IsServerName(name, owner);

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
