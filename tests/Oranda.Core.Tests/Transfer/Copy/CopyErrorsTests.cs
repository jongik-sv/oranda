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

    [Fact]
    public void Combine_returns_the_target_classifier_itself_for_the_same_instance_or_the_same_dbms()
    {
        var target = new OwningClassifier();

        Assert.Same(target, CopyErrors.Combine(target, target));
        Assert.Same(target, CopyErrors.Combine(target, new DefaultClassifier())); // 둘 다 Tibero
    }

    [Fact]
    public void Combine_of_two_dbms_asks_the_target_first_then_the_source_and_owns_either_drivers_exceptions()
    {
        var target = new OracleLikeClassifier();
        var source = new OwningClassifier(); // Tibero, ClientException 을 자기 것으로 안다
        var errors = CopyErrors.Combine(target, source);

        Assert.Equal(Dbms.Oracle, errors.Dbms);
        Assert.Equal(DbErrorKind.Cancelled, errors.Classify(1013)); // 번호 분류는 대상 것
        Assert.Equal("ORA-1", errors.FromException(new FakeDbException())?.Code); // 둘 다 알면 대상 먼저
        Assert.Equal("TBR-1", errors.FromException(new ClientException())?.Code); // 대상이 모르면 원본
        Assert.True(errors.IsOwnDriverException(new ClientException()));
        Assert.True(CopyErrors.IsServer(errors, new ClientException())); // 원본 드라이버 예외도 복사 밖으로 새지 않는다
        Assert.False(errors.IsOwnDriverException(new InvalidOperationException()));
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

    /// <summary>다른 DBMS(Oracle) 흉내: <see cref="FakeDbException"/> 만 자기 것으로 알고 1013 을 취소로 본다.</summary>
    private sealed class OracleLikeClassifier : IDbErrorClassifier
    {
        public Dbms Dbms => Dbms.Oracle;

        public DbErrorKind Classify(int number) => number == 1013 ? DbErrorKind.Cancelled : DbErrorKind.Other;

        public DbError? FromException(Exception exception) => exception is FakeDbException ? new DbError("ORA-1", exception.Message) { Dbms = Dbms.Oracle } : null;
    }
}
