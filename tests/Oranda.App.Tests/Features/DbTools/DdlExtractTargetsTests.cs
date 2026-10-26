using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.DbTools;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Testing;
using Xunit.Abstractions;

namespace Oranda.App.Tests.Features.DbTools;

/// <summary>TSK-13-01 DDL 일괄 추출 대상 미리보기 시험. <c>ORANDA_RENDER_DIR</c> 가 있으면 상태별 라이트·다크 PNG 를 남긴다.</summary>
public class DdlExtractTargetsTests(ITestOutputHelper output)
{
    private static readonly DateTime Ddl = new(2026, 10, 20, 14, 5, 0);

    private static ObjectSearchRow Row(string type, string name, string status = "VALID", DateTime? ddl = null)
        => new(new DbObjectRef("SCOTT", name, type), status, null, ddl ?? Ddl, null);

    private static readonly ObjectSearchRow[] Sample =
    [
        Row("VIEW", "EMP_V"),
        Row("TABLE", "EMP"),
        Row("TABLE", "DEPT"),
        Row("PROCEDURE", "BAD_P", "INVALID", new DateTime(2026, 10, 25, 9, 30, 0)),
    ];

    private static DdlExtractViewModel NewVm(FakeAdmin admin, FakeMeta? meta = null, bool attach = true)
    {
        var vm = new DdlExtractViewModel(meta ?? new FakeMeta(), admin) { TargetDebounceMilliseconds = 20 };
        if (attach)
        {
            vm.Attach(new FakeSession());
        }

        return vm;
    }

    // ---- 목록 만들기 ----

    [Fact]
    public async Task Attaching_a_session_lists_the_targets_sorted_by_type_then_name_all_checked()
    {
        var admin = new FakeAdmin { Rows = Sample };
        var vm = NewVm(admin);
        await vm.PendingRefresh;

        Assert.Equal(DdlTargetState.Ready, vm.TargetState);
        Assert.Equal(["DEPT", "EMP", "EMP_V", "BAD_P"], vm.Targets.Select(t => t.Name));
        Assert.All(vm.Targets, t => Assert.True(t.IsChecked));
        Assert.Equal(4, vm.CheckedCount);
        Assert.True(vm.AllChecked);
        Assert.Equal("대상 4개 중 4개 선택", vm.TargetSummary);
        Assert.Equal("대상 (4)", vm.TargetsHeader);
        Assert.Equal("대상 4개 중 4개 선택", vm.StatusText);
        Assert.Equal(0, vm.SelectedTab);
        Assert.False(vm.IsLoadingTargets);

        var bad = vm.Targets.Single(t => t.Name == "BAD_P");
        Assert.True(bad.IsInvalid);
        Assert.Equal("2026-10-25 09:30", bad.LastDdl);
        Assert.False(vm.Targets.Single(t => t.Name == "EMP").IsInvalid);
    }

    [Fact]
    public async Task The_query_carries_owner_types_name_pattern_and_invalid_only()
    {
        var admin = new FakeAdmin { Rows = Sample };
        var vm = NewVm(admin);
        await vm.PendingRefresh;

        vm.NameText = "emp";
        vm.InvalidOnly = true;
        foreach (var t in vm.TypeChoices.Where(t => t.Name is not ("TABLE" or "VIEW")))
        {
            t.IsSelected = false;
        }

        await vm.PendingRefresh;

        var q = admin.Queries[^1];
        Assert.Equal("SCOTT", q.Owner);
        Assert.Equal("%EMP%", q.NamePattern);
        Assert.True(q.InvalidOnly);
        Assert.Equal(["TABLE", "VIEW"], q.ObjectTypes);
        Assert.Equal(DdlExtractViewModel.MaxTargets + 1, q.Limit);
    }

    [Fact]
    public async Task Rapid_input_is_debounced_into_a_single_lookup_with_the_last_value()
    {
        var admin = new FakeAdmin { Rows = Sample };
        var vm = NewVm(admin);
        await vm.PendingRefresh;
        var before = admin.Queries.Count;
        vm.TargetDebounceMilliseconds = 80;

        vm.NameText = "e";
        vm.NameText = "em";
        vm.NameText = "emp";
        await vm.PendingRefresh;

        Assert.Equal(before + 1, admin.Queries.Count);
        Assert.Equal("%EMP%", admin.Queries[^1].NamePattern);
    }

    [Fact]
    public async Task A_new_input_cancels_the_lookup_in_flight_and_only_the_newest_result_is_shown()
    {
        var gate = new TaskCompletionSource();
        var admin = new FakeAdmin
        {
            Handler = async (q, ct) =>
            {
                if (q.NamePattern == "%SLOW%")
                {
                    await gate.Task.WaitAsync(ct);
                    return [Row("TABLE", "SLOW_T")];
                }

                return [Row("TABLE", "FAST_T")];
            },
        };
        var vm = NewVm(admin);
        await vm.PendingRefresh;
        vm.TargetDebounceMilliseconds = 0;

        vm.NameText = "slow";
        var slow = vm.PendingRefresh;
        await admin.WaitStarted("%SLOW%");
        Assert.True(vm.IsLoadingTargets);

        vm.NameText = "fast";
        await vm.PendingRefresh;
        gate.TrySetResult();
        await slow;

        Assert.Equal(["FAST_T"], vm.Targets.Select(t => t.Name));
        Assert.Contains("%SLOW%", admin.Cancelled);
        Assert.False(vm.IsLoadingTargets);
    }

    [Fact]
    public async Task The_old_list_stays_until_the_new_one_arrives_as_a_whole()
    {
        var gate = new TaskCompletionSource();
        var admin = new FakeAdmin
        {
            Handler = async (q, ct) =>
            {
                if (q.NamePattern == "%NEW%")
                {
                    await gate.Task.WaitAsync(ct);
                    return [Row("TABLE", "NEW_1"), Row("TABLE", "NEW_2")];
                }

                return Sample;
            },
        };
        var vm = NewVm(admin);
        await vm.PendingRefresh;
        vm.TargetDebounceMilliseconds = 0;
        var changes = new List<string?>();
        vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        vm.NameText = "new";
        await admin.WaitStarted("%NEW%");

        Assert.Equal(4, vm.Targets.Count); // 조회 중에도 앞 목록이 그대로 있다
        Assert.Equal(DdlTargetState.Ready, vm.TargetState);
        Assert.True(vm.IsLoadingTargets);

        gate.SetResult();
        await vm.PendingRefresh;

        Assert.Equal(["NEW_1", "NEW_2"], vm.Targets.Select(t => t.Name));
        Assert.Equal(1, changes.Count(c => c == nameof(DdlExtractViewModel.Targets)));
    }

    // ---- 체크 ----

    [Fact]
    public async Task Unchecking_updates_counts_header_check_and_the_plan_lists_only_checked_objects()
    {
        var vm = NewVm(new FakeAdmin { Rows = Sample });
        await vm.PendingRefresh;

        vm.Targets.Single(t => t.Name == "EMP_V").IsChecked = false;

        Assert.Equal(3, vm.CheckedCount);
        Assert.Null(vm.AllChecked);
        Assert.Equal("대상 4개 중 3개 선택", vm.StatusText);
        var plan = vm.BuildPlan();
        Assert.NotNull(plan.Objects);
        Assert.Equal(["DEPT", "EMP", "BAD_P"], plan.Objects!.Select(o => o.Name));
    }

    [Fact]
    public async Task The_header_check_selects_or_clears_all_and_from_mixed_it_selects_all()
    {
        var vm = NewVm(new FakeAdmin { Rows = Sample });
        await vm.PendingRefresh;

        vm.AllChecked = false;
        Assert.Equal(0, vm.CheckedCount);
        Assert.All(vm.Targets, t => Assert.False(t.IsChecked));
        Assert.False(vm.AllChecked);

        vm.Targets[0].IsChecked = true;
        Assert.Null(vm.AllChecked);
        vm.AllChecked = false; // 일부만 골라 둔 칸을 누르면 비우지 않고 모두 고른다
        Assert.Equal(4, vm.CheckedCount);
        Assert.True(vm.AllChecked);

        vm.AllChecked = false;
        Assert.Equal(0, vm.CheckedCount);
        Assert.Equal("대상 4개 중 0개 선택", vm.TargetSummary);
    }

    [Fact]
    public async Task A_filter_change_starts_over_with_everything_checked()
    {
        var vm = NewVm(new FakeAdmin { Rows = Sample });
        await vm.PendingRefresh;
        vm.AllChecked = false;

        vm.NameText = "x";
        await vm.PendingRefresh;

        Assert.Equal(4, vm.CheckedCount);
        Assert.True(vm.AllChecked);
    }

    // ---- 추출 ----

    [Fact]
    public async Task Run_extracts_only_the_checked_objects_and_skips_the_filter_lookup()
    {
        var dir = TempDir();
        try
        {
            var meta = new FakeMeta();
            var vm = NewVm(new FakeAdmin { Rows = [Row("TABLE", "A"), Row("TABLE", "B"), Row("TABLE", "C")] }, meta);
            await vm.PendingRefresh;
            vm.OutputDirectory = dir;
            vm.FilePerObject = true;
            vm.Targets.Single(t => t.Name == "B").IsChecked = false;

            await vm.RunAsync();

            Assert.Equal(["A", "C"], meta.DdlRequests.Order(StringComparer.Ordinal));
            Assert.Equal(0, meta.ListCalls);
            Assert.Equal(2, vm.Files.Count);
            Assert.Equal(["A.sql", "C.sql"], vm.Files.Select(Path.GetFileName).Order(StringComparer.Ordinal));
            Assert.Equal(2, vm.Results.Count);
            Assert.Equal(1, vm.SelectedTab);
            Assert.Contains("2개 추출", vm.StatusText);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public async Task Run_with_nothing_checked_says_so_and_runs_nothing()
    {
        var meta = new FakeMeta();
        var vm = NewVm(new FakeAdmin { Rows = Sample }, meta);
        await vm.PendingRefresh;
        vm.OutputDirectory = TempDir();
        vm.AllChecked = false;
        var ran = false;
        vm.Runner = (_, _, _, _, _) =>
        {
            ran = true;
            return Task.FromResult(new DdlExtractOutcome([], []));
        };

        await vm.RunAsync();

        Assert.False(ran);
        Assert.Equal("체크한 객체가 없다", vm.StatusText);
        Assert.Equal(0, vm.SelectedTab);
    }

    [Fact]
    public async Task Run_waits_for_a_lookup_in_flight_instead_of_using_the_old_list()
    {
        var dir = TempDir();
        try
        {
            var gate = new TaskCompletionSource();
            var meta = new FakeMeta();
            var admin = new FakeAdmin
            {
                Handler = async (q, ct) =>
                {
                    if (q.NamePattern == "%NEW%")
                    {
                        await gate.Task.WaitAsync(ct);
                        return [Row("TABLE", "NEW_ONLY")];
                    }

                    return [Row("TABLE", "OLD_ONLY")];
                },
            };
            var vm = NewVm(admin, meta);
            await vm.PendingRefresh;
            vm.OutputDirectory = dir;
            vm.TargetDebounceMilliseconds = 0;
            vm.NameText = "new";
            await admin.WaitStarted("%NEW%");

            var run = vm.RunAsync();
            Assert.False(run.IsCompleted);
            gate.SetResult();
            await run;

            Assert.Equal(["NEW_ONLY"], meta.DdlRequests);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public async Task Without_a_search_provider_the_targets_cannot_be_previewed_and_run_extracts_by_the_filter()
    {
        var dir = TempDir();
        try
        {
            var meta = new FakeMeta();
            var vm = new DdlExtractViewModel(meta);
            vm.Attach(new FakeSession());
            vm.OutputDirectory = dir;

            Assert.Equal(DdlTargetState.Unavailable, vm.TargetState);
            Assert.Null(vm.BuildPlan().Objects);

            await vm.RunAsync();

            Assert.Equal(1, meta.ListCalls);
            Assert.Equal(2, vm.Results.Count);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // ---- 상태 ----

    [Fact]
    public async Task States_no_connection_owner_type_empty_and_error_each_have_a_message_and_a_next_step()
    {
        var admin = new FakeAdmin { Rows = [] };
        var vm = NewVm(admin, attach: false);
        Assert.Equal(DdlTargetState.NoConnection, vm.TargetState);
        Assert.Contains("접속", vm.TargetMessage);
        Assert.Equal("대상", vm.TargetsHeader);

        vm.Attach(new FakeSession());
        await vm.PendingRefresh;
        Assert.Equal(DdlTargetState.Empty, vm.TargetState);
        Assert.Contains("조건에 맞는 객체가 없습니다", vm.TargetMessage);
        Assert.Contains("이름 필터", vm.TargetHint);
        Assert.Equal("조건에 맞는 객체가 없습니다", vm.StatusText);
        Assert.False(vm.ShowTargetRows);
        vm.InvalidOnly = true;
        await vm.PendingRefresh;
        Assert.Contains("INVALID", vm.TargetHint);
        vm.InvalidOnly = false;
        await vm.PendingRefresh;

        vm.Owner = " ";
        Assert.Equal(DdlTargetState.NeedOwner, vm.TargetState);
        Assert.Contains("스키마", vm.TargetMessage);
        Assert.Equal(0, vm.TargetCount);

        vm.Owner = "scott";
        await vm.PendingRefresh;
        foreach (var t in vm.TypeChoices)
        {
            t.IsSelected = false;
        }

        Assert.Equal(DdlTargetState.NeedType, vm.TargetState);

        admin.Handler = (_, _) => throw new InvalidOperationException("ORA-00942 테이블 또는 뷰가 존재하지 않습니다");
        vm.TypeChoices[2].IsSelected = true;
        await vm.PendingRefresh;
        Assert.Equal(DdlTargetState.Error, vm.TargetState);
        Assert.Contains("ORA-00942", vm.TargetHint);
        Assert.Contains("F5", vm.TargetHint);
        Assert.Contains("ORA-00942", vm.StatusText);

        await vm.RunAsync();
        Assert.Contains("대상을 불러오지 못했다", vm.StatusText);
    }

    [Fact]
    public async Task F5_style_refresh_recovers_from_an_error()
    {
        var fail = true;
        var admin = new FakeAdmin
        {
            Handler = (_, _) => fail ? throw new InvalidOperationException("연결이 끊겼다") : Task.FromResult<IReadOnlyList<ObjectSearchRow>>(Sample),
        };
        var vm = NewVm(admin);
        await vm.PendingRefresh;
        Assert.Equal(DdlTargetState.Error, vm.TargetState);

        fail = false;
        await vm.RefreshTargetsAsync();

        Assert.Equal(DdlTargetState.Ready, vm.TargetState);
        Assert.Equal(4, vm.TargetCount);
    }

    [Fact]
    public async Task More_than_the_limit_shows_only_the_first_5000_and_says_to_narrow_the_filter()
    {
        var rows = Enumerable.Range(0, DdlExtractViewModel.MaxTargets + 1).Select(i => Row("TABLE", "T" + i.ToString("D5", System.Globalization.CultureInfo.InvariantCulture))).ToArray();
        var vm = NewVm(new FakeAdmin { Rows = rows });
        await vm.PendingRefresh;

        Assert.Equal(DdlExtractViewModel.MaxTargets, vm.TargetCount);
        Assert.True(vm.TargetsTruncated);
        Assert.Equal("대상 (5000+)", vm.TargetsHeader);
        Assert.Contains("(더 있음)", vm.TargetSummary);
        Assert.Equal(DdlExtractViewModel.MaxTargets, vm.BuildPlan().Objects!.Count); // 추출은 보이는(체크한) 것만

        var exact = NewVm(new FakeAdmin { Rows = [.. rows.Take(DdlExtractViewModel.MaxTargets)] });
        await exact.PendingRefresh;
        Assert.False(exact.TargetsTruncated);
    }

    /// <summary>
    /// TSK-10-03 측정: 대상 5,000개(한도 + 1)가 목록에 들어와 화면에 그려지는 시간과 머리글 체크(모두 끔·모두 켬) 시간.
    /// 기본 실행에서 빠지는 Perf 묶음이다(숫자는 결과서 docs/oranda1/itest/TSK-10-03.md 에 적는다). 한도는 멈춤만 잡는 넉넉한 값.
    /// </summary>
    [Fact]
    [Trait("Category", "Perf")]
    public Task Five_thousand_targets_fill_the_tab_draw_and_toggle_in_time() => HeadlessHost.Run(async () =>
    {
        var rows = Enumerable.Range(0, DdlExtractViewModel.MaxTargets + 1)
            .Select(i => Row(i % 3 == 0 ? "VIEW" : "TABLE", "T" + i.ToString("D5", System.Globalization.CultureInfo.InvariantCulture), i % 50 == 0 ? "INVALID" : "VALID"))
            .ToArray();
        var vm = NewVm(new FakeAdmin { Rows = rows }, attach: false);
        vm.TargetDebounceMilliseconds = 0;
        var (window, _) = Show(vm, 1100, 640);
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            vm.Attach(new FakeSession());
            await vm.PendingRefresh;
            Pump();
            window.UpdateLayout();
            var fill = sw.Elapsed;

            sw.Restart();
            vm.AllChecked = false;
            Pump();
            window.UpdateLayout();
            var uncheck = sw.Elapsed;
            Assert.Equal(0, vm.CheckedCount);

            sw.Restart();
            vm.AllChecked = true;
            Pump();
            window.UpdateLayout();
            var check = sw.Elapsed;

            Assert.Equal(DdlExtractViewModel.MaxTargets, vm.TargetCount);
            Assert.Equal(DdlExtractViewModel.MaxTargets, vm.CheckedCount);
            Assert.True(vm.TargetsTruncated);
            output.WriteLine($"TSK-10-03 DDL 대상 5,000개: 목록+그리기 {fill.TotalMilliseconds:N0} ms, 모두 끔 {uncheck.TotalMilliseconds:N0} ms, 모두 켬 {check.TotalMilliseconds:N0} ms");
            Assert.True(fill < TimeSpan.FromSeconds(30), $"목록+그리기 {fill}");
            Assert.True(uncheck < TimeSpan.FromSeconds(30) && check < TimeSpan.FromSeconds(30), $"체크 {uncheck} / {check}");
        }
        finally
        {
            window.Close();
        }
    });

    // ---- 화면 ----

    [Fact]
    public Task The_view_shows_rows_with_checks_and_placeholder_per_state() => HeadlessHost.Run(async () =>
    {
        var admin = new FakeAdmin { Rows = Sample };
        var vm = NewVm(admin, attach: false);
        var (window, view) = Show(vm);
        Assert.False(Visible(view, "TargetList"));
        Assert.Contains(TextsOf(view), t => t.Contains("접속이 없습니다"));

        vm.Attach(new FakeSession());
        await vm.PendingRefresh;
        Pump();

        var list = view.FindControl<ListBox>("TargetList")!;
        Assert.True(list.IsEffectivelyVisible);
        Assert.Equal(4, list.ItemCount);
        Assert.DoesNotContain(TextsOf(view), t => t.Contains("접속이 없습니다"));
        var boxes = list.GetVisualDescendants().OfType<CheckBox>().ToList();
        Assert.Equal(4, boxes.Count);
        Assert.All(boxes, b => Assert.True(b.IsChecked));
        Assert.Contains(TextsOf(view), t => t == "INVALID");

        // 줄의 체크를 끄면 요약이 바뀐다.
        boxes[0].IsChecked = false;
        Pump();
        Assert.Contains(TextsOf(view), t => t == "대상 4개 중 3개 선택");
        Assert.Null(view.FindControl<CheckBox>("AllBox")!.IsChecked);

        // 머리글 체크: 일부만 고른 칸을 누르면 모두 고르고, 한 번 더 누르면 모두 해제.
        vm.AllChecked = false;
        Pump();
        Assert.All(list.GetVisualDescendants().OfType<CheckBox>(), b => Assert.True(b.IsChecked));
        vm.AllChecked = false;
        Pump();
        Assert.All(list.GetVisualDescendants().OfType<CheckBox>(), b => Assert.False(b.IsChecked));

        window.Close();
    });

    [Fact]
    public Task Starting_the_run_switches_to_the_results_tab() => HeadlessHost.Run(async () =>
    {
        var vm = NewVm(new FakeAdmin { Rows = Sample });
        var (window, view) = Show(vm);
        await vm.PendingRefresh;
        vm.OutputDirectory = TempDir();
        vm.Runner = (_, _, _, _, _) => Task.FromResult(new DdlExtractOutcome([new DdlExtractRow("TABLE SCOTT.EMP", true, null)], []));
        var tabs = view.FindControl<TabControl>("RightTabs")!;
        Assert.Equal(0, tabs.SelectedIndex);

        await vm.RunAsync();
        Pump();

        Assert.Equal(1, tabs.SelectedIndex);
        Assert.Contains(TextsOf(view), t => t.StartsWith("완료  TABLE", StringComparison.Ordinal));
        window.Close();
    });

    [Fact]
    public Task Screens_for_every_state_render_in_light_and_dark() => HeadlessHost.Run(async () =>
    {
        var gate = new TaskCompletionSource();
        var admin = new FakeAdmin { Rows = RenderRows() };
        var vm = NewVm(admin, attach: false);
        vm.Owner = "SCOTT";
        vm.OutputDirectory = @"C:\Users\me\Documents\oranda-ddl";
        var (window, _) = Show(vm, 1100, 640);

        Render(window, "1-no-connection", 1100, 640);

        vm.Attach(new FakeSession());
        await vm.PendingRefresh;
        Pump();
        vm.Targets[1].IsChecked = false;
        vm.Targets[3].IsChecked = false;
        Pump();
        Render(window, "2-targets", 1100, 640);

        // 새로 조회하는 동안 앞 목록은 그대로(불러오는 중 표시).
        admin.Handler = async (_, ct) =>
        {
            await gate.Task.WaitAsync(ct);
            return [];
        };
        vm.TargetDebounceMilliseconds = 0;
        vm.NameText = "emp";
        Pump();
        Render(window, "3-reloading", 1100, 640);
        gate.SetResult();
        await vm.PendingRefresh;
        Pump();
        Render(window, "4-empty", 1100, 640);

        admin.Handler = (_, _) => throw new InvalidOperationException("ORA-01031: 권한이 불충분합니다");
        vm.NameText = "emp2";
        await vm.PendingRefresh;
        Pump();
        Render(window, "5-error", 1100, 640);

        admin.Handler = null;
        admin.Rows = [.. Enumerable.Range(0, DdlExtractViewModel.MaxTargets + 1).Select(i => Row("TABLE", "TABLE_" + i.ToString("D5", System.Globalization.CultureInfo.InvariantCulture)))];
        vm.NameText = string.Empty;
        await vm.PendingRefresh;
        Pump();
        Render(window, "6-truncated", 1100, 640);

        admin.Rows = RenderRows();
        vm.NameText = string.Empty;
        vm.Owner = " ";
        Pump();
        Render(window, "7-need-owner", 1100, 640);

        vm.Owner = "SCOTT";
        await vm.PendingRefresh;
        vm.SelectedTab = 1;
        Pump();
        Render(window, "8-results-empty", 1100, 640);

        vm.Runner = (_, _, _, _, _) => Task.FromResult(new DdlExtractOutcome(
            [
                new DdlExtractRow("TABLE SCOTT.DEPT", true, null),
                new DdlExtractRow("TABLE SCOTT.EMP", true, null),
                new DdlExtractRow("PROCEDURE SCOTT.BAD_P", false, "ORA-31603: object not found"),
            ],
            [@"C:\Users\me\Documents\oranda-ddl\SCOTT.sql"]));
        await vm.RunAsync();
        Pump();
        Render(window, "9-results", 1100, 640);

        window.Close();
    });

    private static ObjectSearchRow[] RenderRows() =>
    [
        Row("SEQUENCE", "SEQ_ORDER_ID", ddl: new DateTime(2026, 9, 2, 10, 0, 0)),
        Row("TABLE", "DEPT", ddl: new DateTime(2026, 10, 1, 11, 20, 0)),
        Row("TABLE", "EMP", ddl: new DateTime(2026, 10, 12, 8, 41, 0)),
        Row("TABLE", "ORDER_HISTORY_ARCHIVE_2026Q3_PARTITIONED_LONG_NAME", ddl: new DateTime(2026, 10, 14, 17, 2, 0)),
        Row("VIEW", "EMP_V", "INVALID", new DateTime(2026, 10, 24, 18, 6, 0)),
        Row("FUNCTION", "F_TAX", ddl: new DateTime(2026, 10, 3, 9, 12, 0)),
        Row("PACKAGE", "PKG_PAY", ddl: new DateTime(2026, 10, 4, 9, 12, 0)),
        Row("PACKAGE BODY", "PKG_PAY", "INVALID", new DateTime(2026, 10, 25, 9, 30, 0)),
        Row("TRIGGER", "TRG_EMP_AUDIT", ddl: new DateTime(2026, 10, 5, 13, 0, 0)),
    ];

    private static void Pump()
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static (Window Window, DdlExtractView View) Show(DdlExtractViewModel vm, int width = 1000, int height = 600)
    {
        var view = new DdlExtractView { DataContext = vm };
        var window = new Window { Content = view, Width = width, Height = height };
        window.Show();
        Pump();
        window.UpdateLayout();
        return (window, view);
    }

    private static bool Visible(DdlExtractView view, string name) => view.FindControl<Control>(name)!.IsEffectivelyVisible;

    private static List<string> TextsOf(Visual root)
        => [.. root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? string.Empty)];

    private static void Render(Window window, string name, int width, int height)
    {
        var dir = Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR");
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        try
        {
            foreach (var (variant, tone) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
            {
                Application.Current!.RequestedThemeVariant = variant;
                Pump();
                window.UpdateLayout();
                using var bmp = new RenderTargetBitmap(new PixelSize(width, height));
                bmp.Render(window);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
                bmp.Save(Path.Combine(dir, $"ddl-{name}-{tone}.png"));
#pragma warning restore CS0618
            }
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            Pump();
        }
    }

    private static string TempDir() => Path.Combine(Path.GetTempPath(), "oranda-ddl-targets-" + Guid.NewGuid().ToString("N")[..8]);

    private static void Cleanup(string dir)
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- 가짜 ----

    private sealed class FakeAdmin : ISchemaAdminProvider
    {
        private readonly List<(string? Pattern, TaskCompletionSource Started)> _started = [];

        public IReadOnlyList<ObjectSearchRow> Rows { get; set; } = [];

        public Func<ObjectSearchQuery, CancellationToken, Task<IReadOnlyList<ObjectSearchRow>>>? Handler { get; set; }

        public List<ObjectSearchQuery> Queries { get; } = [];

        public HashSet<string?> Cancelled { get; } = [];

        public async Task<IReadOnlyList<ObjectSearchRow>> SearchObjectsAsync(ISession s, ObjectSearchQuery q, CancellationToken ct)
        {
            lock (Queries)
            {
                Queries.Add(q);
                foreach (var w in _started.Where(w => w.Pattern == q.NamePattern))
                {
                    w.Started.TrySetResult();
                }
            }

            try
            {
                return Handler is { } h ? await h(q, ct) : Rows;
            }
            catch (OperationCanceledException)
            {
                lock (Queries)
                {
                    Cancelled.Add(q.NamePattern);
                }

                throw;
            }
        }

        /// <summary>그 이름 패턴으로 조회가 시작될 때까지 기다린다.</summary>
        public Task WaitStarted(string? pattern)
        {
            lock (Queries)
            {
                if (Queries.Any(q => q.NamePattern == pattern))
                {
                    return Task.CompletedTask;
                }

                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _started.Add((pattern, tcs));
                return tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        public Task<SearchHits<SourceSearchHit>> SearchSourceAsync(ISession s, SourceSearchQuery q, CancellationToken ct) => throw new NotSupportedException();

        public Task<SearchHits<ColumnSearchHit>> SearchColumnsAsync(ISession s, ColumnSearchQuery q, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<ObjectReference>> GetReferencesAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<RecycleBinItem>> ListRecycleBinAsync(ISession s, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeMeta : IMetadataProvider
    {
        public List<string> DdlRequests { get; } = [];

        public int ListCalls { get; private set; }

        public Task<IReadOnlyList<DbObjectRef>> ListObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct)
        {
            ListCalls++;
            IReadOnlyList<DbObjectRef> list = [new("SCOTT", "EMP", "TABLE"), new("SCOTT", "DEPT", "TABLE")];
            return Task.FromResult(list);
        }

        public Task<string> GetDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct)
        {
            lock (DdlRequests)
            {
                DdlRequests.Add(obj.Name);
            }

            return Task.FromResult($"CREATE TABLE \"{obj.Owner}\".\"{obj.Name}\" (ID NUMBER);");
        }

        public Task<IReadOnlyList<string>> ListSchemasAsync(ISession s, CancellationToken ct) => throw new NotSupportedException();

        public Task<TableInfo> GetTableAsync(ISession s, DbObjectRef table, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<Oranda.Core.Metadata.ObjectDetail> GetObjectDetailAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetSourceAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<CompileError>> GetCompileErrorsAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();
    }
}
