using Oranda.Core.Execution;

namespace Oranda.Core.Rollback;

/// <summary>통로 질의의 결과. <paramref name="Truncated"/> = 행이 <c>maxRows</c> 보다 많아 거기서 멈췄다.</summary>
/// <param name="Rows">행마다 열 값(SELECT 목록 차례). 값은 드라이버가 준 .NET 값이고 DB NULL 은 null.</param>
public sealed record RollbackRows(IReadOnlyList<object?[]> Rows, bool Truncated);

/// <summary>
/// 공급자 연결 위의 통로(설계 5장). 기능 안쪽 문장(시점 읽기, <c>COUNT(*)</c>, 잠금 읽기, 직후 값 읽기, 끝 판정, 내부 저장점)을
/// 정책 관문을 거치지 않고 연결에 직접 보낸다. 이 문장들은 실행 이력에 남기지 않는다.
/// <para>
/// Begin·Complete·Probe 단계에서는 호출한 쪽이 명령 잠금을 쥐고 있다. Prepare 단계에 넘기는 통로는 메서드마다 스스로 잠금을 잡고 놓는다.
/// 리더는 끝까지 읽고 닫은 뒤 돌려준다.
/// </para>
/// <para>
/// 오류: 서버 오류는 <see cref="RollbackChannelException"/>(분류한 <see cref="DbError"/>, <see cref="DbError.Kind"/> 채움)으로 던진다.
/// 취소: <c>ct</c> 가 취소되면 드라이버 명령을 취소하고 <see cref="OperationCanceledException"/> 을 던진다.
/// 엔진은 제한 시간(예: <c>COUNT(*)</c> 10초)을 연결된 취소 토큰으로 건다.
/// </para>
/// </summary>
public interface IRollbackChannel
{
    /// <param name="sql">보낼 질의 글</param>
    /// <param name="binds">바인드 이름(<c>:</c> 제외) → 값. 없으면 빈 사전</param>
    /// <param name="maxRows">이만큼 읽으면 멈추고 <see cref="RollbackRows.Truncated"/> 를 켠다(남은 행이 있을 때만)</param>
    /// <param name="ct">취소</param>
    Task<RollbackRows> QueryAsync(string sql, IReadOnlyDictionary<string, BindValue> binds, int maxRows, CancellationToken ct);

    /// <summary>SAVEPOINT·ROLLBACK TO 내부 문장만 보낸다.</summary>
    Task ExecuteAsync(string sql, CancellationToken ct);
}

/// <summary>
/// <see cref="IRollbackChannel"/> 이 서버 오류를 알릴 때 던지는 예외. 의미 판단은 <see cref="DbError.Kind"/> 로 한다(D-046):
/// 엔진은 <see cref="DbErrorKind.InsufficientPrivileges"/> 를 「만들지 않음 · 권한 없음」으로 바꾸고, 그 밖의 오류는 「만들지 않음 · 실패」로 둔다.
/// </summary>
public sealed class RollbackChannelException(DbError error, Exception? innerException = null) : Exception(error.Message, innerException)
{
    public DbError Error { get; } = error;
}
