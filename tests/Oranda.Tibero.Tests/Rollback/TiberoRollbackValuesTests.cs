using Oranda.Tibero.Rollback;

namespace Oranda.Tibero.Tests.Rollback;

/// <summary>되돌리기 지점 Tibero 방언 값(서버 없음). 서버 실측은 <c>RollbackDialectMeasureIt</c>.</summary>
public sealed class TiberoRollbackValuesTests
{
    [Fact]
    public void Point_is_tsn_read_in_the_copy_order_and_as_of_uses_tsn()
    {
        Assert.Equal("TSN", TiberoRollbackValues.PointName);
        Assert.Equal(
            ["SELECT DBMS_FLASHBACK.GET_SYSTEM_CHANGE_NUMBER FROM DUAL", "SELECT TIMESTAMP_TO_TSN(SYSTIMESTAMP) FROM DUAL", "SELECT CURRENT_TSN FROM V$DATABASE"],
            TiberoRollbackValues.PointQueries);
        Assert.Equal("AS OF TSN 48213907", TiberoRollbackValues.AsOf(48213907));
        Assert.Throws<ArgumentOutOfRangeException>(() => TiberoRollbackValues.AsOf(-1));
    }

    [Fact]
    public void Measured_values_and_side_effects_query_use_both_binds_and_no_oracle_only_dictionary()
    {
        Assert.Equal("ORANDA$RB", TiberoRollbackValues.SavepointName);
        Assert.True(TiberoRollbackValues.SupportsReturningRowId);
        Assert.Equal(128, TiberoRollbackValues.MaxIdentifierBytes);

        var sql = TiberoRollbackValues.SideEffectsSql;
        Assert.Contains("ALL_TRIGGERS", sql, StringComparison.Ordinal);
        Assert.Contains("DELETE_RULE IN ('CASCADE', 'SET NULL')", sql, StringComparison.Ordinal);
        Assert.Contains(":owner", sql, StringComparison.Ordinal);
        Assert.Contains(":name", sql, StringComparison.Ordinal);
        Assert.Contains("SELECT 'TRIGGER' AS KIND", sql, StringComparison.Ordinal);
        Assert.Contains("SELECT DISTINCT c.DELETE_RULE AS KIND", sql, StringComparison.Ordinal); // CASCADE | SET NULL (Oracle 판과 같은 글)
        Assert.DoesNotContain("DBA_", sql, StringComparison.Ordinal);
    }
}
