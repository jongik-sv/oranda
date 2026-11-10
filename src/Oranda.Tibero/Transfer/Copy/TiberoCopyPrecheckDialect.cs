using System.Globalization;
using System.Text;
using Oranda.Core.Connection;
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
    /// 「테이블 다시 만들기」를 막지 않는다(null, TR1 — 사용자 요청 2026-11-11, 조정 결정 103, D-064 가 TB4 의 일괄 차단을 대신한다).
    /// TB4 리뷰의 남은 위험 다섯 가지는 표마다 이렇게 다룬다(서버로 끝까지 도는 확인은 <c>TiberoRecreateStepIt</c>·<c>CrossDbmsRecreateIntoTiberoIt</c>).
    /// <list type="number">
    /// <item>컴파일 오류 트리거: Tibero 는 오류 없이 INVALID 로 받는다(#79). 그대로 다시 만들고 되살린 뒤 INVALID 면 꺼 둔다(<see cref="TiberoCopyDialect.TriggerStatusSql"/>).
    /// 지우기 전부터 INVALID 였던 것(<c>copy.invalid_triggers</c>)은 알림만 하고 결과를 실패로 두지 않으며, 확인 글에 트리거 이름을 적는다(<see cref="RecreateTableNoteAsync"/>).</item>
    /// <item>큰 바이트 길이 열: 글자 단위로 다시 쓰면 한도(VARCHAR2 65532·CHAR 2000 바이트)를 넘을 수 있는 바이트 단위 열은 원본처럼 BYTE 로 만든다
    /// (<see cref="CrossTableMap.TiberoByteKeptColumns"/>). 글자 단위 큰 열은 CHAR 그대로 두고 확인 글에 알린다(서버가 받지 않으면 옛 표를 휴지통에서 되살린다).</item>
    /// <item>부모·자식을 함께 다시 만들 때 휴지통의 자식 FK: 자식 FK 를 첫 DROP 전에 떼고(<see cref="TiberoCopyDialect.DetachChildForeignKeysBeforeDrop"/>) 적재 뒤 원본 정의로 부모 다음에 붙인다.</item>
    /// <item>DEFERRABLE FK: 카탈로그에 지연 여부가 없다(-8026). 즉시 검사로 다시 만들어지는 FK 를 이름으로 확인 글에 적는다.</item>
    /// <item>서버 이름 제약(<c>&lt;소유자&gt;_CON&lt;숫자&gt;</c>): <see cref="IsServerName"/> 판정으로 이름 없이 만들어 대상 서버가 새로 짓는다.</item>
    /// </list>
    /// 어느 것도 데이터를 잃게 하지 않으므로 표마다 막지 않는다. CREATE 가 DROP 뒤 실패해도 실행기가 옛 표를 휴지통에서 되살린다(결정 66·67).
    /// </summary>
    public string? RecreateBlockedReason(ISession target) => null;

    /// <summary>
    /// 「없으면 만들기」는 막지 않는다(null, xdb-copy XC8b 2026-11-10, D11 변경). Tibero→Tibero 와 Oracle→Tibero 모두 대상이 Tibero 면 새 표를 만든다.
    /// 근거는 XC5 DDL 실측(D19): CHAR/BYTE 길이·DEFAULT·CHECK·일반/UNIQUE 인덱스·COMMENT 가 모두 만들어지고, <c>DROP TABLE … PURGE</c> 는 휴지통에 남기지 않으며,
    /// CREATE TABLE 은 앞 트랜잭션을 묵시 커밋하므로 대상 쓰기 트랜잭션을 열기 전에 만든다(<c>CopyRunner.CreateMissingTablesAsync</c>).
    /// </summary>
    public string? CreateBlockedReason(ISession target) => null;

    /// <summary>
    /// 다시 만들 표 하나의 Tibero 안내(TR1 위험 1·4): 지금 INVALID 인 대상 트리거(<c>copy.invalid_triggers</c>)와, 지연 여부를 알 수 없어 즉시 검사로 다시 만들어지는 FK
    /// (이 표를 참조하는 다른 표의 FK = <c>copy.recreate_referencing_fks</c>, Tibero 원본이면 작업 안 FK 도). 조회가 서버 오류면 「확인하지 못함」 으로 적는다.
    /// </summary>
    public async Task<string> RecreateTableNoteAsync(RecreateNoteInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var note = new StringBuilder();
        var invalid = await input.TargetRows("copy.invalid_triggers", ct).ConfigureAwait(false);
        if (invalid is null)
        {
            note.Append(" 대상 트리거의 컴파일 상태(INVALID)를 확인하지 못했습니다. 되살린 뒤 INVALID 인 트리거는 꺼 두고 알립니다.");
        }
        else if (invalid.Count > 0)
        {
            var names = invalid.Select(r => $"{r[0]}.{r[1]}({(string.Equals(r[2], "ENABLED", StringComparison.Ordinal) ? "켜짐" : "꺼짐")})");
            note.Append(" 대상 트리거 ").Append(string.Join(", ", names))
                .Append(" 은(는) 지금 컴파일 오류(INVALID) 상태입니다. 그대로 다시 만들고, 켜져 있던 트리거가 다시 만든 뒤에도 INVALID 이면 꺼 두고 알립니다")
                .Append("(켜 두면 이 테이블의 DML 이 막힙니다). 테이블 복사 결과에는 영향이 없습니다.");
        }

        var referencing = await input.TargetRows("copy.recreate_referencing_fks", ct).ConfigureAwait(false);
        var unknown = new List<string>();
        if (referencing is null)
        {
            note.Append(" 이 테이블을 참조하는 FK 를 읽지 못해 지연 검사(DEFERRABLE) 여부를 알릴 FK 를 확인하지 못했습니다.");
        }
        else
        {
            unknown.AddRange(referencing.Select(r => $"{r[0]}.{r[1]}.{r[2]}").Distinct(StringComparer.Ordinal));
        }

        if (input.SourceDbms == Dbms.Tibero)
        {
            unknown.AddRange(input.JobForeignKeys.Select(n => $"{input.TargetOwner}.{input.Table}.{n}"));
        }

        if (unknown.Count > 0)
        {
            note.Append(" FK ").Append(string.Join(", ", unknown))
                .Append(" 은(는) Tibero 카탈로그가 지연 검사(DEFERRABLE) 여부를 알려 주지 않아 즉시 검사 FK 로 다시 만듭니다. 지연 FK 였다면 다시 만든 뒤 직접 바꾸세요.");
        }

        return note.ToString();
    }

    /// <summary>
    /// 문자열 길이 한도가 같은 Tibero 끼리뿐 아니라 Oracle 원본도 막을 것이 없다: 교차 복사는 이 함수에 원본을 Tibero 로 옮긴 구조(<c>CrossTableMap</c>)를 넘기고,
    /// 대상 Tibero VARCHAR2 한도(65532바이트) 안이다. Oracle 의 MAX_STRING_SIZE 같은 문자열 한도 파라미터가 Tibero 에는 확인되지 않았다.
    /// 다시 만들기 DDL 은 길이를 CHAR 단위로 쓴다: <c>VARCHAR2(4000 BYTE)</c> 를 CHAR 길이로 다시 만들어 4000바이트 값을 다시 넣는 것은 <c>TiberoRecreateStepIt</c> 가 단언한다.
    /// 글자 단위로 쓰면 한도를 넘을 수 있는 큰 바이트 단위 열(예: 65532 BYTE)은 막지 않고 BYTE 그대로 만든다(TR1 위험 2, <see cref="CrossTableMap.TiberoByteKeptColumns"/>).
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
            ? " 이 테이블은 SYSTEM 테이블스페이스에 있어 휴지통에 남지 않고 영구 삭제됩니다."
            : " 휴지통(USE_RECYCLEBIN)이 꺼져 있으면 지운 테이블은 영구 삭제되고, 켜져 있어도 휴지통 이름으로는 읽을 수 없어 FLASHBACK TABLE 로만 되살릴 수 있습니다.";

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
    /// 문자 길이는 CHAR 단위(<c>TiberoRecreateStepIt</c> 가 BYTE → CHAR 를 확인)이고, 글자 단위로 쓰면 바이트 한도를 넘을 수 있는 큰 바이트 열만 BYTE 그대로다(TR1 위험 2, 열 이름은 Core 가 따로 적는다).
    /// 즉시 검사로 바뀌는 FK 의 이름은 <see cref="RecreateTableNoteAsync"/> 가 적는다. 파티션·IOT·임시 표는 Core DDL 이 일반 표로 만든다.
    /// </summary>
    public string RecreateDifferenceNote(bool hasDomain) =>
        " 원본과 다르게 만들어지는 것: 문자 길이는 글자 단위(CHAR, 글자 단위로 쓰면 바이트 한도를 넘을 수 있는 큰 바이트 열은 바이트 그대로),"
        + " DEFERRABLE 제약(PK/UK 와, Tibero 카탈로그가 지연 여부를 알려 주지 않아 FK 도)은 즉시 검사로 바뀌고,"
        + " 파티션·IOT·임시 테이블은 일반 테이블이 됩니다.";

    /// <summary>서버가 지은 이름 = <c>&lt;소유자&gt;_CON&lt;숫자&gt;</c>(#76 실측)·<c>_SYS_…</c>·Oracle 모양. 실행기의 <see cref="TiberoCopyDialect.IsServerName"/> 과 같은 판정.</summary>
    public bool IsServerName(string name, string owner) => TiberoRecreateStep.IsServerName(name, owner);

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
