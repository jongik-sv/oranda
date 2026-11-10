using System.Globalization;
using Oranda.Core.Connection;
using Oranda.Core.Execution;

namespace Oranda.Core.Transfer.Copy;

/// <summary>
/// 원본의 지금 시점을 읽는 조회 하나(<see cref="CopyReadPointSupport.Queries"/>). <paramref name="Sql"/> 은 <c>SELECT</c> 로 시작하고 바인드가 없으며
/// 결과 = 행 하나·열 하나(시점 번호). <paramref name="Method"/> = 작업 로그에 남는 방법 이름.
/// </summary>
public sealed record CopyReadPointQuery(string Sql, string Method);

/// <summary>
/// 같은 시점 읽기를 동시 복사 레인 여럿에서 맞추는 원본 DBMS 의 시점 지정 읽기(PF2b, 원본 방언 <see cref="ICopyDialect.ReadPoint"/>).
/// 실행을 시작할 때 원본 시점 번호 하나를 잡고(<see cref="Queries"/> 를 차례로, 처음 숫자를 준 것), 모든 테이블 SELECT 의 원본 표 뒤에
/// <see cref="Clause"/>(Oracle·Tibero <c>AS OF SCN :ORANDA_SCN</c>)를 붙여 그 시점으로 읽는다.
/// </summary>
/// <param name="Name">시점 번호 이름(Oracle <c>SCN</c>, Tibero <c>TSN</c>). 작업 로그·화면에 「SCN 1234」로 보인다.</param>
/// <param name="ClauseFormat">원본 표 이름 뒤에 붙는 글. <c>{0}</c> = 쌍점 없는 바인드 이름(<see cref="CopyReadPoint.BindName"/>).</param>
/// <param name="Queries">지금 시점을 읽는 조회(순서대로 시도, 서버 오류면 다음).</param>
/// <param name="ObjectPrivilege">다른 계정의 표를 시점 지정으로 읽을 때 필요한 객체 권한(Oracle <c>FLASHBACK</c>).</param>
/// <param name="SystemPrivilege">같은 일을 모든 표에 주는 시스템 권한(Oracle <c>FLASHBACK ANY TABLE</c>).</param>
public sealed record CopyReadPointSupport(string Name, string ClauseFormat, IReadOnlyList<CopyReadPointQuery> Queries, string ObjectPrivilege, string SystemPrivilege)
{
    /// <summary>시점 이전 데이터를 UNDO 에서 잃었을 때 안내에 붙는 오류 이름(Oracle <c>ORA-01555·ORA-08180</c>). 모르면 null.</summary>
    public string? SnapshotTooOldErrors { get; init; }

    /// <summary>시점 뒤에 표 정의가 바뀌어(DDL) 그 시점으로 읽을 수 없는 서버 오류 번호(Oracle 1466). 안내 글에 「그 번호」가 들어간다.</summary>
    public IReadOnlyList<int> DefinitionChangedNumbers { get; init; } = [];

    /// <summary>시점 번호가 올바르지 않은 서버 오류 번호(Oracle 8181).</summary>
    public IReadOnlyList<int> InvalidPointNumbers { get; init; } = [];

    /// <summary>
    /// 오류 분류기가 번호로 모르는 「시점 데이터 잃음」을 메시지 글로 알아보는 조각(대소문자 무관, PF2b2 Tibero <c>snapshot too old</c>).
    /// 번호를 실측하지 못한 DBMS 가 쓴다. 맞으면 <see cref="DbErrorKind.SnapshotTooOld"/> 와 같이(실행 전체 중단) 다룬다.
    /// </summary>
    public IReadOnlyList<string> SnapshotTooOldMessages { get; init; } = [];

    /// <summary>
    /// 참이면 위 번호표로 모르는 서버 오류(<see cref="DbErrorKind.Other"/>)도 시점 지정 읽기 실패로 그 테이블만 실패시키고 시점·「지금 데이터로 바꿔 읽지 않습니다」를 적는다
    /// (PF2b2 Tibero: 시점 데이터 잃음·정의 바뀜·잘못된 시점의 번호를 실측하지 못했다). 거짓이면 그 오류는 예전 처리다.
    /// </summary>
    public bool UnmappedErrorsFailTable { get; init; }

    /// <summary>
    /// 참이면 다른 계정의 테이블을 시점 지정으로 읽는 권한(<see cref="ObjectPrivilege"/>·<see cref="SystemPrivilege"/>)이 이 DBMS 에서 맞는 이름인지 실측하지 못했다
    /// (PF2b2 Tibero). 사전 점검은 권한이 안 보일 때 오류 대신 확인 필요로 둔다.
    /// </summary>
    public bool PrivilegeUnverified { get; init; }

    /// <summary>원본 표 이름 뒤에 붙는 글(바인드 이름 = <see cref="CopyReadPoint.BindName"/>).</summary>
    public string Clause => string.Format(CultureInfo.InvariantCulture, ClauseFormat, CopyReadPoint.BindName);
}

/// <summary>
/// 이번 실행이 잡은 원본 시점(PF2b). <see cref="Number"/> = 시점 번호(Oracle SCN·Tibero TSN), <see cref="Support"/> = 원본 방언의 시점 지정 읽기.
/// 원본 SELECT(<see cref="CopySqlBuilder.SourceSelect"/>)가 원본 표 뒤에 <see cref="CopyReadPointSupport.Clause"/> 를 붙이고 바인드 <see cref="BindName"/> 에 번호를 넣는다.
/// </summary>
public sealed record CopyReadPoint(long Number, CopyReadPointSupport Support)
{
    /// <summary>시점 번호 바인드 이름(쌍점 없음). 사용자 조건의 바인드(<c>:B1_FROM</c>·작업 변수)와 겹치지 않는 이름이다.</summary>
    public const string BindName = "ORANDA_SCN";

    /// <summary>사전 점검 미리 보기용(번호 없음): 글은 실행과 같고 바인드 값만 0 이다.</summary>
    public static CopyReadPoint Preview(CopyReadPointSupport support) => new(0, support);

    /// <summary>원본 표 뒤에 붙는 글(<c>AS OF SCN :ORANDA_SCN</c>).</summary>
    public string Clause => Support.Clause;

    public BindValue Bind => new(BindDataType.Number, (decimal)Number);

    /// <summary>화면·로그 글(「SCN 1234」).</summary>
    public string Text => Support.Name + " " + Number.ToString(CultureInfo.InvariantCulture);
}

/// <summary>시점 지정 읽기 실패의 안내(<see cref="CopyReadPointErrors.Describe"/>). <see cref="AbortRun"/> = 다른 테이블도 같은 이유로 못 읽으니 실행 전체를 멈춘다.</summary>
public readonly record struct CopyReadPointFailure(string Message, bool AbortRun);

/// <summary>
/// 시점 지정 읽기(<c>AS OF SCN</c>)가 서버 오류로 실패했을 때 테이블마다의 한국어 안내(PF2b). 어떤 경우에도 지금 데이터로 바꿔 읽지 않는다 —
/// 그 테이블은 실패(시점 데이터를 잃었으면 실행 전체를 멈춤)다. 시점 읽기와 상관없는 오류면 null(예전 처리).
/// </summary>
public static class CopyReadPointErrors
{
    /// <summary>안내 끝에 붙는 공통 문장.</summary>
    public const string NoFallback = "지금 데이터로 바꿔 읽지 않습니다.";

    public static CopyReadPointFailure? Describe(string table, CopyReadPoint point, DbError error)
    {
        ArgumentNullException.ThrowIfNull(point);
        ArgumentNullException.ThrowIfNull(error);
        var support = point.Support;
        var at = point.Text;
        if (error.Kind == DbErrorKind.SnapshotTooOld || MatchesAny(error.Message, support.SnapshotTooOldMessages))
        {
            // 번호를 모르는 DBMS(Tibero)는 받은 오류 코드를 그대로 적는다.
            var name = (support.SnapshotTooOldErrors ?? error.Code) is { Length: > 0 } n ? $"({n})" : string.Empty;
            return new CopyReadPointFailure(
                $"{table}: 원본 {at} 시점의 데이터가 원본 UNDO 에 남아 있지 않아 읽지 못했습니다{name}. {NoFallback} 원본 UNDO_RETENTION 을 늘리거나 기간을 나눠 실행하거나, 동시 테이블 수를 줄이세요.",
                AbortRun: true);
        }

        if (error.Kind == DbErrorKind.InsufficientPrivileges)
        {
            return new CopyReadPointFailure(
                $"{table}: 원본 테이블을 {at} 시점으로 읽을 권한({support.ObjectPrivilege} 또는 {support.SystemPrivilege})이 없습니다({error.Code}). {NoFallback} 권한을 받거나, 동시 테이블 수를 1로 하거나, 같은 시점 읽기를 끄세요.",
                AbortRun: false);
        }

        var number = error.Number == int.MinValue ? 0 : Math.Abs(error.Number); // Tibero 드라이버는 번호를 음수로 준다
        if (support.DefinitionChangedNumbers.Contains(number))
        {
            return new CopyReadPointFailure(
                $"{table}: 원본 테이블 정의가 {at} 뒤에 바뀌어(DDL) 그 시점으로 읽을 수 없습니다({error.Code}). {NoFallback} 다시 실행하세요.",
                AbortRun: false);
        }

        if (support.InvalidPointNumbers.Contains(number))
        {
            return new CopyReadPointFailure(
                $"{table}: 원본이 {at} 을(를) 올바른 시점으로 받지 않았습니다({error.Code}). {NoFallback} 다시 실행하세요.",
                AbortRun: true);
        }

        if (support.UnmappedErrorsFailTable && error.Kind == DbErrorKind.Other)
        {
            return new CopyReadPointFailure(
                $"{table}: 원본 테이블을 {at} 시점으로 읽지 못했습니다({ErrorText(error)}). {NoFallback}"
                + " 시점 데이터가 UNDO 에 없거나, 그 시점 뒤에 테이블 정의가 바뀌었거나(DDL), 시점 번호를 받지 않았을 수 있습니다. 다시 실행하거나 동시 테이블 수를 1로 하세요.",
                AbortRun: false);
        }

        return null;
    }

    /// <summary>오류 코드 + 메시지 첫 줄(따옴표 안 값은 가림, <see cref="CopyRunner.SafeError"/>). 메시지가 코드로 시작하면 코드를 두 번 쓰지 않는다.</summary>
    private static string ErrorText(DbError error)
    {
        var detail = string.IsNullOrEmpty(error.Message) ? string.Empty : CopyRunner.SafeError(error.Message).Trim();
        return detail.Length == 0 ? error.Code
            : detail.StartsWith(error.Code, StringComparison.OrdinalIgnoreCase) ? detail
            : error.Code + ": " + detail;
    }

    private static bool MatchesAny(string? message, IReadOnlyList<string> fragments) =>
        !string.IsNullOrEmpty(message) && fragments.Any(f => message.Contains(f, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// 원본 DBMS 가 동시 복사에서 같은 시점 읽기(시점 지정 읽기)를 쓰는가(PF2b). 화면은 세션 없이 DBMS 만 알므로 DBMS 로 정한다.
/// 이 표와 원본 방언의 <see cref="ICopyDialect.ReadPoint"/> 가 같아야 한다(시험이 본다).
/// <list type="bullet">
/// <item>Oracle: 씀(<c>AS OF SCN</c>).</item>
/// <item>Tibero: 씀(PF2b2, 조정자 회차 xdb-copy 결정 100). 6.2.15 실측(#187): <c>DBMS_FLASHBACK.GET_SYSTEM_CHANGE_NUMBER</c>·<c>TIMESTAMP_TO_TSN(SYSTIMESTAMP)</c>·
/// <c>V$DATABASE.CURRENT_TSN</c> 이 같은 TSN 을 주고 <c>AS OF SCN</c>·<c>AS OF TSN</c> 이 그 시점의 행을 돌려준다. 시점 읽기 실패의 오류 번호는 실측하지 못해
/// 모르는 서버 오류는 그 테이블만 실패로 둔다(<see cref="CopyReadPointSupport.UnmappedErrorsFailTable"/>).</item>
/// </list>
/// </summary>
public static class CopyReadPointDbms
{
    public static bool Supports(Dbms source) => source is Dbms.Oracle or Dbms.Tibero;
}
