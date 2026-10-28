using Oranda.Core.Execution;
using Oranda.Core.Formatting;
using Oranda.Core.Language;
using Oranda.Language.Lexing;
using Oranda.Language.Parsing;

namespace Oranda.Language.Tests.Lexing;

// 소유: TSK-14-23. CREATE JAVA SOURCE … AS <Java 코드> 는 Java 코드 안의 ; { } 문자열·주석에서 끊지 않고 / 단독 줄까지 한 문장이다(SQL*Plus 와 같다).
public class JavaSourceSplitTests
{
    private static IReadOnlyList<SqlStatement> Split(string sql, SplitOptions? o = null) => StatementSplitter.Split(sql, o ?? SplitOptions.Default);

    private const string Body =
        "public class Hello {\n" +
        "    // a; b; } still a comment\n" +
        "    /* block ; { comment */\n" +
        "    static String s = \"x;y}\";\n" +
        "    static char c = '\\'';\n" +
        "    public static String hi(String n) {\n" +
        "        if (n == null) { return \"none;\"; }\n" +
        "\n" +
        "        return \"Hello \" + n + (n.length() > 3 ? \"!\" : \";\");\n" +
        "    }\n" +
        "}";

    [Fact]
    public void Java_source_runs_to_the_slash_line_as_one_statement()
    {
        var sql = "CREATE OR REPLACE AND COMPILE JAVA SOURCE NAMED \"com/example/Hello\" AS\n" + Body + "\n/\n";
        var r = Split(sql);
        var s = Assert.Single(r);
        Assert.Equal(StatementKind.Ddl, s.Kind);
        Assert.Equal("CREATE", s.Verb);
        Assert.EndsWith("    }\n}", s.Text, StringComparison.Ordinal); // 끝 / 는 빼고 보낸다
        Assert.StartsWith("CREATE OR REPLACE AND COMPILE JAVA SOURCE", s.Text, StringComparison.Ordinal);
        Assert.Empty(s.BindNames); // Java 의 ? : 는 바인드가 아니다
        Assert.Empty(s.SubstitutionNames);
    }

    [Fact]
    public void Blank_lines_inside_java_do_not_split_even_with_blank_line_option()
    {
        var sql = "CREATE JAVA SOURCE NAMED Hello AS\n" + Body + "\n/";
        var r = Split(sql, SplitOptions.Default with { SplitOnBlankLine = true });
        Assert.Single(r);
    }

    [Theory]
    [InlineData("CREATE JAVA SOURCE NAMED Hello AS public class Hello { void f() { int a; } }\n/")]
    [InlineData("create or replace and resolve noforce java source named \"Hello\" as\npublic class Hello { void f() { int a; } }\n/")]
    [InlineData("CREATE OR REPLACE NOFORCE JAVA SOURCE NAMED scott.\"Hello\"\n  AUTHID CURRENT_USER\nAS\npublic class Hello { void f() { int a; } }\n/")]
    public void Java_source_header_variants(string sql)
    {
        var s = Assert.Single(Split(sql));
        Assert.EndsWith("int a; } }", s.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Java_source_mixed_in_a_script()
    {
        var sql = "select 1 from dual;\n" +
                  "CREATE OR REPLACE AND COMPILE JAVA SOURCE NAMED \"com/example/Hello\" AS\n" + Body + "\n/\n" +
                  "ALTER JAVA SOURCE \"com/example/Hello\" COMPILE;\n" +
                  "begin null; end;\n/\n" +
                  "select 2 from dual;";
        var r = Split(sql);
        Assert.Equal(5, r.Count);
        Assert.Equal("select 1 from dual", r[0].Text);
        Assert.EndsWith("}", r[1].Text, StringComparison.Ordinal);
        Assert.Equal("ALTER JAVA SOURCE \"com/example/Hello\" COMPILE", r[2].Text);
        Assert.Equal(StatementKind.PlSql, r[3].Kind);
        Assert.Equal("select 2 from dual", r[4].Text);
    }

    [Fact]
    public void Java_source_without_slash_runs_to_end_of_text()
    {
        var sql = "CREATE JAVA SOURCE NAMED Hello AS\npublic class Hello { int a; }\n\n";
        var s = Assert.Single(Split(sql));
        Assert.EndsWith("int a; }", s.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Caret_inside_java_body_finds_the_java_statement()
    {
        var sql = "select 1 from dual;\nCREATE JAVA SOURCE NAMED Hello AS\npublic class Hello {\n  int a;\n}\n/\nselect 2 from dual;";
        var at = sql.IndexOf("int a", StringComparison.Ordinal);
        var s = StatementSplitter.GetStatementAt(sql, at, SplitOptions.Default);
        Assert.NotNull(s);
        Assert.StartsWith("CREATE JAVA SOURCE", s!.Text, StringComparison.Ordinal);

        var afterSlash = sql.IndexOf("\n/", StringComparison.Ordinal) + 2; // 같은 줄의 / 뒤 커서도 그 문장
        Assert.StartsWith("CREATE JAVA SOURCE", StatementSplitter.GetStatementAt(sql, afterSlash, SplitOptions.Default)!.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Java_source_using_bfile_ends_at_semicolon()
    {
        var r = Split("CREATE JAVA SOURCE NAMED Hello USING BFILE (src_dir, 'Hello.java');\nselect 1 from dual;");
        Assert.Equal(2, r.Count);
        Assert.Equal("CREATE JAVA SOURCE NAMED Hello USING BFILE (src_dir, 'Hello.java')", r[0].Text);
    }

    [Theory]
    [InlineData("CREATE OR REPLACE JAVA CLASS USING BFILE (bin_dir, 'Hello.class');", "CREATE OR REPLACE JAVA CLASS USING BFILE (bin_dir, 'Hello.class')")]
    [InlineData("CREATE JAVA CLASS USING BFILE (bin_dir, 'Hello.class')\n/", "CREATE JAVA CLASS USING BFILE (bin_dir, 'Hello.class')")]
    [InlineData("CREATE AND RESOLVE JAVA RESOURCE NAMED \"com/example/app.properties\" USING BFILE (res_dir, 'app.properties');", "CREATE AND RESOLVE JAVA RESOURCE NAMED \"com/example/app.properties\" USING BFILE (res_dir, 'app.properties')")]
    public void Java_class_and_resource_are_single_statements(string sql, string expected)
    {
        var r = Split(sql + "\nselect 1 from dual;");
        Assert.Equal(2, r.Count);
        Assert.Equal(expected, r[0].Text);
        Assert.Equal(StatementKind.Ddl, r[0].Kind);
    }

    [Fact]
    public void Formatter_keeps_java_source_verbatim()
    {
        var type = typeof(LanguageAssembly).Assembly.GetType("Oranda.Language.Formatting.SqlFormatter")!;
        var formatter = (ISqlFormatter)Activator.CreateInstance(type, nonPublic: true)!;
        var java = "create or replace and compile java source named \"Hello\" as\n" + Body + "\n/";
        var r = formatter.Format("select a,b from t;\n" + java + "\n", new FormatterOptions());
        Assert.True(r.Succeeded, $"{r.Failure}: {r.Message}");
        Assert.Contains(java, r.Text.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void Analyzer_does_not_flag_java_code_as_sql_errors()
    {
        var s = Assert.Single(Split("CREATE OR REPLACE AND COMPILE JAVA SOURCE NAMED \"com/example/Hello\" AS\n" + Body + "\n/"));
        var a = PlSqlAnalyzer.Analyze(s.Text, s.Kind);
        Assert.DoesNotContain(a.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }
}
