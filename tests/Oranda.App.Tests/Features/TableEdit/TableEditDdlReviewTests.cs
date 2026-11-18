using Oranda.App.Features.ChangeBundle.Ai;
using Oranda.App.Tests.Features.Ai.Kit;
using Oranda.Core.Settings;
using static Oranda.App.Tests.Features.TableEdit.TableEditTestSupport;

namespace Oranda.App.Tests.Features.TableEdit;

// 소유: TSK-15-09. 테이블 편집 DDL 미리 보기의 「AI 검토」: 미리 보기 글·편집하는 표·검사 줄을 재료로, 문장이 없으면 꺼짐, 편집이 바뀌면 새 검토.
public class TableEditDdlReviewTests
{
    private static DdlReviewViewModel Review(Func<DdlReviewInput?> input, FakeDriver driver) =>
        new(input, new DdlReviewServices(() => driver, () => new AiSettings { Model = "opus", Driver = AiDriverKind.ClaudeCode }));

    [Fact]
    public async Task Preview_text_and_the_edited_table_are_the_review_material()
    {
        var (vm, _, _) = CreateDetail();
        var driver = new FakeDriver();
        vm.DdlReviewFactory = input => Review(input, driver);
        await vm.OpenAsync(new FakeSession(), Orders, showDdl: false);
        await vm.SetTableEditingAsync(true);

        var review = vm.DdlReview!;
        Assert.True(vm.HasDdlReview);
        Assert.Null(vm.DdlReviewInput()); // 안 고치면 문장이 없다
        Assert.False(review.Entry.IsEnabled);

        vm.TableEdit!.Columns.Single(c => c.Name == "STATUS").LengthText = "20";
        review.Refresh();

        var input = vm.DdlReviewInput()!;
        Assert.Equal(vm.TableEdit.PreviewText, input.Script);
        Assert.Equal([new DdlReviewTarget(Orders.Owner, Orders.Name)], input.Targets);
        Assert.True(review.Entry.IsEnabled);

        await review.Entry.RunCommand.ExecuteAsync(null);
        Assert.Contains(input.Script.Trim(), Assert.Single(driver.Requests).Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ending_the_edit_drops_the_old_review()
    {
        var (vm, _, _) = CreateDetail();
        var driver = new FakeDriver();
        vm.DdlReviewFactory = input => Review(input, driver);
        await vm.OpenAsync(new FakeSession(), Orders, showDdl: false);
        await vm.SetTableEditingAsync(true);
        var first = vm.DdlReview;

        await vm.SetTableEditingAsync(false);
        Assert.Null(vm.DdlReview);

        await vm.SetTableEditingAsync(true);
        Assert.NotNull(vm.DdlReview);
        Assert.NotSame(first, vm.DdlReview);
    }
}
