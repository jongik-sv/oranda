using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.Ai.Kit;
using Oranda.App.Features.ChangeJournal;
using Oranda.App.Features.ChangeJournal.Ai;
using Oranda.App.Tests.Features.Ai.InPlace;
using Oranda.App.Tests.Features.Ai.Kit;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Connection;
using Oranda.Core.Journal;
using Oranda.Core.Metadata;
using Oranda.Core.Rollback;
using Oranda.Testing.Rollback;

namespace Oranda.App.Tests.Features.ChangeJournal.Ai;

/// <summary>변경 일지 「이 기간 요약」(설계 §4.2): 지금 접속의 줄만, 가벼운 일 요청, 요약 띠, 「값 없이 요약했습니다」.</summary>
public class ChangeJournalAiTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 18, 4, 0, 0, TimeSpan.Zero);
    private static readonly DbObjectRef Orders = new("SAMPLE", "ORDERS", "TABLE");

    private static RollbackGroup Group(Guid profileId, DateTimeOffset ended, string summary) => new()
    {
        ProfileId = profileId,
        ProfileName = "dev-example",
        DbKey = "dev-example",
        StartedAt = ended.AddMinutes(-1),
        EndedAt = ended,
        EndKind = TransactionEndKind.Commit,
        State = RollbackScriptState.Confirmed,
        HasScriptFile = true,
        Tables = [Orders],
        Statements = [new RollbackStatementInfo(1, ended, DmlVerb.Update, Orders, summary, 120, null, RollbackScriptState.Confirmed, null, 120, false)],
    };

    private static async Task<(ChangeJournalViewModel Vm, FakeDriver Driver)> CreateAsync(AiAccessLevel level, bool hasProfile = true)
    {
        var mine = new ConnectionProfile("dev-example", "app", "db:1521/ORCL", Id: Guid.NewGuid(), AiAccess: level);
        var store = new InMemoryRollbackStore();
        await store.SaveAsync(Group(mine.Id, T0, "UPDATE SAMPLE.ORDERS SET STATUS = 'SECRET-VALUE'"), Doc(), default);
        await store.SaveAsync(Group(Guid.NewGuid(), T0.AddHours(1), "DELETE FROM SAMPLE.OTHER_DB"), Doc(), default);
        var journal = new FixedJournal(
        [
            new JournalEntry
            {
                Id = 3, CapturedAt = T0.AddHours(-1), ProfileId = mine.Id, ProfileName = "dev-example", DbKey = "dev-example",
                Owner = "SAMPLE", Name = "ORDERS_IDX1", ObjectType = "INDEX", Cause = JournalCause.Ddl,
                StatementSummary = "CREATE INDEX SAMPLE.ORDERS_IDX1 ON SAMPLE.ORDERS (STATUS)",
            },
        ]);
        var driver = new FakeDriver { Answer = _ => KitFakes.Answer("## 요약\nORDERS 를 바꿨습니다.\n## 눈여겨볼 점\n- 없음") };
        var vm = ChangeJournalViewModel.ForTests(journal, store);
        vm.CurrentProfile = () => hasProfile ? mine : null;
        vm.UseAi(InPlaceFakes.Host(driver));
        await vm.ReloadAsync();
        return (vm, driver);
    }

    private static RollbackScriptDocument Doc() => new(RollbackScriptDocument.CurrentSchemaVersion, 0,
        [new RollbackScriptStatement(1, T0, "UPDATE x", [], [new RollbackStep("UPDATE y")], [], false, null, 1)]);

    [Fact]
    public async Task Only_rows_of_the_current_connection_are_summarized()
    {
        var (vm, _) = await CreateAsync(AiAccessLevel.Plan);

        var input = vm.AiInput()!;

        Assert.Equal(2, input.Entries.Count);
        Assert.DoesNotContain(input.Entries, e => e.StatementText?.Contains("OTHER_DB", StringComparison.Ordinal) == true);
        Assert.Equal(T0.AddHours(-1), input.From);
        Assert.Equal(T0, input.To);
    }

    [Fact]
    public async Task The_entry_explains_why_it_is_off()
    {
        Assert.True((await CreateAsync(AiAccessLevel.Metadata)).Vm.AiEntry!.IsEnabled);
        var none = (await CreateAsync(AiAccessLevel.Plan, hasProfile: false)).Vm.AiEntry!;
        Assert.False(none.IsEnabled);
        Assert.Equal(ChangeJournalViewModel.AiNoConnectionText, none.ToolTip);
        Assert.Equal(AiEntryRule.AccessOffText, (await CreateAsync(AiAccessLevel.Off)).Vm.AiEntry!.ToolTip);
    }

    [Fact]
    public async Task Summary_is_a_light_request_without_values_below_query_rows_and_says_so()
    {
        var (vm, driver) = await CreateAsync(AiAccessLevel.Plan);

        await vm.AiEntry!.RunCommand.ExecuteAsync(null);

        var request = Assert.Single(driver.Requests);
        Assert.Equal(AiTier.Light, request.Tier);
        Assert.DoesNotContain("SECRET-VALUE", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("SAMPLE.ORDERS", request.Prompt, StringComparison.Ordinal);
        Assert.Equal("ORDERS 를 바꿨습니다.", vm.AiStrip!.Answer!["요약"]!.Body);
        Assert.Equal(JournalSummaryPrompt.NoValuesNote, vm.AiNote);
    }

    [Fact]
    public async Task Query_rows_sends_the_statements_and_no_note()
    {
        var (vm, driver) = await CreateAsync(AiAccessLevel.QueryRows);

        await vm.AiEntry!.RunCommand.ExecuteAsync(null);

        Assert.Contains("SECRET-VALUE", Assert.Single(driver.Requests).Prompt, StringComparison.Ordinal);
        Assert.Equal(string.Empty, vm.AiNote);
    }

    [Fact]
    public Task The_panel_shows_the_entry_and_the_summary_strip_above_the_list() => HeadlessHost.Run(async () =>
    {
        var (vm, _) = await CreateAsync(AiAccessLevel.Plan);
        var view = new ChangeJournalView { DataContext = vm };
        var window = new Window { Width = 1100, Height = 700, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var strip = Find<AiInlineStrip>(view, "AiSummaryStrip");
        Assert.False(strip.IsVisible);
        var button = Find<Button>(view, "AiSummaryButton");
        Assert.True(button.IsEffectivelyVisible);

        button.Command!.Execute(null);
        await HeadlessHost.WaitUntil(() => vm.AiStrip!.State == AiStripState.Done);
        Dispatcher.UIThread.RunJobs();

        Assert.True(strip.IsEffectivelyVisible);
        Assert.True(Find<TextBlock>(view, "AiSummaryNote").IsEffectivelyVisible);
        window.Close();
    });

    private sealed class FixedJournal(IReadOnlyList<JournalEntry> all) : IChangeJournalStore
    {
        public Task<long?> AddAsync(JournalEntry entry, CancellationToken ct) => Task.FromResult<long?>(null);

        public Task<IReadOnlyList<JournalEntry>> SearchAsync(JournalQuery query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<JournalEntry>>([.. all.OrderByDescending(e => e.CapturedAt).Take(query.Limit)]);

        public Task<JournalEntry?> GetAsync(long id, CancellationToken ct) => Task.FromResult(all.FirstOrDefault(e => e.Id == id));

        public Task PruneAsync(JournalRetention retention, CancellationToken ct) => Task.CompletedTask;
    }

    private static T Find<T>(Control root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);
}
