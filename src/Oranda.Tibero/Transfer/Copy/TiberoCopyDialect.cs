using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Journal;
using Oranda.Core.Text;
using Oranda.Core.Transfer.Copy;
using Oranda.Tibero.Catalog;
using Oranda.Tibero.Execution;

namespace Oranda.Tibero.Transfer.Copy;

/// <summary>
/// Tibero 복사 실행 방언(<see cref="ICopyDialect"/>, T5 TB3b). 문장만 만들고 실행하지 않는다. 실측 = docs/design/tibero-driver-transfer.md 3장(TM0b).
/// 글은 Oracle <c>CopyStatements</c> 와 같고(식별자는 모두 따옴표) 달라진 것은 다음뿐이다.
/// <list type="bullet">
/// <item>읽기 전용 트랜잭션: 최상위 <c>SET TRANSACTION READ ONLY</c> 는 TBR-7001 → PSM 블록(<see cref="TiberoExecutionService.ReadOnlyBlock"/>, 3.2).
/// Tibero 의 READ ONLY 는 쓰기를 막지 않아 같은 시점 읽기(일관성)만 준다 — 쓰기 차단은 정책 관문 몫.</item>
/// <item>휴지통 켜기: <c>ALTER SESSION SET RECYCLEBIN = ON</c> 은 TBR-7001 → <c>USE_RECYCLEBIN = Y</c>(3.7).</item>
/// <item>오류 판정: FK VALIDATE 실패 = TBR-7161(Oracle ORA-02298, 3.5), 참조되는 부모 DROP 거부 = TBR-7156(Oracle ORA-02449, 3.5). 드라이버는 번호를 음수로 주므로 부호를 무시한다.</item>
/// <item>IDENTITY 없음(3.6): <see cref="RestartIdentity"/> 는 부를 일이 없다(Tibero 메타데이터는 IDENTITY 열을 내지 않는다).</item>
/// </list>
/// 하위 형식이 생성자에서 넘긴다(키 서비스 아님). 실행기 하위 형식 = <c>TiberoCopyRunner</c>(TI1b), 키 등록은 두 번째 계정 복사 IT 뒤(TI1b 결정65).
/// </summary>
internal sealed class TiberoCopyDialect : ICopyDialect
{
    public static readonly TiberoCopyDialect Instance = new();

    /// <summary>TBR-7161: 부모 키가 없는 자식 행이 있어 FK 를 VALIDATE 로 켜지 못함(Oracle ORA-02298).</summary>
    internal const int ParentKeysNotFound = 7161;

    /// <summary>TBR-7156: 다른 표의 FK 가 참조하는 PK·UK 가 있어 DROP·TRUNCATE 를 거부(Oracle ORA-02449).</summary>
    internal const int ReferencedByForeignKeys = 7156;

    internal const string IdentityNotSupported = "Tibero 에는 IDENTITY 열이 없어 다음 값을 옮길 일이 없다";

    public SqlStatement DisableTrigger(string owner, string trigger) => Ddl($"ALTER TRIGGER {Q(owner, trigger)} DISABLE");

    public SqlStatement EnableTrigger(string owner, string trigger) => Ddl($"ALTER TRIGGER {Q(owner, trigger)} ENABLE");

    public SqlStatement DisableConstraint(DisabledObject fk)
    {
        ArgumentNullException.ThrowIfNull(fk);
        return Ddl($"ALTER TABLE {Q(fk.Owner, fk.Table)} DISABLE CONSTRAINT {SqlIdentifier.QuoteAlways(fk.Name)}");
    }

    public SqlStatement DropConstraint(DisabledObject fk)
    {
        ArgumentNullException.ThrowIfNull(fk);
        return Ddl($"ALTER TABLE {Q(fk.Owner, fk.Table)} DROP CONSTRAINT {SqlIdentifier.QuoteAlways(fk.Name)}");
    }

    /// <summary><paramref name="validate"/> 가 거짓이면 <c>ENABLE NOVALIDATE</c>(부모 없는 자식 행이 있어도 켠다, 3.5 실측).</summary>
    public SqlStatement EnableConstraint(DisabledObject fk, bool validate)
    {
        ArgumentNullException.ThrowIfNull(fk);
        return Ddl($"ALTER TABLE {Q(fk.Owner, fk.Table)} ENABLE {(validate ? "VALIDATE" : "NOVALIDATE")} CONSTRAINT {SqlIdentifier.QuoteAlways(fk.Name)}");
    }

    /// <summary>
    /// 「테이블 다시 만들기」의 DROP. Oracle 과 같이 <c>PURGE</c>·<c>CASCADE CONSTRAINTS</c> 를 붙이지 않는다: 보이지 않는 FK 가 남아 있으면 서버가 TBR-7156 으로 거부한다.
    /// 위험 명령(대상 이름 재입력 확인)이고 변경 일지가 지우기 전 DDL 을 보관한다.
    /// </summary>
    public SqlStatement DropTable(string owner, string table) =>
        new($"DROP TABLE {Q(owner, table)}", default, StatementKind.Ddl)
        {
            Verb = "DROP",
            DangerousTarget = table,
            ChangeTarget = new ChangeTarget(owner, table, "TABLE", ChangeOperation.Drop),
        };

    /// <summary>시퀀스 값 묶음 소비. <c>CONNECT BY LEVEL &lt;= :N</c> 은 Tibero 도 된다(3.6, 읽은 행보다 더 소비될 수 있음 — Oracle 같음).</summary>
    public SqlStatement NextValues(string owner, string sequence) =>
        CopyCommonStatements.Query($"SELECT {Q(owner, sequence)}.NEXTVAL FROM DUAL CONNECT BY LEVEL <= :{CopyCommonStatements.NextValuesBind}");

    /// <summary>
    /// PSM 블록으로 연다(최상위 문장은 TBR-7001). 실행기가 앞에서 <c>ROLLBACK</c> 을 보내지만 블록도 먼저 되돌려
    /// 「트랜잭션의 첫 문장」 조건을 스스로 맞춘다 — 실행 서비스의 읽기 전용 블록과 같은 글.
    /// </summary>
    public SqlStatement SetTransactionReadOnly() =>
        new(TiberoExecutionService.ReadOnlyBlock, default, StatementKind.TransactionControl) { Verb = "SET TRANSACTION" };

    /// <summary>
    /// 같은 시점 읽기 + 동시 복사(PF2b2, 조정자 회차 xdb-copy 결정 100): <c>AS OF SCN :ORANDA_SCN</c>(Tibero 는 TSN 을 <c>AS OF SCN</c> 으로도 받는다, 6.2.15 #187 실측).
    /// 지금 TSN 을 읽는 순서와 이유:
    /// <list type="number">
    /// <item><c>DBMS_FLASHBACK.GET_SYSTEM_CHANGE_NUMBER</c>: 정확한 지금 TSN(Oracle 과 같은 첫 방법). 패키지 실행 권한이 없으면 서버 오류 → 다음.</item>
    /// <item><c>TIMESTAMP_TO_TSN(SYSTIMESTAMP)</c>: 권한이 필요 없는 SQL 함수. 시각→TSN 대응 간격만큼 앞 TSN 일 수 있다(실측에서는 위와 같은 값). 앞 시점이어도 모든 테이블이 같은 TSN 으로 읽으므로 테이블 사이는 맞는다.</item>
    /// <item><c>V$DATABASE.CURRENT_TSN</c>: 정확하지만 V$ 조회 권한이 필요해 일반 계정은 실패한다(실패마다 작업 로그에 경고가 남으므로 마지막). 위 둘이 다 없는 판에서만 쓰인다.</item>
    /// </list>
    /// 오류: 권한 없음은 분류기의 TBR-8053(<see cref="DbErrorKind.InsufficientPrivileges"/>)이 안내로 간다. 이전 시점 데이터 잃음·정의 바뀜·잘못된 TSN 의 번호는 실측하지 못해
    /// 번호를 넣지 않는다 — 메시지에 「snapshot too old」 가 있으면 실행 전체를 멈추고, 그 밖의 모르는 서버 오류는 그 테이블만 실패로 둔다(<see cref="CopyReadPointSupport.UnmappedErrorsFailTable"/>).
    /// 어느 쪽이든 지금 데이터로 바꿔 읽지 않는다. 다른 계정의 테이블에 FLASHBACK 권한이 필요한지는 실측하지 못해 사전 점검은 확인 필요로 둔다(<see cref="CopyReadPointSupport.PrivilegeUnverified"/>).
    /// </summary>
    public CopyReadPointSupport ReadPoint => Point;

    /// <summary><see cref="ReadPoint"/> 의 값(한 번만 만든다).</summary>
    internal static readonly CopyReadPointSupport Point = new(
        "TSN",
        "AS OF SCN :{0}",
        [
            new CopyReadPointQuery("SELECT DBMS_FLASHBACK.GET_SYSTEM_CHANGE_NUMBER FROM DUAL", "DBMS_FLASHBACK.GET_SYSTEM_CHANGE_NUMBER"),
            new CopyReadPointQuery("SELECT TIMESTAMP_TO_TSN(SYSTIMESTAMP) FROM DUAL", "TIMESTAMP_TO_TSN(SYSTIMESTAMP)"),
            new CopyReadPointQuery("SELECT CURRENT_TSN FROM V$DATABASE", "V$DATABASE.CURRENT_TSN"),
        ],
        "FLASHBACK",
        "FLASHBACK ANY TABLE")
    {
        SnapshotTooOldErrors = null, // 번호 미실측(SnapshotTooOldErrorName 과 같다)
        SnapshotTooOldMessages = ["snapshot too old"],
        UnmappedErrorsFailTable = true,
        PrivilegeUnverified = true,
    };

    public string BindPlaceholder(string bindName, string serverType) => TiberoBulkValueCodec.BindExpression(bindName, serverType);

    /// <summary>
    /// 교차 쓰기 값 자리: TIMESTAMP WITH TIME ZONE 은 <c>TO_TIMESTAMP_TZ</c> 로 감싸고 BINARY_* 는 감싸지 않는다(대량 실행이 기대하는 글과 같다,
    /// <see cref="TiberoBulkValueCodec.CrossBindExpression"/>). 같은 DBMS 의 <see cref="BindPlaceholder"/> 는 이 형식을 받지 않는다(xdb-copy XC8c, win144 #137).
    /// </summary>
    public string CrossBindPlaceholder(string bindName, string bindType) => TiberoBulkValueCodec.CrossBindExpression(bindName, bindType);

    public SqlStatement Savepoint(string name) =>
        new("SAVEPOINT " + name, default, StatementKind.TransactionControl) { Verb = "SAVEPOINT" };

    public SqlStatement RollbackToSavepoint(string name) =>
        new("ROLLBACK TO SAVEPOINT " + name, default, StatementKind.TransactionControl) { Verb = "ROLLBACK" };

    /// <summary>이 세션의 휴지통을 켠다(서버 기본은 꺼짐). <c>RECYCLEBIN = ON</c> 은 TBR-7001, <c>USE_RECYCLEBIN = Y</c> 가 된다(3.7). 커밋하지 않는다.</summary>
    public SqlStatement RecycleBinOn() =>
        new("ALTER SESSION SET USE_RECYCLEBIN = Y", default, StatementKind.Other) { Verb = "ALTER SESSION" };

    /// <summary>
    /// 부를 일이 없다(Tibero 메타데이터는 IDENTITY 열을 내지 않는다). 그래도 불리면 예외 대신 안내 글을 돌려준다:
    /// 실행기는 이 호출을 되살리기 시도 밖에서 하므로 예외면 다른 표의 트리거·권한·FK 되살리기까지 멈춘다. 이 글은 실행해도 할 일이 없다는 주석이다.
    /// </summary>
    public string RestartIdentity(string owner, string table, string column) => "-- " + IdentityNotSupported;

    /// <summary>FK 를 붙이는 DDL 끝에 <c>ENABLE NOVALIDATE</c>(TBR-7161 뒤에만). ADD 와 함께 쓰는 꼴은 두 번째 계정 복사 IT(TI1b 결정65 - 지금 TI1b IT 는 같은 계정 거부만 본다)가 확인한다.</summary>
    public string AddForeignKeyWithoutValidation(string addForeignKeyDdl)
    {
        ArgumentNullException.ThrowIfNull(addForeignKeyDdl);
        return addForeignKeyDdl + " ENABLE NOVALIDATE";
    }

    // ---- 안내 글의 오류 이름(ICopyDialect 기본 = Oracle 번호). 실측한 것만 쓰고 나머지는 번호 없는 일반 문구로 둔다. ----

    /// <summary>TBR-7156(3.5 실측). 안내에 붙는 이름.</summary>
    public string? ReferencedByForeignKeysErrorName => "TBR-" + ReferencedByForeignKeys.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// 미실측 → 번호 없음. 없는 열을 가리키는 트리거 CREATE 는 오류 없이 받아 INVALID+ENABLED 로 남는다(TB4 #79) — 컴파일 오류 번호가 나오지 않으니 문구에 붙일 번호가 없다.
    /// </summary>
    public string? TriggerCompileErrorName => null;

    private static readonly Lazy<string> TriggerStatus = new(() => TiberoEmbeddedCatalog.Load().Get("copy.trigger_status", 6, Dbms.Tibero).Sql);

    /// <summary>
    /// TB4 #79 실측: 없는 열을 가리키는 트리거 CREATE 를 오류 없이 받아 <c>user_objects</c> INVALID + <c>user_triggers</c> ENABLED 로 남긴다.
    /// 그래서 실행기가 되살린 트리거마다 <c>copy.trigger_status</c> 카탈로그 글(Tibero 에만 있는 id)로 상태를 보고 INVALID 이면 끈다
    /// (안내 글의 번호는 위 <see cref="TriggerCompileErrorName"/> = 없음).
    /// </summary>
    public string? TriggerStatusSql => TriggerStatus.Value;

    /// <summary>서버가 지은 이름 = <c>&lt;소유자&gt;_CON&lt;숫자&gt;</c>(#76 실측)·<c>_SYS_…</c>·Oracle 모양. <see cref="TiberoRecreateStep.IsServerName"/> 과 같은 판정.</summary>
    public bool IsServerName(string name, string owner) => TiberoRecreateStep.IsServerName(name, owner);

    /// <summary>
    /// 먼저 지운 자식 표가 휴지통에 남아 그 FK 로 부모 DROP 이 TBR-7156 으로 거부될 수 있다(3.7: 휴지통 표 이름 <c>&lt;계정&gt;_TBL…</c>, 같은 DROP 거부는 3.5 실측).
    /// 휴지통에 FK 가 남는지 자체는 이 조합으로 재지 못했다(미실측) → 「있을 수 있다」 로 적는다. TR1 부터는 <see cref="DetachChildForeignKeysBeforeDrop"/> 로 자식 FK 를
    /// DROP 전에 떼므로 이 글은 그래도 거부됐을 때(다른 길로 남은 FK)만 나온다.
    /// </summary>
    public string? RecycledChildForeignKeyNote(IReadOnlyList<string> children) =>
        $"이 실행에서 먼저 지운 자식 테이블 {string.Join(", ", children)} 이(가) 휴지통에 남아 그 FK 가 이 테이블을 참조하고 있을 수 있습니다(휴지통의 그 테이블을 PURGE 해야 할 수 있습니다).";

    /// <summary>
    /// TR1 위험 3: 함께 다시 만드는 자식 표의 FK 도 첫 DROP 전에 뗀다. 지운 자식 표가 휴지통에 FK 를 쥔 채 남아 부모 DROP 을 막는 일(TBR-7156)을 휴지통 동작과 상관없이 없앤다.
    /// 자식 FK 는 적재 뒤 원본 정의(작업 안 FK)로 부모 다음에 붙는다. 서버 동작은 <c>TiberoRecreateStepIt</c> 가 출력한다.
    /// </summary>
    public bool DetachChildForeignKeysBeforeDrop => true;

    /// <summary>
    /// 미실측 → 번호 없음(Tibero 의 이전 시점 읽기 실패 번호를 재지 못했다, <c>TiberoErrorClassifier</c> 도 SnapshotTooOld 를 분류하지 않는다).
    /// 같은 시점 읽기 + 동시 복사(<see cref="ReadPoint"/>)는 메시지 글(「snapshot too old」)로 알아본다.
    /// </summary>
    public string? SnapshotTooOldErrorName => null;

    /// <summary>
    /// 휴지통 이름은 <c>&lt;계정&gt;_…</c> 꼴(BIN$ 아님, 3.7 실측)이고, 그 이름으로 <c>FLASHBACK … RENAME TO</c> 가 된다. 트리거·인덱스·권한·참조 FK 가 함께 돌아오는지는 미실측이다.
    /// </summary>
    public string FlashbackRestoreNote =>
        "(트리거·인덱스·권한과 이 테이블을 참조하던 FK 가 함께 돌아오는지는 Tibero 에서 확인되지 않았습니다. 돌아왔는지 확인하고 없는 것은 직접 만드세요)";

    public bool IsParentKeysNotFound(DbError? dbError) => Is(dbError, ParentKeysNotFound);

    public bool IsReferencedByForeignKeys(DbError? dbError) => Is(dbError, ReferencedByForeignKeys);

    private static bool Is(DbError? dbError, int number) =>
        dbError is { Number: var n } && n != int.MinValue && Math.Abs(n) == number;

    private static string Q(string owner, string name) => CopyCommonStatements.Qualified(owner, name);

    private static SqlStatement Ddl(string text) => new(text, default, StatementKind.Ddl) { Verb = "ALTER" };
}
