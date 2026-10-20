using Oranda.App.Features.SchemaBrowser;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Extensibility;
using Oranda.Core.Metadata;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Core.Text;

namespace Oranda.App.Tests.Features.Navigation;

/// <summary>DB 객체로 이동·스키마 브라우저에서 찾기 시험이 함께 쓰는 가짜(메타데이터·세션·편집기).</summary>
internal sealed class NavFakeSession(string user = "SCOTT", string dbKey = "FREE/FREEPDB1/SCOTT") : ISession
{
    public Guid Id { get; } = Guid.NewGuid();

    public ConnectionProfile Profile { get; } = new("t", user, "localhost/FREEPDB1", Id: Guid.NewGuid());

    public ServerInfo Server => throw new NotSupportedException();

    public SessionPurpose Purpose => SessionPurpose.Work;

    public string DbKey { get; } = dbKey;

    public SessionState State => SessionState.Idle;

    public bool HasPendingTransaction => false;

    public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;

    public Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class NavFakeSessions : ISessionManager
{
    public List<ISession> Sessions { get; } = [];

    IReadOnlyList<ISession> ISessionManager.Sessions => Sessions;

    public Task<ISession> OpenAsync(ConnectionProfile profile, SecureCredential credential, SessionPurpose purpose, CancellationToken ct) =>
        throw new NotSupportedException();
}

internal sealed class NavFakeEditor : IEditorContext
{
    public EditorSnapshot? Doc { get; set; }

    public EditorSnapshot? GetActiveDocument() => Doc;

    public Oranda.Core.Execution.SqlStatement? GetCurrentStatement() => null;

    public Task<bool> ProposeEditAsync(TextEdit edit, CancellationToken ct) => Task.FromResult(false);

    /// <summary>글과 캐럿(<c>|</c> 자리)으로 문서를 만든다.</summary>
    public static EditorSnapshot At(string textWithCaret, Guid? profileId = null)
    {
        var caret = textWithCaret.IndexOf('|', StringComparison.Ordinal);
        var text = textWithCaret.Remove(caret, 1);
        return new EditorSnapshot(Guid.NewGuid(), text, new TextRange(caret, 0), caret) { ConnectionProfileId = profileId };
    }
}

/// <summary>목록을 메모리에서 거르는 메타데이터 제공자. 객체 목록 호출 수와 마지막 조건을 기록한다.</summary>
internal sealed class NavFakeMeta : IMetadataProvider
{
    public List<string> Schemas { get; } = [];

    public List<DbObjectRef> Objects { get; } = [];

    public int ListObjectsCalls { get; private set; }

    public List<ObjectQuery> Queries { get; } = [];

    public Task<IReadOnlyList<string>> ListSchemasAsync(ISession s, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>([.. Schemas]);

    public Task<IReadOnlyList<DbObjectRef>> ListObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct)
    {
        ListObjectsCalls++;
        Queries.Add(q);
        var like = q.NamePattern?.Trim('%');
        IReadOnlyList<DbObjectRef> r =
        [
            .. Objects
                .Where(o => q.Owner is null || o.Owner == q.Owner)
                .Where(o => q.ObjectTypes.Count == 0 || q.ObjectTypes.Contains(o.ObjectType))
                .Where(o => like is null || o.Name.Contains(like, StringComparison.Ordinal)),
        ];
        return Task.FromResult(r);
    }

    public Task<TableInfo> GetTableAsync(ISession s, DbObjectRef table, CancellationToken ct) =>
        Task.FromResult(new TableInfo(table, [new ColumnInfo("ID", 1, "NUMBER")]));

    public Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

    public Task<string> GetDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct) => throw new NotSupportedException();

    public Task<Oranda.Core.Metadata.ObjectDetail> GetObjectDetailAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

    public Task<string> GetSourceAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<CompileError>> GetCompileErrorsAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();
}

/// <summary>스키마 브라우저 ViewModel 과 그 서비스(가짜)를 한데 묶는다.</summary>
internal sealed class NavBrowserHarness
{
    public NavBrowserHarness()
    {
        Meta.Schemas.AddRange(["HR", "SCOTT"]);
        Meta.Objects.AddRange(
        [
            new DbObjectRef("SCOTT", "EMP", "TABLE"),
            new DbObjectRef("SCOTT", "DEPT", "TABLE"),
            new DbObjectRef("SCOTT", "V_EMP", "VIEW"),
            new DbObjectRef("SCOTT", "EMP_PKG", "PACKAGE"),
            new DbObjectRef("SCOTT", "EMP_PKG", "PACKAGE BODY"),
            new DbObjectRef("HR", "JOBS", "TABLE"),
            new DbObjectRef("HR", "EMP_API", "PACKAGE"),
        ]);
        Sessions.Sessions.Add(Session);
        Vm = new SchemaBrowserViewModel(new Provider(this)) { PostToUi = a => a() };
        Vm.DebounceMilliseconds = 3_600_000; // 자동 적용은 시험에서 끈다(명시 호출만)
    }

    public NavFakeSession Session { get; } = new();

    public NavFakeSessions Sessions { get; } = new();

    public NavFakeMeta Meta { get; } = new();

    public NavFakeEditor Editor { get; } = new();

    public EventBus Bus { get; } = new();

    public InMemorySchemaFavoriteStore Favorites { get; } = new();

    public SchemaBrowserViewModel Vm { get; }

    private sealed class Provider(NavBrowserHarness h) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IMetadataProvider) ? h.Meta
            : serviceType == typeof(IEventBus) ? h.Bus
            : serviceType == typeof(ISessionManager) ? h.Sessions
            : serviceType == typeof(IEditorContext) ? h.Editor
            : serviceType == typeof(ISchemaFavoriteStore) ? h.Favorites
            : null;
    }
}
