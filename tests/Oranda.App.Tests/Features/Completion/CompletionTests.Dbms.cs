using Oranda.App.Features.Completion;
using Oranda.App.Shell.Features;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Language;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Language.Completion;

namespace Oranda.App.Tests.Features.Completion;

// 소유: Tibero U2. 메타데이터 구현이 없는 DBMS 의 세션은 후보 제공자에 넘기지 않아(DB 를 읽지 않고 키워드·내장 목록만) 예외 없이 조용히 끝난다.
public sealed partial class CompletionTests
{
    private sealed class RecordingProvider(ICompletionProvider inner) : ICompletionProvider
    {
        public int Calls { get; private set; }

        public ISession? LastSession { get; private set; }

        public Task<CompletionResult> GetCompletionsAsync(ISession? session, string text, int offset, CancellationToken ct)
        {
            Calls++;
            LastSession = session;
            return inner.GetCompletionsAsync(session, text, offset, ct);
        }
    }

    private sealed class DbmsSession(Dbms dbms) : ISession
    {
        public Guid Id { get; } = Guid.NewGuid();

        public ConnectionProfile Profile { get; } = new("test", "scott", "localhost/FREEPDB1", Dbms: dbms);

        public ServerInfo Server { get; } = new("23.26.0.0.0", "FREE", "localhost", "FREEPDB1", "FREE", "FREEPDB1", true, 1,
            new ServerCapabilities(false, true, true, false, false), SessionUser: "SCOTT");

        public SessionPurpose Purpose => SessionPurpose.Work;

        public string DbKey => "FREE/FREEPDB1/SCOTT";

        public SessionState State => SessionState.Idle;

        public bool HasPendingTransaction => false;

        public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public static readonly EmptyServices Instance = new();

        public object? GetService(Type serviceType) => null;
    }

    private static CompletionController DbmsController(DbmsSession session, RecordingProvider provider, Action onMetadataResolved, IServiceProvider? services)
        => new()
        {
            ProviderResolver = () => provider,
            MetadataResolver = () =>
            {
                onMetadataResolved();
                return new Meta();
            },
            SessionResolver = _ => session,
            HasMetadataService = s => DbmsServiceKeys.HasImplementation(services, s.Profile.Dbms, DbmsServiceArea.Metadata),
        };

    [Fact]
    public Task Dbms_without_metadata_service_asks_the_provider_without_a_session_and_opens_no_list() => HeadlessHost.Run(async () =>
    {
        var (window, view, _) = Show("SELECT * FROM emp", 17);
        var provider = new RecordingProvider(new CompletionProvider(new Meta()));
        var c = DbmsController(new DbmsSession(Dbms.Tibero), provider, () => { }, EmptyServices.Instance);

        Assert.False(await c.ShowCompletionAsync(view.TextEditorControl.TextArea, null, automatic: true)); // 표 이름 자리: 키워드·내장 후보가 없다 = 조용히 끝
        Assert.Equal(1, provider.Calls);
        Assert.Null(provider.LastSession); // DB 를 읽을 세션을 넘기지 않았다
        Assert.Null(c.CurrentWindow);
        window.Close();
    });

    [Fact]
    public Task Dbms_without_metadata_service_still_lists_keywords_and_builtins() => HeadlessHost.Run(async () =>
    {
        var (window, view, _) = Show("SELECT * FROM emp WHE", 21);
        var provider = new RecordingProvider(new CompletionProvider(new Meta()));
        var c = DbmsController(new DbmsSession(Dbms.Tibero), provider, () => { }, EmptyServices.Instance);

        Assert.True(await c.ShowCompletionAsync(view.TextEditorControl.TextArea, null, automatic: false));
        Assert.Null(provider.LastSession);
        Assert.Contains(c.LastResult!.Items, i => i.Label.Equals("WHERE", StringComparison.OrdinalIgnoreCase));
        c.CloseCompletion();
        window.Close();
    });

    [Fact]
    public Task Dbms_without_metadata_service_does_not_open_parameter_hints_or_resolve_metadata() => HeadlessHost.Run(async () =>
    {
        var (window, view, _) = Show("BEGIN emp_pkg.hire('A', ", 24);
        var resolved = 0;
        var provider = new RecordingProvider(new CompletionProvider(new Meta()));
        var c = DbmsController(new DbmsSession(Dbms.Tibero), provider, () => resolved++, EmptyServices.Instance);

        Assert.False(await c.ShowParameterHintsAsync(view.TextEditorControl.TextArea, null));
        Assert.Equal(0, resolved);
        Assert.Null(c.CurrentInsight);
        window.Close();
    });

    [Fact]
    public Task Oracle_session_is_passed_to_the_provider_as_before() => HeadlessHost.Run(async () =>
    {
        var (window, view, _) = Show("SELECT e. FROM emp e", 9);
        var oracle = new DbmsSession(Dbms.Oracle);
        var provider = new RecordingProvider(new CompletionProvider(new Meta()));
        var c = DbmsController(oracle, provider, () => { }, EmptyServices.Instance); // 선택기가 없어도 Oracle 은 구현이 있다고 본다

        Assert.True(await c.ShowCompletionAsync(view.TextEditorControl.TextArea, null, automatic: true));
        Assert.Same(oracle, provider.LastSession);
        c.CloseCompletion();
        window.Close();
    });
}
