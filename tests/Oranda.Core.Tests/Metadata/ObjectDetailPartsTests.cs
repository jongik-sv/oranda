using Oranda.Core.Metadata;

namespace Oranda.Core.Tests.Metadata;

/// <summary>DL-1 상세 탭 목록 차례(질의 없이 만드는 탭 머리)와 시간 기록.</summary>
public class ObjectDetailPartsTests
{
    private static readonly DetailSection Columns = new("열", ["이름"], [["ID"]]);

    [Fact]
    public void Table_outline_keeps_the_whole_detail_order_and_loads_only_the_table_sections()
    {
        var outline = ObjectDetailParts.Outline(new DbObjectRef("SCOTT", "EMP", "TABLE"), [Columns, new DetailSection("인덱스", [], [])], partitioned: true);

        Assert.Equal(["일반", "열", "인덱스", "트리거", "참조됨", "파티션", "통계", "크기", "권한", "의존"], outline.Tabs.Select(t => t.Title));
        Assert.Equal([false, true, true, false, false, false, false, false, false, false], outline.Tabs.Select(t => t.Section is not null));
        Assert.Equal(DetailPart.Table, outline.Tabs[1].Part);
        Assert.Equal(DetailPart.Partitions, outline.Tabs[5].Part);
    }

    [Fact]
    public void Partitions_tab_is_left_out_of_a_plain_table()
    {
        var outline = ObjectDetailParts.Outline(new DbObjectRef("SCOTT", "EMP", "TABLE"), [Columns], partitioned: false);

        Assert.DoesNotContain(outline.Tabs, t => t.Part == DetailPart.Partitions);
        Assert.Contains(DetailPart.Partitions, ObjectDetailParts.TailParts("TABLE")); // 모르면(전체 읽기) 넣는다
    }

    [Theory]
    [InlineData("VIEW", new[] { DetailPart.Triggers, DetailPart.Privileges, DetailPart.Dependencies })]
    [InlineData("MATERIALIZED VIEW", new[] { DetailPart.Size, DetailPart.Privileges, DetailPart.Dependencies })]
    [InlineData("INDEX", new[] { DetailPart.Size, DetailPart.Privileges, DetailPart.Dependencies })]
    [InlineData("PACKAGE", new[] { DetailPart.Privileges, DetailPart.Dependencies })]
    public void Tail_parts_follow_the_object_type(string type, DetailPart[] expected)
        => Assert.Equal(expected, ObjectDetailParts.TailParts(type));

    [Fact]
    public async Task Timed_read_passes_sections_through_and_rethrows_failures()
    {
        var obj = new DbObjectRef("SCOTT", "EMP", "TABLE");

        var ok = await ObjectDetailParts.TimedAsync("K", obj, DetailPart.Stats, () => Task.FromResult<IReadOnlyList<DetailSection>>([Columns]));
        Assert.Same(Columns, Assert.Single(ok));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ObjectDetailParts.TimedAsync("K", obj, DetailPart.Stats, () => Task.FromException<IReadOnlyList<DetailSection>>(new InvalidOperationException("x"))));
    }
}
