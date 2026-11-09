using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;
using Oranda.Core.Execution;
using Oranda.Core.Transfer.Copy;
using Oranda.Oracle.Execution;
using Oranda.Oracle.Transfer.Copy;

namespace Oranda.Oracle.Tests.Transfer.Copy;

// 소유: TP0b-1 커밋 B. 사전 점검을 Core 로 옮기기 전, Oracle 이 넘기는 값 해석·문장·오류 범위가 옮기기 전과 같은지(DB 없음).
public sealed class OracleCopyDialectTests
{
    private static readonly OracleBulkValueCodec Codec = OracleBulkValueCodec.Instance;

    // ---- 값 해석: 예전 CopyCatalogQueries.Text·ToLong 과 같은 글자·값 ----

    [Fact]
    public void Text_keeps_odp_number_text_and_plain_values()
    {
        foreach (var number in new[] { "12345678901234567890.5", "-1234.5", "0", "12345678901234567890123456789012345678", "0.00000000000000000000000000000000012345" })
        {
            var d = OracleDecimal.Parse(number);
            Assert.Equal(d.ToString(), Codec.ToText(d)); // 예전 분기 그대로: ODP.NET 글자(지수·문화권 구분자도 손대지 않음)
        }

        Assert.Null(Codec.ToText(OracleDecimal.Null));
        Assert.Null(Codec.ToText(null));
        Assert.Equal("SCOTT", Codec.ToText("SCOTT"));
        Assert.Equal("42", Codec.ToText(42m));
        Assert.Equal("1.5", Codec.ToText(1.5d)); // invariant
    }

    [Fact]
    public void ToInt64_truncates_and_clamps_like_before()
    {
        Assert.Null(Codec.ToInt64(null));
        Assert.Null(Codec.ToInt64(OracleDecimal.Null));
        Assert.Equal(12, Codec.ToInt64(OracleDecimal.Parse("12.9")));
        Assert.Equal(-12, Codec.ToInt64(OracleDecimal.Parse("-12.9")));
        Assert.Equal(long.MaxValue, Codec.ToInt64(OracleDecimal.Parse("123456789012345678901234567890")));
        Assert.Equal(long.MinValue, Codec.ToInt64(OracleDecimal.Parse("-123456789012345678901234567890")));
        Assert.Equal(900, Codec.ToInt64("900")); // V$PARAMETER.VALUE 는 글자
        Assert.Equal(12, Codec.ToInt64("12.7"));
        Assert.Equal(5, Codec.ToInt64(5));
        Assert.Null(Codec.ToInt64(new object()));
    }

    [Theory]
    [InlineData("VARCHAR2(10 CHAR)", BulkKind.Text)]
    [InlineData("NCHAR", BulkKind.Text)]
    [InlineData("CLOB", BulkKind.Clob)]
    [InlineData("NCLOB", BulkKind.Clob)]
    [InlineData("BLOB", BulkKind.Blob)]
    [InlineData("NUMBER(10,2)", BulkKind.Other)]
    [InlineData("TIMESTAMP(6) WITH TIME ZONE", BulkKind.Other)]
    public void Kind_follows_bulk_bind_types(string serverType, BulkKind kind)
    {
        Assert.True(Codec.IsSupported(serverType));
        Assert.Equal(kind, Codec.KindOf(serverType));
    }

    [Fact]
    public void Unsupported_types_match_bulk_binds_and_key_text_is_failed_row_keys()
    {
        Assert.False(Codec.IsSupported("XMLTYPE"));
        Assert.False(Codec.IsSupported("LONG"));
        Assert.Throws<NotSupportedException>(() => BulkBinds.ToDbType("XMLTYPE"));
        Assert.Throws<NotSupportedException>(() => Codec.KindOf("XMLTYPE"));

        var number = OracleDecimal.Parse("12345678901234567890.5");
        Assert.Equal(FailedRowKeys.ToText(number, "NUMBER"), Codec.ToKeyText(number, "NUMBER"));
        Assert.Equal(FailedRowKeys.FromText("2026-03-05T14:30:15", "DATE"), Codec.FromKeyText("2026-03-05T14:30:15", "DATE"));
        Assert.Equal(FailedRowKeys.BindTypeOf("TIMESTAMP(6)"), Codec.BindTypeOf("TIMESTAMP(6)"));
        Assert.Throws<NotSupportedException>(() => Codec.BindTypeOf("CLOB")); // 키로 쓸 수 없는 형식
    }

    // ---- 문장: 미리 보기(ICopyDialect)와 실행기(CopyStatements)가 같은 글 ----

    [Fact]
    public void Dialect_statements_equal_copy_statements()
    {
        var dialect = OracleCopyDialect.Instance;
        var fk = new DisabledObject(DisabledKind.ForeignKey, "APP", "ORDER_ITEMS", "FK_ITEMS_ORDERS");

        SameStatement(CopyStatements.DisableTrigger("APP", "TRG_ORDERS"), dialect.DisableTrigger("APP", "TRG_ORDERS"));
        SameStatement(CopyStatements.DisableConstraint(fk), dialect.DisableConstraint(fk));
        SameStatement(CopyStatements.DropTable("APP", "ORDERS"), dialect.DropTable("APP", "ORDERS"));
        SameStatement(CopyStatements.NextValues("APP", "SEQ_ORDERS"), dialect.NextValues("APP", "SEQ_ORDERS"));
        Assert.Equal("SELECT \"APP\".\"SEQ_ORDERS\".NEXTVAL FROM DUAL CONNECT BY LEVEL <= :N", dialect.NextValues("APP", "SEQ_ORDERS").Text);

        var delete = new SqlWithBinds("DELETE FROM \"APP\".\"ORDERS\"", new Dictionary<string, BindValue>());
        SameStatement(CopyStatements.Delete(delete), CopyCommonStatements.Delete(delete));
        SameStatement(CopyStatements.Query("SELECT 1 FROM DUAL"), CopyCommonStatements.Query("SELECT 1 FROM DUAL"));
    }

    private static void SameStatement(SqlStatement expected, SqlStatement actual)
    {
        Assert.Equal(expected.Text, actual.Text);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.Verb, actual.Verb);
        Assert.Equal(expected.IsUnfilteredDml, actual.IsUnfilteredDml);
        Assert.Equal(expected.DangerousTarget, actual.DangerousTarget);
        Assert.Equal(expected.ChangeTarget, actual.ChangeTarget);
        Assert.Equal(expected.BindNames, actual.BindNames);
    }

    // ---- 오류 범위: 예전 catch (OracleException) 과 같게 바깥 예외만 ----

    [Fact]
    public async Task Error_filter_catches_only_an_outermost_oracle_exception()
    {
        var errors = new OracleErrorClassifier();
        var direct = await RealOracleExceptionAsync();
        var wrapped = new InvalidOperationException("wrapped", direct);

        Assert.True(CopyErrors.IsServer(errors, direct));
        Assert.NotNull(CopyErrors.Server(errors, direct));

        // 분류기 자체는 안쪽까지 찾지만(FromException), 사전 점검은 예전처럼 감싼 예외를 삼키지 않는다.
        Assert.NotNull(errors.FromException(wrapped));
        Assert.False(CopyErrors.IsServer(errors, wrapped));
        Assert.False(CopyErrors.IsServer(errors, new AggregateException(direct)));

        Assert.False(CopyErrors.IsServer(errors, new OperationCanceledException()));
        Assert.False(CopyErrors.IsNoAccess(errors, new OperationCanceledException()));
    }

    /// <summary>닫힌 포트(127.0.0.1:1)로 여는 실제 ODP.NET 예외(OracleException 은 공개 생성자가 없다).</summary>
    private static async Task<OracleException> RealOracleExceptionAsync()
    {
        await using var conn = new OracleConnection(
            "User Id=u;Password=p;Pooling=false;Connection Timeout=3;"
            + "Data Source=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=127.0.0.1)(PORT=1))(CONNECT_DATA=(SID=X)))");
        return await Assert.ThrowsAsync<OracleException>(() => conn.OpenAsync());
    }
}
