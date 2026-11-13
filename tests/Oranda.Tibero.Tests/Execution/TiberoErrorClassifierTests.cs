using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Tests.Driver;

namespace Oranda.Tibero.Tests.Execution;

public class TiberoErrorClassifierTests
{
    private readonly TiberoErrorClassifier _classifier = new();

    // 드라이버 예외는 가짜 드라이버(같은 형식 이름·모양, 실측 docs/design/tibero-driver-session.md 1장)로 만든다 — 분류기는 어댑터로 알아본다.
    private static Exception Server(string message, int number) => FakeDriverHost.ServerError(number, message);

    private static Exception Client(int code, string message) => FakeDriverHost.ClientError(code, message);

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
    [InlineData(-17004, DbErrorKind.InsufficientPrivileges)] // 일반 계정의 KILL SESSION
    [InlineData(12033, DbErrorKind.ResourceBusy)]
    [InlineData(12034, DbErrorKind.ResourceBusy)]
    [InlineData(12022, DbErrorKind.InvalidCredentials)]
    [InlineData(-21003, DbErrorKind.SnapshotTooOld)] // RB-13 실측: 시점 데이터 잃음·시점 뒤 DDL
    [InlineData(-21034, DbErrorKind.SnapshotTooOld)] // RB-13 실측: 아주 옛 TSN
    [InlineData(-90603, DbErrorKind.Disconnected)] // 실측: 닫힌 연결에 명령
    [InlineData(-90403, DbErrorKind.Disconnected)]
    [InlineData(-590729, DbErrorKind.Disconnected)]
    public void Classifies_known_numbers(int number, DbErrorKind expected)
    {
        Assert.Equal(expected, _classifier.Classify(number));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(942)] // Oracle 번호는 Tibero 분류가 아니다.
    [InlineData(-90401)] // 접속 거부 = 접속 실패지 끊김이 아니다(Oracle 12541 과 같은 취급).
    [InlineData(-12079)] // DB 이름 불일치
    [InlineData(int.MinValue)]
    public void Unknown_or_connect_failure_numbers_are_other(int number)
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
    public void Create_fills_code_number_dbms_and_kind_and_trims_line_ends()
    {
        // 실측 모양: Number 음수, 메시지 줄 끝에 공백.
        var error = TiberoErrorClassifier.Create(-12040, "ODP-12040:Statement canceled.   ");

        Assert.Equal("TBR-12040", error.Code);
        Assert.Equal(-12040, error.Number);
        Assert.Equal(Dbms.Tibero, error.Dbms);
        Assert.Equal(DbErrorKind.Cancelled, error.Kind);
        Assert.Equal("ODP-12040:Statement canceled.", error.Message);
    }

    [Fact]
    public void Create_falls_back_to_the_number_in_the_message()
    {
        var error = TiberoErrorClassifier.Create(0, "ODP-90603:Invalid operation: disconnected from the server.");

        Assert.Equal("TBR-90603", error.Code);
        Assert.Equal(DbErrorKind.Disconnected, error.Kind);
    }

    [Fact]
    public void Create_without_number_or_code_keeps_the_message()
    {
        var error = TiberoErrorClassifier.Create(int.MinValue, "no code here");

        Assert.Equal("TBR", error.Code);
        Assert.Equal(DbErrorKind.Other, error.Kind);
        Assert.Equal("no code here", error.Message);
    }

    // 메시지는 6.2.15 실측 원문(docs/design/tibero-driver-session.md 6장).
    [Theory]
    [InlineData("SELEC 1 FROM DUAL", "ODP-15146:PSM compilation error.   \nODP-15002:Parsing failed.   \nat line 1, column 1 of null:\nSELEC 1 FROM DUAL\n^^^^^", 0, 1, 1)]
    [InlineData("SELECT a,\n  b\nFROM DUAL WHERE", "ODP-8007:Missing boolean expression.   \nat line 3, column 11 of null:\nFROM DUAL WHERE\n          ^", 24, 3, 11)]
    [InlineData("SELECT *\n  FROM ORT_NO_SUCH_TABLE_XYZ", "ODP-8033:Specified schema object was not found.   \nat line 2, column 9 of null:\n  FROM ORT_NO_SUCH_TABLE_XYZ\n       ^", 16, 2, 8)]
    [InlineData("BEGIN\n  x := 1;\nEND;", "ODP-15146:PSM compilation error.   \nODP-15044:Undefined identifier 'X'.   \nat line 2, column 3 of null:\n  x := 1;\n  ^", 8, 2, 3)]
    [InlineData("DECLARE n NUMBER; BEGIN\n  n := 1/0;\nEND;", "ODP-5070:Divide by zero error.   \nTBR-15163: Unhandled exception at  line 2.   ", 24, 2, 1)]
    [InlineData("SELECT 1\r\n  FROM NOPE", "ODP-8033:Specified schema object was not found.   \r\nat line 2, column 9 of null:\r\n  FROM NOPE\r\n       ^", 17, 2, 8)]
    [InlineData("SELECT x FROM DUAL", "ODP-8026:Invalid identifier.   \nat line 1, column 8 of null:", 7, 1, 8)] // ^ 줄 없음 → 보고된 열
    public void Locate_maps_the_message_position_onto_the_statement(string text, string message, int offset, int line, int column)
    {
        Assert.Equal((offset, line, column), TiberoErrorClassifier.Locate(message, text));
        Assert.Equal(text[offset], text.Split('\n')[line - 1].TrimEnd('\r')[column - 1]);
    }

    [Theory]
    [InlineData("SELECT 1/0 FROM DUAL", "ODP-5070:Divide by zero error.   ")] // 위치 없음
    [InlineData("SELECT 1 FROM DUAL", "ODP-8007:x.\nat line 5, column 1 of null:\nfoo\n^")] // 원문보다 긴 줄 번호
    [InlineData("SELECT 1 FROM DUAL", "ODP-8007:x.\nat line 1, column 99 of null:")] // 원문 밖 열
    [InlineData("SELECT 1\nFROM X", "ODP-8007:x.\nat line 1, column 12 of null:")] // 줄 끝을 넘는 열(원문 안이지만 다음 줄)
    [InlineData("SELECT 1\r\nFROM X", "ODP-8007:x.\nat line 1, column 10 of null:")] // \r 은 줄에 넣지 않는다
    [InlineData("BEGIN p; END;", "ODP-8033:x.\nat line 3, column 5 of SCOTT.P:\n  y := 1;\n    ^")] // 저장 객체 안 위치
    [InlineData("BEGIN p; END;", "ODP-5070:x.\nTBR-15163: Unhandled exception at SCOTT.P line 3.")]
    public void Locate_without_a_statement_position_is_empty(string text, string message)
    {
        Assert.Equal((null, null, null), TiberoErrorClassifier.Locate(message, text));
    }

    [Fact]
    public void Locate_accepts_the_position_just_past_the_end_of_a_line()
    {
        // 「WHERE 뒤 식 없음」처럼 줄 끝 바로 뒤를 가리키는 위치는 받는다(다음 줄 첫 글자로 넘어가지는 않는다).
        Assert.Equal((24, 1, 25), TiberoErrorClassifier.Locate("ODP-8007:x.\nat line 1, column 25 of null:", "SELECT 1 FROM DUAL WHERE\nX"));
    }

    [Fact]
    public void Plain_exceptions_are_not_driver_errors()
    {
        var plain = new InvalidOperationException("TBR-10007 in a plain exception", new IOException("ODP-90603"));

        Assert.Null(_classifier.FromException(plain));
        Assert.Null(TiberoErrorClassifier.ToDbError(plain, "SELECT 1 FROM DUAL"));
        Assert.False(TiberoErrorClassifier.IsDriverException(plain));
    }

    [Fact]
    public void It_serves_tibero()
    {
        Assert.Equal(Dbms.Tibero, _classifier.Dbms);
    }

    [Fact]
    public void FromException_reads_the_server_exception()
    {
        // 실측: Number 음수, 메시지 머리말 ODP-.
        var error = _classifier.FromException(Server("ODP-12040:Statement canceled.   ", -12040));

        Assert.NotNull(error);
        Assert.Equal("TBR-12040", error.Code);
        Assert.Equal(-12040, error.Number);
        Assert.Equal(Dbms.Tibero, error.Dbms);
        Assert.Equal(DbErrorKind.Cancelled, error.Kind);
    }

    [Fact]
    public void FromException_reads_the_driver_side_exception()
    {
        // 실측: 닫힌 연결에 명령 → tbcomm.err.TbOdpException(System.Exception 바탕), ErrorCode=-90603, Number 속성 없음.
        var ex = Client(-90603, "ODP-90603:Invalid operation: disconnected from the server.");

        var error = _classifier.FromException(ex);

        Assert.NotNull(error);
        Assert.Equal("TBR-90603", error.Code);
        Assert.Equal(-90603, error.Number);
        Assert.Equal(DbErrorKind.Disconnected, error.Kind);
        Assert.True(TiberoErrorClassifier.IsDriverException(ex));
    }

    [Fact]
    public void FromException_finds_the_driver_exception_inside_wrappers()
    {
        var inner = Client(-90403, "ODP-90403:broken");

        Assert.Equal(DbErrorKind.Disconnected, _classifier.FromException(new InvalidOperationException("x", inner))!.Kind);
        Assert.Equal(DbErrorKind.Disconnected, _classifier.FromException(new AggregateException(new IOException("y"), inner))!.Kind);
    }

    [Fact]
    public void ToDbError_adds_the_statement_position()
    {
        const string text = "SELECT *\n  FROM ORT_NO_SUCH_TABLE_XYZ";
        var ex = Server("ODP-8033:Specified schema object was not found.   \nat line 2, column 9 of null:\n  FROM ORT_NO_SUCH_TABLE_XYZ\n       ^", -8033);

        var error = TiberoErrorClassifier.ToDbError(ex, text);

        Assert.NotNull(error);
        Assert.Equal(DbErrorKind.ObjectNotFound, error.Kind);
        Assert.Equal((16, 2, 8), (error.Offset, error.Line, error.Column));
    }

    [Fact]
    public void Unreadable_number_falls_back_to_the_message()
    {
        var error = _classifier.FromException(Server("TBR-12032: deadlock", int.MinValue));

        Assert.NotNull(error);
        Assert.Equal(DbErrorKind.Other, error.Kind); // int.MinValue 는 번호로 보지 않는다
        Assert.Equal(DbErrorKind.Deadlock, _classifier.FromException(Server("TBR-12032: deadlock", 0))!.Kind);
    }
}
