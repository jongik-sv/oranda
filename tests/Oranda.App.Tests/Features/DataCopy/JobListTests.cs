using Oranda.App.Features.DataCopy;

namespace Oranda.App.Tests.Features.DataCopy;

/// <summary>내 작업 메뉴(설계 6.5): 가짜 저장소의 호출 순서와 인자를 확인한다.</summary>
public sealed class JobListTests
{
    [Fact]
    public async Task Job_menu_new_duplicate_rename_export_import_delete()
    {
        var store = new FakeJobStore();
        var list = new JobListViewModel(new DataCopyServices { Store = store });

        var created = await list.NewAsync("월말 복사");
        Assert.NotNull(created);
        Assert.False(string.IsNullOrEmpty(created.MaskSalt)); // 해시 비밀 값은 만들 때 한 번
        Assert.Equal(created.Id, list.Selected?.Job.Id);

        var copy = await list.DuplicateAsync("월말 복사 (2)");
        Assert.NotNull(copy);
        Assert.Equal(copy.Id, list.Selected?.Job.Id);

        await list.RenameAsync("월말 복사 (3)");
        await list.ExportAsync("/tmp/job.json");
        var imported = await list.ImportAsync("/tmp/in.json");
        Assert.Equal(imported?.Id, list.Selected?.Job.Id);
        await list.DeleteAsync();

        Assert.Equal(
            [
                "save:월말 복사", "list",
                $"duplicate:{created.Id}:월말 복사 (2)", "list",
                $"rename:{copy.Id}:월말 복사 (3)", "list",
                $"export:{copy.Id}:/tmp/job.json",
                "import:/tmp/in.json", "list",
                $"delete:{imported!.Id}", "list",
            ],
            store.Calls);
    }

    [Fact]
    public async Task Missing_store_reports_notice_instead_of_throwing()
    {
        var list = new JobListViewModel(new DataCopyServices());

        await list.RefreshAsync();
        Assert.Null(await list.NewAsync("이름"));

        Assert.False(string.IsNullOrEmpty(list.Notice));
    }
}
