using System.Data.Common;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Transfer.Copy;

namespace Oranda.Core.Tests.Transfer.Copy;

/// <summary>
/// 복사의 서버 오류 범위(<see cref="CopyErrors"/>): <see cref="DbException"/> 이면 서버 오류, 아니면 분류기가 그 예외 자체를 자기 것으로 알아볼 때만(<see cref="IDbErrorClassifier.IsOwnDriverException"/>).
/// 기본 구현(false)을 쓰는 DBMS(Oracle)는 예전 범위 그대로다.
/// </summary>
public sealed class CopyErrorsTests
{
    [Fact]
    public void A_classifier_with_the_default_counts_only_db_exceptions()
    {
        IDbErrorClassifier errors = new DefaultClassifier();

        Assert.False(errors.IsOwnDriverException(new ClientException()));
        Assert.NotNull(CopyErrors.Server(errors, new FakeDbException()));
        Assert.Null(CopyErrors.Server(errors, new ClientException()));
        Assert.False(CopyErrors.IsServer(errors, new ClientException()));
    }

    [Fact]
    public void A_classifier_that_owns_its_client_exceptions_extends_the_range_to_that_exception_itself()
    {
        var errors = new OwningClassifier();
        var client = new ClientException();

        Assert.NotNull(CopyErrors.Server(errors, client));
        Assert.True(CopyErrors.IsServer(errors, client));
        Assert.NotNull(CopyErrors.Server(errors, new FakeDbException()));
        Assert.False(CopyErrors.IsServer(errors, new InvalidOperationException("wrapped", client))); // 감싼 예외는 삼키지 않는다
        Assert.False(CopyErrors.IsServer(errors, new OperationCanceledException()));
    }

    private sealed class FakeDbException : DbException;

    private sealed class ClientException : Exception;

    /// <summary>안쪽까지 찾는 분류기(<see cref="IDbErrorClassifier.FromException"/> 계약) 흉내. 두 예외 형식만 자기 것으로 안다. <c>IsOwnDriverException</c> 은 구현하지 않는다(기본 = false).</summary>
    private class DefaultClassifier : IDbErrorClassifier
    {
        public Dbms Dbms => Dbms.Tibero;

        public DbErrorKind Classify(int number) => DbErrorKind.Other;

        public DbError? FromException(Exception exception)
        {
            for (var e = exception; e is not null; e = e.InnerException)
            {
                if (e is FakeDbException or ClientException)
                {
                    return new DbError("TBR-1", e.Message) { Dbms = Dbms.Tibero };
                }
            }

            return null;
        }
    }

    private sealed class OwningClassifier : DefaultClassifier, IDbErrorClassifier
    {
        bool IDbErrorClassifier.IsOwnDriverException(Exception exception) => exception is ClientException;
    }
}
