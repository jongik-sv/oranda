using Oranda.Language.Redaction;

namespace Oranda.Language.Tests.Redaction;

// 소유: TSK-06-06. 비밀 가림 규칙(data-model 5.3).
public class SecretRedactorTests
{
    private static readonly SecretRedactor Redactor = new();

    [Theory]
    [InlineData("ALTER USER a IDENTIFIED BY \"Pw1\"", "ALTER USER a IDENTIFIED BY ***")]
    [InlineData("alter user a identified by Pw1#x", "alter user a identified by ***")]
    [InlineData("CREATE USER a IDENTIFIED BY 9abc_d QUOTA UNLIMITED ON users", "CREATE USER a IDENTIFIED BY *** QUOTA UNLIMITED ON users")]
    [InlineData("ALTER USER a IDENTIFIED BY 'x y'", "ALTER USER a IDENTIFIED BY ***")]
    [InlineData("CREATE USER a IDENTIFIED BY VALUES 'S:ABCDEF'", "CREATE USER a IDENTIFIED BY VALUES ***")]
    [InlineData("ALTER USER a IDENTIFIED BY new1 REPLACE old1", "ALTER USER a IDENTIFIED BY *** REPLACE ***")]
    [InlineData("CREATE DATABASE LINK l CONNECT TO u IDENTIFIED BY \"p\" USING 'db'", "CREATE DATABASE LINK l CONNECT TO u IDENTIFIED BY *** USING 'db'")]
    public void Identified_by_is_masked(string input, string expected)
        => Assert.Equal(expected, Redactor.Redact(input));

    [Theory]
    [InlineData("CONNECT scott/tiger@db", "CONNECT scott/***@db")]
    [InlineData("conn scott/tiger", "conn scott/***")]
    [InlineData("CONNECT scott/\"p@ss\"@//h:1521/s", "CONNECT scott/***@//h:1521/s")]
    [InlineData("CONNECT scott@db", "CONNECT scott@db")]
    [InlineData("CONNECT / AS SYSDBA", "CONNECT / AS SYSDBA")]
    public void Sqlplus_connect_is_masked(string input, string expected)
        => Assert.Equal(expected, Redactor.Redact(input));

    [Fact]
    public void Password_command_argument_is_masked()
        => Assert.Equal("PASSWORD ***", Redactor.Redact("PASSWORD scott"));

    [Theory]
    [InlineData("SELECT password FROM users")]
    [InlineData("CREATE OR REPLACE VIEW v AS SELECT 1 FROM dual")]
    [InlineData("SELECT * FROM emp CONNECT BY PRIOR id = parent")]
    [InlineData("SELECT * FROM emp\nCONNECT BY PRIOR id = parent")]
    [InlineData("SELECT 'IDENTIFIED BY secret' FROM dual")]
    [InlineData("-- ALTER USER a IDENTIFIED BY secret\nSELECT 1 FROM dual")]
    [InlineData("/* IDENTIFIED BY secret */ SELECT 1 FROM dual")]
    public void Other_text_is_untouched(string input)
        => Assert.Equal(input, Redactor.Redact(input));

    [Fact]
    public void Masks_only_the_secret_in_a_script()
    {
        var input = "SELECT 1 FROM dual;\nALTER USER a IDENTIFIED BY Secret9;\nSELECT 2 FROM dual;";
        var output = Redactor.Redact(input);

        Assert.DoesNotContain("Secret9", output, StringComparison.Ordinal);
        Assert.Contains("SELECT 2 FROM dual;", output, StringComparison.Ordinal);
        Assert.Contains("IDENTIFIED BY ***;", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Replace_after_a_previous_statement_is_not_masked()
        => Assert.Equal("ALTER USER a IDENTIFIED BY ***;\nCREATE OR REPLACE VIEW v AS SELECT 1 FROM dual",
            Redactor.Redact("ALTER USER a IDENTIFIED BY x;\nCREATE OR REPLACE VIEW v AS SELECT 1 FROM dual"));

    [Theory]
    [InlineData("password", true)]
    [InlineData("P_PWD", true)]
    [InlineData("api_Secret", true)]
    [InlineData("tokenValue", true)]
    [InlineData("userid", false)]
    [InlineData("", false)]
    public void Sensitive_bind_names(string name, bool expected)
        => Assert.Equal(expected, Redactor.IsSensitiveBindName(name));
}
