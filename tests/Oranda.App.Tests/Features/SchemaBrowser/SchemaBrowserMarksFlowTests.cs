using System.Collections.Concurrent;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.SchemaBrowser;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Composition;
using Oranda.Core.Composition.Dispatch;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;
using Oranda.Platform.Storage;

namespace Oranda.App.Tests.Features.SchemaBrowser;

/// <summary>
/// 실앱과 같은 조립으로 표식이 트리에 붙는지 본다: 디스패처(<see cref="MetadataProviderDispatcher"/>) → 캐시 메타데이터 제공자(<see cref="CachedMetadataProvider"/>)
/// → SQLite 캐시(<see cref="SqliteMetadataCache"/>), 하나의 이벤트 버스. 캐시가 빈 상태에서 시작해 첫 표식 조회는 빈 결과이고, 뒤 읽기가 끝나
/// <see cref="ObjectMarksRefreshed"/> 가 오면(다른 스레드에서, UI 로 넘겨) 이미 펼친 노드에 뱃지가 붙어야 한다. 이름 필터·다른 스키마를 펼친 경우.
/// </summary>
public sealed class SchemaBrowserMarksFlowTests : IDisposable
{
    private const string Me = "JONGIKJANG";
    private const string Other = "MESAPUSER";

    private static readonly string[] Typed = ["t", "te", "tem", "temp"];

    private readonly string _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "oranda-marks-flow-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Marks_read_behind_attach_to_the_filtered_tree_of_another_schema()
    {
        using var flow = new Flow(_dir);
        await flow.Vm.SetSessionAsync(flow.Session);
        await flow.PumpAsync();
        flow.Vm.FilterText = "temp";
        await flow.Vm.ApplyFilterAsync();
        var other = flow.Vm.Roots.Single(r => r.Owner == Other);
        await flow.Vm.ExpandAsync(other);
        other.IsExpanded = true;
        var tables = other.Children.Single(c => c.CategoryType == "TABLE");
        await flow.Vm.ExpandAsync(tables);
        tables.IsExpanded = true;
        var gtt = tables.Children.Single(c => c.Title == "TB_TEMP_CSM");
        Assert.Null(gtt.Mark); // 첫 조회: 캐시에 표식이 아직 없다(뒤 읽기는 아직 서버를 기다린다)

        flow.Provider.Release.TrySetResult();
        await flow.WaitAsync(() => gtt.Mark is not null);

        Assert.Equal("GTT_TRANSACTION", gtt.Mark!.SubKind);
        Assert.Equal("GTT·T", gtt.Look.Badges.Single().Label);
        Assert.Null(tables.Children.Single(c => c.Title == "TB_TEMP_PLAIN").Mark);
    }

    [Fact]
    public Task Badges_appear_in_the_view_after_marks_are_read_behind() => HeadlessHost.Run(async () =>
    {
        using var flow = new Flow(_dir, uiThread: true);
        var view = new SchemaBrowserView { DataContext = flow.Vm };
        var window = new Window { Width = 400, Height = 600, Content = view };
        window.Show();
        await flow.Vm.SetSessionAsync(flow.Session);
        flow.Vm.FilterText = "temp";
        await flow.Vm.ApplyFilterAsync();
        var other = flow.Vm.Roots.Single(r => r.Owner == Other);
        other.IsExpanded = true;
        await flow.WaitAsync(() => other.Loaded);
        var tables = other.Children.Single(c => c.CategoryType == "TABLE");
        tables.IsExpanded = true;
        await flow.WaitAsync(() => tables.Loaded);
        var gtt = tables.Children.Single(c => c.Title == "TB_TEMP_CSM");
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(view.GetVisualDescendants().OfType<Border>(), p => p.Classes.Contains("schema-badge")); // 그린 뒤에 표식이 온다

        var offUi = 0;
        gtt.PropertyChanged += (_, _) => offUi += Dispatcher.UIThread.CheckAccess() ? 0 : 1;
        flow.Provider.Release.TrySetResult();
        await flow.WaitAsync(() => gtt.Mark is not null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, offUi); // 뱃지는 UI 스레드에서 바뀐다(다른 스레드면 화면 바인딩이 따라오지 않는다)

        var badge = view.GetVisualDescendants().OfType<Border>().FirstOrDefault(p => p.Classes.Contains("schema-badge"));
        Assert.NotNull(badge);
        Assert.Equal("임시 테이블(트랜잭션 유지)", ((SchemaBadge)badge.Tag!).Tip);
        Assert.NotNull(badge.BorderBrush); // 늦게 붙은 표식도 테마 토큰이 풀렸다
        Assert.True(badge.IsEffectivelyVisible);
        Assert.True(badge.Bounds.Width > 0);
        window.Close();
    });

    [Fact]
    public Task Marks_reach_the_tree_through_the_app_composition() => HeadlessHost.Run(async () =>
    {
        using var container = Oranda.App.Shell.Composition.AppComposition.Create(new Oranda.App.Shell.Composition.AppCompositionOptions { DataDirectory = _dir });
        _ = container.DbmsServices.AvailableDbms; // 지연 어셈블리를 훑은 뒤 Oracle 제공자 자리를 서버 대역으로 바꾼다(캐시·버스는 앱 것 그대로)
        var provider = new MarkingProvider((SqliteMetadataCache)container.GetRequiredService<Oranda.Core.Storage.IMetadataCache>(), container.GetRequiredService<IEventBus>());
        container.DbmsServices.Add<IMetadataProvider>(Dbms.Oracle, () => provider);
        var vm = (SchemaBrowserViewModel)container.Create(typeof(SchemaBrowserViewModel));
        vm.DebounceMilliseconds = 3_600_000;
        var session = new FlowSession();
        await vm.SetSessionAsync(session);
        vm.FilterText = "temp";
        await vm.ApplyFilterAsync();
        var other = vm.Roots.Single(r => r.Owner == Other);
        await vm.ExpandAsync(other);
        other.IsExpanded = true;
        var tables = other.Children.Single(c => c.CategoryType == "TABLE");
        await vm.ExpandAsync(tables);
        tables.IsExpanded = true;
        var gtt = tables.Children.Single(c => c.Title == "TB_TEMP_CSM");
        Assert.Null(gtt.Mark);

        provider.Release.TrySetResult();
        for (var i = 0; i < 500 && gtt.Mark is null; i++)
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal("GTT_TRANSACTION", gtt.Mark?.SubKind);
    });

    [Fact]
    public Task Marks_attach_when_the_read_behind_waits_for_a_busy_session_and_the_list_changes_meanwhile() => HeadlessHost.Run(async () =>
    {
        using var flow = new Flow(_dir, uiThread: true);
        flow.Provider.BusyTimes = 3;
        flow.Provider.RetryDelay = TimeSpan.FromMilliseconds(30);
        await flow.Vm.SetSessionAsync(flow.Session);
        flow.Vm.FilterText = "temp";
        await flow.Vm.ApplyFilterAsync();
        var other = flow.Vm.Roots.Single(r => r.Owner == Other);
        other.IsExpanded = true;
        await flow.WaitAsync(() => other.Loaded);
        var tables = other.Children.Single(c => c.CategoryType == "TABLE");
        tables.IsExpanded = true;
        await flow.WaitAsync(() => tables.Loaded);

        // 바빠 기다리는 사이 목록 뒤 갱신이 끝나 바뀜을 알린다(다른 스레드에서): 트리는 다시 그려진다.
        flow.Provider.Release.TrySetResult();
        await Task.Run(() =>
        {
            flow.Bus.Publish(new MetadataInvalidated(flow.Session.DbKey, [new DbObjectRef(Other, "TB_OTHER", "TABLE")]));
            flow.Bus.Publish(new ObjectListRefreshed(flow.Session.DbKey, DateTimeOffset.UtcNow, ObjectListChange.None));
        });
        flow.Vm.FilterText = "tem";
        await flow.Vm.ApplyFilterAsync();

        await flow.WaitAsync(() => Find(flow.Vm, "TB_TEMP_CSM")?.Mark is not null);
        Assert.Equal("GTT_TRANSACTION", Find(flow.Vm, "TB_TEMP_CSM")!.Mark!.SubKind);
    });

    [Fact]
    public Task Marks_attach_after_typing_the_filter_while_another_schema_is_open() => HeadlessHost.Run(async () =>
    {
        using var flow = new Flow(_dir, uiThread: true);
        await flow.Vm.SetSessionAsync(flow.Session);
        var other = flow.Vm.Roots.Single(r => r.Owner == Other);
        other.IsExpanded = true;
        await flow.WaitAsync(() => other.Loaded);
        other.Children.Single(c => c.CategoryType == "TABLE").IsExpanded = true;
        flow.Vm.DebounceMilliseconds = 5;
        foreach (var text in Typed)
        {
            flow.Vm.FilterText = text; // 글자마다 앞 적용을 취소하고 다시 적용한다
            for (var i = 0; i < 3; i++)
            {
                await Task.Delay(2);
                Dispatcher.UIThread.RunJobs();
            }
        }

        await flow.WaitAsync(() => other.Loaded && !flow.Vm.IsLoading && Find(flow.Vm, "TB_TEMP_CSM") is not null);
        flow.Provider.Release.TrySetResult();
        await flow.WaitAsync(() => Find(flow.Vm, "TB_TEMP_CSM")?.Mark is not null);
    });

    [Fact]
    public Task Marks_attach_on_the_refreshed_event_even_when_the_first_marks_read_of_that_schema_failed() => HeadlessHost.Run(async () =>
    {
        using var flow = new Flow(_dir, uiThread: true);
        await flow.Vm.SetSessionAsync(flow.Session);
        flow.Vm.FilterText = "temp";
        await flow.Vm.ApplyFilterAsync();

        // 스키마를 펼칠 때의 표식 읽기는 뒤 읽기를 시작한 뒤 실패한다(캐시를 잠깐 못 씀 등): 패널은 이 소유자의 표식을 읽은 적이 없다.
        flow.FailNextMarks = true;
        var other = flow.Vm.Roots.Single(r => r.Owner == Other);
        other.IsExpanded = true;
        await flow.WaitAsync(() => other.Loaded);
        var tables = other.Children.Single(c => c.CategoryType == "TABLE");
        tables.IsExpanded = true;
        await flow.WaitAsync(() => tables.Loaded);
        Assert.False(flow.FailNextMarks);

        flow.Provider.Release.TrySetResult();
        await flow.WaitAsync(() => Find(flow.Vm, "TB_TEMP_CSM")?.Mark is not null);
    });

    private static SchemaNode? Find(SchemaBrowserViewModel vm, string name) =>
        vm.Roots.Single(r => r.Owner == Other).Children.SingleOrDefault(c => c.CategoryType == "TABLE")?.Children.SingleOrDefault(c => c.Title == name);

    /// <summary>앱과 같은 조립: 하나의 버스, 디스패처 → 제공자 → SQLite 캐시. UI 로 넘기기는 큐(UI 스레드가 아닌 곳의 발행을 흉내).</summary>
    private sealed class Flow : IDisposable
    {
        private readonly ConcurrentQueue<Action> _posted = new();
        private readonly bool _uiThread;

        public Flow(string dir, bool uiThread = false)
        {
            _uiThread = uiThread;
            Cache = new SqliteMetadataCache(new AppDataPaths(dir));
            Provider = new MarkingProvider(Cache, Bus);
            var table = new DbmsServiceTable().Add<IMetadataProvider>(Dbms.Oracle, () => Provider);
            var meta = System.Reflection.DispatchProxy.Create<IMetadataProvider, MetaTap>();
            ((MetaTap)(object)meta).Init(new MetadataProviderDispatcher(table), this);
            var services = new FlowServices(meta, Bus);
            Vm = new SchemaBrowserViewModel(services) { DebounceMilliseconds = 3_600_000 };
            if (!uiThread)
            {
                Vm.PostToUi = _posted.Enqueue;
            }
        }

        public EventBus Bus { get; } = new();

        /// <summary>다음 표식 조회 하나는 제공자에 넘긴 뒤(뒤 읽기는 시작된다) 실패로 돌려준다.</summary>
        public bool FailNextMarks { get; set; }

        public SqliteMetadataCache Cache { get; }

        public MarkingProvider Provider { get; }

        public FlowSession Session { get; } = new();

        public SchemaBrowserViewModel Vm { get; }

        public void Dispose() => Cache.Dispose();

        public async Task PumpAsync()
        {
            if (_uiThread)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Yield();
                return;
            }

            while (_posted.TryDequeue(out var a))
            {
                a();
            }

            await Task.Yield();
        }

        public async Task WaitAsync(Func<bool> condition)
        {
            for (var i = 0; i < 500 && !condition(); i++)
            {
                await Task.Delay(10);
                await PumpAsync();
            }

            Assert.True(condition());
        }
    }

    /// <summary>패널 → 디스패처 사이에 끼워 표식 조회를 한 번 실패시킨다(<see cref="Flow.FailNextMarks"/>, 나머지는 그대로 넘긴다).</summary>
    public class MetaTap : System.Reflection.DispatchProxy
    {
        private IMetadataProvider? _inner;
        private Flow? _flow;

        internal void Init(IMetadataProvider inner, object flow)
        {
            _inner = inner;
            _flow = (Flow)flow;
        }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            try
            {
                var result = targetMethod!.Invoke(_inner, args);
                if (targetMethod.Name == nameof(IMetadataProvider.ListObjectMarksAsync) && _flow!.FailNextMarks)
                {
                    _flow.FailNextMarks = false;
                    return Task.FromException<IReadOnlyDictionary<DbObjectRef, ObjectMark>>(new IOException("database is locked"));
                }

                return result;
            }
            catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
            {
                throw ex.InnerException;
            }
        }
    }

    private sealed class FlowServices(IMetadataProvider meta, IEventBus bus) : IServiceProvider
    {
        private readonly Oranda.App.Shell.Settings.SettingsService _settings = new(new Oranda.App.Tests.Features.Update.MemorySettingsStore());

        public object? GetService(Type serviceType) =>
            serviceType == typeof(IMetadataProvider) ? meta
            : serviceType == typeof(IEventBus) ? bus
            : serviceType == typeof(Oranda.App.Shell.Settings.SettingsService) ? _settings
            : null;
    }

    internal sealed class FlowSession : ISession
    {
        public Guid Id { get; } = Guid.NewGuid();

        public ConnectionProfile Profile { get; } = new("usmeap", Me, "usmeap", Id: Guid.NewGuid());

        public ServerInfo Server { get; } = new("19.0.0.0.0", null, null, null, null, null, false, 1, new ServerCapabilities(false, false, false, false, false));

        public SessionPurpose Purpose => SessionPurpose.Work;

        public string DbKey => "USMEAP/-/" + Me;

        public SessionState State => SessionState.Idle;

        public bool HasPendingTransaction => false;

        public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>서버 대역: 전체 목록 + 표식 보조 질의(테이블만). Oracle 제공자처럼 <see cref="CachedMetadataProvider"/> 를 상속하고 인터페이스를 다시 단다.</summary>
    internal sealed class MarkingProvider(SqliteMetadataCache cache, IEventBus bus)
        : CachedMetadataProvider(cache, bus, Timeout.InfiniteTimeSpan), IMetadataProvider
    {
        private static readonly (string Owner, string Name, string Type)[] Objects =
        [
            (Me, "MY_TEMP", "TABLE"),
            (Other, "TB_TEMP_CSM", "TABLE"),
            (Other, "TB_TEMP_PLAIN", "TABLE"),
            (Other, "TB_OTHER", "TABLE"),
        ];

        public int MarkQueries { get; private set; }

        /// <summary>표식 보조 질의가 서버를 기다리다 이것이 끝나면 답한다(첫 조회가 빈 결과인 것을 보고 풀어 준다).</summary>
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override IReadOnlyList<string> MarkQueryIds => [ObjectMarkRows.Tables];

        /// <summary>이만큼 세션이 바쁘다고 답한 뒤 읽는다(실앱: 목록 뒤 갱신·편집기 실행과 겹친다).</summary>
        public int BusyTimes { get; set; }

        public TimeSpan RetryDelay
        {
            get => BackgroundRetryDelay;
            set => BackgroundRetryDelay = value;
        }

        protected override async Task<IReadOnlyList<object?[]>> RunMarkQueryAsync(ISession s, string id, string owner, CancellationToken ct)
        {
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            if (BusyTimes > 0)
            {
                BusyTimes--;
                throw new SessionBusyException();
            }

            MarkQueries++;
            return owner == Other ? [["TB_TEMP_CSM", "Y", "SYS$TRANSACTION", null, null, null, "NO", "NO"]] : [];
        }

        protected override Task<IReadOnlyList<MetaObject>> FetchObjectsAsync(ISession s, ObjectQuery q, DateTimeOffset fetchedAt, CancellationToken ct)
        {
            IReadOnlyList<MetaObject> rows = q.InvalidOnly
                ? []
                : [.. Objects.Where(o => q.Owner is null || o.Owner == q.Owner).Select(o => new MetaObject
                {
                    DbKey = s.DbKey,
                    Owner = o.Owner,
                    Name = o.Name,
                    Type = o.Type,
                    Status = "VALID",
                    LastDdlTime = fetchedAt,
                    FetchedAt = fetchedAt,
                })];
            return Task.FromResult(rows);
        }

        protected override bool IsIgnorableBackgroundRefreshFailure(Exception ex) => ex is InvalidOperationException;

        public Task<IReadOnlyList<string>> ListSchemasAsync(ISession s, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([Me, Other]);

        public Task<TableInfo> GetTableAsync(ISession s, DbObjectRef table, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct) => throw new NotSupportedException();

        public Task<Oranda.Core.Metadata.ObjectDetail> GetObjectDetailAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetSourceAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<CompileError>> GetCompileErrorsAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();
    }
}
