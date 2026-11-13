using System.Globalization;
using System.Text.RegularExpressions;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Rollback;

namespace Oranda.App.Features.Execution;

/// <summary>미커밋 상태로 끝낼 때의 선택(SAFE: 종료 시 확인).</summary>
public enum UncommittedChoice
{
    Commit,
    Rollback,
    Cancel,

    /// <summary>트랜잭션을 이전 접속에 그대로 두고 탭의 접속만 바꾼다(접속 콤보, TSK-12-04). 그 묻기에서만 나온다.</summary>
    Keep,
}

/// <summary>접속 안 됨 탭을 실행할 때의 선택(CONN-06).</summary>
public enum MissingConnectionChoice
{
    /// <summary>탭에 묶인 접속으로 다시 접속한다.</summary>
    Reconnect,

    /// <summary>탭을 지금 접속으로 옮긴다.</summary>
    UseCurrent,

    Cancel,
}

/// <summary>실행 확인 대화상자에 필요한 것. 접속은 머리 띠(접속 이름·사용자·서비스)에 크게 보인다.</summary>
public sealed record ConfirmRequest(SqlStatement Statement, PolicyDecision Decision, ConnectionProfile? Profile)
{
    /// <summary>원복 스크립트·백업 테이블 제안(되돌리기 지점 설계 10.1). 없으면 원복 줄을 보이지 않는다.</summary>
    public ConfirmExtras Extras { get; init; } = ConfirmExtras.None;

    /// <summary>원복 줄의 보관 일수·SCN 기록 줄을 정하는 지금 설정.</summary>
    public RollbackSettings Rollback { get; init; } = new();
}

/// <summary>
/// 실행 중 사용자에게 묻는 대화상자 모음. 화면 구현은 <see cref="AvaloniaExecutionDialogs"/>, 시험은 가짜를 넣는다.
/// 어느 스레드에서 불러도 된다(구현이 UI 스레드로 옮긴다).
/// </summary>
public interface IExecutionDialogs
{
    /// <summary>운영·WHERE 없는 DML·위험 명령 확인. true = 진행.</summary>
    Task<bool> ConfirmAsync(ConfirmRequest request, CancellationToken ct);

    /// <summary>
    /// 원복 줄·백업 테이블 확인란이 있는 확인(되돌리기 지점 설계 10.1·9장). 기본은 <see cref="ConfirmAsync"/> 로 묻고 제안의 기본값으로 답한다.
    /// </summary>
    async Task<ConfirmAnswer> ConfirmChangeAsync(ConfirmRequest request, CancellationToken ct) =>
        await ConfirmAsync(request, ct).ConfigureAwait(false) ? ConfirmAnswer.Default(request.Extras) : ConfirmAnswer.Declined;

    /// <summary>
    /// 커밋하려는 트랜잭션의 원복 스크립트가 불완전할 때(설계 10.3): 「… 행 수 다름 n문장. 커밋할까요?」. true = 커밋. 기본은 묻지 않고 커밋.
    /// </summary>
    Task<bool> ConfirmIncompleteCommitAsync(string message, CancellationToken ct) => Task.FromResult(true);

    /// <summary>치환 변수 값 입력. <paramref name="last"/> 는 마지막으로 넣은 값. null = 취소.</summary>
    Task<IReadOnlyDictionary<string, string>?> AskSubstitutionsAsync(
        IReadOnlyList<string> names, IReadOnlyDictionary<string, string> last, CancellationToken ct);

    Task<ScriptErrorAction> AskScriptErrorAsync(SqlStatement stmt, DbError failure, CancellationToken ct);

    Task<UncommittedChoice> AskUncommittedAsync(CancellationToken ct);

    /// <summary>전용 세션 탭(D-017)의 미커밋 확인. <paramref name="subject"/> = 탭 이름. 기본은 탭 이름 없이 묻는다.</summary>
    Task<UncommittedChoice> AskUncommittedAsync(string subject, CancellationToken ct) => AskUncommittedAsync(ct);

    /// <summary>
    /// 접속 하나의 미커밋 확인(접속 끊기·앱 종료 때 접속마다, CONN-06). <paramref name="connection"/> = 「번호 - 사용자@이름」.
    /// 기본은 접속 이름 없이 묻는다.
    /// </summary>
    Task<UncommittedChoice> AskConnectionUncommittedAsync(string connection, CancellationToken ct) => AskUncommittedAsync(ct);

    /// <summary>
    /// 접속 콤보로 탭의 접속을 바꾸기 전, 이전 접속(<paramref name="connection"/> = 「번호 - 사용자@이름」)의 미커밋 확인(TSK-12-04):
    /// 커밋 / 롤백 / 그대로 두고 바꾸기(<see cref="UncommittedChoice.Keep"/>) / 취소. 기본은 「그대로 두고 바꾸기」 없이 접속 이름으로 묻는다.
    /// </summary>
    Task<UncommittedChoice> AskSwitchUncommittedAsync(string connection, CancellationToken ct) => AskConnectionUncommittedAsync(connection, ct);

    /// <summary>
    /// 여러 작업 공간이 함께 쓰는 세션(<paramref name="connection"/> = 「번호 - 사용자@이름 (n)」)을 손으로 커밋·롤백하기 전(TSK-14-10, D-042):
    /// 그 트랜잭션은 <paramref name="workspaces"/> 모두의 변경이라 함께 확정·취소된다고 밝히고 한 번 묻는다. true = 진행. 기본은 묻지 않고 진행.
    /// </summary>
    Task<bool> ConfirmSharedTransactionAsync(string connection, IReadOnlyList<string> workspaces, bool commit, CancellationToken ct) =>
        Task.FromResult(true);

    /// <summary>
    /// 탭에 묶인 접속이 열려 있지 않은데 실행하려 한다(CONN-06): 다시 접속 / 지금 접속(<paramref name="current"/>)으로 바꾸기 / 취소.
    /// <paramref name="profile"/> = 탭에 묶인 접속 이름, <paramref name="current"/> = 지금 접속 이름(없으면 null, 그 단추를 감춘다).
    /// 기본은 취소(실행하지 않는다).
    /// </summary>
    Task<MissingConnectionChoice> AskMissingConnectionAsync(string profile, string? current, CancellationToken ct) =>
        Task.FromResult(MissingConnectionChoice.Cancel);

    /// <summary>DML 미리보기(그리드 반영 전, GRID-05). true = 반영.</summary>
    Task<bool> PreviewDmlAsync(string text, CancellationToken ct);

    /// <summary>
    /// 반영 동작을 밝히는 DML 미리보기. <paramref name="commit"/> = 반영 뒤 바로 커밋하는 동작(제목·주 단추 글자가 갈린다).
    /// 기본은 동작을 밝히지 않는 <see cref="PreviewDmlAsync(string, CancellationToken)"/> 로 넘긴다.
    /// </summary>
    Task<bool> PreviewDmlAsync(string text, bool commit, CancellationToken ct) => PreviewDmlAsync(text, ct);
}

/// <summary>실행 확인 대화상자의 판단 부분(화면 없이 시험한다).</summary>
public sealed partial class ConfirmDialogModel(ConfirmRequest request)
{
    public ConfirmRequest Request { get; } = request;

    public bool IsProduction => Request.Profile?.Environment == ConnectionEnvironment.Production;

    /// <summary>다시 입력해야 하는 이름. 없으면 null.</summary>
    public string? RetypeText => string.IsNullOrWhiteSpace(Request.Decision.RetypeText) ? null : Request.Decision.RetypeText;

    public string Message => Request.Decision.Message ?? "이 문장을 실행합니다.";

    /// <summary>머리 띠 첫 줄: 접속 이름.</summary>
    public string HeaderTitle => Request.Profile?.Name ?? "접속 없음";

    /// <summary>머리 띠 둘째 줄: 사용자@서비스.</summary>
    public string HeaderDetail => Request.Profile is { } p ? $"{p.User}@{p.DataSource}" : string.Empty;

    /// <summary>환경 칩 글.</summary>
    public string EnvironmentText => Request.Profile?.Environment switch
    {
        ConnectionEnvironment.Production => "운영",
        ConnectionEnvironment.Test => "테스트",
        ConnectionEnvironment.Development => "개발",
        _ => string.Empty,
    };

    // ---- 원복 줄(되돌리기 지점 설계 10.1) ----------------------------------------------------

    private RollbackOffer? Offer => Request.Extras.Rollback;

    /// <summary>「원복 스크립트 만들기」 확인란을 보인다: 원복 대상 문장이다.</summary>
    public bool ShowsScriptOption => Offer is { Applicable: true };

    /// <summary>확인란 글: 「원복 스크립트 만들기 (예상 1,240행)」 또는 「(예상 행 수 알 수 없음)」.</summary>
    public string ScriptOptionText => Offer?.EstimatedRows is { } rows
        ? $"원복 스크립트 만들기 (예상 {rows.ToString("N0", CultureInfo.CurrentCulture)}행)"
        : "원복 스크립트 만들기 (예상 행 수 알 수 없음)";

    /// <summary>확인란을 바꿀 수 있다. 상한을 넘으면 꺼지고 흐려진다(6.5).</summary>
    public bool ScriptOptionEnabled => Offer is { Applicable: true, OverLimit: false };

    /// <summary>확인란의 처음 값.</summary>
    public bool ScriptOptionDefault => Offer is { Applicable: true, OverLimit: false, DefaultOn: true };

    /// <summary>상한 넘음 문구: 「상한을 넘어 원복 스크립트 없이 실행합니다(예상 123,456행 > 100,000)」. 넘지 않으면 null.</summary>
    public string? OverLimitText => Offer is { Applicable: true, OverLimit: true } o
        ? o.Message ?? $"상한을 넘어 원복 스크립트 없이 실행합니다(예상 {(o.EstimatedRows ?? 0).ToString("N0", CultureInfo.CurrentCulture)}행 > {Request.Rollback.Normalized().MaxRowsPerStatement.ToString("N0", CultureInfo.CurrentCulture)})"
        : null;

    /// <summary>확인란 아래 회색 글(보관 일수는 설정 값).</summary>
    public string? ScriptHintText => ShowsScriptOption
        ? $"같은 조건으로 먼저 잠그고 읽으므로 실행 시간이 늘어납니다. 이 PC 에 암호화해 {Request.Rollback.Normalized().RetentionDays}일 보관합니다."
        : null;

    /// <summary>경고 줄(트리거·CASCADE·큰 열). 원복 대상일 때만.</summary>
    public IReadOnlyList<string> ScriptWarnings => Offer is { Applicable: true } o ? o.Warnings : [];

    /// <summary>
    /// 원복 줄 대신 보이는 한 줄: 대상이 아닌 문장(「이 문장은 원복 스크립트를 만들지 않습니다(MERGE). 실행 직전 SCN 만 기록합니다.」),
    /// 안전한 저장소 없음. 그 밖의 까닭(개발·테스트 접속, 실패 등)은 줄을 남기지 않는다.
    /// </summary>
    public string? NotTargetText => Offer switch
    {
        { Applicable: false, Reason: NotCreatedReason.NotTarget } o => o.Message is { Length: > 0 } m && m.StartsWith("이 문장은", StringComparison.Ordinal)
            ? m
            : $"이 문장은 원복 스크립트를 만들지 않습니다{(o.Message is { Length: > 0 } why ? "(" + why + ")" : string.Empty)}." + (Request.Rollback.RecordPoint ? $" 실행 직전 {PointName} 만 기록합니다." : string.Empty),
        { Applicable: false, Reason: NotCreatedReason.NoSecureStore } => "이 PC 에는 안전한 키 저장소가 없어 원복 스크립트를 만들 수 없습니다.",
        _ => null,
    };

    /// <summary>맨 아래 줄: 「실행 직전 SCN 은 원복 스크립트와 상관없이 기록합니다.」 (층 B 가 켜져 있고 원복 줄이 있을 때)</summary>
    public string? PointText => Request.Rollback.RecordPoint && ShowsScriptOption
        ? $"실행 직전 {PointName} 은 원복 스크립트와 상관없이 기록합니다."
        : null;

    /// <summary>시점 번호 이름: Tibero = TSN, 그 밖 = SCN.</summary>
    public string PointName => Request.Profile?.Dbms == Dbms.Tibero ? "TSN" : "SCN";

    // ---- 백업 테이블(설계 9장) --------------------------------------------------------------

    /// <summary>「먼저 백업 테이블 만들기」 확인란을 보인다.</summary>
    public bool ShowsBackupOption => Request.Extras.Backup is not null;

    /// <summary>확인란 글.</summary>
    public string BackupOptionText => Request.Extras.Backup is { } b
        ? $"먼저 백업 테이블 만들기 ({b.Table.Owner}.{b.Table.Name} → 이름 아래 칸)"
        : string.Empty;

    /// <summary>백업 테이블 제안의 까닭과 CTAS 가 커밋한다는 안내.</summary>
    public string BackupHintText => Request.Extras.Backup is { } b
        ? (string.IsNullOrWhiteSpace(b.Reason) ? string.Empty : b.Reason + " ") + "백업 테이블을 만드는 CREATE TABLE … AS SELECT 는 DDL 이라 열린 트랜잭션을 먼저 커밋합니다."
        : string.Empty;

    /// <summary>이름 칸의 처음 값.</summary>
    public string BackupDefaultName => Request.Extras.Backup?.BackupName ?? string.Empty;

    /// <summary>이름 칸 값이 쓸 수 있는 이름이다(따옴표 없는 식별자, 128자 이하).</summary>
    public static bool IsValidBackupName(string? name) => name?.Trim() is { Length: > 0 and <= 128 } trimmed && BackupNamePattern().IsMatch(trimmed);

    /// <summary>
    /// 확인 창의 답. <paramref name="createScript"/> 는 확인란 값(흐린 확인란은 거짓), <paramref name="backup"/> 이면 이름 칸의 이름으로 CTAS 를 만든다.
    /// 이름을 바꿨으면 제안 문장의 이름을 바꿔 넣는다.
    /// </summary>
    public ConfirmAnswer Answer(bool proceed, bool createScript, bool backup, string? backupName)
    {
        if (!proceed)
        {
            return ConfirmAnswer.Declined;
        }

        string? sql = null;
        if (backup && Request.Extras.Backup is { } b)
        {
            // 쓸 수 없는 이름(빈 칸 포함)이면 제안한 이름 그대로. 화면은 그런 이름이면 실행 단추를 끈다.
            var name = (backupName ?? string.Empty).Trim();
            sql = !IsValidBackupName(name) || string.Equals(name, b.BackupName, StringComparison.OrdinalIgnoreCase)
                ? b.CreateSql
                : RenameTarget(b.CreateSql, b.BackupName, name);
        }

        return new ConfirmAnswer(true, createScript && ScriptOptionEnabled, sql);
    }

    /// <summary>
    /// <c>CREATE TABLE [스키마.]이름</c> 의 이름 자리만 바꾼다(다른 곳에 같은 글이 있어도 건드리지 않는다). 그 모양이 아니면 원래 문장.
    /// </summary>
    internal static string RenameTarget(string createSql, string oldName, string newName)
    {
        var pattern = new Regex(@"^(\s*CREATE\s+TABLE\s+(?:""?[A-Za-z0-9_$#]+""?\.)?)""?" + Regex.Escape(oldName) + @"""?(?=[\s(])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return pattern.IsMatch(createSql) ? pattern.Replace(createSql, m => m.Groups[1].Value + newName, 1) : createSql;
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_$#]*$")]
    private static partial Regex BackupNamePattern();

    /// <summary>실행 단추를 켤 수 있는지. 이름 재입력이 필요하면 같은 이름(대소문자 무시)을 쳐야 한다.</summary>
    public bool CanAccept(string typed) =>
        RetypeText is not { } expected || string.Equals(typed?.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase);
}
