using Oranda.App.Features.ChangeJournal;
using Oranda.App.Features.Rollback;
using Oranda.Core.Connection;
using Oranda.Core.Journal;
using Oranda.Core.Metadata;
using Oranda.Core.Rollback;
using Oranda.Testing.Rollback;

namespace Oranda.App.Tests.Features.ChangeJournal;

/// <summary>
/// 변경 일지 화면 1(되돌리기 지점 설계 11.1·11.2): DDL 보관본과 커밋 묶음을 시간 역순으로 합친 목록, 묶음 접기·펼치기, 상태 표시,
/// 원인 거르기 「DML」, 「원복 스크립트 열기」·「이 실행 전과 비교…」 가 명령 id 로만 부르는 것.
/// </summary>
public sealed class ChangeJournalRollbackTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 12, 5, 0, 0, TimeSpan.Zero);
    private static readonly DbObjectRef Products = new("SAMPLE", "PRODUCTS", "TABLE");
    private static readonly DbObjectRef Items = new("SAMPLE", "ORDER_ITEMS", "TABLE");

    private static JournalEntry Ddl(long id, DateTimeOffset at, JournalCause cause = JournalCause.Ddl) => new()
    {
        Id = id,
        CapturedAt = at,
        ProfileName = "dev-example",
        DbKey = "dev-example",
        Owner = "SAMPLE",
        Name = "EMPLOYEES",
        ObjectType = "TABLE",
        Cause = cause,
        StatementSummary = "ALTER TABLE EMPLOYEES ADD JOB_TITLE VARCHAR2(30)",
        PreviousDdl = "CREATE TABLE EMPLOYEES (ID NUMBER)",
    };

    private static RollbackStatementInfo Stmt(int seq, DmlVerb verb, string summary, long rows, int steps, long point) =>
        new(seq, T0.AddMinutes(seq), verb, Products, summary, rows, new ChangePoint(point, "SCN", T0, "test"),
            RollbackScriptState.Open, null, steps, false);

    private static RollbackGroup Confirmed(DateTimeOffset ended) => new()
    {
        ProfileId = Guid.NewGuid(),
        ProfileName = "prod-example",
        DbKey = "prod-example",
        Environment = ConnectionEnvironment.Production,
        StartedAt = ended.AddMinutes(-2),
        EndedAt = ended,
        EndKind = TransactionEndKind.Commit,
        BasePoint = new ChangePoint(48213907, "SCN", ended, "test"),
        CommitPoint = new ChangePoint(48214022, "SCN", ended, "test"),
        State = RollbackScriptState.Confirmed,
        StepCount = 16,
        RowsTotal = 16,
        HasScriptFile = true,
        Tables = [Products],
        Statements =
        [
            Stmt(1, DmlVerb.Update, "UPDATE SAMPLE.PRODUCTS SET UNIT_PRICE = UNIT_PRICE * 1.1 WHERE CATEGORY_ID = 3", 12, 12, 48213907),
            Stmt(2, DmlVerb.Delete, "DELETE FROM SAMPLE.PRODUCTS WHERE DISCONTINUED = 'Y'", 3, 3, 48213954),
            Stmt(3, DmlVerb.InsertValues, "INSERT INTO SAMPLE.PRODUCTS (PRODUCT_ID, …) VALUES (2001, …)", 1, 1, 48213991),
        ],
    };

    private static async Task<(ChangeJournalViewModel Vm, InMemoryRollbackStore Store, long ConfirmedId, long RolledBackId)> CreateAsync()
    {
        var store = new InMemoryRollbackStore();
        var confirmedId = await store.SaveAsync(Confirmed(T0.AddHours(2)), new RollbackScriptDocument(RollbackScriptDocument.CurrentSchemaVersion, 0, []), default);
        var rolledBackId = await store.SaveAsync(Confirmed(T0) with
        {
            EndKind = TransactionEndKind.Rollback,
            State = RollbackScriptState.Invalid,
            HasScriptFile = false,
            Tables = [Items, Products, Products],
        }, null, default);
        await store.SaveAsync(Confirmed(T0.AddHours(3)) with
        {
            ProfileName = "dev-example",
            Environment = ConnectionEnvironment.Development,
            State = RollbackScriptState.NotCreated,
            Reason = NotCreatedReason.NotProduction,
            HasScriptFile = false,
            Statements = [],
        }, null, default);
        var journal = new FixedJournal([Ddl(7, T0.AddHours(1)), Ddl(8, T0.AddHours(4), JournalCause.Compile)]);
        var vm = new ChangeJournalViewModel(journal, store) { RetentionDaysProvider = () => 30 };
        await vm.ReloadAsync();
        return (vm, store, confirmedId, rolledBackId);
    }

    [Fact]
    public async Task Ddl_entries_and_commit_groups_are_merged_newest_first_with_state_texts()
    {
        var (vm, _, confirmedId, rolledBackId) = await CreateAsync();

        Assert.Equal(5, vm.Rows.Count);
        Assert.Equal(["컴파일", "커밋", "커밋", "DDL 실행", "롤백"], vm.Rows.Select(r => r.CauseText));
        Assert.Equal([8L, 7L], vm.Items.Select(i => i.Id)); // Items 는 DDL 보관본만
        Assert.Contains("30일 뒤 지웁니다", vm.StatusMessage, StringComparison.Ordinal);
        Assert.StartsWith("5건", vm.StatusMessage, StringComparison.Ordinal);

        var confirmed = vm.Rows.OfType<RollbackGroupItem>().Single(g => g.Id == confirmedId);
        Assert.Equal("확정 · 16문장", confirmed.ScriptText);
        Assert.Equal("SCN 48213907 → 48214022", confirmed.PointText);
        Assert.Equal("문장 3개 · prod-example", confirmed.SummaryText);
        Assert.Equal("SAMPLE.PRODUCTS", confirmed.ObjectText);
        Assert.True(confirmed.CanOpenScript);

        var rolledBack = vm.Rows.OfType<RollbackGroupItem>().Single(g => g.Id == rolledBackId);
        Assert.Equal("무효 · 롤백함", rolledBack.ScriptText);
        Assert.Equal("SCN 48213907", rolledBack.PointText); // 롤백 묶음은 기준만
        Assert.Equal("SAMPLE.ORDER_ITEMS 외 2", rolledBack.ObjectText);
        Assert.False(rolledBack.CanOpenScript);
        vm.ToggleExpand(rolledBack);
        Assert.All(vm.Rows.OfType<RollbackStatementItem>().Where(r => ReferenceEquals(r.Parent, rolledBack)), r => Assert.Equal("무효", r.ScriptText));
        vm.ToggleExpand(rolledBack);

        Assert.Equal("만들지 않음 · 개발 접속", vm.Rows.OfType<RollbackGroupItem>().Single(g => g.Group.ProfileName == "dev-example").ScriptText);
    }

    [Fact]
    public async Task Expanded_group_shows_its_statements_in_run_order_and_stays_open_after_reload()
    {
        var (vm, _, confirmedId, _) = await CreateAsync();
        var group = vm.Rows.OfType<RollbackGroupItem>().Single(g => g.Id == confirmedId);
        Assert.Equal("▸", group.ExpandGlyph);
        await vm.SetRowSelectionAsync([group]);

        vm.ToggleExpand(group);
        Assert.Same(group, vm.SelectedGroup); // 펼쳐도 고른 묶음은 그대로
        var at = vm.Rows.IndexOf(group);
        var children = vm.Rows.Skip(at + 1).Take(3).Cast<RollbackStatementItem>().ToList();
        Assert.Equal(["DML 실행", "DML 실행", "DML 실행"], children.Select(c => c.CauseText));
        Assert.Equal(["12문장", "3문장", "1문장"], children.Select(c => c.ScriptText));
        Assert.Equal("48213907", children[0].PointText);
        Assert.Equal("12", children[0].RowsText);

        await vm.ReloadAsync();
        Assert.Equal(8, vm.Rows.Count); // 다시 읽어도 펼친 묶음은 펼친 채
        Assert.Equal(confirmedId, vm.SelectedGroup?.Id); // 같은 번호의 새 줄로 옮긴다

        vm.ToggleExpand(vm.Rows.OfType<RollbackGroupItem>().Single(g => g.Id == confirmedId));
        Assert.Equal(5, vm.Rows.Count);
    }

    [Fact]
    public async Task Cause_filter_dml_shows_only_groups_and_ddl_shows_only_ddl_entries()
    {
        var (vm, _, _, _) = await CreateAsync();

        vm.SelectedCause = vm.CauseChoices.Single(c => c.Filter == JournalCauseFilter.Dml);
        await vm.ReloadAsync();
        Assert.All(vm.Rows, r => Assert.IsType<RollbackGroupItem>(r));
        Assert.Equal(3, vm.Rows.Count);

        vm.SelectedCause = vm.CauseChoices.Single(c => c.Filter == JournalCauseFilter.Ddl);
        await vm.ReloadAsync();
        Assert.Equal([7L], vm.Rows.Cast<JournalItem>().Select(i => i.Id));
    }

    [Fact]
    public async Task Buttons_call_the_rollback_commands_with_the_selected_group()
    {
        var (vm, _, confirmedId, rolledBackId) = await CreateAsync();
        var calls = new List<(string Id, object Arg)>();
        vm.CommandRunner = (id, arg) =>
        {
            calls.Add((id, arg));
            return Task.FromResult(true);
        };

        Assert.False(vm.CanOpenScript);
        var group = vm.Rows.OfType<RollbackGroupItem>().Single(g => g.Id == confirmedId);
        vm.ToggleExpand(group);
        var statement = vm.Rows.OfType<RollbackStatementItem>().First();
        await vm.SetRowSelectionAsync([statement]); // 문장 줄을 고르면 그 묶음이 대상
        Assert.Same(group, vm.SelectedGroup);
        Assert.Contains("[1/3]", vm.DetailText, StringComparison.Ordinal);
        Assert.True(vm.CanOpenScript);
        Assert.True(vm.CanCompare);

        Assert.True(await vm.OpenScriptAsync());
        Assert.True(await vm.CompareAsync());
        Assert.Equal(RollbackCommandIds.OpenScript, calls[0].Id);
        Assert.Equal(new RollbackScriptArgs(confirmedId), calls[0].Arg);
        Assert.Equal(RollbackCommandIds.ComparePoint, calls[1].Id);
        var compare = Assert.IsType<PointCompareArgs>(calls[1].Arg);
        Assert.Equal(confirmedId, compare.GroupId);
        Assert.Equal(Products, compare.Table);

        // 롤백(무효) 묶음은 원복 스크립트를 열 수 없고, 비교는 된다.
        await vm.SetRowSelectionAsync([vm.Rows.OfType<RollbackGroupItem>().Single(g => g.Id == rolledBackId)]);
        Assert.False(vm.CanOpenScript);
        Assert.False(await vm.OpenScriptAsync());
        Assert.True(vm.CanCompare);
        Assert.Equal(2, calls.Count);

        // DDL 보관본을 고르면 묶음 단추가 꺼진다.
        await vm.SetRowSelectionAsync([vm.Items[0]]);
        Assert.Null(vm.SelectedGroup);
        Assert.False(vm.CanCompare);
    }

    [Fact]
    public async Task Without_a_rollback_store_the_list_is_the_old_ddl_journal()
    {
        var vm = new ChangeJournalViewModel(new FixedJournal([Ddl(1, T0)]), null);
        await vm.ReloadAsync();
        Assert.Single(vm.Rows);
        Assert.Equal("1건", vm.StatusMessage);
    }

    private sealed class FixedJournal(IReadOnlyList<JournalEntry> all) : IChangeJournalStore
    {
        public Task<long?> AddAsync(JournalEntry entry, CancellationToken ct) => Task.FromResult<long?>(null);

        public Task<IReadOnlyList<JournalEntry>> SearchAsync(JournalQuery query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<JournalEntry>>([.. all.OrderByDescending(e => e.CapturedAt).Take(query.Limit)]);

        public Task<JournalEntry?> GetAsync(long id, CancellationToken ct) => Task.FromResult(all.FirstOrDefault(e => e.Id == id));

        public Task PruneAsync(JournalRetention retention, CancellationToken ct) => Task.CompletedTask;
    }
}
