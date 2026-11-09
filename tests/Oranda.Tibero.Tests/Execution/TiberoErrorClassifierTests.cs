using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Tibero.Execution;

namespace Oranda.Tibero.Tests.Execution;

/// <summary>드라이버 예외 모양만 흉내 낸다(형식 이름 <c>TiberoException</c> + 공개 속성 <c>Number</c>).</summary>
#pragma warning disable CA1032, CA1064, CA1711 // 이름이 드라이버와 같아야 찾는다.
internal sealed class TiberoException(string message, int number) : Exception(message)
{
    public int Number { get; } = number;
}
#pragma warning restore CA1032, CA1064, CA1711

public class TiberoErrorClassifierTests
{
    private readonly TiberoErrorClassifier _classifier = new();

    [Theory]
    [InlineData(12040, DbErrorKind.Cancelled)]
    [InlineData(12032, DbErrorKind.Deadlock)]
    [InlineData(12001, DbErrorKind.Disconnected)]
    [InlineData(12002, DbErrorKind.Disconnected)]
    [InlineData(10007, DbErrorKind.ConstraintViolation)]
    [InlineData(10008, DbErrorKind.ConstraintViolation)]
    [InlineData(10009, DbErrorKind.ConstraintViolation)]
    [InlineData(10005, DbErrorKind.ConstraintViolation)]
    [InlineData(10006, DbErrorKind.ConstraintViolation)]
    [InlineData(4005, DbErrorKind.ConstraintViolation)]
    [InlineData(4006, DbErrorKind.ConstraintViolation)]
    [InlineData(12094, DbErrorKind.Disconnected)]
    [InlineData(2069, DbErrorKind.Disconnected)]
    [InlineData(8033, DbErrorKind.ObjectNotFound)]
    [InlineData(8053, DbErrorKind.InsufficientPrivileges)]
    [InlineData(12033, DbErrorKind.ResourceBusy)]
    [InlineData(12034, DbErrorKind.ResourceBusy)]
    [InlineData(12022, DbErrorKind.InvalidCredentials)]
    public void Classifies_known_numbers(int number, DbErrorKind expected)
    {
        Assert.Equal(expected, _classifier.Classify(number));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(942)] // Oracle 번호는 Tibero 분류가 아니다.
    [InlineData(int.MinValue)]
    public void Unknown_numbers_are_other(int number)
    {
        Assert.Equal(DbErrorKind.Other, _classifier.Classify(number));
    }

    [Fact]
    public void Sign_of_the_number_is_ignored()
    {
        Assert.Equal(DbErrorKind.ConstraintViolation, _classifier.Classify(-10007));
    }

    [Theory]
    [InlineData("TBR-10007: Unique constraint violation", 10007)]
    [InlineData("  error TBR-8033 (object not found) TBR-1", 8033)]
    [InlineData("ODP-8033:Specified schema object was not found.   \nat line 1, column 16 of null:\nSELECT * FROM T\n ^", 8033)] // 실측(TDP.NET 1.1.35, 서버 6)
    [InlineData("ODP-15146:PSM compilation error.   \nODP-15002:Parsing failed.", 15146)]
    public void Parses_the_first_code_in_a_message(string text, int expected)
    {
        Assert.True(TiberoErrorClassifier.TryParseCode(text, out var number));
        Assert.Equal(expected, number);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("ORA-00942: table or view does not exist")]
    [InlineData("TBR-")]
    [InlineData("XTBR-123")]
    [InlineData("TBR-1234567890")]
    public void Message_without_a_code_does_not_parse(string? text)
    {
        Assert.False(TiberoErrorClassifier.TryParseCode(text, out _));
    }

    [Fact]
    public void FromException_fills_code_number_dbms_and_kind_from_the_driver_exception()
    {
        var error = _classifier.FromException(new TiberoException("TBR-10007: Unique constraint violation", -10007));

        Assert.NotNull(error);
        Assert.Equal("TBR-10007", error.Code);
        Assert.Equal(-10007, error.Number);
        Assert.Equal(Dbms.Tibero, error.Dbms);
        Assert.Equal(DbErrorKind.ConstraintViolation, error.Kind);
    }

    [Fact]
    public void FromException_matches_the_measured_driver_shape_for_cancel()
    {
        // 실측: 실행 중 Cancel()·CommandTimeout 모두 Number=-12040, Message="ODP-12040:Statement canceled.   ".
        var error = _classifier.FromException(new TiberoException("ODP-12040:Statement canceled.   ", -12040));

        Assert.NotNull(error);
        Assert.Equal("TBR-12040", error.Code);
        Assert.Equal(-12040, error.Number);
        Assert.Equal(DbErrorKind.Cancelled, error.Kind);
    }

    [Fact]
    public void FromException_falls_back_to_the_number_in_the_message()
    {
        var error = _classifier.FromException(new TiberoException("TBR-12040: cancelled", 0));

        Assert.NotNull(error);
        Assert.Equal("TBR-12040", error.Code);
        Assert.Equal(DbErrorKind.Cancelled, error.Kind);
    }

    [Fact]
    public void FromException_finds_the_driver_exception_inside_wrappers()
    {
        var inner = new TiberoException("TBR-12032: deadlock", 12032);

        Assert.Equal(DbErrorKind.Deadlock, _classifier.FromException(new InvalidOperationException("x", inner))!.Kind);
        Assert.Equal(DbErrorKind.Deadlock, _classifier.FromException(new AggregateException(new IOException("y"), inner))!.Kind);
    }

    [Fact]
    public void FromException_with_unreadable_number_and_no_code_keeps_the_message()
    {
        var error = _classifier.FromException(new TiberoException("no code here", int.MinValue));

        Assert.NotNull(error);
        Assert.Equal("TBR", error.Code);
        Assert.Equal(DbErrorKind.Other, error.Kind);
        Assert.Equal("no code here", error.Message);
    }

    [Fact]
    public void FromException_returns_null_for_other_exceptions()
    {
        Assert.Null(_classifier.FromException(new InvalidOperationException("TBR-10007 in a plain exception")));
    }

    [Fact]
    public void It_serves_tibero()
    {
        Assert.Equal(Dbms.Tibero, _classifier.Dbms);
    }
}
