using Oranda.App.Features.DataCopy;
using Oranda.Core.Metadata;
using Oranda.Core.Transfer.Copy;

namespace Oranda.App.Tests.Features.DataCopy;

/// <summary>규칙 편집 중 저장: 「저장」 단추·Ctrl+S, 다른 작업을 열 때, 창을 닫을 때(사전 점검으로 넘어가지 않아도 한 일을 잃지 않는다).</summary>
public class DataCopySaveTests
{
    [Fact]
    public async Task Save_writes_the_rules_edited_so_far_without_going_to_precheck()
    {
        var h = new DataCopyHarness();
        h.Metadata.NameSteps[("APP", "ORDERS")] = NameStep.TableOrView;
        var job = TestJobs.Job("저장");
        var vm = await h.OpenAsync(job);
        await vm.SaveCurrentJobAsync(quiet: true); // 연 직후의 정리된 모양으로 한 번 맞춘다
        h.Store.Calls.Clear();

        await vm.AddTableInputAsync("orders");
        Assert.True(vm.HasUnsavedChanges);

        Assert.True(await vm.SaveCurrentJobAsync());
        Assert.Equal(["save:저장", "list"], h.Store.Calls);
        Assert.Equal("ORDERS", Assert.Single(h.Store.Jobs[job.Id].Tables).Table);
        Assert.False(vm.HasUnsavedChanges);
        Assert.Contains("저장했습니다", vm.StatusText, StringComparison.Ordinal);
        Assert.Equal(DataCopyStep.RuleEdit, vm.Step);
    }

    [Fact]
    public async Task Saving_without_changes_does_not_write()
    {
        var h = new DataCopyHarness();
        h.Metadata.NameSteps[("APP", "ORDERS")] = NameStep.TableOrView;
        var vm = await h.OpenAsync(TestJobs.Job("그대로"));
        await vm.SaveCurrentJobAsync(quiet: true);
        h.Store.Calls.Clear();

        Assert.False(await vm.SaveCurrentJobAsync());
        Assert.Empty(h.Store.Calls);
        Assert.Contains("바뀐 내용이 없습니다", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Opening_another_job_saves_the_current_one_first()
    {
        var h = new DataCopyHarness();
        h.Metadata.NameSteps[("APP", "ORDERS")] = NameStep.TableOrView;
        var first = TestJobs.Job("첫 작업");
        var second = TestJobs.Job("둘째 작업");
        h.Store.Jobs[second.Id] = second;
        var vm = await h.OpenAsync(first);

        await vm.AddTableInputAsync("ORDERS");
        await vm.OpenJobAsync(second);

        Assert.Equal("ORDERS", Assert.Single(h.Store.Jobs[first.Id].Tables).Table);
        Assert.Equal(second.Id, vm.CurrentJob!.Id);
    }

    [Fact]
    public async Task Save_failure_is_reported_and_changes_stay_unsaved()
    {
        var h = new DataCopyHarness();
        h.Metadata.NameSteps[("APP", "ORDERS")] = NameStep.TableOrView;
        var vm = await h.OpenAsync(TestJobs.Job("실패"));
        await vm.AddTableInputAsync("ORDERS");
        h.Store.SaveFailure = new IOException("디스크가 가득 찼습니다");

        Assert.False(await vm.SaveCurrentJobAsync());
        Assert.True(vm.StatusIsError);
        Assert.Contains("디스크가 가득 찼습니다", vm.StatusText, StringComparison.Ordinal);
        Assert.True(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task Read_only_job_is_not_saved()
    {
        var h = new DataCopyHarness();
        h.Metadata.NameSteps[("APP", "ORDERS")] = NameStep.TableOrView;
        var job = TestJobs.Job("새 버전", rules: TestJobs.Rule("ORDERS"));
        h.Store.ReadOnlyIds.Add(job.Id);
        var vm = await h.OpenAsync(job);
        h.Store.Calls.Clear();

        Assert.False(vm.HasUnsavedChanges);
        Assert.False(await vm.SaveCurrentJobAsync());
        Assert.DoesNotContain(h.Store.Calls, c => c.StartsWith("save:", StringComparison.Ordinal));
    }
}
