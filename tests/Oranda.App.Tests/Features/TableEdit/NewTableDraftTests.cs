using Oranda.App.Features.ObjectDetail;
using Oranda.App.Features.SchemaBrowser;
using Oranda.App.Features.TableEdit;
using Oranda.App.Tests.Shell;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Shell;
using static Oranda.App.Tests.Features.TableEdit.TableEditTestSupport;

namespace Oranda.App.Tests.Features.TableEdit;

/// <summary>
/// 새 테이블 작업 저장 연결(설계 18.7·TE-43, T16d): 탭 ↔ <see cref="TableDraftHook"/>(<see cref="TableDraftLibrary"/>·메모리 저장소).
/// 닫기 「버리기 / 남겨 두기 / 취소」, 실패 시 탭 남김, 앱 종료 = 써 두고 쥔 채, 실행 성공 = 지움, 되살리기(쥐기 한 번), 「만들던 새 테이블…」 명령·고르기, 시작 때 복원.
/// </summary>
public class NewTableDraftTests
{
    private const string Me = "100@1000";
    private const string Dead = "300@1000";

    private readonly FakeTableDraftStore _store = new();
    private readonly TableDraftHook _hook;

    public NewTableDraftTests()
        => _hook = new TableDraftHook(new TableDraftLibrary(_store) { Instance = Me, IsInstanceAlive = i => i == Me });

    private (ObjectDetailViewModel Vm, Probe Probe, List<string> Asked) Create(NewTableCloseChoice answer = NewTableCloseChoice.Keep)
    {
        var (vm, _, probe) = CreateDetail();
        vm.TableEditEngine = CoreTableEditEngine.Instance;
        vm.NewTableDraftHook = _hook;
        vm.RefreshSchemaTables = (_, _, _) => Task.CompletedTask;
        var asked = new List<string>();
        vm.AskCloseNewTable = (name, _) =>
        {
            asked.Add(name);
            return Task.FromResult(answer);
        };
        return (vm, probe, asked);
    }

    private static void Fill(ObjectDetailViewModel vm, string name = "ORDER_ITEMS")
    {
        vm.NewTableName = name;
        vm.TableEdit!.AddColumn()!.Name = "ID";
    }

    private Task<TableDraftEntry> SeedAsync(string name, string? openBy, string owner = "SAMPLE")
        => _store.SaveAsync(null, "dev-example", Dbms.Oracle,
            TableDraftFactory.Empty(owner) with { Name = name, Columns = [new ColumnDraft(DraftKeys.New(), "ID") { DataType = "NUMBER" }] },
            openBy ?? "seed", openBy is not null, default)!;

    [Fact]
    public async Task Keep_writes_the_draft_and_releases_it_for_the_owner_menu()
    {
        var (vm, _, asked) = Create(NewTableCloseChoice.Keep);
        Assert.True(await vm.OpenNewTableAsync(new FakeSession(), "SAMPLE"));
        Assert.Equal(1, _hook.LiveCount);
        Fill(vm);

        Assert.True(await vm.CloseConfirmation()!(CancellationToken.None));

        var row = Assert.Single(_store.Rows.Values);
        Assert.Equal("ORDER_ITEMS", row.Entry.Name);
        Assert.Equal("dev-example", row.Entry.ConnectionName);
        Assert.Null(row.Entry.OpenBy); // 놓았다 → 「만들던 새 테이블…」 목록
        Assert.Equal(["SAMPLE.ORDER_ITEMS"], asked);
        Assert.Equal(0, _hook.LiveCount);
        Assert.Single(await _hook.Library.ListForOwnerAsync("dev-example", "SAMPLE", default));
    }

    [Fact]
    public async Task Discard_deletes_what_autosave_wrote()
    {
        var row = await SeedAsync("KEPT", null); // 남겨 둔 것을 다시 열어 고친 뒤 버린다
        var stored = (await _store.GetAsync(row.Id, default))!;
        var (vm, _, asked) = Create(NewTableCloseChoice.Discard);
        Assert.True(await NewTableLauncher.OpenStoredAsync(null, _hook, new FakeSession(), stored, ["SAMPLE"], () => vm, (_, _) => true, default));
        vm.TableEdit!.AddColumn()!.Name = "NAME";

        Assert.True(await vm.CloseConfirmation()!(CancellationToken.None));
        Assert.Empty(_store.Rows);
        Assert.Equal(["SAMPLE.KEPT"], asked);
    }

    [Fact]
    public async Task A_failing_store_keeps_the_tab_open_and_says_why()
    {
        var (vm, _, _) = Create(NewTableCloseChoice.Keep);
        Assert.True(await vm.OpenNewTableAsync(new FakeSession(), "SAMPLE"));
        Fill(vm);
        _store.FailNext = true;

        Assert.False(await vm.CloseConfirmation()!(CancellationToken.None));
        Assert.Contains("disk full", vm.StatusText, StringComparison.Ordinal);
        Assert.True(vm.HasDraftLink); // 다시 닫으면 다시 묻는다

        Assert.True(await vm.CloseConfirmation()!(CancellationToken.None));
        Assert.Single(_store.Rows);
    }

    [Fact]
    public async Task App_exit_does_not_ask_and_leaves_the_draft_held_for_the_next_run()
    {
        var (vm, _, asked) = Create();
        Assert.True(await vm.OpenNewTableAsync(new FakeSession(), "SAMPLE"));
        Fill(vm);

        Assert.True(await vm.CloseConfirmation(appExit: true)!(CancellationToken.None)); // 묻지 않고 지금 써 둔다
        var row = Assert.Single(_store.Rows.Values);
        Assert.Equal(Me, row.Entry.OpenBy); // 쥔 채 → 다음 실행에는 끝난 인스턴스 것 = 복원 목록
        Assert.Equal(1, _hook.LiveCount); // 끝맺지 않았다(뒤 단계가 종료를 취소해도 탭과 자동 저장은 그대로)

        vm.TableEdit!.AddColumn()!.Name = "LATE";
        await _hook.SaveAllAsync(default); // 종료 작업: 마지막 것까지 써 두고 멈춘다
        Assert.Equal(2, _store.Rows.Values.Single().Draft.Columns.Count);
        Assert.Empty(asked);
        vm.EndNewTableDraft(); // 종료 중 문서가 닫혀도 지우거나 놓지 않는다
        Assert.Equal(Me, Assert.Single(_store.Rows.Values).Entry.OpenBy);
    }

    [Fact]
    public async Task An_unwritable_store_never_pretends_the_draft_was_kept()
    {
        var (vm, probe, _) = Create(NewTableCloseChoice.Keep);
        Assert.True(await vm.OpenNewTableAsync(new FakeSession(), "SAMPLE"));
        Fill(vm);
        _store.Writable = false;

        Assert.False(await vm.CloseConfirmation()!(CancellationToken.None)); // 「남겨 두기」를 해도 쓸 수 없다 → 탭을 남긴다
        Assert.Contains("쓸 수 없습니다", vm.StatusText, StringComparison.Ordinal);

        probe.Answer = false;
        Assert.False(await vm.CloseConfirmation(appExit: true)!(CancellationToken.None)); // 종료: 써 두지 못했으니 버릴지 묻는다
        Assert.Contains("써 두지 못했습니다", Assert.Single(probe.Asked), StringComparison.Ordinal);
        Assert.Empty(_store.Rows);
    }

    [Fact]
    public async Task An_untouched_copy_structure_tab_is_kept_too()
    {
        var (vm, _, asked) = Create(NewTableCloseChoice.Keep);
        Assert.True(await vm.OpenNewTableAsync(new FakeSession(), "SAMPLE", ["SAMPLE"], copyFrom: Orders));

        Assert.True(await vm.CloseConfirmation()!(CancellationToken.None)); // 복사해 온 모델도 「고친 것」 = 남길 것

        Assert.Single(asked);
        var row = Assert.Single(_store.Rows.Values);
        Assert.Equal("ORDERS_COPY", row.Entry.Name);
        Assert.Equal(4, row.Draft.Columns.Count);
    }

    [Fact]
    public async Task A_resumed_tab_keeps_saving_its_own_edit_session_after_leaving_new_table_mode()
    {
        var row = await SeedAsync("KEPT", null);
        var stored = (await _store.GetAsync(row.Id, default))!;
        var (vm, _, _) = Create();
        Assert.True(await NewTableLauncher.OpenStoredAsync(null, _hook, new FakeSession(), stored, ["SAMPLE"], () => vm, (_, _) => true, default));
        vm.TableEdit!.AddColumn()!.Name = "NEW_COL";

        await vm.OpenAsync(new FakeSession(), Orders, showDdl: false); // 다른 객체를 열었다(편집 끔 → 놓기)
        await HeadlessHostless.WaitAsync(() => _store.Rows[row.Id].Entry.OpenBy is null);

        var saved = Assert.Single(_store.Rows.Values);
        Assert.Null(saved.Entry.OpenBy); // 놓였다 → 「만들던 새 테이블…」 목록
        Assert.Equal(["ID", "NEW_COL"], saved.Draft.Columns.Select(c => c.Name)); // 옛 저장분으로 되돌리지 않는다
        Assert.Single(await _hook.Library.ListForOwnerAsync("dev-example", "SAMPLE", default));
    }

    [Fact]
    public async Task A_draft_that_cannot_be_deleted_after_the_run_is_released_into_the_owner_menu()
    {
        var (vm, _, _) = Create();
        vm.Exec = new TableEditRunTests.FakeExec();
        vm.TableEditChecks = new TableEditRunTests.FakeChecks();
        var row = await SeedAsync("KEPT", null);
        var stored = (await _store.GetAsync(row.Id, default))!;
        Assert.True(await NewTableLauncher.OpenStoredAsync(null, _hook, new FakeSession(), stored, ["SAMPLE"], () => vm, (_, _) => true, default));
        _store.FailNext = true; // 실행 뒤 지우기가 실패한다

        var result = await vm.RunTableEditAsync();

        Assert.Equal(TableEditRunOutcome.Succeeded, result!.Outcome);
        Assert.Contains("만들던 새 테이블", vm.StatusText, StringComparison.Ordinal);
        await HeadlessHostless.WaitAsync(() => _store.Rows[row.Id].Entry.OpenBy is null); // 놓아서 목록에 보인다(거기서 지운다)
        Assert.True(await _hook.Library.DeleteAsync((await _hook.Library.ListForOwnerAsync("dev-example", "SAMPLE", default)).Single(), default));
    }

    [Fact]
    public async Task Untouched_tab_writes_nothing_and_closes_quietly()
    {
        var (vm, _, asked) = Create();
        Assert.True(await vm.OpenNewTableAsync(new FakeSession(), "SAMPLE"));

        Assert.True(await vm.CloseConfirmation()!(CancellationToken.None));
        await _hook.SaveAllAsync(default);

        Assert.Empty(_store.Rows);
        Assert.Empty(asked);
        Assert.Equal(0, _store.Saves);
    }

    [Fact]
    public async Task Successful_run_deletes_the_saved_draft()
    {
        var (vm, _, _) = Create();
        vm.Exec = new TableEditRunTests.FakeExec();
        vm.TableEditChecks = new TableEditRunTests.FakeChecks();
        var session = new FakeSession();
        var row = await SeedAsync("KEPT", null);
        var stored = (await _store.GetAsync(row.Id, default))!;
        Assert.True(await NewTableLauncher.OpenStoredAsync(null, _hook, session, stored, ["SAMPLE"], () => vm, (_, _) => true, default));
        Assert.Equal(Me, _store.Rows[row.Id].Entry.OpenBy); // 다시 연 것은 이 인스턴스가 쥔다

        var result = await vm.RunTableEditAsync();

        Assert.Equal(TableEditRunOutcome.Succeeded, result!.Outcome);
        Assert.Empty(_store.Rows);
        Assert.False(vm.HasDraftLink);
    }

    [Fact]
    public async Task A_stored_draft_opens_once_with_its_model()
    {
        var row = await SeedAsync("ORDERS_NEW", Dead);
        var stored = (await _store.GetAsync(row.Id, default))!;
        var (vm, _, _) = Create();
        var opened = new List<string>();

        Assert.True(await NewTableLauncher.OpenStoredAsync(null, _hook, new FakeSession(), stored, ["HR"], () => vm, (_, t) =>
        {
            opened.Add(t);
            return true;
        }, default));

        Assert.Equal(["✎ 새 테이블"], opened);
        Assert.Equal("ORDERS_NEW", vm.NewTableName);
        Assert.Equal(["HR", "SAMPLE"], vm.NewTableOwners);
        Assert.Equal("ID", Assert.Single(vm.TableEdit!.Draft.Columns).Name);
        Assert.True(vm.HasDraftLink);

        // 같은 저장분을 또 열면(다른 탭) 이미 이 인스턴스가 쥐었으니 열지 않는다.
        var (other, _, _) = Create();
        Assert.False(await NewTableLauncher.OpenStoredAsync(null, _hook, new FakeSession(), stored, ["SAMPLE"], () => other, (_, _) => true, default));
        Assert.False(other.IsTableEditing);
    }

    [Fact]
    public async Task Resume_command_lists_kept_and_dead_drafts_and_opens_the_chosen_one()
    {
        await SeedAsync("KEPT", null);
        await SeedAsync("DEAD", Dead);
        await SeedAsync("LIVE", Me); // 이 앱이 쥔 것(열린 탭)은 목록에 없다
        await SeedAsync("OTHER", null, owner: "HR");
        var notes = new List<string>();
        TableDraftPickerViewModel? shown = null;
        var opened = new List<ObjectDetailViewModel>();
        var handler = new NewTableCommandHandler
        {
            SessionProvider = () => new FakeSession(),
            Owners = () => ["HR", "SAMPLE"],
            DraftHook = _hook,
            Notify = m =>
            {
                notes.Add(m);
                return Task.CompletedTask;
            },
            ShowPicker = picker =>
            {
                shown = picker;
                return Task.FromResult<TableDraftEntry?>(picker.Items.Single(i => i.Entry.Name == "DEAD").Entry);
            },
            CreateViewModel = () => Create().Vm,
            OpenDocument = (vm, _) =>
            {
                opened.Add(vm);
                return true;
            },
        };

        await handler.ExecuteAsync(new CommandContext(NewTableCommandHandler.ResumeId, FakeContextKeys.AllTrue,
            new SchemaNode(null, SchemaNodeKind.Schema, "SAMPLE") { Owner = "SAMPLE" }), default);

        Assert.Equal(["DEAD", "KEPT"], shown!.Items.Select(i => i.Entry.Name).Order());
        var vm = Assert.Single(opened);
        Assert.Equal("DEAD", vm.NewTableName);
        Assert.Empty(notes);

        // 남은 것이 없는 소유자는 알리기만 한다.
        await handler.ExecuteAsync(new CommandContext(NewTableCommandHandler.ResumeId, FakeContextKeys.AllTrue, "SCOTT"), default);
        Assert.Contains("SCOTT", Assert.Single(notes), StringComparison.Ordinal);
        Assert.Single(opened);
    }

    [Fact]
    public void Resume_command_sits_in_the_new_table_submenu_with_the_owner_key()
    {
        var command = ShellTestHelpers.CreateCommandRegistry().Find(NewTableCommandHandler.ResumeId)!;
        Assert.Contains("context:schema/detail/newTable@45", command.Placements);
        Assert.Contains("ownerNodeSelected", command.Enablement, StringComparison.Ordinal);
        Assert.Contains("!readOnlySession", command.Enablement, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Picker_deletes_rows_and_keeps_rows_someone_opened_meanwhile()
    {
        var kept = await SeedAsync("KEPT", null);
        var dead = await SeedAsync("DEAD", Dead);
        var entries = await _hook.Library.ListForOwnerAsync("dev-example", "SAMPLE", default);
        var picker = new TableDraftPickerViewModel("SAMPLE", entries, (e, ct) => _hook.Library.DeleteAsync(e, ct));
        Assert.Equal(2, picker.Items.Count);
        Assert.NotNull(picker.SelectedItem);

        picker.SelectedItem = picker.Items.Single(i => i.Entry.Id == kept.Id);
        await picker.DeleteSelectedAsync();
        Assert.False(_store.Rows.ContainsKey(kept.Id));
        Assert.Single(picker.Items);
        Assert.Contains("지웠습니다", picker.StatusText, StringComparison.Ordinal);

        // 목록을 읽은 뒤 다른 곳이 쥐었다 → 지우지 않는다.
        await _store.ClaimAsync(dead.Id, Dead, "999@1", default);
        await picker.DeleteSelectedAsync();
        Assert.True(_store.Rows.ContainsKey(dead.Id));
        Assert.Single(picker.Items);
        Assert.Contains("지우지 않았습니다", picker.StatusText, StringComparison.Ordinal);

        var closed = 0;
        picker.CloseRequested += (_, _) => closed++;
        picker.OpenSelected();
        Assert.Equal(dead.Id, picker.Chosen!.Id);
        Assert.Equal(1, closed);
        Assert.Equal("남겨 둠", new TableDraftPickerItem(kept).StateText);
        Assert.Equal("SAMPLE.(이름 없음)", new TableDraftPickerItem(kept with { Name = string.Empty }).Title);
    }

    [Fact]
    public async Task Startup_restore_opens_dead_drafts_when_their_connection_opens()
    {
        var mine = await SeedAsync("A", Dead);
        await SeedAsync("KEPT", null); // 놓인 것은 시작 때 되살리지 않는다(메뉴로)
        var other = await _store.SaveAsync(null, "prod-example", Dbms.Oracle, TableDraftFactory.Empty("SAMPLE") with { Name = "B" }, Dead, true, default);
        var services = new SingleService(typeof(Oranda.App.Features.TableEdit.INewTableDraftHook), _hook);
        var opened = new List<(string Connection, string Name)>();
        var startup = new TableDraftStartup(services, null!, null!)
        {
            OpenStored = (session, stored) =>
            {
                opened.Add((session.Profile.Name, stored.Draft.Name));
                return Task.FromResult(true);
            },
        };
        startup.Enqueue(await _hook.Library.ListForRestoreAsync(default));
        Assert.Equal(2, startup.Pending.Count);

        await startup.RestoreForAsync(new FakeSession(), default); // dev-example

        Assert.Equal([("dev-example", "A")], opened);
        Assert.Equal(other!.Id, Assert.Single(startup.Pending).Id); // 다른 접속 것은 그 접속이 열릴 때까지 기다린다
        await startup.RestoreForAsync(new FakeSession(), default);
        Assert.Single(opened);
        Assert.True(_store.Rows.ContainsKey(mine.Id));
    }

    private sealed class SingleService(Type type, object value) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == type ? value : null;
    }
}
