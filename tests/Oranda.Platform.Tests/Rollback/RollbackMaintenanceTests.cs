using Oranda.Core.Rollback;
using Oranda.Core.Settings;
using Oranda.Platform.Rollback;
using Oranda.Platform.Tests.Ai.Cli;
using Oranda.Testing;

namespace Oranda.Platform.Tests.Rollback;

// 소유: RB-22. 기동 정리(설계 7.4·12.3).
public sealed class RollbackMaintenanceTests
{
    [Fact]
    public async Task Startup_marks_open_groups_then_prunes_with_retention_setting()
    {
        var store = new CountingStore();
        var settings = new FixedSettings(new AppSettings { Rollback = new RollbackSettings { RetentionDays = 45 } });
        var task = new RollbackMaintenance(new FakeEventBus(), store, settings);

        await task.RunAsync(default);

        Assert.Equal(["mark", "prune:45"], store.Calls);
    }

    [Fact]
    public async Task Every_hundredth_saved_group_prunes_again_and_failures_are_swallowed()
    {
        var store = new CountingStore { FailPrune = true };
        var events = new FakeEventBus();
        var task = new RollbackMaintenance(events, store, new FixedSettings(new AppSettings()));
        await task.RunAsync(default);

        for (var i = 0; i < RollbackMaintenance.PruneEverySaves * 2 - 1; i++)
        {
            events.Publish(new RollbackGroupSaved(i + 1, RollbackScriptState.Confirmed));
        }

        await task.DrainAsync();
        Assert.Equal(["mark", "prune:30", "prune:30"], store.Calls);
    }

    private sealed class CountingStore : IRollbackStore
    {
        public List<string> Calls { get; } = [];

        public bool FailPrune { get; init; }

        public bool CanStoreScripts => true;

        public Task<int> MarkOrphansAsync(CancellationToken ct)
        {
            Calls.Add("mark");
            return Task.FromResult(0);
        }

        public Task PruneAsync(int retentionDays, CancellationToken ct)
        {
            lock (Calls)
            {
                Calls.Add("prune:" + retentionDays);
            }

            return FailPrune ? Task.FromException(new IOException("disk")) : Task.CompletedTask;
        }

        public Task<long> SaveAsync(RollbackGroup group, RollbackScriptDocument? document, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<RollbackGroup>> SearchAsync(RollbackQuery query, CancellationToken ct) => throw new NotSupportedException();

        public Task<RollbackGroup?> GetAsync(long id, CancellationToken ct) => throw new NotSupportedException();

        public Task<RollbackScriptDocument?> LoadDocumentAsync(long id, CancellationToken ct) => throw new NotSupportedException();
    }
}
