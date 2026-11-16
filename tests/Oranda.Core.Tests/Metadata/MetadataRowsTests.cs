using Oranda.Core.Metadata;

namespace Oranda.Core.Tests.Metadata;

// Oracle·Tibero 메타데이터 제공자가 같이 쓰는 행 → 모델 규칙(MetadataRows). 두 제공자에 따로 있던 같은 코드를 모은 것.
public class MetadataRowsTests
{
    [Fact]
    public void Object_row_reads_last_ddl_time_as_utc_and_keeps_missing_status_null()
    {
        var fetched = new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero);
        var meta = MetadataRows.ToMeta("DB", ["APP", "EMP", "TABLE", null, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified)], fetched);

        Assert.Equal(("DB", "APP", "EMP", "TABLE"), (meta.DbKey, meta.Owner, meta.Name, meta.Type));
        Assert.Null(meta.Status);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), meta.LastDdlTime);
        Assert.Equal(fetched, meta.FetchedAt);
        Assert.Null(MetadataRows.ToMeta("DB", ["APP", "EMP", "TABLE", "VALID", null], fetched).LastDdlTime);
    }

    [Fact]
    public void Index_rows_group_by_name_in_order_and_yes_flags_are_case_sensitive()
    {
        var indexes = MetadataRows.BuildIndexes(
        [
            ["PK_EMP", "UNIQUE", "NORMAL", "ID", 1],
            ["IX_EMP", "NONUNIQUE", "NORMAL", "B", 1],
            ["PK_EMP", "UNIQUE", "NORMAL", "SEQ", 2],
        ]);

        Assert.Equal(["PK_EMP", "IX_EMP"], indexes.Select(i => i.Name));
        Assert.True(indexes[0].IsUnique);
        Assert.Equal(["ID", "SEQ"], indexes[0].Columns);
        Assert.False(indexes[1].IsUnique);

        var column = MetadataRows.ToColumn(["C", 1, "VARCHAR2", 10, null, null, "N", " 'x' ", "yes", "YES", "NO", null, null]);
        Assert.False(column.IsNullable);
        Assert.Equal("'x'", column.DefaultValue);
        Assert.False(column.IsVirtual);
        Assert.True(column.IsIdentity);
        Assert.Null(column.ByteLength);
    }
}
