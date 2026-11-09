using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Language.Errors;

namespace Oranda.Language.Tests.Errors;

/// <summary>정적 <see cref="DbErrorClassification.Selector"/> 를 바꾸므로 다른 시험과 같이 돌리지 않는다.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DbErrorClassificationSerial
{
    public const string Name = "DbErrorClassification";
}

/// <summary>
/// 예외·글 → DbErrorKind (A4). Selector 가 없을 때(Oracle 한정 안전판, 지금과 같은 번호 판정)와 있을 때(공급자 분류기) 둘 다 고정한다.
/// </summary>
[Collection(DbErrorClassificationSerial.Name)]
public sealed class DbErrorClassificationTests : IDisposable
{
    private readonly IDbmsServiceSelector? _before = DbErrorClassification.Selector;

    public void Dispose() => DbErrorClassification.Selector = _before;

    // ---------- Selector 없음: Oracle 번호 판정이 그대로 ----------

    [Theory]
    [InlineData("ORA-00942: table or view does not exist", true)]
    [InlineData("ORA-01031: insufficient privileges", true)]
    [InlineData("ORA-00001: unique constraint violated", false)]
    [InlineData("연결 실패", false)]
    public void Without_selector_no_access_follows_the_old_ora_numbers(string message, bool expected)
    {
        DbErrorClassification.Selector = null;

        Assert.Equal(expected, DbErrorClassification.IsNoAccess(new InvalidOperationException(message)));
    }

    [Theory]
    [InlineData("ORA-03113: end-of-file on communication channel", true)]
    [InlineData("ORA-03114: not connected to ORACLE", true)]
    [InlineData("ORA-03135: connection lost contact", true)]
    [InlineData("ORA-00028: your session has been killed", true)]
    [InlineData("ORA-00942: table or view does not exist", false)]
    public void Without_selector_disconnect_follows_the_old_ora_numbers(string message, bool expected)
    {
        DbErrorClassification.Selector = null;

        Assert.Equal(expected, DbErrorClassification.IsDisconnected(new InvalidOperationException(message)));
    }

    [Theory]
    [InlineData("ORA-01456: may not perform insert/delete/update operation inside a READ ONLY transaction", true)]
    [InlineData("ORA-01031: insufficient privileges", true)]
    [InlineData("ORA-16000: database or pluggable database open for read-only access", true)]
    [InlineData("ORA-00904: invalid identifier", false)]
    public void Without_selector_environment_refusal_follows_the_old_ora_numbers(string message, bool expected)
    {
        DbErrorClassification.Selector = null;

        Assert.Equal(expected, DbErrorClassification.IsEnvironmentRefusal(new InvalidOperationException(message)));
    }

    [Fact]
    public void Without_selector_the_whole_exception_chain_is_read()
    {
        DbErrorClassification.Selector = null;
        var wrapped = new InvalidOperationException("조회 실패", new InvalidOperationException("ORA-00942: table or view does not exist"));

        Assert.True(DbErrorClassification.IsNoAccess(wrapped));
    }

    [Fact]
    public void Without_selector_message_text_with_24344_is_compiled_with_errors()
    {
        DbErrorClassification.Selector = null;

        Assert.True(DbErrorClassification.HasInMessage("ORA-24344 success with compilation error", DbErrorKind.CompiledWithErrors));
        Assert.False(DbErrorClassification.HasInMessage("ORA-00942 x", DbErrorKind.CompiledWithErrors));
    }

    // ---------- Selector 있음: 공급자 분류기가 정한다 ----------

    [Fact]
    public void With_selector_the_provider_table_decides()
    {
        DbErrorClassification.Selector = Selector();

        // 가짜 Oracle 표: 1039 도 권한 부족(안전판에는 없는 번호), 8033 은 Tibero 표.
        Assert.True(DbErrorClassification.IsNoAccess(new InvalidOperationException("ORA-01039: insufficient privileges on underlying objects")));
        Assert.True(DbErrorClassification.IsNoAccess(new InvalidOperationException("TBR-8033: object does not exist")));
        Assert.False(DbErrorClassification.IsNoAccess(new InvalidOperationException("TBR-10007: unique constraint")));
        Assert.True(DbErrorClassification.Has(new InvalidOperationException("TBR-10007: unique constraint"), DbErrorKind.ConstraintViolation));
    }

    [Fact]
    public void With_selector_a_driver_exception_is_classified_by_its_provider()
    {
        DbErrorClassification.Selector = Selector();

        Assert.True(DbErrorClassification.IsDisconnected(new FakeDriverException(3113)));
        Assert.False(DbErrorClassification.IsDisconnected(new FakeDriverException(942)));
    }

    [Fact]
    public void With_selector_an_oracle_number_is_not_read_by_the_tibero_table()
    {
        DbErrorClassification.Selector = Selector();

        // ORA-08033 은 Tibero 번호(8033 = 객체 없음)와 숫자가 같지만 접두가 다르다.
        Assert.False(DbErrorClassification.IsNoAccess(new InvalidOperationException("ORA-08033: some unrelated oracle error")));
    }

    // ---------- 바깥 메시지만 / 맨 앞 번호 (예전 비교와 같은 범위) ----------

    [Fact]
    public void Top_only_checks_ignore_the_inner_exception()
    {
        DbErrorClassification.Selector = null;
        var wrapped = new InvalidOperationException("조회 실패", new InvalidOperationException("ORA-00942: table or view does not exist"));

        Assert.False(DbErrorClassification.IsNoAccessTop(wrapped));
        Assert.True(DbErrorClassification.IsNoAccessTop(new InvalidOperationException("ORA-00942: x")));
    }

    [Fact]
    public void Leading_kind_reads_only_the_first_code_of_the_outer_message()
    {
        DbErrorClassification.Selector = null;

        Assert.Equal(DbErrorKind.InsufficientPrivileges, DbErrorClassification.LeadingKindOf(new InvalidOperationException("ORA-01031: insufficient privileges")));
        Assert.Equal(DbErrorKind.Other, DbErrorClassification.LeadingKindOf(new InvalidOperationException("ORA-00604: recursive SQL error\nORA-01031: insufficient privileges")));
        Assert.Equal(DbErrorKind.Other, DbErrorClassification.LeadingKindOf(new InvalidOperationException("조회 실패", new InvalidOperationException("ORA-01031: x"))));
    }

    [Fact]
    public void CodeOfKind_returns_the_code_that_caused_the_kind()
    {
        DbErrorClassification.Selector = null;
        var ex = new InvalidOperationException("ORA-00604: recursive SQL error\nORA-01456: read only transaction");

        Assert.Equal("ORA-01456", DbErrorClassification.CodeOfKind(ex, DbErrorKind.ReadOnly));
        Assert.Null(DbErrorClassification.CodeOfKind(ex, DbErrorKind.Deadlock));
        Assert.True(DbErrorClassification.IsReadOnly(ex));
    }

    [Fact]
    public void Aggregate_inner_exceptions_are_read()
    {
        DbErrorClassification.Selector = null;
        var agg = new AggregateException(new InvalidOperationException("x"), new InvalidOperationException("ORA-03114: not connected"));

        Assert.True(DbErrorClassification.IsDisconnected(agg));
    }

    [Fact]
    public void With_selector_that_has_one_dbms_the_other_prefix_is_not_classified()
    {
        DbErrorClassification.Selector = new DbmsServiceTable().Add<IDbErrorClassifier>(Dbms.Tibero, () => new FakeClassifier(Dbms.Tibero));

        Assert.True(DbErrorClassification.IsNoAccess(new InvalidOperationException("TBR-8033: x")));
        Assert.False(DbErrorClassification.IsNoAccess(new InvalidOperationException("ORA-00942: x")));
    }

    [Fact]
    public void With_selector_leading_kind_uses_the_driver_error_first()
    {
        DbErrorClassification.Selector = Selector();

        Assert.Equal(DbErrorKind.Disconnected, DbErrorClassification.LeadingKindOf(new FakeDriverException(3113)));
    }

    // ---------- 코드 표기 ----------

    [Theory]
    [InlineData("ORA-00942: table or view does not exist", "ORA-00942")]
    [InlineData("잠금 읽기 실패 (ORA-01031)", "ORA-01031")]
    [InlineData("TBR-8033: no such object", "TBR-8033")]
    [InlineData("XORA-00942: not a code", null)]
    [InlineData("TBR-8033 before ORA-00942", "TBR-8033")]
    [InlineData("plain", null)]
    public void CodeOf_finds_the_first_server_code(string message, string? expected)
    {
        Assert.Equal(expected, DbErrorClassification.CodeOf(message));
    }

    [Theory]
    [InlineData(Dbms.Oracle, 942, "ORA-00942")]
    [InlineData(Dbms.Oracle, 12541, "ORA-12541")]
    [InlineData(Dbms.Tibero, 8033, "TBR-8033")]
    [InlineData(Dbms.Tibero, -10007, "TBR-10007")]
    public void Display_uses_the_dbms_notation(Dbms dbms, int number, string expected)
    {
        Assert.Equal(expected, DbErrorCodes.Display(dbms, number));
    }

    private static DbmsServiceTable Selector() =>
        new DbmsServiceTable()
            .Add<IDbErrorClassifier>(Dbms.Oracle, () => new FakeClassifier(Dbms.Oracle))
            .Add<IDbErrorClassifier>(Dbms.Tibero, () => new FakeClassifier(Dbms.Tibero));

    private sealed class FakeDriverException(int number) : Exception("driver")
    {
        public int Number { get; } = number;
    }

    private sealed class FakeClassifier(Dbms dbms) : IDbErrorClassifier
    {
        public Dbms Dbms { get; } = dbms;

        public DbErrorKind Classify(int number) => (Dbms, number) switch
        {
            (Dbms.Oracle, 942) => DbErrorKind.ObjectNotFound,
            (Dbms.Oracle, 1031 or 1039) => DbErrorKind.InsufficientPrivileges,
            (Dbms.Oracle, 3113) => DbErrorKind.Disconnected,
            (Dbms.Tibero, 8033) => DbErrorKind.ObjectNotFound,
            (Dbms.Tibero, 10007) => DbErrorKind.ConstraintViolation,
            _ => DbErrorKind.Other,
        };

        public DbError? FromException(Exception exception) =>
            Dbms == Dbms.Oracle && exception is FakeDriverException f
                ? new DbError($"ORA-{f.Number:D5}", f.Message) { Number = f.Number, Dbms = Dbms.Oracle, Kind = Classify(f.Number) }
                : null;
    }
}
