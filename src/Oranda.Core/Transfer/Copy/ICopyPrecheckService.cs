using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.Core.Transfer.Copy;

/// <summary>사전 점검. 구현: Oranda.Oracle(<c>Transfer/Copy</c>).</summary>
public interface ICopyPrecheckService
{
    Task<PrecheckReport> RunAsync(CopySessions sessions, CopyJob job, ResolvedPeriod period,
                                  IProgress<string>? progress, CancellationToken ct);
}

public sealed record CopySessions(ISession Source, ISession Target)
{
    /// <summary>
    /// 대기 원인 감시(WH1, <see cref="CopyWaitProbe"/>)용 짧은 세션을 받은 세션과 같은 접속으로 연다(실행 때 받은 비밀번호를 다시 쓴다, 새로 묻지 않음 — 못 열면 null).
    /// null = 감시하지 않음(대기 시간만 보인다). 실행기는 대상 쪽 대기만 감시하고 실행당 감시 세션은 하나다.
    /// </summary>
    public Func<ISession, CancellationToken, Task<ISession?>>? OpenMonitor { get; init; }

    /// <summary>외부 적재 도구 준비물(조정 결정 88). null = 쓸 수 없음 — 적재 방식이 외부 도구인 테이블도 기본 방식으로 넣는다(사전 점검 알림).</summary>
    public ExternalLoad.CopyExternalLoad? ExternalLoad { get; init; }

    /// <summary>
    /// 동시 테이블 복사(PF2)의 추가 레인 세션 쌍(원본 읽기 전용·대상, 전용 세션). 주 쌍(<see cref="Source"/>·<see cref="Target"/>)이 첫 레인이고 이것들이 둘째 레인부터다.
    /// 비면(기본) 한 테이블씩 복사한다. 여닫기는 부른 쪽(앱)이 한다: 실행기는 열지도 닫지도 않는다. 사전 점검은 쓰지 않는다.
    /// </summary>
    public IReadOnlyList<CopySessions> Workers { get; init; } = [];
}

public enum CheckLevel { Pass, Confirm, Error }

/// <summary>
/// 사전 점검 항목 코드(<see cref="CheckItem.Code"/>) 가운데 묶음 B 에서 새로 생긴 것. 화면의 코드 표시 이름 표가 같은 글을 쓴다.
/// 나머지 코드(<c>SourceNoSelect</c>·<c>TargetNoWrite</c>·<c>TargetMissing</c>·<c>SameSourceTarget</c>·<c>RuleInvalid</c>·<c>RecreateNoPrivilege</c> …)는 글 그대로다.
/// </summary>
public static class CopyCheckCodes
{
    /// <summary>확인 필요: 대상 접속 계정이 아닌 계정의 표를 지우고 넣는다(<see cref="CheckItem.Table"/> 은 null, 작업 전체 한 항목).</summary>
    public const string OtherOwnerTargets = "OtherOwnerTargets";

    /// <summary>확인 필요: 표를 더할 때 적은 시노님(<see cref="TableRule.WrittenAs"/>)이 지금은 다른 표를 가리키거나 풀리지 않는다. 작업은 저장된 표를 그대로 복사한다.</summary>
    public const string SynonymChanged = "SynonymChanged";

    /// <summary>오류: 기간 규칙이 있는데 기간 범위가 비었다(끝 경계가 시작보다 늦지 않음). 그대로 두면 0행을 복사하고 성공으로 끝난다.</summary>
    public const string EmptyPeriod = "EmptyPeriod";

    /// <summary>확인 필요: 대상에 표가 없어 원본 구조로 만든 뒤 넣는다(<see cref="TableRule.CreateIfMissing"/>). 메시지에 만드는 것·안 만드는 것·실패 때의 정리를 적는다.</summary>
    public const string CreateTable = "CreateTable";

    /// <summary>오류: 「없으면 만들기」를 쓸 수 없다 — 대상 소유자 계정이 아니거나 CREATE TABLE 권한이 없다.</summary>
    public const string CreateNoPrivilege = "CreateNoPrivilege";

    /// <summary>오류: 원본 구조로 만들 수 없는 이유가 있다(지원하지 않는 형식 열·문자열 한도·가리키는 객체 없음·이름 겹침 …, <c>RecreateBlocked</c> 와 같은 조건).</summary>
    public const string CreateBlocked = "CreateBlocked";

    /// <summary>확인 필요(교차 복사만): 대상 DBMS 로 옮길 형식이 없는 열을 복사에서 뺀다(표·열·형식·이유). 같은 DBMS 의 <c>UnsupportedType</c> 오류와 다르다.</summary>
    public const string ColumnDropped = "ColumnDropped";

    /// <summary>오류(교차 복사만): 키(PK)·기간 조건·부모 키 연결 열을 대상 DBMS 로 옮길 형식이 없어 복사에서 빠진다 — 키가 없어지거나 조건 열이 NULL 로 들어가 다시 실행하면 행이 겹친다. 열마다 한 항목.</summary>
    public const string KeyColumnDropped = "KeyColumnDropped";

    /// <summary>교차 복사만: 형식을 바꿔 옮기는 열(표마다 한 항목). 값이 바뀌거나 행이 실패할 수 있는 변환(BINARY_* → NUMBER·시간대 지역 → 오프셋)이 있으면 확인 필요, 아니면 통과.</summary>
    public const string TypeConverted = "TypeConverted";

    /// <summary>확인 필요(교차 복사로 Tibero 에 넣을 때, 작업 전체 한 항목): 대상 문자셋 밖 글자는 실패하거나 바뀔 수 있다.</summary>
    public const string CrossCharset = "CrossCharset";

    /// <summary>통과 안내(교차 복사로 Tibero 에 BLOB 을 넣을 때): 길이가 0 인 BLOB 은 NULL 이 된다.</summary>
    public const string BlobEmptyToNull = "BlobEmptyToNull";

    /// <summary>확인 필요(교차 복사로 Oracle 에 넣을 때, 표마다 한 항목): 원본 문자 단위(CHAR) 길이 열이 대상 Oracle 의 바이트 한도(VARCHAR2 4000)를 넘는 값을 담을 수 있다 — 넘는 값은 그 행이 실패한다.</summary>
    public const string CrossStringBytes = "CrossStringBytes";

    /// <summary>통과 안내(교차 복사로 Oracle CHAR 를 Tibero 에 넣을 때, 표마다 한 항목): Tibero 는 CHAR(n CHAR) 를 글자 수로 공백을 채워 뒤 공백 수가 달라질 수 있다.</summary>
    public const string CharPadDiffers = "CharPadDiffers";

    /// <summary>
    /// 확인 필요(표를 만들 때, 열마다 한 항목): 그 열의 DEFAULT 시퀀스.NEXTVAL 을 빼고 만든다 — 시퀀스가 다른 계정에 있는데 대상에 없음,
    /// 대상에 만들 수 없음(CREATE SEQUENCE 권한 없음·원본 속성을 못 읽음), 대상 서버가 시퀀스 기본값을 받지 않음(Oracle 11g, 시퀀스는 만든다).
    /// </summary>
    public const string SequenceDefaultDropped = "SequenceDefaultDropped";

    /// <summary>
    /// 동시 테이블 복사(PF2, 작업 전체 한 항목). 실제로 동시에 돌면 확인 필요(지우기를 먼저 확정해 중지해도 지운 행이 돌아오지 않는다),
    /// 동시 수를 2 이상 골랐지만 한 테이블씩 돌면 통과 안내(그 이유, <see cref="CopyConcurrency.Effective"/>).
    /// </summary>
    public const string ConcurrentCopy = "ConcurrentCopy";

    /// <summary>
    /// 같은 시점 읽기를 켠 채 동시 복사(PF2b, 작업 전체 한 항목): 실행을 시작할 때 원본 SCN 하나를 잡아 모든 테이블을 그 시점(<c>AS OF SCN</c>)으로 읽는다.
    /// 예상 읽기 시간이 원본 UNDO 보존 시간보다 길면 확인 필요, 아니면 통과. 실행기는 이 항목이 있어야 시점 읽기로 동시에 돈다(<see cref="CopyConcurrency.ForRun"/>).
    /// </summary>
    public const string ReadPoint = "ReadPoint";

    /// <summary>오류(PF2b, 원본 테이블마다): 같은 시점 읽기로 동시에 복사하는데 다른 계정의 원본 테이블(부모 키 규칙의 부모 포함)에 FLASHBACK 권한이 없다.</summary>
    public const string FlashbackNoPrivilege = "FlashbackNoPrivilege";

    /// <summary>
    /// 확인 필요(표를 만들 때, 표마다 한 항목): 원본 테이블에 연결된 시퀀스(열 DEFAULT·그 테이블 트리거가 쓰는 시퀀스)가 대상 스키마에 없어 테이블보다 먼저 원본 속성으로 만든다
    /// (START WITH = 원본 다음 값, 적재 뒤 키 최댓값 위로 맞춤). 만드는 문장은 미리 보기에 있다(조정 결정 76).
    /// </summary>
    public const string SequenceCreated = "SequenceCreated";

    /// <summary>확인 필요(표를 만들 때): 대상에 이미 있는 같은 이름 시퀀스는 건드리지 않는데, 그 다음 값이 원본 키 최댓값 이하라 새 행의 키가 겹칠 수 있다.</summary>
    public const string SequenceBehind = "SequenceBehind";

    /// <summary>
    /// 표를 만들 때(표마다 한 항목): 적재 뒤 함께 만드는 객체(트리거·권한·함수 기반 인덱스·작업 밖 부모 FK, <see cref="CopyTablePlan.Companions"/>)와 만들지 않는 것.
    /// 빼는 것이 있으면 확인 필요, 없으면 통과(조정 결정 78).
    /// </summary>
    public const string CompanionObjects = "CompanionObjects";

    /// <summary>
    /// 확인 필요(외부 도구로 넣는 테이블마다 한 항목, 조정 결정 88): 적재 도구(SQL*Loader·tbLoader)로 넣는다. 대상 정리(DELETE)는 도구가 잠금에 막히지 않게 적재 전에 확정하고,
    /// 이번에 만든 테이블은 direct path, 이미 있는 테이블은 일반 경로로 넣는다. 도구가 실패하면 기본 방식으로 다시 넣는다(추가만 하기는 도구가 넣은 행이 남아 다시 하지 않는다).
    /// 제어 파일은 미리 보기에 있다.
    /// </summary>
    public const string ExternalLoad = "ExternalLoad";

    /// <summary>통과(테이블마다 한 항목): 적재 방식을 외부 도구로 골랐지만 기본 방식으로 넣는다 — 그 이유(도구 없음·버전·키로 맞추기·커밋 방식·열 형식·인증 방식).</summary>
    public const string ExternalLoadFallback = "ExternalLoadFallback";
}

public enum CheckChoice { DisableTriggers, DisableForeignKeys, SkipColumn, ProceedSequenceGap, SwitchToMerge }

/// <summary>점검 항목 하나. <paramref name="Table"/> 은 규칙 키(<see cref="CopyOwners.Key(CopyJob, TableRule)"/>)이고, 작업 전체 항목이면 null.</summary>
public sealed record CheckItem(string? Table, string Code, CheckLevel Level, string Message)
{
    public IReadOnlyList<CheckChoice> Choices { get; init; } = [];
    public string? Column { get; init; }

    /// <summary>시퀀스 후보 항목일 때 후보 시퀀스 이름. 화면이 「연결」 체크로 작업에 저장한다.</summary>
    public string? Sequence { get; init; }
}

public sealed record CopyTablePlan(TableRule Rule, TableInfo Source, TableInfo Target,
                                   IReadOnlyList<string> Columns, IReadOnlyList<string> KeyColumns)
{
    /// <summary>
    /// 대상에 표가 없어 이번 실행이 원본 구조로 만든 뒤 넣는 표(<see cref="TableRule.CreateIfMissing"/>). <see cref="Target"/> 은 만들 구조다.
    /// 새 표라 지우기를 건너뛰고(INSERT 로 넣는다) 제약·트리거·권한 점검을 하지 않는다.
    /// </summary>
    public bool CreateMissing { get; init; }

    /// <summary>
    /// 이 표를 만들기 전에 대상에 만드는 시퀀스(<see cref="CopyCheckCodes.SequenceCreated"/>, 「없으면 만들기」·「다시 만들기」만). 여러 표가 같은 시퀀스를 쓰면 표마다 들어 있고 실행기는 한 번만 만든다.
    /// </summary>
    public IReadOnlyList<CopySequencePlan> CreateSequences { get; init; } = [];

    /// <summary>
    /// 작업의 시퀀스 보정 연결(<see cref="SequenceRule"/>)이 쓰는데 대상에 없어 적재 전에 만드는 시퀀스(SQ3, 결정 84, <see cref="CopyCheckCodes.SequenceCreated"/>).
    /// 원본 계정에 같은 이름 시퀀스가 있으면 그 속성, 없으면 기본 속성(<see cref="SequenceSpec.Defaults"/>)이다. 만든 뒤 적재 뒤 보정은 지금과 같다.
    /// <see cref="CreateSequences"/> 에 이미 있는 시퀀스는 들어 있지 않다.
    /// </summary>
    public IReadOnlyList<CopySequencePlan> RuleSequences { get; init; } = [];

    /// <summary>만드는 표에 적재 뒤 함께 만드는 객체(<see cref="CopyCheckCodes.CompanionObjects"/>). 만들지 않는 표는 비어 있다.</summary>
    public CompanionDdl Companions { get; init; } = CompanionDdl.Empty;

    /// <summary>
    /// 실제 적재 방식(사전 점검이 정한 것, <see cref="CopyLoad"/>). 외부 도구로 골랐어도 못 쓰면 <see cref="LoadMethod.Standard"/>(<see cref="CopyCheckCodes.ExternalLoadFallback"/> 알림).
    /// </summary>
    public LoadMethod Load { get; init; }

    public bool CanDisableTriggers { get; init; }
    public IReadOnlyList<string> Triggers { get; init; } = [];

    /// <summary>이미 있는 대상 테이블에 트리거가 하나도 없다(대상 소유자 세션으로 확인, 결정 96). 외부 도구의 direct path 판정(<see cref="CopyLoad.Direct"/>)에 쓴다.</summary>
    public bool NoTargetTriggers { get; init; }
    public IReadOnlyList<DisabledObject> DisablableForeignKeys { get; init; } = [];
}

public enum PreviewSide { Source, Target }

public sealed record PreviewStatement(string Sql, PreviewSide Side, PolicyDecisionKind Policy,
                                      string? PolicyMessage, string? RetypeText);

/// <summary><paramref name="Order"/> 는 부모 → 자식 순서다. <paramref name="EstimatedRows"/> 는 규칙 키로 찾는다.</summary>
public sealed record PrecheckReport(IReadOnlyList<CheckItem> Items, IReadOnlyList<CopyTablePlan> Order,
                                    IReadOnlyList<PreviewStatement> Preview,
                                    IReadOnlyDictionary<string, long> EstimatedRows)
{
    public bool HasErrors => Items.Any(i => i.Level == CheckLevel.Error);

    /// <summary>
    /// 동시 복사(PF2)의 FK 단계(규칙 키, <see cref="CopyOrder.Stages"/>). 한 단계의 테이블은 함께 적재해도 되고, 단계는 앞 단계가 모두 끝난 뒤 시작한다.
    /// null 이면(이 칸이 없던 점검 결과) 동시에 복사하지 않는다.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>>? Stages { get; init; }

    /// <summary>외부 도구로 넣는 테이블이 있을 때 확인한 도구(경로·버전, 조정 결정 88). 없으면 null.</summary>
    public ExternalLoad.ExternalToolInfo? ExternalTool { get; init; }
}
