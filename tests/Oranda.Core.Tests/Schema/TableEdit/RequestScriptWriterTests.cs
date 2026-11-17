using System.Text;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Text;

namespace Oranda.Core.Tests.Schema.TableEdit;

// 소유: te-m1 (T2). 설계 9.4 의뢰용 스크립트 형식, TE-17(UTF-8 BOM 없음)·TE-22(테이블스페이스 안내).

public class RequestScriptWriterTests
{
    private static readonly RequestScriptHeader Header =
        new("ORDERS 컬럼 추가", new DateOnly(2026, 11, 17), Dbms.Oracle);

    private static SqlStatement Ddl(string text) => new(text, default(TextRange), StatementKind.Ddl);

    private static SqlStatement Of(string text, StatementKind kind) => new(text, default(TextRange), kind);

    [Fact]
    public void Header_has_name_date_dbms_count_and_tablespace_note()
    {
        var text = RequestScriptWriter.Write(Header, [Ddl("ALTER TABLE SAMPLE.ORDERS ADD (MEMO VARCHAR2(20))")], "\n");

        var lines = text.Split('\n');
        Assert.Equal("-- 이름: ORDERS 컬럼 추가", lines[0]);
        Assert.Equal("-- 만든 날짜: 2026-11-17", lines[1]);
        Assert.Equal("-- 대상 DBMS: Oracle", lines[2]);
        Assert.Equal("-- 문장 수: 1", lines[3]);
        Assert.Equal("-- 테이블스페이스를 쓰지 않은 문장은 실행 계정의 기본 테이블스페이스에 만들어진다.", lines[4]);
    }

    [Fact]
    public void Header_for_tibero_names_the_dbms()
    {
        var text = RequestScriptWriter.Write(Header with { Dbms = Dbms.Tibero }, [Ddl("DROP TABLE SAMPLE.T")], "\n");

        Assert.Contains("-- 대상 DBMS: Tibero", text);
    }

    [Fact]
    public void Header_has_no_connection_name_or_host()
    {
        // 머리 주석 글 전체에서 접속을 가리킬 만한 칸이 없다: 줄 접두어가 이름·날짜·DBMS·문장 수·안내뿐.
        var text = RequestScriptWriter.Write(Header, [Ddl("DROP TABLE SAMPLE.T")], "\n");
        var comments = text.Split('\n').Where(l => l.StartsWith("--", StringComparison.Ordinal)).ToList();

        Assert.Equal(5, comments.Count);
        Assert.DoesNotContain(comments, l => l.Contains("접속", StringComparison.Ordinal));
    }

    [Fact]
    public void Ddl_statements_end_with_semicolon_and_are_separated_by_blank_line()
    {
        var text = RequestScriptWriter.Write(
            Header,
            [Ddl("ALTER TABLE SAMPLE.ORDERS ADD (MEMO VARCHAR2(20))"), Ddl("CREATE INDEX SAMPLE.IX_ORDERS_01 ON SAMPLE.ORDERS (MEMO)")],
            "\n");

        Assert.EndsWith(
            "ALTER TABLE SAMPLE.ORDERS ADD (MEMO VARCHAR2(20));\n\nCREATE INDEX SAMPLE.IX_ORDERS_01 ON SAMPLE.ORDERS (MEMO);\n",
            text);
    }

    [Fact]
    public void Trailing_semicolon_in_statement_text_is_not_doubled()
    {
        var text = RequestScriptWriter.Write(Header, [Ddl("DROP TABLE SAMPLE.T; ")], "\n");

        Assert.EndsWith("DROP TABLE SAMPLE.T;\n", text);
        Assert.DoesNotContain(";;", text);
    }

    [Fact]
    public void Plsql_unit_creation_ends_with_slash_line()
    {
        var body = "CREATE OR REPLACE TRIGGER SAMPLE.TRG_ORDERS BEFORE INSERT ON SAMPLE.ORDERS\nBEGIN\n  NULL;\nEND;";
        var text = RequestScriptWriter.Write(Header, [Ddl(body)], "\n");

        Assert.EndsWith("END;\n/\n", text);
        Assert.DoesNotContain("END;;", text);
    }

    [Theory]
    [InlineData("CREATE PROCEDURE SAMPLE.P AS BEGIN NULL; END;")]
    [InlineData("create or replace function SAMPLE.F return number as begin return 1; end;")]
    [InlineData("CREATE OR REPLACE EDITIONABLE PACKAGE SAMPLE.PK AS END;")]
    [InlineData("CREATE OR REPLACE TYPE BODY SAMPLE.TB AS END;")]
    public void Plsql_unit_creation_variants_use_slash(string body)
    {
        var text = RequestScriptWriter.Write(Header, [Ddl(body)], "\n");

        Assert.EndsWith("\n/\n", text);
    }

    [Fact]
    public void Plsql_block_kind_uses_slash()
    {
        var text = RequestScriptWriter.Write(Header, [Of("BEGIN\n  NULL;\nEND;", StatementKind.PlSql)], "\n");

        Assert.EndsWith("END;\n/\n", text);
    }

    [Fact]
    public void Create_table_is_not_mistaken_for_plsql()
    {
        var text = RequestScriptWriter.Write(Header, [Ddl("CREATE TABLE SAMPLE.TYPE_CODES (ID NUMBER)")], "\n");

        Assert.EndsWith("(ID NUMBER);\n", text);
        Assert.DoesNotContain("\n/\n", text);
    }

    [Theory]
    [InlineData(StatementKind.Dml)]
    [InlineData(StatementKind.Query)]
    [InlineData(StatementKind.TransactionControl)]
    [InlineData(StatementKind.SqlPlus)]
    [InlineData(StatementKind.Other)]
    public void Non_ddl_statement_is_rejected(StatementKind kind)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => RequestScriptWriter.Write(Header, [Ddl("DROP TABLE SAMPLE.T"), Of("DELETE FROM SAMPLE.ORDERS", kind)], "\n"));

        Assert.Contains("DDL", ex.Message);
    }

    [Fact]
    public void Newlines_in_statements_follow_the_requested_newline()
    {
        var text = RequestScriptWriter.Write(
            Header, [Ddl("CREATE TABLE SAMPLE.T (\r\n  ID NUMBER,\n  NAME VARCHAR2(10)\r\n)")], "\r\n");

        Assert.DoesNotContain("\n", text.Replace("\r\n", string.Empty));
        Assert.Contains("CREATE TABLE SAMPLE.T (\r\n  ID NUMBER,\r\n  NAME VARCHAR2(10)\r\n);\r\n", text);
    }

    [Fact]
    public void Multiline_name_cannot_break_out_of_the_header_comment()
    {
        var header = Header with { Name = "첫 줄\nDROP TABLE SAMPLE.ORDERS;\r\n끝" };
        var text = RequestScriptWriter.Write(header, [Ddl("DROP TABLE SAMPLE.T")], "\n");

        foreach (var line in text.Split('\n').TakeWhile(l => l.Length > 0))
        {
            Assert.StartsWith("--", line);
        }

        Assert.StartsWith("-- 이름: 첫 줄 DROP TABLE SAMPLE.ORDERS; 끝", text);
    }

    [Fact]
    public void Empty_statement_list_writes_header_with_zero_count()
    {
        var text = RequestScriptWriter.Write(Header, [], "\n");

        Assert.Contains("-- 문장 수: 0", text);
        Assert.EndsWith("\n", text);
    }

    [Fact]
    public void Blank_statement_text_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => RequestScriptWriter.Write(Header, [Ddl("   ")], "\n"));
    }

    [Fact]
    public void Encoding_is_utf8_without_bom()
    {
        Assert.Empty(RequestScriptWriter.Encoding.GetPreamble());
        Assert.IsType<UTF8Encoding>(RequestScriptWriter.Encoding);

        var bytes = RequestScriptWriter.Encoding.GetBytes(RequestScriptWriter.Write(Header, [Ddl("DROP TABLE SAMPLE.T")], "\n"));
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Equal((byte)'-', bytes[0]);
    }

    [Fact]
    public void Default_newline_is_the_environment_newline()
    {
        var text = RequestScriptWriter.Write(Header, [Ddl("DROP TABLE SAMPLE.T")]);

        Assert.Contains(Environment.NewLine, text);
    }
}
