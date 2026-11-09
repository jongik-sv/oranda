using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.DataCopy;
using Oranda.App.Features.Localization;
using Oranda.App.Shell;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Metadata;
using Oranda.Testing;

namespace Oranda.App.Tests.Features.DataCopy;

/// <summary>
/// 데이터 복사 「테이블 찾기」 팝업: 모델(검색·강조·체크 유지·계정 바꾸기), 작업 창 경로(목록 읽기·한 번에 더하기), 창 그리기.
/// <c>ORANDA_RENDER_DIR</c> 이 있으면 창을 라이트·다크 PNG(<c>table-finder-impl-*.png</c>)로 남긴다.
/// </summary>
[Collection("UiLanguage")]
public sealed class TableFinderTests : IDisposable
{
    private readonly IShellText? _previousText = UiText.Current;

    public void Dispose() => UiText.Current = _previousText;

    private static readonly TableFinderSource[] AppTables =
    [
        new("APP", "MO_HEADER", "TABLE", "제조 오더 헤더"),
        new("APP", "MO_LINE", "TABLE", "제조 오더 줄"),
        new("APP", "XMO_A", "TABLE", null),
        new("APP", "ORDERS", "TABLE", "주문"),
        new("APP", "ORD_SYN", "SYNONYM", null),
        new("APP", "CODES", "TABLE", "공통 코드"),
        new("APP", "PLAN", "TABLE", "생산 계획(제조)"),
    ];

    private sealed class Loader
    {
        public Dictionary<string, IReadOnlyList<TableFinderSource>> ByOwner { get; } = new(StringComparer.Ordinal) { ["APP"] = AppTables };

        public List<string> Calls { get; } = [];

        public Exception? Failure { get; set; }

        public Task<IReadOnlyList<TableFinderSource>> Load(string owner, CancellationToken ct)
        {
            Calls.Add(owner);
            return Failure is { } e
                ? Task.FromException<IReadOnlyList<TableFinderSource>>(e)
                : Task.FromResult(ByOwner.TryGetValue(owner, out var list) ? list : (IReadOnlyList<TableFinderSource>)[]);
        }
    }

    private static async Task<(TableFinderViewModel Vm, Loader Loader)> FinderAsync(Func<string, string, bool>? isAdded = null, Loader? loader = null)
    {
        loader ??= new Loader();
        var vm = new TableFinderViewModel(["APP", "APP2"], "APP", loader.Load, isAdded ?? ((_, _) => false));
        await vm.LoadAsync();
        return (vm, loader);
    }

    private static TableFinderRow Row(TableFinderViewModel vm, string name) => vm.Rows.Single(r => r.Name == name);

    [Fact]
    public async Task Empty_filter_shows_no_rows_and_the_total()
    {
        var (vm, _) = await FinderAsync();

        Assert.Empty(vm.Rows);
        Assert.True(vm.HasEmptyText);
        Assert.Equal("이름을 입력하면 걸러 보입니다 — 전체 7개", vm.EmptyText);
        Assert.Equal("APP 계정 · 전체 7개", vm.SummaryText);
        Assert.False(vm.AllVisibleChecked);
        Assert.False(vm.CanCheckAll);
        Assert.False(vm.CanAdd);
    }

    [Fact]
    public async Task Filter_ignores_case_and_also_matches_the_table_comment()
    {
        var (vm, _) = await FinderAsync();

        vm.FilterText = "mo";
        Assert.Equal(["MO_HEADER", "MO_LINE", "XMO_A"], vm.Rows.Select(r => r.Name));
        Assert.Equal(string.Empty, vm.EmptyText);
        Assert.Equal("APP 계정 · 7개 중 3개 일치 · 0개 고름", vm.SummaryText);

        vm.FilterText = "제조"; // 테이블 명(주석)으로 찾는다
        Assert.Equal(["MO_HEADER", "MO_LINE", "PLAN"], vm.Rows.Select(r => r.Name));
        Assert.All(vm.Rows, r => Assert.Equal((r.Name, "", ""), (r.NameBefore, r.NameMatch, r.NameAfter))); // 이름에는 맞는 곳이 없다

        vm.FilterText = "zzz";
        Assert.Empty(vm.Rows);
        Assert.Equal("「zzz」 와 맞는 표가 없습니다", vm.EmptyText);
    }

    [Fact]
    public async Task Wildcard_matches_the_whole_name()
    {
        var (vm, _) = await FinderAsync();

        vm.FilterText = "mo_*";
        Assert.Equal(["MO_HEADER", "MO_LINE"], vm.Rows.Select(r => r.Name)); // XMO_A 는 앞이 달라 빠진다
        Assert.Equal(("", "MO_", "HEADER"), (Row(vm, "MO_HEADER").NameBefore, Row(vm, "MO_HEADER").NameMatch, Row(vm, "MO_HEADER").NameAfter));

        vm.FilterText = "*_a";
        Assert.Equal(["XMO_A"], vm.Rows.Select(r => r.Name));
    }

    [Fact]
    public async Task Highlight_splits_the_name_keeping_its_original_case()
    {
        var loader = new Loader();
        loader.ByOwner["APP"] = [new("APP", "Mixed_MoCase", "TABLE", null)];
        var (vm, _) = await FinderAsync(loader: loader);

        vm.FilterText = "MOC";
        var row = Assert.Single(vm.Rows);
        Assert.Equal(("Mixed_", "MoC", "ase"), (row.NameBefore, row.NameMatch, row.NameAfter));
        Assert.Equal("테이블", row.TypeText);
        Assert.False(row.IsSynonym);
    }

    [Fact]
    public async Task Already_added_tables_cannot_be_checked()
    {
        var (vm, _) = await FinderAsync(isAdded: (owner, table) => owner == "APP" && table == "MO_LINE");

        vm.FilterText = "mo";
        var added = Row(vm, "MO_LINE");
        Assert.True(added.IsAdded);
        Assert.False(added.CanCheck);
        added.IsChecked = true;
        Assert.False(added.IsChecked);
        Assert.Equal(0, vm.PickedCount);
        Assert.True(Row(vm, "MO_HEADER").CanCheck);
    }

    [Fact]
    public async Task Picks_survive_changing_the_filter()
    {
        var (vm, _) = await FinderAsync();

        vm.FilterText = "mo";
        Row(vm, "MO_HEADER").IsChecked = true;
        Row(vm, "MO_LINE").IsChecked = true;
        Assert.Equal("APP 계정 · 7개 중 3개 일치 · 2개 고름", vm.SummaryText);

        vm.FilterText = "ord";
        Assert.Equal(["ORDERS", "ORD_SYN"], vm.Rows.Select(r => r.Name)); // 이름 순(서수)
        Assert.All(vm.Rows, r => Assert.False(r.IsChecked));
        Row(vm, "ORD_SYN").IsChecked = true;
        Assert.Equal("시노님", Row(vm, "ORD_SYN").TypeText);

        vm.FilterText = "mo";
        Assert.True(Row(vm, "MO_HEADER").IsChecked);
        Assert.True(Row(vm, "MO_LINE").IsChecked);
        Assert.False(Row(vm, "XMO_A").IsChecked);
        Assert.Equal(3, vm.PickedCount);
        Assert.True(vm.CanAdd);
        Assert.Equal("선택한 3개 추가", vm.AddButtonText);
        Assert.Contains("고른 표 3개", vm.PickedText, StringComparison.Ordinal);
        Assert.Equal(
            [new DbObjectRef("APP", "MO_HEADER", "TABLE"), new DbObjectRef("APP", "MO_LINE", "TABLE"), new DbObjectRef("APP", "ORD_SYN", "SYNONYM")],
            vm.PickedTables.OrderBy(t => t.Name, StringComparer.Ordinal));

        Row(vm, "MO_LINE").IsChecked = false;
        Assert.Equal(2, vm.PickedCount);
    }

    [Fact]
    public async Task Check_all_affects_only_visible_rows_that_can_be_checked()
    {
        var (vm, _) = await FinderAsync(isAdded: (_, table) => table == "XMO_A");
        vm.FilterText = "ord";
        Row(vm, "ORDERS").IsChecked = true; // 보이지 않게 될 고른 표

        vm.FilterText = "mo";
        Assert.False(vm.AllVisibleChecked);
        Assert.True(vm.CanCheckAll);

        vm.AllVisibleChecked = true;
        Assert.True(vm.AllVisibleChecked);
        Assert.True(Row(vm, "MO_HEADER").IsChecked);
        Assert.True(Row(vm, "MO_LINE").IsChecked);
        Assert.False(Row(vm, "XMO_A").IsChecked); // 이미 있음은 빼고
        Assert.Equal(3, vm.PickedCount);

        Row(vm, "MO_LINE").IsChecked = false;
        Assert.Null(vm.AllVisibleChecked); // 일부만

        vm.AllVisibleChecked = false;
        Assert.False(vm.AllVisibleChecked);
        Assert.All(vm.Rows, r => Assert.False(r.IsChecked));
        Assert.Equal([new DbObjectRef("APP", "ORDERS", "TABLE")], vm.PickedTables); // 안 보이는 고른 표는 그대로
    }

    [Fact]
    public async Task Synonyms_can_be_left_out()
    {
        var (vm, _) = await FinderAsync();
        vm.FilterText = "ord";
        Assert.Contains(vm.Rows, r => r.IsSynonym);

        vm.IncludeSynonyms = false;
        Assert.Equal(["ORDERS"], vm.Rows.Select(r => r.Name));
        Assert.Equal("APP 계정 · 6개 중 1개 일치 · 0개 고름", vm.SummaryText);

        vm.FilterText = string.Empty;
        Assert.Equal("APP 계정 · 전체 6개", vm.SummaryText);
    }

    [Fact]
    public async Task Changing_the_schema_loads_it_once()
    {
        var loader = new Loader();
        loader.ByOwner["APP2"] = [new("APP2", "MO_APP2", "TABLE", null)];
        var (vm, _) = await FinderAsync(loader: loader);
        Assert.Equal(["APP"], loader.Calls);
        vm.FilterText = "mo";

        vm.SelectedOwner = "APP2";
        Assert.Equal(["APP", "APP2"], loader.Calls);
        Assert.Equal(["MO_APP2"], vm.Rows.Select(r => r.Name));
        Assert.StartsWith("APP2 계정 · 1개 중 1개 일치", vm.SummaryText, StringComparison.Ordinal);

        vm.SelectedOwner = "APP";
        vm.SelectedOwner = "APP2";
        await vm.LoadAsync();
        Assert.Equal(["APP", "APP2"], loader.Calls); // 읽은 계정은 다시 읽지 않는다
        Assert.Equal(["MO_APP2"], vm.Rows.Select(r => r.Name));
    }

    [Fact]
    public void Default_schema_missing_from_the_list_is_put_first()
    {
        var vm = new TableFinderViewModel(["APP2"], "APP", (_, _) => Task.FromResult<IReadOnlyList<TableFinderSource>>([]), (_, _) => false);

        Assert.Equal(["APP", "APP2"], vm.Owners);
        Assert.Equal("APP", vm.SelectedOwner);
    }

    [Fact]
    public async Task Load_failure_shows_the_reason()
    {
        var loader = new Loader { Failure = new InvalidOperationException("ORA-00942: 표가 없습니다") };
        var (vm, _) = await FinderAsync(loader: loader);

        Assert.Empty(vm.Rows);
        Assert.False(vm.IsLoading);
        Assert.Equal("APP 계정의 표를 읽지 못했습니다: ORA-00942: 표가 없습니다", vm.EmptyText);

        vm.FilterText = "mo"; // 검색 값을 바꿔도 이유가 지워지지 않는다
        Assert.Equal("APP 계정의 표를 읽지 못했습니다: ORA-00942: 표가 없습니다", vm.EmptyText);
        vm.FilterText = string.Empty;

        loader.Failure = null; // 실패한 계정은 다시 고르면 다시 읽는다
        await vm.LoadAsync();
        Assert.Equal(2, loader.Calls.Count);
        Assert.Equal("APP 계정 · 전체 7개", vm.SummaryText);
    }

    [Fact]
    public async Task More_than_max_visible_matches_are_cut_with_a_notice()
    {
        var loader = new Loader();
        loader.ByOwner["APP"] = [.. Enumerable.Range(1, TableFinderViewModel.MaxVisible + 20).Select(i => new TableFinderSource("APP", $"T_{i:0000}", "TABLE", null))];
        var (vm, _) = await FinderAsync(loader: loader);

        vm.FilterText = "t_";
        Assert.Equal(TableFinderViewModel.MaxVisible, vm.Rows.Count);
        Assert.Contains("520개 중 520개 일치", vm.SummaryText, StringComparison.Ordinal);
        Assert.Contains("처음 500개만", vm.SummaryText, StringComparison.Ordinal);

        vm.FilterText = "t_00";
        Assert.DoesNotContain("처음 500개만", vm.SummaryText, StringComparison.Ordinal);
    }

    // ── 작업 창 경로 ─────────────────────────────────────────────

    private static DataCopyHarness FinderHarness()
    {
        var h = new DataCopyHarness();
        h.Metadata.Schemas.AddRange(["APP", "APP2", "SYS"]);
        h.Metadata.Objects.AddRange(
        [
            new DbObjectRef("APP", "MO_HEADER", "TABLE"),
            new DbObjectRef("APP", "MO_LINE", "TABLE"),
            new DbObjectRef("APP", "MO_V", "VIEW"),
            new DbObjectRef("APP", "ORDERS", "SYNONYM"),
            new DbObjectRef("APP", "CODES", "TABLE"),
            new DbObjectRef("APP2", "ORDERS", "TABLE"),
        ]);
        h.Metadata.Comments["APP"] = new(StringComparer.Ordinal) { ["MO_HEADER"] = "제조 오더 헤더", ["CODES"] = "공통 코드" };
        h.Metadata.NameSteps[("APP", "MO_HEADER")] = NameStep.TableOrView;
        h.Metadata.NameSteps[("APP", "MO_LINE")] = NameStep.TableOrView;
        h.Metadata.NameSteps[("APP", "ORDERS")] = NameStep.Synonym("APP2", "ORDERS");
        h.Metadata.NameSteps[("APP2", "ORDERS")] = NameStep.TableOrView;
        h.Metadata.Tables[new DbObjectRef("APP", "MO_HEADER", "TABLE")] = TestJobs.Table("MO_HEADER", TestJobs.Column("MO_ID", "NUMBER", 1));
        h.Metadata.Tables[new DbObjectRef("APP", "MO_LINE", "TABLE")] = TestJobs.Table("MO_LINE", TestJobs.Column("MO_ID", "NUMBER", 1));
        h.Metadata.Tables[new DbObjectRef("APP2", "ORDERS", "TABLE")] = TestJobs.Table("ORDERS", TestJobs.Column("ORDER_ID", "NUMBER", 1));
        return h;
    }

    [Fact]
    public async Task Finder_reads_the_source_tables_and_synonyms_with_comments_and_marks_existing_rules()
    {
        var h = FinderHarness();
        var vm = await h.OpenAsync(TestJobs.Job("찾기", rules: TestJobs.Rule("CODES")));

        var finder = await vm.CreateTableFinderAsync();

        Assert.NotNull(finder);
        Assert.True(Assert.Single(h.Opened).ReadOnly); // 원본을 읽기 전용으로 연다
        Assert.Equal(h.Source.Name, h.Opened[0].Name);
        Assert.Equal(["APP", "APP2", "SYS"], finder.Owners);
        Assert.Equal("APP", finder.SelectedOwner); // 작업의 기본 원본 계정

        await finder.LoadAsync();
        Assert.Equal(["SYNONYM", "TABLE"], h.Metadata.LastObjectQuery!.ObjectTypes.Order(StringComparer.Ordinal));
        Assert.Equal("APP", h.Metadata.LastObjectQuery.Owner);
        Assert.Equal("APP 계정 · 전체 4개", finder.SummaryText); // 뷰는 빠진다

        finder.FilterText = "o";
        Assert.Equal(["CODES", "MO_HEADER", "MO_LINE", "ORDERS"], finder.Rows.Select(r => r.Name));
        Assert.Equal("제조 오더 헤더", Row(finder, "MO_HEADER").Comment);
        Assert.Null(Row(finder, "MO_LINE").Comment);
        Assert.True(Row(finder, "CODES").IsAdded); // 이미 규칙에 있다
        Assert.False(Row(finder, "MO_HEADER").IsAdded);
        Assert.True(Row(finder, "ORDERS").IsSynonym);

        finder.SelectedOwner = "APP2"; // 다른 계정의 같은 이름은 「이미 있음」이 아니다
        Assert.False(Row(finder, "ORDERS").IsAdded);
    }

    [Fact]
    public async Task Finder_is_not_made_without_a_source_session()
    {
        var h = new DataCopyHarness { RefuseOpen = _ => true };
        var vm = await h.OpenAsync(TestJobs.Job("세션 없음"));

        Assert.Null(await vm.CreateTableFinderAsync());
        Assert.True(vm.StatusIsError);
    }

    [Fact]
    public async Task Found_tables_and_a_synonym_are_added_as_rules_in_one_go()
    {
        var h = FinderHarness();
        var vm = await h.OpenAsync(TestJobs.Job("한 번에"));

        await vm.AddFoundTablesAsync(
        [
            new DbObjectRef("APP", "MO_HEADER", "TABLE"),
            new DbObjectRef("APP", "MO_LINE", "TABLE"),
            new DbObjectRef("APP", "ORDERS", "SYNONYM"),
        ]);

        Assert.Equal(["APP.MO_HEADER", "APP.MO_LINE", "APP2.ORDERS"], vm.Editor.Rules.Select(r => r.SourceName));
        Assert.Equal("APP.ORDERS", vm.Editor.Rules[2].WrittenAs); // 시노님은 실제 계정의 표로 풀렸다(적힌 이름은 계정.시노님)
        Assert.False(vm.StatusIsError);
        Assert.Equal("테이블 3개 규칙을 추가했습니다.", vm.StatusText);
        Assert.Single(h.Opened); // 원본 세션은 한 번만 연다
    }

    [Fact]
    public async Task Unresolvable_found_table_is_reported_with_its_reason()
    {
        var h = FinderHarness();
        var vm = await h.OpenAsync(TestJobs.Job("섞임"));

        await vm.AddFoundTablesAsync(
        [
            new DbObjectRef("APP", "MO_HEADER", "TABLE"),
            new DbObjectRef("APP", "GONE_SYN", "SYNONYM"), // 사전에 없다
        ]);

        Assert.Equal(["APP.MO_HEADER"], vm.Editor.Rules.Select(r => r.SourceName));
        Assert.True(vm.StatusIsError);
        Assert.StartsWith("테이블 1개 규칙을 추가했습니다. 더하지 못한 것: ", vm.StatusText, StringComparison.Ordinal);
        Assert.Contains("GONE_SYN", vm.StatusText, StringComparison.Ordinal);
        Assert.Contains("원본에서 찾을 수 없습니다", vm.StatusText, StringComparison.Ordinal);
    }

    // ── 창 그리기 ─────────────────────────────────────────────

    private static void Pump()
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void Settle(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(20);
            Pump();
            window.UpdateLayout();
        }
    }

    private static void Shots(Window window, string name)
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
                var size = new PixelSize((int)Math.Ceiling(window.Bounds.Width), (int)Math.Ceiling(window.Bounds.Height));
                using var bmp = new RenderTargetBitmap(size);
                bmp.Render(window);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
                bmp.Save(Path.Combine(dir, $"{name}-{tone}.png"));
#pragma warning restore CS0618
            }
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            Pump();
        }
    }

    [Fact]
    public Task Finder_window_renders_rows_without_clipping() => HeadlessHost.Run(async () =>
    {
        var text = new ShellText(language: "ko");
        UiText.Current = text;
        LiteralTranslator.Install(text);

        var loader = new Loader();
        loader.ByOwner["APP"] =
        [
            .. AppTables,
            new("APP", "MO_PRODUCTION_ORDER_HISTORY_ARCHIVE", "TABLE", "제조 오더 이력 보관(오래된 오더를 옮겨 두는 표, 조회 전용)"),
            new("APP", "MO_SYN", "SYNONYM", "다른 계정 제조 오더"),
        ];
        var vm = new TableFinderViewModel(["APP", "APP2"], "APP", loader.Load, (_, table) => table == "MO_LINE");
        var window = new TableFinderWindow { DataContext = vm };
        window.Show();
        LiteralTranslator.Translate(window);
        Settle(window);
        try
        {
            Assert.Equal(["APP"], loader.Calls); // 열리면 기본 계정을 읽는다
            Assert.True(vm.HasEmptyText);
            Assert.Empty(TextClipping.Find(window));

            vm.FilterText = "mo";
            Row(vm, "MO_HEADER").IsChecked = true;
            Row(vm, "MO_PRODUCTION_ORDER_HISTORY_ARCHIVE").IsChecked = true;
            Settle(window);

            Assert.Equal(5, vm.Rows.Count);
            var clipped = TextClipping.Find(window);
            Assert.True(clipped.Count == 0, string.Join(" | ", clipped));
            AssertNameCellsFit(window);
            Assert.True(window.FindControl<Button>("AddButton")!.IsEffectivelyEnabled);
            Shots(window, "table-finder-impl");
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>이름 칸(이름 + 「이미 있음」)의 글이 240 칸을 넘어 유형 칸을 덮지 않는다.</summary>
    private static void AssertNameCellsFit(Window window)
    {
        var list = window.FindControl<ListBox>("List")!;
        var cells = list.GetVisualDescendants().OfType<DockPanel>().Where(p => Grid.GetColumn(p) == 1 && p.GetVisualParent() is Grid).ToList();
        Assert.NotEmpty(cells);
        foreach (var cell in cells)
        {
            foreach (var child in cell.Children.OfType<Control>().Where(c => c.IsVisible))
            {
                Assert.True(child.Bounds.Right <= cell.Bounds.Width + 0.5, $"{child.GetType().Name}: 오른쪽 {child.Bounds.Right:0.#} > 칸 폭 {cell.Bounds.Width:0.#}");
            }
        }

        var chips = cells.SelectMany(c => c.GetVisualDescendants().OfType<TextBlock>()).Where(t => t.Text == "이미 있음" && t.IsEffectivelyVisible);
        Assert.Single(chips); // MO_LINE 줄에만
    }
}
