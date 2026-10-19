using Oranda.App.Features.ObjectDetail;
using Oranda.App.Features.SchemaBrowser;
using Oranda.App.Tests.Shell;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Features.ObjectDetail;

/// <summary>TSK-07-03 객체 상세·DDL 보기 시험(ViewModel·명령 처리기·등록).</summary>
public class ObjectDetailTests
{
    private static readonly DbObjectRef Emp = new("SCOTT", "EMP", "TABLE");

    [Fact]
    public void Frame_exists()
    {
        Assert.NotNull(new ObjectDetailViewModel());
    }

    [Fact]
    public void Panel_and_commands_are_registered()
    {
        var panel = ShellTestHelpers.RequirePanel("oranda.panel.objectDetail");
        Assert.Equal(ToolPanelKind.Document, panel.Kind);
        Assert.True(panel.AllowMultiple);
        var reg = ShellTestHelpers.CreateCommandRegistry();
        Assert.NotNull(reg.Find("oranda.schema.openDetail"));
        Assert.NotNull(reg.Find("oranda.schema.showDdl"));
    }

    [Fact]
    public void Section_text_aligns_columns_and_formats_values()
    {
        var section = new ObjectDetailSection(new DetailSection("열", ["이름", "형식"],
            [["ID", "NUMBER"], ["CREATED", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)], ["N", null]]));

        var lines = section.Text.Split(Environment.NewLine);
        Assert.Equal("이름       형식", lines[0].TrimEnd());
        Assert.StartsWith("ID", lines[2]);
        Assert.Contains("NUMBER", lines[2]);
        Assert.Contains("2026-01-02 03:04:05", lines[3]);
        Assert.Equal(5, lines.Length);
    }

    [Fact]
    public async Task Schema_properties_order_tabs_like_orange_load_definition_on_demand_and_keep_the_tab()
    {
        var meta = new FakeMeta();
        var session = new FakeSession();
        using var props = new SchemaPropertiesViewModel(() => meta, () => session) { Delay = TimeSpan.Zero };

        await props.ShowAsync(Emp);
        Assert.Equal(["열", "권한", "정의"], props.Tabs.Select(t => t.Title));
        Assert.Equal("열", props.SelectedTab!.Title);
        Assert.Null(meta.LastOptions); // 정의는 그 탭을 고를 때만 읽는다

        props.SelectedTab = props.Tabs[^1];
        Assert.Equal("CREATE TABLE EMP;", props.SelectedTab.DdlText);

        // 다른 객체로 옮겨도 보던 탭(정의)을 유지하고 그 객체의 정의를 읽는다.
        await props.ShowAsync(new DbObjectRef("SCOTT", "DEPT", "TABLE"));
        Assert.Equal("정의", props.SelectedTab!.Title);
        Assert.Equal("CREATE TABLE DEPT;", props.SelectedTab.DdlText);
        Assert.Contains("SCOTT.DEPT", props.Header, StringComparison.Ordinal);

        await props.ShowAsync(null);
        Assert.Empty(props.Tabs);
    }

    [Fact]
    public async Task Schema_properties_report_a_failed_read_in_the_header()
    {
        using var props = new SchemaPropertiesViewModel(() => new FakeMeta { DetailFails = true }, () => new FakeSession()) { Delay = TimeSpan.Zero };
        await props.ShowAsync(Emp);
        Assert.Empty(props.Tabs);
        Assert.Contains("boom", props.Header, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Open_fills_sections_and_ddl()
    {
        var meta = new FakeMeta();
        var vm = new ObjectDetailViewModel(meta);

        await vm.OpenAsync(new FakeSession(), Emp, showDdl: true);

        Assert.Equal("SCOTT.EMP", vm.Title);
        Assert.Equal(["권한", "열"], vm.Sections.Select(s => s.Title));
        Assert.Equal("열", vm.SelectedSection?.Title);
        Assert.Equal("CREATE TABLE EMP;", vm.DdlText);
        Assert.True(vm.IsDdlSelected);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Changing_ddl_options_reloads_ddl_with_those_options()
    {
        var meta = new FakeMeta();
        var vm = new ObjectDetailViewModel(meta);
        await vm.OpenAsync(new FakeSession(), Emp, showDdl: false);
        Assert.False(meta.LastOptions!.IncludeStorage);
        Assert.True(meta.LastOptions.SqlTerminator);

        vm.IncludeStorage = true;
        vm.IncludeDependentObjects = true;
        vm.SqlTerminator = false;
        await vm.LoadDdlAsync();

        Assert.True(meta.LastOptions.IncludeStorage);
        Assert.True(meta.LastOptions.IncludeDependentObjects);
        Assert.False(meta.LastOptions.SqlTerminator);
    }

    [Fact]
    public async Task Ddl_failure_keeps_detail_and_reports_error()
    {
        var meta = new FakeMeta { DdlFails = true };
        var vm = new ObjectDetailViewModel(meta);

        await vm.OpenAsync(new FakeSession(), Emp, showDdl: true);

        Assert.Equal(2, vm.Sections.Count);
        Assert.Equal(string.Empty, vm.DdlText);
        Assert.Contains("DDL 을 읽지 못했다", vm.StatusText);
        Assert.Contains("ORA-31603", vm.StatusText);
    }

    [Fact]
    public async Task Detail_failure_keeps_ddl_and_reports_error()
    {
        var meta = new FakeMeta { DetailFails = true };
        var vm = new ObjectDetailViewModel(meta);

        await vm.OpenAsync(new FakeSession(), Emp, showDdl: false);

        Assert.Empty(vm.Sections);
        Assert.Equal("CREATE TABLE EMP;", vm.DdlText);
        Assert.Contains("상세를 읽지 못했다", vm.StatusText);
    }

    [Fact]
    public async Task Handler_opens_document_for_node_parameter_and_selects_ddl_for_showDdl()
    {
        var meta = new FakeMeta();
        var session = new FakeSession();
        ObjectDetailViewModel? opened = null;
        string? openedTitle = null;
        var handler = new ObjectDetailCommandHandler(new Services(meta))
        {
            SessionProvider = () => session,
            OpenDocument = (vm, title) =>
            {
                opened = vm;
                openedTitle = title;
                return true;
            },
        };
        var node = new SchemaNode(null, SchemaNodeKind.Item, "EMP") { Ref = Emp };

        await handler.ExecuteAsync(new CommandContext("oranda.schema.showDdl", FakeContextKeys.AllTrue, node), default);

        Assert.NotNull(opened);
        Assert.Equal("SCOTT.EMP", openedTitle);
        Assert.True(opened.IsDdlSelected);
        Assert.Equal("CREATE TABLE EMP;", opened.DdlText);

        await handler.ExecuteAsync(new CommandContext("oranda.schema.openDetail", FakeContextKeys.AllTrue, Emp), default);
        Assert.False(opened.IsDdlSelected);
        Assert.Equal(2, opened.Sections.Count);
    }

    [Fact]
    public async Task Handler_does_nothing_without_target_or_session()
    {
        var calls = 0;
        var handler = new ObjectDetailCommandHandler(new Services(new FakeMeta()))
        {
            SessionProvider = () => null,
            OpenDocument = (_, _) =>
            {
                calls++;
                return true;
            },
        };

        await handler.ExecuteAsync(new CommandContext("oranda.schema.openDetail", FakeContextKeys.AllTrue, Emp), default);
        await handler.ExecuteAsync(new CommandContext("oranda.schema.openDetail", FakeContextKeys.AllTrue), default);
        await new ObjectDetailCommandHandler().ExecuteAsync(new CommandContext("oranda.schema.openDetail", FakeContextKeys.AllTrue, Emp), default);

        Assert.Equal(0, calls);
    }

    private sealed class Services(IMetadataProvider meta) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IMetadataProvider) ? meta : null;
    }

    private sealed class FakeSession : ISession
    {
        public Guid Id { get; } = Guid.NewGuid();

        public ConnectionProfile Profile { get; } = new("t", "SCOTT", "localhost/FREEPDB1", Id: Guid.NewGuid());

        public ServerInfo Server => throw new NotSupportedException();

        public SessionPurpose Purpose => SessionPurpose.Work;

        public string DbKey => "FREE/FREEPDB1/SCOTT";

        public SessionState State => SessionState.Idle;

        public bool HasPendingTransaction => false;

        public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeMeta : IMetadataProvider
    {
        public bool DdlFails { get; init; }

        public bool DetailFails { get; init; }

        public DdlOptions? LastOptions { get; private set; }

        public Task<string> GetDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct)
        {
            LastOptions = o;
            return DdlFails
                ? Task.FromException<string>(new InvalidOperationException("ORA-31603: not found"))
                : Task.FromResult($"CREATE TABLE {obj.Name};");
        }

        public Task<Oranda.Core.Metadata.ObjectDetail> GetObjectDetailAsync(ISession s, DbObjectRef obj, CancellationToken ct)
            => DetailFails
                ? Task.FromException<Oranda.Core.Metadata.ObjectDetail>(new InvalidOperationException("boom"))
                : Task.FromResult(new Oranda.Core.Metadata.ObjectDetail(obj,
                [
                    new DetailSection("권한", ["받는 사람", "권한"], [["HR", "SELECT"]]),
                    new DetailSection("열", ["이름", "형식"], [["ID", "NUMBER"]]),
                ]));

        public Task<IReadOnlyList<string>> ListSchemasAsync(ISession s, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<DbObjectRef>> ListObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct) => throw new NotSupportedException();

        public Task<TableInfo> GetTableAsync(ISession s, DbObjectRef table, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetSourceAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<CompileError>> GetCompileErrorsAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();
    }
}
