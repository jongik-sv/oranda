using System.Globalization;

namespace Oranda.Core.Rollback;

/// <summary>
/// 시점 번호 하나(Oracle SCN, Tibero TSN). <paramref name="Method"/> 는 읽은 방법(시점 읽기 질의 글이나 짧은 이름, 진단용).
/// </summary>
public sealed record ChangePoint(long Number, string Name, DateTimeOffset ReadAt, string Method)
{
    /// <summary>화면 글(예: <c>SCN 48213907</c>).</summary>
    public string Text => Name + " " + Number.ToString(CultureInfo.InvariantCulture);
}

/// <summary>원복 스크립트의 상태(설계 11.2). 이름은 <c>rollback_group.state</c>·<c>rollback_statement.state</c> 열 값과 같다.</summary>
public enum RollbackScriptState
{
    /// <summary>만들지 않음(이유는 <see cref="NotCreatedReason"/>).</summary>
    NotCreated,

    /// <summary>열림: 트랜잭션이 아직 끝나지 않았다. 문장 기록에서는 「완전」.</summary>
    Open,

    /// <summary>확정: 커밋했고 모든 문장의 원복 문장을 만들었다.</summary>
    Confirmed,

    /// <summary>불완전: 커밋했지만 일부 행·문장의 원복 문장을 만들지 못했다.</summary>
    Incomplete,

    /// <summary>무효: 롤백했다.</summary>
    Invalid,

    /// <summary>확인 필요: 트랜잭션이 어떻게 끝났는지 모른다(RD-6).</summary>
    Unverified,
}

/// <summary>원복 스크립트를 만들지 않은 이유(설계 11.2).</summary>
public enum NotCreatedReason
{
    None,
    NotProduction,
    SettingOff,
    Declined,
    OverLimit,
    NotTarget,
    NoSecureStore,
    Failed,
    Suppressed,
}

/// <summary>호출처별 기록 방식(설계 7.5, RD-21). <see cref="Execution.ExecutionOptions.Recording"/>.</summary>
public enum ChangeRecording
{
    /// <summary>층 A(운영 접속 원복 스크립트) + 층 B(시점 기록).</summary>
    Normal,

    /// <summary>층 B 만(시점 기록).</summary>
    PointOnly,

    /// <summary>기록하지 않음(원복 실행·비교 반영이 스스로 반대 문장을 기록, RD-8).</summary>
    None,
}

/// <summary>트랜잭션 끝 종류(설계 7.3).</summary>
public enum TransactionEndKind
{
    Commit,
    Rollback,
    RollbackToSavepoint,
    Unknown,
}

/// <summary>트랜잭션 끝을 안 자리(설계 7.3 표). 이름은 <c>rollback_group.end_source</c> 열 값과 같다.</summary>
public enum TransactionEndSource
{
    SessionApi,
    Statement,
    Ddl,
    PlSql,
    SessionBroken,
    SessionClosed,
    AppRestart,
}

/// <summary>묶음이 생긴 까닭(설계 11.3, RD-8). 이름은 <c>rollback_group.origin</c> 열 값과 같다.</summary>
public enum RollbackOrigin
{
    Execution,
    RollbackRun,
    CompareApply,
}
