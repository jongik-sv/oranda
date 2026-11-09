using Oranda.Core.Connection;
using Oranda.Core.Execution;

namespace Oranda.Core.Tests.Execution;

// 소유: tb-core C2(D-046). DbError 의 DBMS·분류 칸과 분류기 계약.

public class DbErrorTests
{
    [Fact]
    public void Existing_construction_reads_as_oracle_and_unclassified()
    {
        var e = new DbError("ORA-00942", "table or view does not exist") { Number = 942 };

        Assert.Equal(Dbms.Oracle, e.Dbms);
        Assert.Equal(DbErrorKind.Other, e.Kind);
        Assert.Equal(DbErrorKind.Other, default(DbErrorKind)); // 채우지 않은 값 = 분류 없음
    }

    [Fact]
    public void Classifier_fills_dbms_and_kind_from_a_driver_exception()
    {
        IDbErrorClassifier classifier = new FakeTiberoClassifier();

        var error = classifier.FromException(new FakeDriverException(-17001, "login failed"));

        Assert.NotNull(error);
        Assert.Equal("TBR-17001", error.Code);
        Assert.Equal(Dbms.Tibero, error.Dbms);
        Assert.Equal(DbErrorKind.InvalidCredentials, error.Kind);
        Assert.Null(classifier.FromException(new InvalidOperationException("not a driver error")));
    }

    [Fact]
    public void No_access_groups_missing_object_and_missing_privilege()
    {
        Assert.True(DbErrorKind.ObjectNotFound.IsNoAccess());
        Assert.True(DbErrorKind.InsufficientPrivileges.IsNoAccess());
        Assert.False(DbErrorKind.Disconnected.IsNoAccess());
        Assert.True(new DbError("ORA-01031", "insufficient privileges") { Kind = DbErrorKind.InsufficientPrivileges }.IsNoAccess());
        Assert.False(((DbError?)null).IsNoAccess());
    }

    private sealed class FakeDriverException(int number, string message) : Exception(message)
    {
        public int Number { get; } = number;
    }

    private sealed class FakeTiberoClassifier : IDbErrorClassifier
    {
        public Dbms Dbms => Dbms.Tibero;

        public DbErrorKind Classify(int number) => number == -17001 ? DbErrorKind.InvalidCredentials : DbErrorKind.Other;

        public DbError? FromException(Exception exception) => exception is FakeDriverException d
            ? new DbError($"TBR-{-d.Number}", d.Message) { Number = d.Number, Dbms = Dbms, Kind = Classify(d.Number) }
            : null;
    }
}
