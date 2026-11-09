using Oranda.Core.Execution;
using Oranda.Core.Transfer.Copy;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Tests.Driver;

namespace Oranda.Tibero.Tests.Transfer.Copy;

/// <summary>
/// 복사의 서버 오류 범위(<see cref="CopyErrors"/>)가 Tibero 드라이버의 클라이언트 쪽 오류(<c>TbOdpException</c>, <c>DbException</c> 이 아님)를 놓치지 않는지 본다(서버 없음, 가짜 드라이버).
/// Tibero 드라이버는 서버 오류(<c>TiberoException : DbException</c>)와 달리 접속 끊김·통신 오류를 <c>System.Exception</c> 바탕 형식으로 낸다(docs/design/tibero-driver-session.md 1장).
/// 이전에는 <c>exception is DbException</c> 만 서버 오류로 보아 이 오류가 복사 읽기·쓰기 밖으로 새어 나갔다.
/// </summary>
public sealed class TiberoCopyErrorsTests
{
    private static readonly TiberoErrorClassifier Errors = new();

    [Fact]
    public void A_driver_side_client_error_counts_as_a_server_error_and_is_classified()
    {
        var broken = FakeDriverHost.ClientError(-90403, "ODP-90403:Connection broken.");

        var error = CopyErrors.Server(Errors, broken);

        Assert.NotNull(error);
        Assert.Equal("TBR-90403", error.Code);
        Assert.Equal(DbErrorKind.Disconnected, error.Kind); // 복사의 COMMIT 판정(CommitUnknown)이 이것을 본다
        Assert.True(CopyErrors.IsServer(Errors, broken));
        Assert.False(CopyErrors.IsNoAccess(Errors, broken));
    }

    [Fact]
    public void A_server_error_still_counts_and_keeps_its_kind()
    {
        var fk = FakeDriverHost.ServerError(-10009, "ODP-10009:child record found");

        var error = CopyErrors.Server(Errors, fk);

        Assert.NotNull(error);
        Assert.Equal(DbErrorKind.ConstraintViolation, error.Kind);
        Assert.True(CopyErrors.IsServer(Errors, fk));
    }

    [Fact]
    public void A_missing_object_server_error_is_no_access()
    {
        Assert.True(CopyErrors.IsNoAccess(Errors, FakeDriverHost.ServerError(-8033, "ODP-8033:Specified schema object was not found.")));
    }

    [Fact]
    public void Wrapped_driver_exceptions_and_non_driver_exceptions_are_not_server_errors()
    {
        // 바깥 예외 자체만 본다(Oracle 의 예전 catch (OracleException) 과 같은 범위) — 감싼 예외를 삼키지 않는다.
        var client = FakeDriverHost.ClientError(-90403, "ODP-90403:Connection broken.");
        var server = FakeDriverHost.ServerError(-10009, "ODP-10009:child record found");

        Assert.False(CopyErrors.IsServer(Errors, new InvalidOperationException("wrapped", client)));
        Assert.False(CopyErrors.IsServer(Errors, new AggregateException(client)));
        Assert.False(CopyErrors.IsServer(Errors, new InvalidOperationException("wrapped", server)));
        Assert.False(CopyErrors.IsServer(Errors, new InvalidOperationException("TBR-10007 in a plain exception")));
        Assert.False(CopyErrors.IsServer(Errors, new IOException("ODP-90603")));
        Assert.False(CopyErrors.IsServer(Errors, new OperationCanceledException()));
    }

    [Fact]
    public void The_classifier_recognizes_only_the_exception_itself()
    {
        IDbErrorClassifier classifier = Errors;
        var client = FakeDriverHost.ClientError(-90603, "ODP-90603:disconnected");

        Assert.True(classifier.IsOwnDriverException(client));
        Assert.True(classifier.IsOwnDriverException(FakeDriverHost.ServerError(-8033, "ODP-8033:x")));
        Assert.False(classifier.IsOwnDriverException(new InvalidOperationException("x", client)));
        Assert.False(classifier.IsOwnDriverException(new IOException("TBR-8033")));
    }
}
