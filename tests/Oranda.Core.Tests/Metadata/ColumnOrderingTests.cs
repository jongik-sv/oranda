using Oranda.Core.Metadata;

namespace Oranda.Core.Tests.Metadata;

// 소유: TSK-13-01. 표 상세 열 표시 순서(column_id 순).
public class ColumnOrderingTests
{
    [Fact]
    public void ByColumnId_sorts_by_position_and_keeps_invisible_columns_last()
    {
        var columns = new[]
        {
            new ColumnInfo("C", 3, "NUMBER"),
            new ColumnInfo("H", 2, "NUMBER") { IsInvisible = true },
            new ColumnInfo("A", 1, "NUMBER"),
            new ColumnInfo("B", 4, "NUMBER"),
        };

        Assert.Equal(["A", "C", "B", "H"], ColumnOrdering.ByColumnId(columns).Select(c => c.Name));
    }
}
