using Oranda.Core.Connection;
using Oranda.Core.Rollback;
using Oranda.Oracle.Rollback;

namespace Oranda.Oracle.Tests.Rollback;

// 소유: rb-m2-oracle RB-10. Oracle 되돌리기 방언의 고정 값과 Oracle 계열 공용 규칙(RB-01) 위임. 리터럴 왕복은 RB-12 통합 시험.
public class OracleRollbackDialectTests
{
    private static readonly OracleRollbackDialect Dialect = OracleRollbackDialect.Instance;

    private static ServerInfo Server(string version) =>
        new(version, "FREE", "localhost", "FREEPDB1", "FREE", "FREEPDB1", true, 1, new ServerCapabilities(false, true, true, false, false));

    [Fact]
    public void Fixed_values_follow_design_7_1()
    {
        Assert.Equal(Dbms.Oracle, Dialect.Dbms);
        Assert.Equal("SCN", Dialect.PointName);
        Assert.Equal("ORANDA$RB", Dialect.SavepointName);
        Assert.True(Dialect.SupportsReturningRowId);
        Assert.Equal("AS OF SCN 48213907", Dialect.AsOf(48_213_907));
        Assert.Equal(
            ["SELECT DBMS_FLASHBACK.GET_SYSTEM_CHANGE_NUMBER FROM DUAL", "SELECT TIMESTAMP_TO_SCN(SYSTIMESTAMP) FROM DUAL"],
            Dialect.PointQueries);
    }

    [Theory]
    [InlineData("11.2.0.4.0", 30)]
    [InlineData("12.1.0.2.0", 30)]
    [InlineData("12.2.0.1.0", 128)]
    [InlineData("19.0.0.0.0", 128)]
    [InlineData("23.26.0.0.0", 128)]
    [InlineData("?", 30)]
    public void Identifier_limit_is_long_from_12_2(string version, int bytes) =>
        Assert.Equal(bytes, Dialect.MaxIdentifierBytes(Server(version)));

    [Fact]
    public void Side_effects_query_binds_owner_and_name_and_lists_triggers_and_cascades()
    {
        var sql = Dialect.SideEffectsSql!;
        Assert.Contains(":owner", sql, StringComparison.Ordinal);
        Assert.Contains(":name", sql, StringComparison.Ordinal);
        Assert.Contains("ALL_TRIGGERS", sql, StringComparison.Ordinal);
        Assert.Contains("'CASCADE', 'SET NULL'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("-- id:", sql, StringComparison.Ordinal); // 머리 주석은 카탈로그가 뗀다
    }

    [Fact]
    public void Values_use_the_oracle_family_rules()
    {
        var price = new RollbackColumn("UNIT_PRICE", "NUMBER(10,2)", RollbackValueKind.Number);
        var name = new RollbackColumn("NAME", "NVARCHAR2(40)", RollbackValueKind.NationalText);

        Assert.Equal("TO_CHAR(t.\"UNIT_PRICE\", 'TM9', 'NLS_NUMERIC_CHARACTERS=''.,''')", Dialect.ReadExpression(price, "t.\"UNIT_PRICE\""));
        Assert.Equal("4730", Dialect.Literal(price, "4730", out var reason));
        Assert.Null(reason);
        Assert.Equal("NULL", Dialect.Literal(price, null, out _));
        Assert.Equal("UNISTR('\\C720\\C790')", Dialect.Literal(name, "유자", out _));
        Assert.Equal("CHARTOROWID('AAAR3sAAEAAAACXAAA')", Dialect.RowIdLiteral("AAAR3sAAEAAAACXAAA"));
        Assert.Equal(RollbackValueKind.TimestampLtz,
            Dialect.KindOf(new Oranda.Core.Metadata.ColumnInfo("C", 1, "TIMESTAMP(6) WITH LOCAL TIME ZONE")));
    }
}
