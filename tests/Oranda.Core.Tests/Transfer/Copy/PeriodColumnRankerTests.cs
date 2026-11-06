using Oranda.Core.Metadata;
using Oranda.Core.Transfer.Copy;
using Xunit;

namespace Oranda.Core.Tests.Transfer.Copy;

public class PeriodColumnRankerTests
{
    [Fact]
    public void Business_date_columns_come_before_created_at()
    {
        ColumnInfo[] cols =
        [
            new("REG_DTM", 1, "TIMESTAMP(6)"),
            new("ORDER_DT", 2, "DATE"),
            new("NAME", 3, "VARCHAR2") { Length = 30 },
            new("SALE_YMD", 4, "VARCHAR2") { Length = 8 },
        ];
        var r = PeriodColumnRanker.Rank(cols);
        Assert.Equal(["ORDER_DT", "SALE_YMD", "REG_DTM"], r.Select(c => c.Column.Name));
        Assert.Equal("YYYYMMDD", r[1].DefaultFormat);
        Assert.Null(r[0].DefaultFormat);
        Assert.True(r[2].IsCreatedAt);
        Assert.False(r[0].IsCreatedAt);
    }
}
