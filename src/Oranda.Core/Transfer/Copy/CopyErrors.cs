using System.Data.Common;
using Oranda.Core.Connection;
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

    /// <summary>
    /// 원본·대상 두 DBMS 의 예외를 함께 알아보는 분류기(xdb-copy XC2). <see cref="IDbErrorClassifier.FromException"/> = 대상 분류 ?? 원본 분류,
    /// <see cref="IDbErrorClassifier.IsOwnDriverException"/> = 둘 중 하나, <see cref="IDbErrorClassifier.Classify"/>·<see cref="IDbErrorClassifier.Dbms"/> = 대상 것.
    /// 둘이 같은 인스턴스이거나 같은 DBMS 면 대상 분류기를 그대로 돌려준다(같은 DBMS 복사는 예전과 같다).
    /// 합친 분류기의 <see cref="IDbErrorClassifier.Classify"/>·<see cref="IDbErrorClassifier.Dbms"/> 는 대상 것이므로, 원본 예외의 번호를 넘겨 <c>Classify</c> 를 부르지 말고
    /// <see cref="IDbErrorClassifier.FromException"/> 이 돌려주는 <see cref="DbError"/> 의 <c>Kind</c>(그 예외를 낸 DBMS 의 분류기가 채운다)를 보아야 한다.
    /// </summary>
    public static IDbErrorClassifier Combine(IDbErrorClassifier target, IDbErrorClassifier source)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);
        return ReferenceEquals(target, source) || target.Dbms == source.Dbms ? target : new CompositeDbErrorClassifier(target, source);
    }

    /// <summary>대상 먼저, 그다음 원본으로 찾는 분류기(<see cref="Combine"/>).</summary>
    private sealed class CompositeDbErrorClassifier(IDbErrorClassifier target, IDbErrorClassifier source) : IDbErrorClassifier
    {
        public Dbms Dbms => target.Dbms;

        public DbErrorKind Classify(int number) => target.Classify(number);

        public DbError? FromException(Exception exception) => target.FromException(exception) ?? source.FromException(exception);

        public bool IsOwnDriverException(Exception exception) => target.IsOwnDriverException(exception) || source.IsOwnDriverException(exception);
    }
}
