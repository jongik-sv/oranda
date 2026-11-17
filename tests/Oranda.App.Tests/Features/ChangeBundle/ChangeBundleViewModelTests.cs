using System.Text;
using Oranda.App.Features.ChangeBundle;
using Oranda.App.Features.Execution;
using Oranda.App.Tests.Shell;
using Oranda.Core.ChangeBundle;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Shell;
using Oranda.Language;
using Oranda.Language.Lexing;
using Oranda.Testing;

namespace Oranda.App.Tests.Features.ChangeBundle;

// 소유: te-bundle (T9). 반영 묶음 창(설계 9.2)·「실행…」 창(TE-15·TE-22)·의뢰용 스크립트 저장 창(9.4, TE-17). 모델 시험(대화상자는 가짜).
[Collection(ChangeBundleDialogsGroup.Name)]
public sealed class ChangeBundleViewModelTests : IDisposable
{
    private readonly ScriptedBundleDialogs _dialogs = new();
    private readonly IChangeBundleDialogs _previous = ChangeBundleDialogs.Current;

    public ChangeBundleViewModelTests() => ChangeBundleDialogs.Current = _dialogs;

    public void Dispose() => ChangeBundleDialogs.Current = _previous;

    private static FakeSession Session(string name, ConnectionEnvironment env, Dbms dbms = Dbms.Oracle, string user = "SAMPLE")
    {
        var profile = TestData.Profile(env) with { Name = name, User = user.ToLowerInvariant(), Id = Guid.NewGuid() };
        var server = TestData.Server() with { Dbms = dbms, SessionUser = user };
        return new FakeSession(profile, server);
    }

    private static async Task<(ChangeBundleViewModel Vm, MemoryBundleStore Store, MemoryBundleService Service)> CreateAsync(
        Func<MemoryBundleStore, Task>? seed = null, IExecutionService? exec = null)
    {
        var store = new MemoryBundleStore();
        if (seed is not null)
        {
            await seed(store);
        }

        var service = new MemoryBundleService(store);
        var vm = ChangeBundleViewModel.ForTests(store, service, new ChangeBundleOwnerRewriter(), new LanguageService(), exec);
        await vm.ReloadAsync();
        return (vm, store, service);
    }

    [Fact]
    public void Panel_and_command_are_registered()
    {
        var panel = ShellTestHelpers.RequirePanel("oranda.panel.changeBundle");
        Assert.Equal(ToolPanelKind.Tool, panel.Kind);
        Assert.Equal(DockLocation.Bottom, panel.DefaultLocation);
        Assert.Equal(46, panel.Order);
        Assert.Equal(typeof(ChangeBundleViewModel), panel.ViewModelType);

        var open = ShellTestHelpers.CreateCommandRegistry().Find("oranda.view.changeBundle");
        Assert.NotNull(open);
        Assert.Contains("menu:tools/changeBundle@51", open.Placements);
    }

    [Fact]
    public async Task Reload_shows_current_bundle_rows_marks_and_dims_unchecked()
    {
        var (vm, store, _) = await CreateAsync(async s =>
        {
            await s.CreateAsync("다른 묶음", Dbms.Oracle, ["SAMPLE"], makeCurrent: false, default);
            var b = await s.SeedAsync("릴리스 1", Dbms.Oracle, ["SAMPLE"], "ALTER TABLE SAMPLE.ORDERS ADD (NOTE VARCHAR2(10))",
                "CREATE INDEX SAMPLE.IX_ORDERS_NOTE ON SAMPLE.ORDERS (NOTE)");
            var items = await s.ListItemsAsync(b.Id, default);
            await s.SetCheckedAsync(items[1].Id, false, default);
            await s.AddRunAsync(new ChangeBundleRun(items[0].Id, ConnectionEnvironment.Development, "dev-example", DateTimeOffset.Now, true), default);
        });

        Assert.Equal(2, vm.Bundles.Count);
        Assert.Equal("릴리스 1", vm.CurrentBundle?.Name);
        Assert.Equal(2, vm.Rows.Count);
        Assert.Equal("1", vm.Rows[0].NumberText);
        Assert.StartsWith("✔ ", vm.Rows[0].DevelopmentText, StringComparison.Ordinal);
        Assert.Equal(string.Empty, vm.Rows[0].TestText);
        Assert.Equal("SQL 편집기", vm.Rows[0].SourceText);
        Assert.False(vm.Rows[0].IsDimmed);
        Assert.True(vm.Rows[1].IsDimmed);
        Assert.Equal("Oracle", vm.DbmsText);
        Assert.Equal("대상: SAMPLE", vm.TargetOwnersText);
        Assert.True(vm.CanRunOrSave);
        Assert.Empty(store.Runs.Where(r => !r.Succeeded));
    }

    [Fact]
    public async Task Empty_store_explains_how_to_start_and_nothing_can_run()
    {
        var (vm, _, _) = await CreateAsync();

        Assert.False(vm.HasBundle);
        Assert.False(vm.CanRunOrSave);
        Assert.Contains("＋", vm.StatusMessage, StringComparison.Ordinal);
        await vm.RunAsync();
        await vm.SaveScriptAsync();
        Assert.Empty(_dialogs.Asked);
    }

    [Fact]
    public async Task New_bundle_takes_current_schema_and_becomes_current()
    {
        var (vm, store, service) = await CreateAsync(s => s.SeedAsync("옛 묶음", Dbms.Oracle, ["OLD"], "DROP TABLE OLD.T"));
        var dev = Session("dev-example", ConnectionEnvironment.Development);
        vm.CurrentSessionProvider = () => dev;
        vm.Today = () => new DateTime(2026, 11, 17);
        service.Origin = ChangeBundleOrigin.From(dev, "SAMPLE");
        _dialogs.Prompts.Enqueue("  릴리스 2  ");

        await vm.NewBundleAsync();

        Assert.Contains("새 반영 묶음: 반영 묶음 2026-11-17", _dialogs.Asked);
        Assert.Equal("릴리스 2", vm.CurrentBundle?.Name);
        Assert.Equal(["SAMPLE"], vm.CurrentBundle!.TargetOwners);
        Assert.Empty(vm.Rows);
        Assert.Single((await store.ListBundlesAsync(default)).Where(b => b.IsCurrent));
    }

    [Fact]
    public async Task New_bundle_cancelled_or_blank_name_creates_nothing()
    {
        var (vm, store, _) = await CreateAsync();
        _dialogs.Prompts.Enqueue(null);
        _dialogs.Prompts.Enqueue("   ");

        await vm.NewBundleAsync();
        await vm.NewBundleAsync();

        Assert.Empty(await store.ListBundlesAsync(default));
    }

    [Fact]
    public async Task Choosing_another_bundle_makes_it_current()
    {
        var (vm, store, _) = await CreateAsync(async s =>
        {
            await s.SeedAsync("A", Dbms.Oracle, ["SAMPLE"], "DROP TABLE SAMPLE.A");
            await s.SeedAsync("B", Dbms.Oracle, ["SAMPLE"], "DROP TABLE SAMPLE.B");
        });
        Assert.Equal("B", vm.CurrentBundle?.Name);

        var reloaded = new TaskCompletionSource();
        vm.Reloaded += (_, _) => reloaded.TrySetResult();
        vm.CurrentBundle = vm.Bundles.Single(b => b.Name == "A");
        await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("A", (await store.GetCurrentAsync(default))?.Name);
        Assert.Equal("DROP TABLE SAMPLE.A", Assert.Single(vm.Rows).FullText);
    }

    [Fact]
    public async Task Delete_bundle_asks_and_cancel_keeps_it()
    {
        var (vm, store, _) = await CreateAsync(s => s.SeedAsync("릴리스 1", Dbms.Oracle, ["SAMPLE"], "DROP TABLE SAMPLE.A"));
        _dialogs.Confirms.Enqueue(false);
        await vm.DeleteBundleAsync();
        Assert.Single(await store.ListBundlesAsync(default));

        _dialogs.Confirms.Enqueue(true);
        await vm.DeleteBundleAsync();
        Assert.Empty(await store.ListBundlesAsync(default));
        Assert.False(vm.HasBundle);
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public async Task Rename_and_target_owners_are_written_with_name_rules()
    {
        var (vm, store, _) = await CreateAsync(s => s.SeedAsync("릴리스 1", Dbms.Oracle, ["SAMPLE"], "DROP TABLE SAMPLE.A"));
        _dialogs.Prompts.Enqueue("릴리스 1a");
        _dialogs.Prompts.Enqueue("sample, \"Mixed\", sample , app_2");

        await vm.RenameBundleAsync();
        await vm.EditTargetOwnersAsync();

        var b = (await store.GetCurrentAsync(default))!;
        Assert.Equal("릴리스 1a", b.Name);
        Assert.Equal(["SAMPLE", "Mixed", "APP_2"], b.TargetOwners);
        Assert.Equal(Dbms.Oracle, b.Dbms);
        Assert.Equal("대상: SAMPLE, Mixed, APP_2", vm.TargetOwnersText);
    }

    [Fact]
    public async Task Move_up_and_down_reorder_and_stop_at_the_ends()
    {
        var (vm, _, _) = await CreateAsync(s => s.SeedAsync("릴리스 1", Dbms.Oracle, ["SAMPLE"], "DROP TABLE SAMPLE.A", "DROP TABLE SAMPLE.B",
            "DROP TABLE SAMPLE.C"));
        vm.SelectedRow = vm.Rows[0];

        await vm.MoveUpAsync(); // 맨 위 — 그대로
        await vm.MoveDownAsync();

        Assert.Equal(["DROP TABLE SAMPLE.B", "DROP TABLE SAMPLE.A", "DROP TABLE SAMPLE.C"], vm.Rows.Select(r => r.FullText));
        Assert.Equal("DROP TABLE SAMPLE.A", vm.SelectedRow?.FullText); // 고른 줄을 따라간다
        Assert.Equal(2, vm.SelectedRow!.Number);
    }

    [Fact]
    public async Task Unchecking_writes_the_store_and_no_check_disables_run_and_save()
    {
        var (vm, store, _) = await CreateAsync(s => s.SeedAsync("릴리스 1", Dbms.Oracle, ["SAMPLE"], "DROP TABLE SAMPLE.A"));

        vm.Rows[0].IsChecked = false;
        await Task.Yield();

        var item = Assert.Single(await store.ListItemsAsync(vm.CurrentBundle!.Id, default));
        Assert.False(item.Checked);
        Assert.False(vm.CanRunOrSave);
    }

    [Fact]
    public async Task Remove_asks_then_deletes_the_statement()
    {
        var (vm, store, _) = await CreateAsync(s => s.SeedAsync("릴리스 1", Dbms.Oracle, ["SAMPLE"], "DROP TABLE SAMPLE.A", "DROP TABLE SAMPLE.B"));
        vm.SelectedRow = vm.Rows[0];
        _dialogs.Confirms.Enqueue(false);
        await vm.RemoveSelectedAsync();
        Assert.Equal(2, vm.Rows.Count);

        vm.SelectedRow = vm.Rows[0];
        _dialogs.Confirms.Enqueue(true);
        await vm.RemoveSelectedAsync();
        Assert.Equal("DROP TABLE SAMPLE.B", Assert.Single(await store.ListItemsAsync(vm.CurrentBundle!.Id, default)).Text);
    }

    [Fact]
    public async Task Editing_the_full_text_clears_its_run_marks()
    {
        var (vm, store, _) = await CreateAsync(async s =>
        {
            var b = await s.SeedAsync("릴리스 1", Dbms.Oracle, ["SAMPLE"], "ALTER TABLE SAMPLE.ORDERS ADD (NOTE VARCHAR2(10))");
            var item = (await s.ListItemsAsync(b.Id, default))[0];
            await s.AddRunAsync(new ChangeBundleRun(item.Id, ConnectionEnvironment.Test, "test-example", DateTimeOffset.Now, true), default);
        });
        Assert.NotEqual(string.Empty, vm.Rows[0].TestText);
        vm.SelectedRow = vm.Rows[0];
        _dialogs.Edits.Enqueue(new ChangeBundleTextEdit("ALTER TABLE SAMPLE.ORDERS ADD (NOTE VARCHAR2(20));", false));

        await vm.OpenSelectedAsync();

        Assert.Equal("ALTER TABLE SAMPLE.ORDERS ADD (NOTE VARCHAR2(20))", vm.Rows[0].FullText);
        Assert.Equal(string.Empty, vm.Rows[0].TestText);
        Assert.Empty(store.Runs);
    }

    [Fact]
    public async Task Editing_into_two_statements_is_refused_and_plsql_keeps_its_end()
    {
        var (vm, store, _) = await CreateAsync(s => s.SeedAsync("릴리스 1", Dbms.Oracle, ["SAMPLE"],
            "CREATE OR REPLACE PROCEDURE SAMPLE.P IS BEGIN NULL; END;"));
        vm.SelectedRow = vm.Rows[0];
        _dialogs.Edits.Enqueue(new ChangeBundleTextEdit("UPDATE SAMPLE.T SET A = 1; DROP TABLE SAMPLE.T", false));
        _dialogs.Edits.Enqueue(new ChangeBundleTextEdit("CREATE OR REPLACE PROCEDURE SAMPLE.P IS BEGIN NULL; NULL; END;", false));

        await vm.OpenSelectedAsync();
        Assert.Contains("문장 하나만", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Equal("CREATE OR REPLACE PROCEDURE SAMPLE.P IS BEGIN NULL; END;", (await store.ListItemsAsync(vm.CurrentBundle!.Id, default))[0].Text);

        vm.SelectedRow = vm.Rows[0];
        await vm.OpenSelectedAsync();
        Assert.Equal("CREATE OR REPLACE PROCEDURE SAMPLE.P IS BEGIN NULL; NULL; END;", (await store.ListItemsAsync(vm.CurrentBundle!.Id, default))[0].Text);
    }

    [Fact]
    public async Task Run_can_be_stopped_and_records_only_the_finished_statements()
    {
        ChangeBundleRunViewModel? run = null;
        var exec = new FakeExecutionService();
        exec.Responder = c =>
        {
            run!.Cancel(); // 첫 문장이 도는 사이 「중지」
            return new ExecutionResult(c.Statement, ExecutionStatus.Succeeded);
        };
        var (vm, store, _) = await CreateAsync(s => s.SeedAsync("릴리스 1", Dbms.Oracle, ["SAMPLE"], "COMMENT ON TABLE SAMPLE.A IS 'a'", "COMMENT ON TABLE SAMPLE.B IS 'b'"), exec);
        var test = Session("test-example", ConnectionEnvironment.Test);
        vm.SessionsProvider = () => [test];
        _dialogs.OnRun = async r =>
        {
            run = r;
            Assert.Equal("닫기", r.CloseText);
            await r.RunAsync();
        };

        await vm.RunAsync();

        Assert.Single(exec.Calls);
        Assert.True(Assert.Single(store.Runs).Succeeded);
        Assert.Contains("취소했다", run!.StatusText, StringComparison.Ordinal);
        Assert.False(run.IsRunning);
        Assert.False(ExecutionState.For(null).IsBusy(test.Id));
    }

    [Fact]
    public async Task Run_uses_the_routed_session_for_the_chosen_connection()
    {
        var exec = new FakeExecutionService();
        var (vm, _, _) = await CreateAsync(s => s.SeedAsync("릴리스 1", Dbms.Oracle, ["SAMPLE"], "COMMENT ON TABLE SAMPLE.A IS 'a'"), exec);
        var picked = Session("test-example", ConnectionEnvironment.Test);
        var workspace = new FakeSession(picked.Profile, picked.Server);
        vm.SessionsProvider = () => [picked];
        vm.RouteSession = s => ReferenceEquals(s, picked) ? workspace : s;
        _dialogs.OnRun = r => r.RunAsync();

        await vm.RunAsync();

        Assert.Same(workspace, Assert.Single(exec.Calls).Session);
    }

    [Fact]
    public async Task Run_failure_that_throws_ends_the_run_with_a_reason()
    {
        var exec = new FakeExecutionService { Responder = _ => throw new InvalidOperationException("세션이 끊겼다") };
        var (vm, _, _) = await CreateAsync(s => s.SeedAsync("릴리스 1", Dbms.Oracle, ["SAMPLE"], "COMMENT ON TABLE SAMPLE.A IS 'a'"), exec);
        vm.SessionsProvider = () => [Session("test-example", ConnectionEnvironment.Test)];
        ChangeBundleRunViewModel? run = null;
        _dialogs.OnRun = async r =>
        {
            run = r;
            await r.RunAsync();
        };

        await vm.RunAsync();

        Assert.False(run!.IsRunning);
        Assert.True(run.Finished);
        Assert.Contains("세션이 끊겼다", run.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Full_text_can_open_in_a_new_editor_without_changing_it()
    {
        var (vm, store, _) = await CreateAsync(s => s.SeedAsync("릴리스 1", Dbms.Oracle, ["SAMPLE"], "DROP TABLE SAMPLE.A"));
        string? opened = null;
        vm.EditorOpener = t =>
        {
            opened = t;
            return true;
        };
        vm.SelectedRow = vm.Rows[0];
        _dialogs.Edits.Enqueue(new ChangeBundleTextEdit(null, true));

        await vm.OpenSelectedAsync();

        Assert.Equal("DROP TABLE SAMPLE.A", opened);
        Assert.Equal("DROP TABLE SAMPLE.A", (await store.ListItemsAsync(vm.CurrentBundle!.Id, default))[0].Text);
    }

    [Fact]
    public async Task Run_defaults_to_a_test_connection_of_the_bundle_dbms_and_disables_other_dbms()
    {
        var exec = new FakeExecutionService();
        var (vm, _, _) = await CreateAsync(s => s.SeedAsync("릴리스 1", Dbms.Oracle, ["SAMPLE"], "DROP TABLE SAMPLE.A"), exec);
        var dev = Session("dev-example", ConnectionEnvironment.Development);
        var tibero = Session("tibero-example", ConnectionEnvironment.Test, Dbms.Tibero);
        var test = Session("test-example", ConnectionEnvironment.Test);
        vm.SessionsProvider = () => [dev, tibero, test];
        ChangeBundleRunViewModel? seen = null;
        _dialogs.OnRun = run =>
        {
            seen = run;
            return Task.CompletedTask;
        };

        await vm.RunAsync();

        Assert.NotNull(seen);
        Assert.Same(test, seen.SelectedConnection?.Session);
        Assert.False(seen.Connections.Single(c => c.Session == tibero).IsEnabled);
        Assert.Empty(exec.Calls); // 창을 닫기만 하면 실행하지 않는다
    }

    [Fact]
    public async Task Run_sends_checked_statements_in_order_with_owner_rewrite_and_marks_each_success()
    {
        var exec = new FakeExecutionService();
        var (vm, store, _) = await CreateAsync(async s =>
        {
            var b = await s.SeedAsync("릴리스 1", Dbms.Oracle, ["SAMPLE"], "ALTER TABLE SAMPLE.ORDERS ADD (NOTE VARCHAR2(10))",
                "COMMENT ON COLUMN SAMPLE.ORDERS.NOTE IS 'SAMPLE.ORDERS 메모'", "DROP TABLE SAMPLE.OLD_ORDERS");
            var items = await s.ListItemsAsync(b.Id, default);
            await s.SetCheckedAsync(items[2].Id, false, default);
        }, exec);
        var test = Session("test-example", ConnectionEnvironment.Test, user: "SAMPLE_T");
        vm.SessionsProvider = () => [test];
        _dialogs.OnRun = async run =>
        {
            run.OwnerMap.Single(o => o.From == "SAMPLE").To = "sample_t";
            await run.RunAsync();
        };

        await vm.RunAsync();

        Assert.Equal(
            ["ALTER TABLE SAMPLE_T.ORDERS ADD (NOTE VARCHAR2(10))", "COMMENT ON COLUMN SAMPLE_T.ORDERS.NOTE IS 'SAMPLE.ORDERS 메모'"],
            exec.Calls.Select(c => c.Statement.Text));
        Assert.All(exec.Calls, c => Assert.Equal(HistorySource.ChangeBundle, c.Options.Source));
        Assert.Equal(2, store.Runs.Count);
        Assert.All(store.Runs, r =>
        {
            Assert.True(r.Succeeded);
            Assert.Equal(ConnectionEnvironment.Test, r.Environment);
            Assert.Equal("test-example", r.ConnectionName);
        });
        Assert.NotEqual(string.Empty, vm.Rows[0].TestText);
        Assert.Equal(string.Empty, vm.Rows[2].TestText);
        Assert.Contains("2/2", vm.StatusMessage, StringComparison.Ordinal);
        Assert.False(ExecutionState.For(null).IsBusy(test.Id));
    }

    [Fact]
    public async Task Run_stops_at_the_first_failure_and_records_only_what_ran()
    {
        var exec = new FakeExecutionService
        {
            Responder = c => c.Statement.Text.Contains("SAMPLE.B", StringComparison.Ordinal)
                ? new ExecutionResult(c.Statement, ExecutionStatus.Failed) { Error = new DbError("ORA-00942", "table or view does not exist") }
                : new ExecutionResult(c.Statement, ExecutionStatus.Succeeded),
        };
        var (vm, store, _) = await CreateAsync(s => s.SeedAsync("릴리스 1", Dbms.Oracle, ["SAMPLE"], "COMMENT ON TABLE SAMPLE.A IS 'a'", "COMMENT ON TABLE SAMPLE.B IS 'b'",
            "COMMENT ON TABLE SAMPLE.C IS 'c'"), exec);
        vm.SessionsProvider = () => [Session("test-example", ConnectionEnvironment.Test)];
        ChangeBundleRunViewModel? run = null;
        _dialogs.OnRun = async r =>
        {
            run = r;
            await r.RunAsync();
            await r.RunAsync(); // 끝난 창은 다시 실행하지 않는다
        };

        await vm.RunAsync();

        Assert.Equal(2, exec.Calls.Count);
        Assert.Equal([true, false], store.Runs.Select(r => r.Succeeded));
        Assert.Contains("2번째 문장에서 멈췄다", run!.StatusText, StringComparison.Ordinal);
        Assert.Contains("ORA-00942", run.StatusText, StringComparison.Ordinal);
        Assert.False(run.CanRun);
    }

    [Fact]
    public async Task Run_refuses_statements_with_redacted_secrets()
    {
        var exec = new FakeExecutionService();
        var (vm, _, _) = await CreateAsync(async s =>
        {
            var b = (await s.CreateAsync("릴리스 1", Dbms.Oracle, ["SAMPLE"], makeCurrent: true, default))!;
            await s.AddItemAsync(b.Id, new NewChangeBundleItem("CREATE USER SAMPLE_APP IDENTIFIED BY ***", ChangeBundleItemSource.SqlEditor, "SAMPLE",
                null, ChangeBundleCautions.SecretRedacted), default);
        }, exec);
        vm.SessionsProvider = () => [Session("test-example", ConnectionEnvironment.Test)];
        ChangeBundleRunViewModel? run = null;
        _dialogs.OnRun = async r =>
        {
            run = r;
            await r.RunAsync();
        };

        await vm.RunAsync();

        Assert.Empty(exec.Calls);
        Assert.Contains("가린", run!.StatusText, StringComparison.Ordinal);
        Assert.True(vm.Rows[0].HasWarning);
        Assert.Contains("***", vm.Rows[0].WarningText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Production_multi_statement_confirms_once_and_sends_each_statement_preconfirmed()
    {
        var exec = new FakeExecutionService();
        var interaction = new FakeExecutionInteraction();
        var (vm, store, _) = await CreateAsync(s => s.SeedAsync("릴리스 1", Dbms.Oracle, ["SAMPLE"],
            "ALTER TABLE SAMPLE.ORDERS ADD (NOTE VARCHAR2(10))", "CREATE INDEX SAMPLE.IX_ORDERS_NOTE ON SAMPLE.ORDERS (NOTE)",
            "COMMENT ON COLUMN SAMPLE.ORDERS.NOTE IS 'memo'"), exec);
        vm.SessionsProvider = () => [Session("prod-example", ConnectionEnvironment.Production)];
        var checks = new CountingChecks();
        vm.ConfigureRun = run =>
        {
            run.Policy = new ConfirmEveryDdl();
            run.Interaction = interaction;
            run.ChecksFor = _ => checks;
        };
        _dialogs.OnRun = r => r.RunAsync();

        await vm.RunAsync();

        Assert.Single(interaction.Confirmations); // 확인 창 한 번(TE-21·TE-34)
        Assert.Equal(3, exec.Calls.Count); // 문장별로, 한 블록이 아니다
        Assert.All(exec.Calls, c =>
        {
            Assert.Equal(StatementKind.Ddl, c.Statement.Kind);
            Assert.Contains(c.Statement.Text, c.Options.PreConfirmed!);
        });
        Assert.Equal(3, store.Runs.Count(r => r.Succeeded && r.Environment == ConnectionEnvironment.Production));
    }

    [Fact]
    public async Task Production_confirmation_declined_sends_nothing()
    {
        var exec = new FakeExecutionService();
        var (vm, store, _) = await CreateAsync(s => s.SeedAsync("릴리스 1", Dbms.Oracle, ["SAMPLE"], "ALTER TABLE SAMPLE.ORDERS ADD (NOTE VARCHAR2(10))"), exec);
        vm.SessionsProvider = () => [Session("prod-example", ConnectionEnvironment.Production)];
        vm.ConfigureRun = run =>
        {
            run.Policy = new ConfirmEveryDdl();
            run.Interaction = new FakeExecutionInteraction { ConfirmAnswer = false };
        };
        ChangeBundleRunViewModel? seen = null;
        _dialogs.OnRun = async r =>
        {
            seen = r;
            await r.RunAsync();
        };

        await vm.RunAsync();

        Assert.Empty(exec.Calls);
        Assert.Empty(store.Runs);
        Assert.False(seen!.Finished); // 접속을 바꿔 다시 실행할 수 있다
    }

    [Fact]
    public async Task Blocking_check_stops_the_run_and_shows_the_line()
    {
        var exec = new FakeExecutionService();
        var (vm, _, _) = await CreateAsync(s => s.SeedAsync("릴리스 1", Dbms.Oracle, ["SAMPLE"],
            "ALTER TABLE SAMPLE.ORDERS MODIFY (NOTE VARCHAR2(5) NOT NULL)", "COMMENT ON TABLE SAMPLE.ORDERS IS 'x'"), exec);
        vm.SessionsProvider = () => [Session("dev-example", ConnectionEnvironment.Development)];
        var checks = new CountingChecks { Nulls = 3 };
        vm.ConfigureRun = run => run.ChecksFor = _ => checks;
        ChangeBundleRunViewModel? seen = null;
        _dialogs.OnRun = async r =>
        {
            seen = r;
            await r.RunAsync();
        };

        await vm.RunAsync();

        Assert.Empty(exec.Calls);
        Assert.Equal(["NOTE"], checks.NullColumns);
        Assert.Equal(new DbObjectRef("SAMPLE", "ORDERS", "TABLE"), checks.LastTable);
        Assert.Contains(seen!.FindingLines, l => l.StartsWith('✖') && l.Contains("#1 NOTE", StringComparison.Ordinal));
        Assert.Contains("✖", seen.StatusText, StringComparison.Ordinal);
        Assert.True(seen.CanRun); // 고친 뒤(다른 접속 등) 다시 누를 수 있다
    }

    [Fact]
    public async Task Test_connection_warning_needs_a_second_press_and_development_does_not()
    {
        var exec = new FakeExecutionService();
        var (vm, _, _) = await CreateAsync(s => s.SeedAsync("릴리스 1", Dbms.Oracle, ["SAMPLE"], "ALTER TABLE SAMPLE.ORDERS DROP COLUMN OLD_NOTE"), exec);
        var test = Session("test-example", ConnectionEnvironment.Test);
        var dev = Session("dev-example", ConnectionEnvironment.Development);
        vm.SessionsProvider = () => [test, dev];
        vm.ConfigureRun = run => run.ChecksFor = _ => new CountingChecks();
        ChangeBundleRunViewModel? seen = null;
        _dialogs.OnRun = async r =>
        {
            seen = r;
            await r.RunAsync();
            Assert.Empty(exec.Calls); // ⚠ 를 보인다
            Assert.Equal("그래도 실행", r.RunText);
            Assert.Contains(r.FindingLines, l => l.StartsWith('⚠'));
            await r.RunAsync();
        };

        await vm.RunAsync();
        Assert.Single(exec.Calls);

        _dialogs.OnRun = async r =>
        {
            r.SelectedConnection = r.Connections.Single(c => c.Session == dev);
            await r.RunAsync(); // 개발 접속은 ⚠ 만이면 창 없이(TE-20)
        };
        await vm.RunAsync();
        Assert.Equal(2, exec.Calls.Count);
        Assert.Same(dev, exec.Calls[1].Session);
    }

    [Fact]
    public async Task Stop_during_the_pre_run_check_sends_nothing_and_frees_the_session()
    {
        var exec = new FakeExecutionService();
        var (vm, _, _) = await CreateAsync(s => s.SeedAsync("릴리스 1", Dbms.Oracle, ["SAMPLE"], "ALTER TABLE SAMPLE.ORDERS MODIFY (NOTE NOT NULL)"), exec);
        var dev = Session("dev-example", ConnectionEnvironment.Development);
        vm.SessionsProvider = () => [dev];
        ChangeBundleRunViewModel? seen = null;
        var checks = new CountingChecks { OnNullCount = () => seen!.Cancel() };
        vm.ConfigureRun = run => run.ChecksFor = _ => checks;
        _dialogs.OnRun = async r =>
        {
            seen = r;
            await r.RunAsync();
        };

        await vm.RunAsync();

        Assert.Empty(exec.Calls);
        Assert.Contains("취소", seen!.StatusText, StringComparison.Ordinal);
        Assert.False(seen.IsRunning);
        Assert.False(seen.Finished);
        Assert.False(ExecutionState.For(null).IsBusy(dev.Id));
    }

    [Fact]
    public async Task Missing_check_reader_turns_checks_into_not_checked_warnings()
    {
        var exec = new FakeExecutionService();
        var (vm, _, _) = await CreateAsync(s => s.SeedAsync("릴리스 1", Dbms.Oracle, ["SAMPLE"], "ALTER TABLE SAMPLE.ORDERS MODIFY (NOTE VARCHAR2(5))"), exec);
        vm.SessionsProvider = () => [Session("dev-example", ConnectionEnvironment.Development)];
        ChangeBundleRunViewModel? seen = null;
        _dialogs.OnRun = async r =>
        {
            seen = r;
            await r.RunAsync();
        };

        await vm.RunAsync();

        Assert.Single(exec.Calls);
        Assert.Contains(seen!.Findings, f => f.Code == CheckCode.NotChecked);
    }

    [Fact]
    public async Task Run_without_open_connections_explains_and_opens_nothing()
    {
        var (vm, _, _) = await CreateAsync(s => s.SeedAsync("릴리스 1", Dbms.Oracle, ["SAMPLE"], "DROP TABLE SAMPLE.A"), new FakeExecutionService());
        var opened = false;
        _dialogs.OnRun = _ =>
        {
            opened = true;
            return Task.CompletedTask;
        };

        await vm.RunAsync();

        Assert.False(opened);
        Assert.Contains(_dialogs.Asked, a => a.StartsWith("묶음 실행:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Save_writes_checked_statements_as_utf8_without_bom_with_owner_rewrite()
    {
        using var dir = new TempDirectory("oranda-bundle-");
        Directory.CreateDirectory(dir.Path);
        var (vm, _, _) = await CreateAsync(async s =>
        {
            var b = await s.SeedAsync("릴리스 1/2", Dbms.Oracle, ["SAMPLE"], "ALTER TABLE SAMPLE.ORDERS ADD (NOTE VARCHAR2(10))",
                "-- 메모 SAMPLE.X\nDROP TABLE SAMPLE.OLD_ORDERS", "CREATE INDEX SAMPLE.IX_A ON SAMPLE.ORDERS (NOTE)");
            var items = await s.ListItemsAsync(b.Id, default);
            await s.SetCheckedAsync(items[2].Id, false, default);
        });
        var path = Path.Combine(dir.Path, "out.sql");
        ChangeBundleSaveViewModel? save = null;
        _dialogs.OnSave = async v =>
        {
            save = v;
            v.OwnerMap.Single().To = "SAMPLE_P";
            Assert.True(await v.SaveAsync(path));
        };

        await vm.SaveScriptAsync();

        Assert.Equal("릴리스_1_2.sql", save!.SuggestedFileName);
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "BOM 이 없어야 한다");
        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("-- 이름: 릴리스 1/2", text, StringComparison.Ordinal);
        Assert.Contains("-- 대상 DBMS: Oracle", text, StringComparison.Ordinal);
        Assert.Contains("-- 문장 수: 2", text, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE SAMPLE_P.ORDERS ADD (NOTE VARCHAR2(10));", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SAMPLE_P.X", text, StringComparison.Ordinal); // 주석 안은 바꾸지 않는다
        Assert.Contains("DROP TABLE SAMPLE_P.OLD_ORDERS;", text, StringComparison.Ordinal);
        Assert.DoesNotContain("IX_A", text, StringComparison.Ordinal);
        Assert.DoesNotContain("dev-example", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_refuses_non_ddl_and_writes_no_file()
    {
        using var dir = new TempDirectory("oranda-bundle-");
        Directory.CreateDirectory(dir.Path);
        var (vm, _, _) = await CreateAsync(s => s.SeedAsync("릴리스 1", Dbms.Oracle, ["SAMPLE"], "DROP TABLE SAMPLE.A",
            "UPDATE SAMPLE.ORDERS SET NOTE = 'x'"));
        var path = Path.Combine(dir.Path, "out.sql");
        ChangeBundleSaveViewModel? save = null;
        _dialogs.OnSave = async v =>
        {
            save = v;
            Assert.False(await v.SaveAsync(path));
        };

        await vm.SaveScriptAsync();

        Assert.False(File.Exists(path));
        Assert.Contains("저장하지 않았다", save!.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_text_uses_the_request_script_format_with_plsql_slash()
    {
        var bundle = new ChangeBundleInfo(1, "릴리스 1", DateTimeOffset.Now, DateTimeOffset.Now, Dbms.Tibero, ["SAMPLE"], true);
        var item = new ChangeBundleItem(1, 1, 1, "CREATE OR REPLACE PROCEDURE SAMPLE.P IS BEGIN NULL; END;", ChangeBundleItemSource.SqlEditor, "SAMPLE",
            "P", true, DateTimeOffset.Now, "h", ChangeBundleCautions.None, null);
        var vm = new ChangeBundleSaveViewModel(bundle, [item], new ChangeBundleOwnerRewriter(), new LanguageService());

        var text = vm.BuildText(new DateOnly(2026, 11, 17), "\n");

        Assert.StartsWith("-- 이름: 릴리스 1\n-- 만든 날짜: 2026-11-17\n-- 대상 DBMS: Tibero\n-- 문장 수: 1\n", text, StringComparison.Ordinal);
        Assert.Contains("-- " + RequestScriptWriter.TablespaceNote, text, StringComparison.Ordinal);
        Assert.EndsWith("END;\n/\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Added_notice_shows_briefly_after_a_bundle_change()
    {
        var bus = new FakeEventBus();
        var store = new MemoryBundleStore();
        await store.SeedAsync("릴리스 1", Dbms.Oracle, ["SAMPLE"], "DROP TABLE SAMPLE.A");
        using var vm = ChangeBundleViewModel.ForTests(store, new MemoryBundleService(store), bus: bus);
        await vm.ReloadAsync();

        bus.Publish(new ChangeBundleChanged(1, 2, 0));

        Assert.Equal("+2", vm.NoticeText);
        Assert.True(vm.HasNotice);
    }

    [Theory]
    [InlineData("반영 묶음", 3, 0, "반영 묶음 (+3)")]
    [InlineData("Change Bundle", 0, 1, "Change Bundle (✔1)")]
    public void Tab_title_flash_text(string title, int added, int marked, string expected) =>
        Assert.Equal(expected, ChangeBundleNotices.FlashTitle(title, added, marked));

    [Fact]
    public void Dbms_mismatch_message_names_the_bundle_and_both_dbms()
    {
        var text = ChangeBundleNotices.MismatchText(new ChangeBundleDbmsMismatch(1, "릴리스 1", Dbms.Oracle, Dbms.Tibero));

        Assert.Contains("릴리스 1", text, StringComparison.Ordinal);
        Assert.Contains("Oracle", text, StringComparison.Ordinal);
        Assert.Contains("Tibero", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Owner_map_keeps_a_lowercase_quoted_owner_when_left_as_default()
    {
        Assert.Empty(OwnerMapRow.ToMap([new OwnerMapRow("sample")]));
        Assert.Equal("SAMPLE_T", OwnerMapRow.ToMap([new OwnerMapRow("sample") { To = "sample_t" }])["sample"]);
    }

    [Fact]
    public void Owner_map_uppercases_plain_names_and_skips_unchanged()
    {
        var rows = new[] { new OwnerMapRow("SAMPLE") { To = "sample_t" }, new OwnerMapRow("APP") { To = "APP" }, new OwnerMapRow("X") { To = " " },
            new OwnerMapRow("Y") { To = "\"Mixed\"" } };

        var map = OwnerMapRow.ToMap(rows);

        Assert.Equal(2, map.Count);
        Assert.Equal("SAMPLE_T", map["SAMPLE"]);
        Assert.Equal("\"Mixed\"", map["Y"]);
    }

    /// <summary>DDL 마다 확인을 원하는 정책(운영 확인 시험용).</summary>
    private sealed class ConfirmEveryDdl : IExecutionPolicy
    {
        public ValueTask<PolicyDecision> EvaluateAsync(PolicyContext ctx, SqlStatement stmt, CancellationToken ct) =>
            ValueTask.FromResult(new PolicyDecision(PolicyDecisionKind.Confirm, PolicyRule.None, "운영 DDL"));
    }

    /// <summary>건수를 정해 두는 검사 읽기.</summary>
    private sealed class CountingChecks : ITableEditChecks
    {
        public Dbms Dbms => Dbms.Oracle;

        public long Nulls { get; init; }

        public List<string> NullColumns { get; } = [];

        public DbObjectRef? LastTable { get; private set; }

        public Task<long> CountRowsAsync(ISession s, DbObjectRef table, CancellationToken ct) => Task.FromResult(0L);

        public Task<long?> NumRowsAsync(ISession s, DbObjectRef table, CancellationToken ct) => Task.FromResult<long?>(10);

        public Task<long> OverflowCountAsync(ISession s, DbObjectRef table, string column, ColumnDraft target, CancellationToken ct) => Task.FromResult(0L);

        public Action? OnNullCount { get; init; }

        public Task<long> NullCountAsync(ISession s, DbObjectRef table, string column, CancellationToken ct)
        {
            OnNullCount?.Invoke();
            ct.ThrowIfCancellationRequested();
            NullColumns.Add(column);
            LastTable = table;
            return Task.FromResult(Nulls);
        }

        public Task<long> NonNullCountAsync(ISession s, DbObjectRef table, string column, CancellationToken ct) => Task.FromResult(0L);

        public Task<long> DuplicateKeyCountAsync(ISession s, DbObjectRef table, IReadOnlyList<string> columns, CancellationToken ct) => Task.FromResult(0L);

        public Task<long> OrphanCountAsync(ISession s, DbObjectRef table, IReadOnlyList<string> columns, DbObjectRef referencedTable,
            IReadOnlyList<string> referencedColumns, CancellationToken ct) => Task.FromResult(0L);

        public Task<long> CheckViolationCountAsync(ISession s, DbObjectRef table, string condition, CancellationToken ct) => Task.FromResult(0L);

        public Task<ExpressionPreview> PreviewExpressionAsync(ISession s, DbObjectRef table, string expression, IReadOnlyList<string> columns, int rows,
            CancellationToken ct) => Task.FromResult(new ExpressionPreview([], []));

        public Task<string?> ValidateExpressionAsync(ISession s, string owner, IReadOnlyList<ColumnDraft> columns, string expression, CancellationToken ct) =>
            Task.FromResult<string?>(null);

        public Task<bool> NameExistsAsync(ISession s, string owner, string name, TableEditNameKind kind, DbObjectRef? table, CancellationToken ct) =>
            Task.FromResult(false);

        public Task<IReadOnlyList<DbObjectRef>> DependentsAsync(ISession s, DbObjectRef table, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DbObjectRef>>([]);
    }
}

/// <summary><see cref="ChangeBundleDialogs.Current"/> 를 바꾸는 시험은 함께 돌지 않는다.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ChangeBundleDialogsGroup
{
    public const string Name = "ChangeBundleDialogs";
}
