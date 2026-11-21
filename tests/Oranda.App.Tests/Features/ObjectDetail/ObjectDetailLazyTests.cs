using Avalonia.Controls;
using Avalonia.Threading;
using Oranda.App.Features.ObjectDetail;
using Oranda.App.Shell.Controls;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Testing;

namespace Oranda.App.Tests.Features.ObjectDetail;

/// <summary>DL-1 객체 상세: 탭 머리는 곧바로, 선택된 탭만 먼저 읽고 다른 탭·DDL 은 고를 때 한 번 읽는다.</summary>
public class ObjectDetailLazyTests
{
    private static readonly DbObjectRef Emp = new("SCOTT", "EMP", "TABLE");

    [Fact]
    public async Task Open_shows_every_tab_head_and_reads_only_the_columns_tab_from_the_outline()
    {
        var meta = new LazyMeta();
        var vm = new ObjectDetailViewModel(meta);

        await vm.OpenAsync(new FakeSession(), Emp, showDdl: false);

        Assert.Equal(["일반", "열", "데이터", "트리거", "권한", "의존"], vm.Sections.Select(s => s.Title));
        Assert.Equal("열", vm.SelectedSection?.Title);
        Assert.Equal([["ID", "NUMBER"]], vm.SelectedSection!.Rows);
        Assert.Empty(meta.Parts); // 열은 탭 목록과 함께 왔다
        Assert.Equal(0, meta.DdlCalls); // DDL 은 그 탭을 고를 때
        Assert.Equal(0, meta.WholeCalls);
        Assert.False(vm.IsBusy);
        Assert.All(vm.Sections.Where(s => s.Title is "일반" or "권한"), s => Assert.False(s.IsLoaded));
    }

    [Fact]
    public async Task Selecting_another_tab_reads_only_that_part_and_never_again()
    {
        var meta = new LazyMeta();
        var vm = new ObjectDetailViewModel(meta);
        await vm.OpenAsync(new FakeSession(), Emp, showDdl: false);

        var privileges = vm.Sections.Single(s => s.Title == "권한");
        vm.SelectedSection = privileges;
        await vm.SectionLoad!;

        Assert.Equal([DetailPart.Privileges], meta.Parts);
        Assert.Equal(["받는 사람", "권한"], privileges.Columns);
        Assert.Equal([["HR", "SELECT"]], privileges.Rows);
        Assert.False(privileges.HasNotice);

        vm.SelectedSection = vm.Sections.Single(s => s.Title == "열");
        vm.SelectedSection = privileges;
        Assert.Equal([DetailPart.Privileges], meta.Parts); // 다시 골라도 다시 묻지 않는다
    }

    [Fact]
    public async Task A_tab_without_rows_says_none_and_a_failed_tab_says_why_and_reads_again_when_chosen_again()
    {
        var meta = new LazyMeta { FailingPart = DetailPart.Dependencies };
        var vm = new ObjectDetailViewModel(meta);
        await vm.OpenAsync(new FakeSession(), Emp, showDdl: false);

        var triggers = vm.Sections.Single(s => s.Title == "트리거");
        vm.SelectedSection = triggers;
        await vm.SectionLoad!;
        Assert.True(triggers.IsLoaded);
        Assert.Contains("없음", triggers.Notice, StringComparison.Ordinal);

        var deps = vm.Sections.Single(s => s.Title == "의존");
        vm.SelectedSection = deps;
        await vm.SectionLoad!;
        Assert.Contains("ORA-00942", deps.Notice, StringComparison.Ordinal);
        Assert.False(deps.IsLoaded);

        meta.FailingPart = null;
        vm.SelectedSection = triggers;
        vm.SelectedSection = deps;
        await vm.SectionLoad!;
        Assert.Equal(2, meta.Parts.Count(p => p == DetailPart.Dependencies)); // 실패한 탭은 다시 고르면 다시 읽는다
        Assert.True(deps.IsLoaded);
        Assert.Equal(1, meta.Parts.Count(p => p == DetailPart.Triggers));
    }

    [Fact]
    public async Task Ddl_is_read_when_its_tab_is_chosen_and_only_once()
    {
        var meta = new LazyMeta();
        var vm = new ObjectDetailViewModel(meta);
        await vm.OpenAsync(new FakeSession(), Emp, showDdl: false);

        vm.IsDdlSelected = true;
        await vm.DdlLoad!;
        Assert.Equal("CREATE TABLE EMP;", vm.DdlText);

        vm.IsDdlSelected = false;
        vm.IsDdlSelected = true;
        Assert.Equal(1, meta.DdlCalls);
        Assert.Empty(meta.Parts);
    }

    [Fact]
    public async Task Opening_on_the_ddl_tab_reads_the_ddl_at_once()
    {
        var meta = new LazyMeta();
        var vm = new ObjectDetailViewModel(meta);

        await vm.OpenAsync(new FakeSession(), Emp, showDdl: true);

        Assert.Equal("CREATE TABLE EMP;", vm.DdlText);
        Assert.Equal(1, meta.DdlCalls);
        Assert.Empty(meta.Parts);
    }

    [Fact]
    public async Task A_package_opens_on_its_general_tab_and_reads_only_that()
    {
        var meta = new LazyMeta();
        var vm = new ObjectDetailViewModel(meta);

        await vm.OpenAsync(new FakeSession(), new DbObjectRef("SCOTT", "PKG", "PACKAGE"), showDdl: false);

        Assert.Equal(["일반", "권한", "의존"], vm.Sections.Select(s => s.Title));
        Assert.Equal("일반", vm.SelectedSection?.Title);
        Assert.Equal([DetailPart.General], meta.Parts);
        Assert.True(vm.SelectedSection!.IsLoaded);
    }

    [Fact]
    public async Task Switching_quickly_to_another_object_drops_the_earlier_reads()
    {
        var meta = new LazyMeta();
        var vm = new ObjectDetailViewModel(meta);
        await vm.OpenAsync(new FakeSession(), Emp, showDdl: false);
        var gate = new TaskCompletionSource();
        meta.PartGate = gate.Task;
        var oldPrivileges = vm.Sections.Single(s => s.Title == "권한");
        vm.SelectedSection = oldPrivileges;
        var pending = vm.SectionLoad!;

        await vm.OpenAsync(new FakeSession(), new DbObjectRef("SCOTT", "DEPT", "TABLE"), showDdl: false);
        gate.SetResult();
        await pending;

        Assert.True(meta.LastPartToken.IsCancellationRequested); // 앞 객체의 읽기는 취소됐다
        Assert.Equal("SCOTT.DEPT", vm.Title);
        Assert.DoesNotContain(oldPrivileges, vm.Sections);
        Assert.Equal("열", vm.SelectedSection?.Title);
    }

    [Fact]
    public async Task Refresh_forgets_the_loaded_tabs_so_they_are_read_again()
    {
        var meta = new LazyMeta();
        var vm = new ObjectDetailViewModel(meta);
        await vm.OpenAsync(new FakeSession(), Emp, showDdl: false);
        vm.SelectedSection = vm.Sections.Single(s => s.Title == "권한");
        await vm.SectionLoad!;

        Assert.True(await vm.RefreshAsync());
        vm.SelectedSection = vm.Sections.Single(s => s.Title == "권한");
        await vm.SectionLoad!;

        Assert.Equal(2, meta.Outlines);
        Assert.Equal([DetailPart.Privileges, DetailPart.Privileges], meta.Parts);
    }

    [Fact]
    public async Task Outline_failure_reports_the_error_and_leaves_no_tabs()
    {
        var meta = new LazyMeta { OutlineFails = true };
        var vm = new ObjectDetailViewModel(meta);

        await vm.OpenAsync(new FakeSession(), Emp, showDdl: false);

        Assert.Empty(vm.Sections);
        Assert.Contains("상세를 읽지 못했다", vm.StatusText, StringComparison.Ordinal);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public Task Detail_view_shows_reading_then_the_table_and_says_none_for_an_empty_tab() => HeadlessHost.Run(async () =>
    {
        var meta = new LazyMeta();
        var vm = new ObjectDetailViewModel(meta);
        await vm.OpenAsync(new FakeSession(), Emp, showDdl: false);
        var view = new ObjectDetailView { DataContext = vm };
        var w = new Window { Content = view, Width = 1100, Height = 700 };
        w.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var notice = view.FindControl<TextBlock>("SectionNoticeText")!;
            var table = view.FindControl<DetailTable>("SectionTable")!;
            Assert.False(notice.IsEffectivelyVisible); // 「열」은 이미 읽혔다
            Assert.Equal(1, table.List.ItemCount);

            var gate = new TaskCompletionSource();
            meta.PartGate = gate.Task;
            vm.SelectedSection = vm.Sections.Single(s => s.Title == "권한");
            Dispatcher.UIThread.RunJobs();
            Assert.True(notice.IsEffectivelyVisible);
            Assert.Equal("읽는 중…", notice.Text);

            gate.SetResult();
            await vm.SectionLoad!;
            Dispatcher.UIThread.RunJobs();
            Assert.False(notice.IsEffectivelyVisible);
            Assert.Equal(["받는 사람", "권한"], table.Columns);
            Assert.Equal(1, table.List.ItemCount);

            meta.PartGate = null;
            vm.SelectedSection = vm.Sections.Single(s => s.Title == "트리거");
            await vm.SectionLoad!;
            Dispatcher.UIThread.RunJobs();
            Assert.True(notice.IsEffectivelyVisible);
            Assert.Equal("없음", notice.Text);
        }
        finally
        {
            w.Close();
        }
    });

    /// <summary>탭 목록·구역 읽기를 따로 세는 가짜 공급자(일반·트리거·권한·의존은 고를 때 읽는다).</summary>
    private sealed class LazyMeta : IMetadataProvider
    {
        public List<DetailPart> Parts { get; } = [];

        public int Outlines { get; private set; }

        public int DdlCalls { get; private set; }

        public int WholeCalls { get; private set; }

        public bool OutlineFails { get; init; }

        public DetailPart? FailingPart { get; set; }

        public Task? PartGate { get; set; }

        public CancellationToken LastPartToken { get; private set; }

        public Task<ObjectDetailOutline> GetDetailOutlineAsync(ISession s, DbObjectRef obj, CancellationToken ct)
        {
            Outlines++;
            if (OutlineFails)
            {
                return Task.FromException<ObjectDetailOutline>(new InvalidOperationException("boom"));
            }

            return Task.FromResult(obj.ObjectType == "TABLE"
                ? new ObjectDetailOutline(obj,
                [
                    new DetailTab("일반", DetailPart.General),
                    new DetailTab("열", DetailPart.Table, new DetailSection("열", ["이름", "형식"], [["ID", "NUMBER"]])),
                    new DetailTab("트리거", DetailPart.Triggers),
                    new DetailTab("권한", DetailPart.Privileges),
                    new DetailTab("의존", DetailPart.Dependencies),
                ])
                : ObjectDetailParts.Outline(obj, [], partitioned: false));
        }

        public async Task<IReadOnlyList<DetailSection>> GetDetailPartAsync(ISession s, DbObjectRef obj, DetailPart part, CancellationToken ct)
        {
            Parts.Add(part);
            LastPartToken = ct;
            if (PartGate is { } gate)
            {
                await gate;
            }

            if (part == FailingPart)
            {
                throw new InvalidOperationException("ORA-00942: table or view does not exist");
            }

            return part switch
            {
                DetailPart.General => [new DetailSection("일반", ["항목", "값"], [["유형", obj.ObjectType]])],
                DetailPart.Privileges => [new DetailSection("권한", ["받는 사람", "권한"], [["HR", "SELECT"]])],
                DetailPart.Dependencies => [new DetailSection("의존", ["방향"], [["참조됨"]])],
                _ => [],
            };
        }

        public Task<Oranda.Core.Metadata.ObjectDetail> GetObjectDetailAsync(ISession s, DbObjectRef obj, CancellationToken ct)
        {
            WholeCalls++;
            return Task.FromResult(new Oranda.Core.Metadata.ObjectDetail(obj, []));
        }

        public Task<string> GetDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct)
        {
            DdlCalls++;
            return Task.FromResult($"CREATE TABLE {obj.Name};");
        }

        public Task<IReadOnlyList<string>> ListSchemasAsync(ISession s, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<DbObjectRef>> ListObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct) => throw new NotSupportedException();

        public Task<TableInfo> GetTableAsync(ISession s, DbObjectRef table, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetSourceAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<CompileError>> GetCompileErrorsAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();
    }
}
