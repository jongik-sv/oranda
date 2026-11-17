using Oranda.App.Features.ObjectDetail;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using static Oranda.App.Tests.Features.TableEdit.TableEditTestSupport;

namespace Oranda.App.Tests.Features.TableEdit;

/// <summary>
/// 「저장」·「파티션」 페이지가 왼쪽 목록에 나오는 때(설계 18.2·18.4): 「저장」은 공급자가 늘 만들어 ✎ 편집을 켜도 목록이 바뀌지 않고,
/// 일반 테이블의 「파티션」은 「저장」 페이지의 「파티션 테이블로 바꾸기…」로만 끼운다(편집을 끄면 뺀다).
/// </summary>
public class ObjectDetailStoragePartitionPageTests
{
    private static Oranda.Core.Metadata.ObjectDetail DetailWithStorage() => new(Orders,
    [
        .. OrdersDetail().Sections.Where(s => s.Title != "통계"),
        new DetailSection(TableStorageSection.Title, ["항목", "값"], [["PCTFREE", "10"]]),
        new DetailSection("통계", ["항목", "값"], []),
    ]);

    private static string[] Titles(ObjectDetailViewModel vm) => [.. vm.Sections.Where(s => !s.IsData).Select(s => s.Title)];

    [Fact]
    public async Task Turning_editing_on_does_not_change_the_page_list_when_the_provider_gave_storage()
    {
        var (vm, meta, _) = CreateDetail();
        meta.Detail = DetailWithStorage();
        await vm.OpenAsync(new FakeSession(), Orders, showDdl: false);
        var before = Titles(vm);
        vm.SelectedSection = vm.Sections.Single(s => s.Title == "인덱스");

        Assert.True(await vm.SetTableEditingAsync(true));

        Assert.Equal(before, Titles(vm));
        Assert.DoesNotContain(ObjectDetailViewModel.PartitionsTitle, Titles(vm));
        Assert.Equal("인덱스", vm.SelectedSection?.Title);
    }

    [Fact]
    public async Task Partition_page_opens_from_the_storage_page_and_goes_away_when_editing_ends()
    {
        var (vm, meta, _) = CreateDetail();
        meta.Detail = DetailWithStorage();
        await vm.OpenAsync(new FakeSession(), Orders, showDdl: false);
        Assert.True(await vm.SetTableEditingAsync(true));
        vm.SelectedSection = vm.Sections.Single(s => s.Title == ObjectDetailViewModel.StorageTitle);
        var edit = vm.TableEdit!;
        Assert.Equal(TableEditPage.Storage, edit.CurrentPage);
        Assert.True(edit.CanOpenPartitioning);

        edit.OpenPartitioning();

        var titles = Titles(vm);
        Assert.Equal(titles.ToList().IndexOf(ObjectDetailViewModel.StorageTitle) + 1, titles.ToList().IndexOf(ObjectDetailViewModel.PartitionsTitle));
        Assert.Equal(ObjectDetailViewModel.PartitionsTitle, vm.SelectedSection?.Title);
        Assert.Equal(TableEditPage.Partitions, edit.CurrentPage);

        edit.OpenPartitioning(); // 다시 눌러도 하나만
        Assert.Single(vm.Sections, s => s.Title == ObjectDetailViewModel.PartitionsTitle);

        Assert.False(await vm.SetTableEditingAsync(false)); // 돌려준 값 = 편집 중인가
        Assert.DoesNotContain(ObjectDetailViewModel.PartitionsTitle, Titles(vm));
        Assert.Contains(ObjectDetailViewModel.StorageTitle, Titles(vm));
    }

    [Fact]
    public async Task Without_storage_from_the_provider_editing_still_adds_the_storage_page()
    {
        var (vm, _, _) = CreateDetail(); // OrdersDetail 에는 「저장」이 없다(옛 캐시·다른 공급자)
        await vm.OpenAsync(new FakeSession(), Orders, showDdl: false);

        Assert.True(await vm.SetTableEditingAsync(true));
        Assert.Contains(ObjectDetailViewModel.StorageTitle, Titles(vm));
        Assert.DoesNotContain(ObjectDetailViewModel.PartitionsTitle, Titles(vm));

        Assert.False(await vm.SetTableEditingAsync(false)); // 돌려준 값 = 편집 중인가
        Assert.DoesNotContain(ObjectDetailViewModel.StorageTitle, Titles(vm));
    }
}
