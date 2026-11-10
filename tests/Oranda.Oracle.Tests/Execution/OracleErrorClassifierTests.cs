using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Oracle.Execution;

namespace Oranda.Oracle.Tests.Execution;

// 소유: Tibero T1 O1. ORA 번호 → DbErrorKind 표를 고정한다. DB 없음(OracleException 은 만들 수 없어 번호 표만 본다).
public class OracleErrorClassifierTests
{
    private static readonly OracleErrorClassifier Sut = new();

    [Theory]
    [InlineData(1013, DbErrorKind.Cancelled)]
    [InlineData(60, DbErrorKind.Deadlock)]
    [InlineData(28, DbErrorKind.Disconnected)]
    [InlineData(3113, DbErrorKind.Disconnected)]
    [InlineData(3114, DbErrorKind.Disconnected)]
    [InlineData(3135, DbErrorKind.Disconnected)]
    [InlineData(12537, DbErrorKind.Disconnected)]
    [InlineData(12570, DbErrorKind.Disconnected)]
    [InlineData(12571, DbErrorKind.Disconnected)]
    [InlineData(12583, DbErrorKind.Disconnected)]
    [InlineData(1, DbErrorKind.ConstraintViolation)]
    [InlineData(1400, DbErrorKind.ConstraintViolation)]
    [InlineData(2290, DbErrorKind.ConstraintViolation)]
    [InlineData(2291, DbErrorKind.ConstraintViolation)]
    [InlineData(2292, DbErrorKind.ConstraintViolation)]
    [InlineData(1555, DbErrorKind.SnapshotTooOld)]
    [InlineData(8180, DbErrorKind.SnapshotTooOld)] // 그 시점의 스냅샷을 찾을 수 없음(PF2b 시점 읽기)
    [InlineData(942, DbErrorKind.ObjectNotFound)]
    [InlineData(4043, DbErrorKind.ObjectNotFound)]
    [InlineData(1031, DbErrorKind.InsufficientPrivileges)]
    [InlineData(1039, DbErrorKind.InsufficientPrivileges)]
    [InlineData(54, DbErrorKind.ResourceBusy)]
    [InlineData(30006, DbErrorKind.ResourceBusy)]
    [InlineData(1456, DbErrorKind.ReadOnly)]
    [InlineData(16000, DbErrorKind.ReadOnly)]
    [InlineData(1017, DbErrorKind.InvalidCredentials)]
    [InlineData(28001, DbErrorKind.PasswordExpired)]
    [InlineData(28000, DbErrorKind.AccountLocked)]
    [InlineData(24344, DbErrorKind.CompiledWithErrors)]
    public void Classify_maps_number_to_kind(int number, DbErrorKind expected)
    {
        Assert.Equal(expected, Sut.Classify(number));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(904)]       // invalid identifier — 분류 없음
    [InlineData(1012)]      // 세션 Broken 집합에는 있지만 Disconnected 로는 보지 않는다(복사 실행기 집합 유지).
    [InlineData(12541)]
    public void Classify_unknown_number_is_other(int number)
    {
        Assert.Equal(DbErrorKind.Other, Sut.Classify(number));
    }

    [Fact]
    public void Classifier_is_oracle()
    {
        Assert.Equal(Dbms.Oracle, Sut.Dbms);
    }

    [Fact]
    public void FromException_returns_null_for_non_driver_exception()
    {
        Assert.Null(Sut.FromException(new InvalidOperationException("x", new TimeoutException())));
        Assert.Null(Sut.FromException(new AggregateException(new InvalidOperationException())));
    }

    // 분류기가 쓰는 Kind 는 IsNoAccess 묶음(942·1031 을 함께 보던 곳)과 같은 뜻이어야 한다.
    [Theory]
    [InlineData(942)]
    [InlineData(1031)]
    [InlineData(1039)]
    public void No_access_numbers_are_no_access(int number)
    {
        Assert.True(Sut.Classify(number).IsNoAccess());
    }
}
