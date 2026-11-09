using System.Data.Common;
using Oranda.Core.Execution;

namespace Oranda.Core.Transfer.Copy;

/// <summary>
/// 복사가 서버 오류를 잡는 범위 한 곳. 잡은 예외 <b>그 자체</b>가 드라이버 예외(<see cref="DbException"/>)이고 분류기가 그 DBMS 의 것으로 알아볼 때만
/// 서버 오류로 본다. <see cref="IDbErrorClassifier.FromException"/> 은 <see cref="Exception.InnerException"/>·<see cref="AggregateException"/> 안쪽까지
/// 찾으므로 그대로 쓰면 예전에 밖으로 나가던 감싼 예외(예: 드라이버 예외를 품은 <see cref="InvalidOperationException"/>)를 삼킨다.
/// Oracle 의 예전 <c>catch (OracleException)</c> 과 같은 범위다. 드라이버 예외가 <see cref="DbException"/> 이 아닌 DBMS(Tibero 의 클라이언트 쪽 오류)는
/// 분류기가 <see cref="IDbErrorClassifier.IsOwnDriverException"/> 으로 그 예외 자체를 알아볼 때 같은 범위로 본다. <see cref="OperationCanceledException"/> 은 드라이버 예외가 아니라 늘 밖으로 나간다.
/// </summary>
public static class CopyErrors
{
    /// <summary>바깥 예외가 이 DBMS 드라이버의 예외면 그 분류, 아니면 null.</summary>
    public static DbError? Server(IDbErrorClassifier errors, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(errors);
        ArgumentNullException.ThrowIfNull(exception);
        // DbException 이 아닌 드라이버 예외(Tibero 의 클라이언트 쪽 오류)는 분류기가 자기 것으로 알아볼 때만 — 바깥 예외 자체에 한한다(안쪽은 보지 않는다).
        return exception is DbException || errors.IsOwnDriverException(exception) ? errors.FromException(exception) : null;
    }

    /// <summary>바깥 예외가 이 DBMS 드라이버의 예외인가(서버 오류면 「확인 못 함」으로 두는 자리).</summary>
    public static bool IsServer(IDbErrorClassifier errors, Exception exception) => Server(errors, exception) is not null;

    /// <summary>바깥 예외가 「볼 수 없음」(객체 없음·권한 부족, <see cref="DbErrorKinds.IsNoAccess(DbErrorKind)"/>) 서버 오류인가.</summary>
    public static bool IsNoAccess(IDbErrorClassifier errors, Exception exception) => Server(errors, exception).IsNoAccess();
}
