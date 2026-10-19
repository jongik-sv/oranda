using Oranda.App.Features.SchemaBrowser;
using Oranda.App.Tests.Shell;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Extensibility;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Security;
using Oranda.Core.Shell;
using Oranda.Core.Text;

namespace Oranda.App.Tests.Features.SchemaBrowser;

/// <summary>TSK-07-02 스키마 브라우저 모델 시험(트리 지연 로딩·필터·즐겨찾기·삽입 텍스트·편집기 계약).</summary>
public class SchemaBrowserTests
{
    private static readonly DbObjectRef Emp = new("SCOTT", "EMP", "TABLE");

    [Fact]
    public void Panel_and_commands_are_registered()
    {
        Assert.Equal(DockLocation.Left, ShellTestHelpers.RequirePanel("oranda.panel.schemaBrowser").DefaultLocation);
        var reg = ShellTestHelpers.CreateCommandRegistry();
        foreach (var id in new[]
        {
            "oranda.schema.refresh", "oranda.schema.filter", "oranda.schema.toggleFavorite",
            "oranda.schema.insertName", "oranda.schema.insertSelect", "oranda.schema.insertColumns",
        })
        {
            Assert.NotNull(reg.Find(id));
        }
    }

    [Fact]
    public void Parameterless_view_model_is_an_empty_frame()
    {
        Assert.Empty(new SchemaBrowserViewModel().Roots);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    [InlineData("emp", "%EMP%")]
    [InlineData("emp*", "EMP%")]
    [InlineData("*_log", "%\\_LOG")]
    [InlineData("a%b", "A%B")]
    [InlineData("\"mixed\"", "%mixed%")]
    public void Filter_text_becomes_like_pattern(string? text, string? expected) =>
        Assert.Equal(expected, SchemaFilter.ToLikePattern(text));

    [Fact]
    public void Quote_only_when_needed()
    {
        Assert.Equal("EMP", SchemaSql.Quote("EMP"));
        Assert.Equal("\"Emp\"", SchemaSql.Quote("Emp"));
        Assert.Equal("\"A B\"", SchemaSql.Quote("A B"));
        Assert.Equal("E$1#", SchemaSql.Quote("E$1#"));
    }

    [Fact]
    public async Task Tree_loads_lazily_and_groups_by_type()
    {
        var h = new Harness();
        await h.Vm.SetSessionAsync(h.Session);

        Assert.Equal(["즐겨찾기", "SCOTT", "HR"], h.Vm.Roots.Select(r => r.Title)); // 내 스키마가 맨 위
        Assert.Equal(2, h.Meta.ListObjectsCalls); // 내 스키마(SCOTT)만 자동 펼침(INVALID 목록 + 전체 목록)
        Assert.True(h.Vm.Roots[1].IsExpanded);
        var scott = h.Vm.Roots[1];
        Assert.Equal(["테이블 (2)", "뷰 (1)", "프로시저 (1)"], scott.Children.Select(c => c.Title));

        var hr = h.Vm.Roots[2];
        Assert.Equal(SchemaNodeKind.Placeholder, hr.Children.Single().Kind);
        await h.Vm.ExpandAsync(hr);
        Assert.Equal(4, h.Meta.ListObjectsCalls);
        Assert.Single(hr.Children);
        Assert.Equal("테이블 (1)", hr.Children[0].Title);
    }

    [Fact]
    public async Task Expanding_a_table_shows_columns()
    {
        var h = new Harness();
        await h.Vm.SetSessionAsync(h.Session);
        var tables = h.Vm.Roots[1].Children[0];
        await h.Vm.ExpandAsync(tables);
        var emp = tables.Children.First(c => c.Title == "EMP");
        await h.Vm.ExpandAsync(emp);

        Assert.Equal(["EMPNO", "ENAME", "HIDDEN"], emp.Children.Select(c => c.Title));
        Assert.Equal("NUMBER(4) NOT NULL", emp.Children[0].Detail);
        Assert.Equal("VARCHAR2(10)", emp.Children[1].Detail);
    }

    [Fact]
    public async Task Ten_thousand_objects_are_shown_in_pages()
    {
        var h = new Harness();
        for (var i = 0; i < 10_000; i++)
        {
            h.Meta.Objects.Add(new DbObjectRef("BIG", $"T{i:D5}", "TABLE"));
        }

        h.Meta.Schemas.Add("BIG");
        await h.Vm.SetSessionAsync(h.Session);
        var big = h.Vm.Roots.Single(r => r.Title == "BIG");
        await h.Vm.ExpandAsync(big);
        var cat = big.Children.Single();
        Assert.Equal("테이블 (10000)", cat.Title);

        await h.Vm.ExpandAsync(cat);
        Assert.Equal(SchemaBrowserViewModel.PageSize + 1, cat.Children.Count);
        var more = cat.Children[^1];
        Assert.Equal(SchemaNodeKind.More, more.Kind);

        h.Vm.ShowMore(more);
        Assert.Equal((2 * SchemaBrowserViewModel.PageSize) + 1, cat.Children.Count);

        for (var i = 0; i < 30; i++)
        {
            if (cat.Children[^1].Kind == SchemaNodeKind.More)
            {
                h.Vm.ShowMore(cat.Children[^1]);
            }
        }

        Assert.Equal(10_000, cat.Children.Count);
        Assert.DoesNotContain(cat.Children, c => c.Kind == SchemaNodeKind.More);
    }

    [Fact]
    public async Task Filter_is_pushed_to_the_server_query()
    {
        var h = new Harness();
        await h.Vm.SetSessionAsync(h.Session);
        h.Vm.FilterText = "emp";
        h.Vm.TypeFilter = "TABLE";
        h.Vm.InvalidOnly = true;
        await h.Vm.ApplyFilterAsync();

        var q = h.Meta.LastQuery!;
        Assert.Equal("SCOTT", q.Owner);
        Assert.Equal("%EMP%", q.NamePattern);
        Assert.Equal(["TABLE"], q.ObjectTypes);
        Assert.True(q.InvalidOnly);
        var scott = h.Vm.Roots.Single(r => r.Title == "SCOTT");
        Assert.Equal(["테이블 (1)"], scott.Children.Select(c => c.Title));
    }

    [Fact]
    public async Task Favorites_are_kept_per_connection_and_listed_first()
    {
        var h = new Harness();
        await h.Vm.SetSessionAsync(h.Session);
        var tables = h.Vm.Roots[1].Children[0];
        await h.Vm.ExpandAsync(tables);
        var emp = tables.Children.First(c => c.Title == "EMP");

        await h.Vm.ToggleFavoriteAsync(emp);
        Assert.True(emp.IsFavorite);
        var fav = h.Vm.Roots[0];
        await h.Vm.ExpandAsync(fav);
        Assert.Equal(["EMP"], fav.Children.Select(c => c.Title));

        // 다른 접속에는 보이지 않는다.
        var other = await h.Favorites.LoadAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.Empty(other);

        await h.Vm.ToggleFavoriteAsync(emp);
        Assert.False(emp.IsFavorite);
    }

    [Fact]
    public async Task Favorites_only_filter_hides_the_rest()
    {
        var h = new Harness();
        await h.Favorites.SetAsync(h.Session.Profile.Id, SchemaSql.FavoriteKey(Emp), true, CancellationToken.None);
        await h.Vm.SetSessionAsync(h.Session);
        h.Vm.FavoritesOnly = true;
        await h.Vm.ApplyFilterAsync();

        var scott = h.Vm.Roots.Single(r => r.Title == "SCOTT");
        Assert.Equal(["테이블 (1)"], scott.Children.Select(c => c.Title));
    }

    [Fact]
    public async Task Select_text_lists_columns_and_skips_invisible()
    {
        var h = new Harness();
        await h.Vm.SetSessionAsync(h.Session);
        var node = h.Vm.Roots[1].Children[0];
        await h.Vm.ExpandAsync(node);
        var emp = node.Children.First(c => c.Title == "EMP");

        Assert.Equal("SCOTT.EMP", await h.Vm.BuildTextAsync(emp, SchemaInsertMode.Name));
        Assert.Equal("EMPNO, ENAME", await h.Vm.BuildTextAsync(emp, SchemaInsertMode.Columns));
        Assert.Equal("SELECT EMPNO,\n       ENAME\nFROM   SCOTT.EMP", await h.Vm.BuildTextAsync(emp, SchemaInsertMode.Select));
        Assert.Equal("SELECT EMPNO,\n       ENAME\nFROM   SCOTT.EMP", await h.Vm.PrepareDragTextAsync(emp, false, false));
        Assert.Equal("SCOTT.EMP", await h.Vm.PrepareDragTextAsync(emp, true, false));
    }

    [Fact]
    public void Sequence_and_procedure_text()
    {
        Assert.Equal("SELECT HR.SEQ1.NEXTVAL FROM DUAL", SchemaSql.Select(new DbObjectRef("HR", "SEQ1", "SEQUENCE"), null));
        Assert.Equal("SCOTT.PROC1", SchemaSql.Select(new DbObjectRef("SCOTT", "PROC1", "PROCEDURE"), null));
    }

    [Fact]
    public async Task Insert_goes_through_editor_context_at_selection()
    {
        var h = new Harness();
        h.Editor.Doc = new EditorSnapshot(Guid.NewGuid(), "abcdef", new TextRange(2, 3), 5);
        await h.Vm.SetSessionAsync(h.Session);
        var node = h.Vm.Roots[1].Children[0];
        await h.Vm.ExpandAsync(node);
        var emp = node.Children.First(c => c.Title == "EMP");

        Assert.True(await h.Vm.InsertAsync(emp, SchemaInsertMode.Select));
        var edit = Assert.Single(h.Editor.Edits);
        Assert.Equal(new TextRange(2, 3), edit.Range);
        Assert.StartsWith("SELECT EMPNO", edit.NewText, StringComparison.Ordinal);

        h.Editor.Doc = new EditorSnapshot(Guid.NewGuid(), "abcdef", new TextRange(4, 0), 4);
        Assert.True(await h.Vm.InsertAsync(emp, SchemaInsertMode.Name));
        Assert.Equal(new TextRange(4, 0), h.Editor.Edits[1].Range);
    }

    [Fact]
    public async Task Insert_without_editor_document_does_nothing()
    {
        var h = new Harness();
        await h.Vm.SetSessionAsync(h.Session);
        Assert.False(await h.Vm.InsertAsync(h.Vm.Roots[1], SchemaInsertMode.Name));
        Assert.Empty(h.Editor.Edits);
    }

    [Fact]
    public async Task Command_handler_runs_insert_with_node_parameter()
    {
        var h = new Harness();
        h.Editor.Doc = new EditorSnapshot(Guid.NewGuid(), "", new TextRange(0, 0), 0);
        await h.Vm.SetSessionAsync(h.Session);
        var tables = h.Vm.Roots[1].Children[0];
        await h.Vm.ExpandAsync(tables);
        var emp = tables.Children.First(c => c.Title == "EMP");

        var handler = new SchemaBrowserCommandHandler(h.Vm);
        await handler.ExecuteAsync(new CommandContext("oranda.schema.insertName", FakeContextKeys.AllTrue, emp), CancellationToken.None);
        Assert.Equal("SCOTT.EMP", Assert.Single(h.Editor.Edits).NewText);

        var filterRequested = false;
        h.Vm.FilterFocusRequested += (_, _) => filterRequested = true;
        await handler.ExecuteAsync(new CommandContext("oranda.schema.filter", FakeContextKeys.AllTrue), CancellationToken.None);
        Assert.True(filterRequested);
    }

    [Fact]
    public async Task Session_events_rebuild_tree()
    {
        var h = new Harness();
        h.Manager.Sessions.Add(h.Session);
        h.Bus.Publish(new SessionOpened(h.Session.Id, h.Session.Profile, Server(), SessionPurpose.Work));
        await WaitForAsync(() => h.Vm.Roots.Count > 0);
        Assert.True(h.Vm.HasSession);
        Assert.Equal("SCOTT", h.Vm.Roots[1].Title);

        h.Manager.Sessions.Clear();
        h.Bus.Publish(new SessionClosed(h.Session.Id));
        await WaitForAsync(() => h.Vm.Roots.Count == 0);
        Assert.False(h.Vm.HasSession);
    }

    [Fact]
    public async Task Monitor_sessions_are_ignored()
    {
        var h = new Harness();
        h.Manager.Sessions.Add(h.Session);
        h.Bus.Publish(new SessionOpened(h.Session.Id, h.Session.Profile, Server(), SessionPurpose.Monitor));
        await Task.Delay(50);
        Assert.Empty(h.Vm.Roots);
    }

    [Fact]
    public async Task Metadata_invalidation_reloads_expanded_schema()
    {
        var h = new Harness();
        await h.Vm.SetSessionAsync(h.Session);
        var calls = h.Meta.ListObjectsCalls;
        h.Bus.Publish(new MetadataInvalidated(h.Session.DbKey, []));
        await WaitForAsync(() => h.Meta.ListObjectsCalls > calls);
        h.Bus.Publish(new MetadataInvalidated("OTHER/DB/USER", []));
        var after = h.Meta.ListObjectsCalls;
        await Task.Delay(50);
        Assert.Equal(after, h.Meta.ListObjectsCalls);
    }

    private static ServerInfo Server() =>
        new("23.26.0.0.0", "FREE", "localhost", "FREEPDB1", "FREE", "FREEPDB1", true, 1,
            new ServerCapabilities(false, true, true, false, false), DbUniqueName: "FREE", SessionUser: "SCOTT");

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private sealed class Harness
    {
        public Harness()
        {
            Session = new TestSession();
            Meta.Schemas.AddRange(["HR", "SCOTT"]);
            Meta.Objects.AddRange(
            [
                Emp,
                new DbObjectRef("SCOTT", "DEPT", "TABLE"),
                new DbObjectRef("SCOTT", "V_EMP", "VIEW"),
                new DbObjectRef("SCOTT", "PROC1", "PROCEDURE"),
                new DbObjectRef("HR", "JOBS", "TABLE"),
            ]);
            Meta.Tables[Emp] = new TableInfo(Emp,
            [
                new ColumnInfo("ENAME", 2, "VARCHAR2") { Length = 10 },
                new ColumnInfo("EMPNO", 1, "NUMBER") { Precision = 4, IsNullable = false },
                new ColumnInfo("HIDDEN", 3, "NUMBER") { IsInvisible = true },
            ]);
            Vm = new SchemaBrowserViewModel(new Services(this)) { PostToUi = a => a() };
            Vm.DebounceMilliseconds = 3_600_000; // 자동 적용은 시험에서 끈다(명시 호출만)
        }

        public TestSession Session { get; }

        public EventBus Bus { get; } = new();

        public TestManager Manager { get; } = new();

        public TestMeta Meta { get; } = new();

        public TestEditor Editor { get; } = new();

        public InMemorySchemaFavoriteStore Favorites { get; } = new();

        public SchemaBrowserViewModel Vm { get; }
    }

    private sealed class Services(Harness h) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IMetadataProvider) ? h.Meta
            : serviceType == typeof(IEventBus) ? h.Bus
            : serviceType == typeof(ISessionManager) ? h.Manager
            : serviceType == typeof(IEditorContext) ? h.Editor
            : serviceType == typeof(ISchemaFavoriteStore) ? h.Favorites
            : null;
    }

    private sealed class TestSession : ISession
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

    private sealed class TestManager : ISessionManager
    {
        public List<ISession> Sessions { get; } = [];

        IReadOnlyList<ISession> ISessionManager.Sessions => Sessions;

        public Task<ISession> OpenAsync(ConnectionProfile profile, SecureCredential credential, SessionPurpose purpose, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class TestEditor : IEditorContext
    {
        public EditorSnapshot? Doc { get; set; }

        public List<TextEdit> Edits { get; } = [];

        public EditorSnapshot? GetActiveDocument() => Doc;

        public Oranda.Core.Execution.SqlStatement? GetCurrentStatement() => null;

        public Task<bool> ProposeEditAsync(TextEdit edit, CancellationToken ct)
        {
            Edits.Add(edit);
            return Task.FromResult(true);
        }
    }

    private sealed class TestMeta : IMetadataProvider
    {
        public List<string> Schemas { get; } = [];

        public List<DbObjectRef> Objects { get; } = [];

        public Dictionary<DbObjectRef, TableInfo> Tables { get; } = [];

        public int ListObjectsCalls { get; private set; }

        public ObjectQuery? LastQuery { get; private set; }

        public Task<IReadOnlyList<string>> ListSchemasAsync(ISession s, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(Schemas);

        public Task<IReadOnlyList<DbObjectRef>> ListObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct)
        {
            ListObjectsCalls++;
            LastQuery = q;
            var like = q.NamePattern is null ? null : q.NamePattern.Trim('%');
            var r = Objects
                .Where(o => q.Owner is null || o.Owner == q.Owner)
                .Where(o => q.ObjectTypes.Count == 0 || q.ObjectTypes.Contains(o.ObjectType))
                .Where(o => like is null || o.Name.Contains(like, StringComparison.Ordinal))
                .ToList();
            return Task.FromResult<IReadOnlyList<DbObjectRef>>(r);
        }

        public Task<TableInfo> GetTableAsync(ISession s, DbObjectRef table, CancellationToken ct) => Task.FromResult(Tables[table]);

        public Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct) => throw new NotSupportedException();

        public Task<Oranda.Core.Metadata.ObjectDetail> GetObjectDetailAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetSourceAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<CompileError>> GetCompileErrorsAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();
    }
}
