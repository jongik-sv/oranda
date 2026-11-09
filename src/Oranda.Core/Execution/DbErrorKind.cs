namespace Oranda.Core.Execution;

/// <summary>
/// 서버 오류의 의미 분류(D-046, docs/design/tibero.md 2장). 기능 코드는 번호(ORA-nnnnn·TBR-nnnn) 대신 이 값으로 판단한다.
/// 번호 → 분류는 DBMS 공급자의 <see cref="IDbErrorClassifier"/> 가 정한다. 값을 더할 때는 끝에 덧붙인다.
/// </summary>
public enum DbErrorKind
{
    /// <summary>분류하지 않은 오류, 또는 서버 오류가 아님(<c>ORANDA-*</c> 등 앱 자체 오류).</summary>
    Other,

    /// <summary>사용자 요청으로 취소됨(Oracle ORA-01013).</summary>
    Cancelled,

    /// <summary>교착 상태로 문장이 되돌려짐(ORA-00060).</summary>
    Deadlock,

    /// <summary>연결이 끊김 또는 세션이 죽음(ORA-03113·03114·03135·00028).</summary>
    Disconnected,

    /// <summary>제약 위반: 유일·참조·검사·NOT NULL(ORA-00001·02290·02291·02292·01400).</summary>
    ConstraintViolation,

    /// <summary>스냅샷이 너무 오래됨(ORA-01555).</summary>
    SnapshotTooOld,

    /// <summary>표·뷰·객체가 없거나 볼 수 없음(ORA-00942·04043).</summary>
    ObjectNotFound,

    /// <summary>권한 부족(ORA-01031·01039).</summary>
    InsufficientPrivileges,

    /// <summary>자원이 사용 중이고 NOWAIT·시간 제한으로 기다리지 않음(ORA-00054·30006).</summary>
    ResourceBusy,

    /// <summary>읽기 전용 트랜잭션·읽기 전용 DB 에서 쓰기 시도(ORA-01456·16000).</summary>
    ReadOnly,

    /// <summary>계정·비밀번호가 틀림(ORA-01017).</summary>
    InvalidCredentials,

    /// <summary>비밀번호가 만료됨 — 새 비밀번호로 다시 접속해야 함(ORA-28001).</summary>
    PasswordExpired,

    /// <summary>계정이 잠김(ORA-28000).</summary>
    AccountLocked,

    /// <summary>객체가 컴파일 오류와 함께 만들어짐(ORA-24344).</summary>
    CompiledWithErrors,
}

/// <summary>여러 기능이 같이 쓰는 분류 묶음.</summary>
public static class DbErrorKinds
{
    /// <summary>
    /// 「볼 수 없음」: 객체가 없거나 권한이 없음(ORA-00942·01031 를 함께 보던 곳). 사전·성능 뷰를 못 읽을 때
    /// 기능을 끄고 안내하는 판단에 쓴다.
    /// </summary>
    public static bool IsNoAccess(this DbErrorKind kind) =>
        kind is DbErrorKind.ObjectNotFound or DbErrorKind.InsufficientPrivileges;

    /// <summary><see cref="DbError.Kind"/> 가 「볼 수 없음」인가(<see cref="IsNoAccess(DbErrorKind)"/>).</summary>
    public static bool IsNoAccess(this DbError? error) => error is not null && error.Kind.IsNoAccess();
}
