using System.Text;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.SessionMonitor;
using Oranda.App.Features.SqlMonitor;
using Oranda.Core.Connection;
using Oranda.Core.Plans;
using Oranda.Core.Monitoring;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Core.Storage;

namespace Oranda.App.Tests.Features.SessionMonitor;

/// <summary>권한 없는 계정 안내 띠, 「다른 접속으로 열기」, 접속 없이 열기(프로필 고르기), 마지막 프로필 기억, 닫기가 UI 스레드를 막지 않는지.</summary>
public class MonitorAccessTests
{
    private static readonly ServerCapabilities Dba = new(true, true, true, true, true);

    private static readonly ServerCapabilities NoPrivilege = new(false, false, false, false, false);

    private static readonly ConnectionProfile Dev = new("ft-dev", "FT", "h:1521/s", Id: Guid.NewGuid(), CredentialRef: "ref-dev", Group: "oranda-ft");

    private static readonly ConnectionProfile DbaProfile = new("ft-dba", "SYSTEM", "h:1521/s", Id: Guid.NewGuid(), CredentialRef: "ref-dba", Group: "oranda-ft");

    private static readonly ConnectionProfile Prod = new("prod", "OPS", "p:1521/s", Id: Guid.NewGuid(), Environment: ConnectionEnvironment.Production);

    [Theory]
    [InlineData("ft", "GRANT SELECT_CATALOG_ROLE TO FT;")]
    [InlineData("Scott_1$#", "GRANT SELECT_CATALOG_ROLE TO SCOTT_1$#;")]
    [InlineData("a b", "GRANT SELECT_CATALOG_ROLE TO \"a b\";")]
    [InlineData("1abc", "GRANT SELECT_CATALOG_ROLE TO \"1abc\";")]
    public void Grant_statement_uses_the_real_account_name(string user, string expected)
    {
        Assert.Equal(expected, MonitorAccessState.GrantStatement(user));
    }

    [Fact]
    public void Denied_notice_names_the_account_and_the_fix()
    {
        var state = new MonitorAccessState();
        state.ShowDenied(Dev);

        Assert.True(state.HasNotice);
        Assert.Contains("이 접속(FT)은 V$ 뷰를 읽을 권한이 없어", state.Notice, StringComparison.Ordinal);
        Assert.Contains("GRANT SELECT_CATALOG_ROLE TO FT;", state.Notice, StringComparison.Ordinal);
        Assert.Contains("SELECT ANY DICTIONARY", state.Notice, StringComparison.Ordinal);
        Assert.Contains("다른 접속으로", state.Notice, StringComparison.Ordinal);

        state.ClearNotice();
        Assert.False(state.HasNotice);
        Assert.Null(state.GrantSql);
    }

    [Fact]
    public void Account_text_shows_name_and_user_and_marks_production()
    {
        var state = new MonitorAccessState();
        state.SetAccount(DbaProfile);
        Assert.Equal("모니터 접속: ft-dba(SYSTEM)", state.AccountText);
        Assert.False(state.IsProduction);

        state.SetAccount(Prod);
        Assert.Equal("모니터 접속: prod(OPS) · 운영", state.AccountText);
        Assert.True(state.IsProduction);

        state.SetAccount(null);
        Assert.False(state.HasAccount);
    }

    [Theory]
    [InlineData("ORA-00942: table or view does not exist", true)]
    [InlineData("ORA-01031: insufficient privileges", true)]
    [InlineData("table or view \"SYS\".\"GV_$SESSION\" does not exist", true)]
    [InlineData("ORA-12170: TNS:Connect timeout occurred", false)]
    public void Privilege_errors_are_recognized_through_inner_exceptions(string message, bool expected)
    {
        Assert.Equal(expected, MonitorAccessState.IsPrivilegeError(new InvalidOperationException("wrap", new InvalidOperationException(message))));
    }

    // ---- 열기 도구(프로필 고르기·비밀번호·기억) ----

    [Fact]
    public async Task Open_without_a_work_session_asks_to_pick_a_profile()
    {
        var h = new OpenerHarness { Pick = DbaProfile };
        h.Connection.Profiles = [Dev, DbaProfile];

        var session = await h.Opener.OpenDefaultAsync(CancellationToken.None);

        Assert.NotNull(session);
        Assert.Equal(1, h.PickCalls);
        Assert.Equal(["ft-dev", "ft-dba"], h.PickedList!.Select(p => p.Name));
        var call = Assert.Single(h.Manager.Calls);
        Assert.Equal("ft-dba", call.Profile);
        Assert.Equal(SessionPurpose.Monitor, call.Purpose);
        Assert.Equal("dba-secret", call.Secret);
        Assert.Equal(0, h.PasswordPrompts);
    }

    [Fact]
    public async Task Cancelling_the_picker_opens_nothing()
    {
        var h = new OpenerHarness { Pick = null };
        h.Connection.Profiles = [Dev];

        Assert.Null(await h.Opener.OpenDefaultAsync(CancellationToken.None));
        Assert.Empty(h.Manager.Calls);
    }

    [Fact]
    public async Task Chosen_profile_without_stored_password_asks_for_it()
    {
        var h = new OpenerHarness { Pick = Prod, Typed = "typed-secret" };
        h.Connection.Profiles = [Dev, Prod];

        var session = await h.Opener.OpenChosenAsync(CancellationToken.None);

        Assert.NotNull(session);
        Assert.Equal(1, h.PasswordPrompts);
        Assert.Equal("prod", h.PromptedFor);
        Assert.Equal("typed-secret", Assert.Single(h.Manager.Calls).Secret);
    }

    [Fact]
    public async Task Cancelling_the_password_prompt_opens_nothing()
    {
        var h = new OpenerHarness { Pick = Prod, Typed = null };
        h.Connection.Profiles = [Prod];

        Assert.Null(await h.Opener.OpenChosenAsync(CancellationToken.None));
        Assert.Empty(h.Manager.Calls);
    }

    [Fact]
    public async Task No_saved_profile_explains_what_to_do()
    {
        var h = new OpenerHarness();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Opener.OpenChosenAsync(CancellationToken.None));
        Assert.Contains("접속 관리자", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Default_uses_the_work_profile_when_it_can_read_the_views()
    {
        var h = new OpenerHarness { Pick = Prod };
        h.Connection.Profiles = [Dev, DbaProfile];
        h.Connection.Active = new MonitorFakeSession(DbaProfile, Dba);
        h.Memory.Remember("sessionMonitor", Dev.Id);

        await h.Opener.OpenDefaultAsync(CancellationToken.None);

        Assert.Equal("ft-dba", Assert.Single(h.Manager.Calls).Profile);
        Assert.Equal(0, h.PickCalls);
    }

    [Fact]
    public async Task Default_prefers_the_remembered_profile_when_the_work_account_has_no_access()
    {
        var h = new OpenerHarness();
        h.Connection.Profiles = [Dev, DbaProfile];
        h.Connection.Active = new MonitorFakeSession(Dev, NoPrivilege);
        h.Memory.Remember("sessionMonitor", DbaProfile.Id);

        await h.Opener.OpenDefaultAsync(CancellationToken.None);

        Assert.Equal("ft-dba", Assert.Single(h.Manager.Calls).Profile);
        Assert.Equal(0, h.PickCalls);
    }

    [Fact]
    public async Task Default_falls_back_to_the_work_profile_when_nothing_is_remembered()
    {
        var h = new OpenerHarness();
        h.Connection.Profiles = [Dev, DbaProfile];
        h.Connection.Active = new MonitorFakeSession(Dev, NoPrivilege);

        await h.Opener.OpenDefaultAsync(CancellationToken.None);

        Assert.Equal("ft-dev", Assert.Single(h.Manager.Calls).Profile);
    }

    [Fact]
    public async Task Picker_starts_on_the_remembered_profile_and_each_monitor_keeps_its_own()
    {
        var h = new OpenerHarness { Pick = DbaProfile };
        h.Connection.Profiles = [Dev, DbaProfile];
        h.Memory.Remember("sessionMonitor", DbaProfile.Id);

        await h.Opener.OpenChosenAsync(CancellationToken.None);
        Assert.Equal(DbaProfile.Id, h.PickedCurrent);

        Assert.Null(h.Memory.Recall("sqlMonitor"));
    }

    [Fact]
    public void Remembered_profile_survives_a_new_instance_and_a_broken_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oranda-mon-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "monitor-profiles.json");
        try
        {
            new MonitorProfileMemory(path).Remember("sessionMonitor", DbaProfile.Id);
            new MonitorProfileMemory(path).Remember("sqlMonitor", Dev.Id);

            var again = new MonitorProfileMemory(path);
            Assert.Equal(DbaProfile.Id, again.Recall("sessionMonitor"));
            Assert.Equal(Dev.Id, again.Recall("sqlMonitor"));
            Assert.DoesNotContain("secret", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);

            File.WriteAllText(path, "{ not json");
            Assert.Null(new MonitorProfileMemory(path).Recall("sessionMonitor"));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    // ---- 세션 모니터 화면 모델 ----

    [Fact]
    public async Task Session_monitor_shows_the_notice_then_opens_with_another_account()
    {
        var denied = new MonitorFakeSession(Dev, NoPrivilege);
        var allowed = new MonitorFakeSession(DbaProfile, Dba);
        var monitor = new MonitorFakeMonitor();
        ConnectionProfile? remembered = null;
        var vm = new SessionMonitorViewModel(monitor, _ => Task.FromResult<ISession?>(denied))
        {
            PostToUi = a => a(),
            ChooseSession = _ => Task.FromResult<ISession?>(allowed),
            Remember = p => remembered = p,
        };

        await vm.StartAsync();

        Assert.False(vm.IsOpen);
        Assert.True(denied.Disposed);
        Assert.True(vm.Access.HasNotice);
        Assert.Contains("GRANT SELECT_CATALOG_ROLE TO FT;", vm.Access.Notice, StringComparison.Ordinal);
        Assert.Null(remembered);

        await vm.OpenWithOtherAccountAsync();

        Assert.True(vm.IsOpen);
        Assert.False(vm.Access.HasNotice);
        Assert.Equal("모니터 접속: ft-dba(SYSTEM)", vm.Access.AccountText);
        Assert.Equal(DbaProfile, remembered);
        Assert.Single(vm.Sessions);
        Assert.Same(allowed, vm.MonitorSession);
    }

    [Fact]
    public async Task Switching_account_closes_the_previous_session_and_clears_the_grid()
    {
        var first = new MonitorFakeSession(Dev, Dba);
        var second = new MonitorFakeSession(DbaProfile, Dba);
        var vm = new SessionMonitorViewModel(new MonitorFakeMonitor(), _ => Task.FromResult<ISession?>(first))
        {
            PostToUi = a => a(),
            ChooseSession = _ => Task.FromResult<ISession?>(second),
        };
        await vm.StartAsync();
        Assert.Equal("모니터 접속: ft-dev(FT)", vm.Access.AccountText);

        await vm.OpenWithOtherAccountAsync();

        Assert.True(first.Disposed);
        Assert.False(second.Disposed);
        Assert.Same(second, vm.MonitorSession);
        Assert.Equal("모니터 접속: ft-dba(SYSTEM)", vm.Access.AccountText);
    }

    [Fact]
    public async Task Another_account_without_access_keeps_the_open_session()
    {
        var current = new MonitorFakeSession(DbaProfile, Dba);
        var denied = new MonitorFakeSession(Dev, NoPrivilege);
        var vm = new SessionMonitorViewModel(new MonitorFakeMonitor(), _ => Task.FromResult<ISession?>(current))
        {
            PostToUi = a => a(),
            ChooseSession = _ => Task.FromResult<ISession?>(denied),
        };
        await vm.StartAsync();

        await vm.OpenWithOtherAccountAsync();

        Assert.True(vm.IsOpen);
        Assert.Same(current, vm.MonitorSession);
        Assert.False(current.Disposed);
        Assert.True(denied.Disposed);
        Assert.False(vm.Access.HasNotice);
        Assert.Contains("권한이 없어", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_the_account_picker_changes_nothing()
    {
        var current = new MonitorFakeSession(DbaProfile, Dba);
        var vm = new SessionMonitorViewModel(new MonitorFakeMonitor(), _ => Task.FromResult<ISession?>(current))
        {
            PostToUi = a => a(),
            ChooseSession = _ => Task.FromResult<ISession?>(null),
            Remember = _ => throw new InvalidOperationException("취소했는데 기억하면 안 된다"),
        };
        await vm.StartAsync();

        await vm.OpenWithOtherAccountAsync();

        Assert.Same(current, vm.MonitorSession);
        Assert.False(current.Disposed);
    }

    [Fact]
    public async Task Missing_privilege_error_while_running_clears_the_grid_and_shows_the_notice()
    {
        var session = new MonitorFakeSession(Dev, Dba);
        var monitor = new MonitorFakeMonitor();
        var vm = new SessionMonitorViewModel(monitor, _ => Task.FromResult<ISession?>(session)) { PostToUi = a => a() };
        await vm.StartAsync();
        Assert.Single(vm.Sessions);

        monitor.Fail = new InvalidOperationException("ORA-00942: table or view does not exist");
        await vm.RefreshAsync();

        Assert.Empty(vm.Sessions);
        Assert.True(vm.IsOpen);
        Assert.True(vm.Access.HasNotice);
        Assert.Contains("GRANT SELECT_CATALOG_ROLE TO FT;", vm.Access.Notice, StringComparison.Ordinal);

        monitor.Fail = null;
        await vm.RefreshAsync();
        Assert.False(vm.Access.HasNotice);
        Assert.Single(vm.Sessions);

        // 권한과 무관한 오류는 띠 없이 상태 줄에만.
        monitor.Fail = new InvalidOperationException("ORA-12170: timeout");
        await vm.RefreshAsync();
        Assert.False(vm.Access.HasNotice);
        Assert.Contains("ORA-12170", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Closing_the_monitor_closes_its_session_without_waiting_for_a_hung_server()
    {
        var hang = new TaskCompletionSource();
        var session = new MonitorFakeSession(DbaProfile, Dba) { DisposeHang = hang };
        var vm = new SessionMonitorViewModel(new MonitorFakeMonitor(), _ => Task.FromResult<ISession?>(session))
        {
            PostToUi = a => a(),
            CloseTimeout = TimeSpan.FromMilliseconds(100),
        };
        await vm.StartAsync();

        var stop = vm.StopAsync();
        var finished = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(stop, finished);
        // 닫기는 스레드 풀에서 시작한다: 기다림을 그만둔 뒤라도(부하 큰 러너는 100 ms 안에 시작 못 할 수 있다) 곧 시작돼야 한다.
        await Oranda.App.Tests.Shell.Headless.HeadlessHost.WaitUntil(() => session.Disposed, 10000);
        Assert.False(vm.IsOpen);
        Assert.False(vm.Access.HasAccount);
        hang.SetResult();
    }

    // ---- SQL 모니터 화면 모델 ----

    [Fact]
    public async Task Sql_monitor_shows_the_notice_and_opens_with_another_account()
    {
        var denied = new MonitorFakeSession(Dev, NoPrivilege);
        var allowed = new MonitorFakeSession(DbaProfile, Dba);
        var vm = new SqlMonitorViewModel(new MonitorFakeMonitor(), null, _ => Task.FromResult<ISession?>(denied))
        {
            PostToUi = a => a(),
            ChooseSession = _ => Task.FromResult<ISession?>(allowed),
        };

        await vm.StartAsync();
        Assert.True(vm.Access.HasNotice);
        Assert.Equal("GRANT SELECT_CATALOG_ROLE TO FT;", vm.Access.GrantSql);

        await vm.OpenWithOtherAccountAsync();
        Assert.True(vm.IsOpen);
        Assert.False(vm.Access.HasNotice);
        Assert.Equal("모니터 접속: ft-dba(SYSTEM)", vm.Access.AccountText);
        Assert.Single(vm.Results);
    }

    [Fact]
    public async Task Sql_monitor_missing_privilege_error_while_running_shows_the_notice()
    {
        var monitor = new MonitorFakeMonitor();
        var vm = new SqlMonitorViewModel(monitor, null, _ => Task.FromResult<ISession?>(new MonitorFakeSession(Dev, Dba))) { PostToUi = a => a() };
        await vm.StartAsync();
        Assert.Single(vm.Results);

        monitor.Fail = new InvalidOperationException("ORA-00942: table or view does not exist");
        await vm.SearchAsync();

        Assert.Empty(vm.Results);
        Assert.True(vm.Access.HasNotice);
    }

    // ---- 가짜들 ----

    private sealed class OpenerHarness
    {
        public OpenerHarness()
        {
            var credentials = new InMemoryCredentials();
            credentials.Put("ref-dev", "dev-secret");
            credentials.Put("ref-dba", "dba-secret");
            Memory = new MemoryOnly();
            Opener = new MonitorSessionOpener(Connection, Manager, credentials, Memory, "sessionMonitor")
            {
                PickProfile = (profiles, current) =>
                {
                    PickCalls++;
                    PickedList = profiles;
                    PickedCurrent = current;
                    return Task.FromResult(Pick);
                },
                AskPassword = profile =>
                {
                    PasswordPrompts++;
                    PromptedFor = profile.Name;
                    return Task.FromResult(Typed);
                },
            };
        }

        public FakeConnection Connection { get; } = new();

        public FakeManager Manager { get; } = new();

        public MemoryOnly Memory { get; }

        public MonitorSessionOpener Opener { get; }

        public ConnectionProfile? Pick { get; set; }

        public string? Typed { get; set; }

        public int PickCalls { get; private set; }

        public int PasswordPrompts { get; private set; }

        public string? PromptedFor { get; private set; }

        public IReadOnlyList<ConnectionProfile>? PickedList { get; private set; }

        public Guid? PickedCurrent { get; private set; }
    }

    private sealed class MemoryOnly : IMonitorProfileMemory
    {
        private readonly Dictionary<string, Guid> _items = [];

        public Guid? Recall(string monitorId) => _items.TryGetValue(monitorId, out var id) ? id : null;

        public void Remember(string monitorId, Guid profileId) => _items[monitorId] = profileId;
    }

    private sealed class InMemoryCredentials : ICredentialStore
    {
        private readonly Dictionary<string, byte[]> _items = [];

        public void Put(string key, string secret) => _items[key] = Encoding.UTF8.GetBytes(secret);

        public Task SaveAsync(string key, ReadOnlyMemory<byte> secret)
        {
            _items[key] = secret.ToArray();
            return Task.CompletedTask;
        }

        public Task<byte[]?> LoadAsync(string key) => Task.FromResult(_items.TryGetValue(key, out var v) ? (byte[]?)v.ToArray() : null);

        public Task DeleteAsync(string key)
        {
            _items.Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeManager : ISessionManager
    {
        private readonly List<ISession> _sessions = [];

        public List<(string Profile, string Secret, SessionPurpose Purpose)> Calls { get; } = [];

        public IReadOnlyList<ISession> Sessions => _sessions;

        public Task<ISession> OpenAsync(ConnectionProfile profile, SecureCredential credential, SessionPurpose purpose, CancellationToken ct)
        {
            Calls.Add((profile.Name, credential.Secret.ToString(), purpose));
            var session = new MonitorFakeSession(profile, Dba);
            _sessions.Add(session);
            return Task.FromResult<ISession>(session);
        }
    }

    private sealed class FakeConnection : IConnectionService
    {
        public IReadOnlyList<ConnectionProfile> Profiles { get; set; } = [];

        public MonitorFakeSession? Active { get; set; }

        public ISession? ActiveSession => Active;

        public ConnectionProfile? LastProfile => Active?.Profile;

        public bool IsBroken => false;

        public IReadOnlyList<ISession> Sessions => Active is null ? [] : [Active];

#pragma warning disable CS0067 // 시험에서 쓰지 않는 이벤트
        public event EventHandler? ActiveSessionChanged;

        public event EventHandler<ISession>? Connected;
#pragma warning restore CS0067

        public ISession? SessionOf(Guid profileId) => Active?.Profile.Id == profileId ? Active : null;

        public bool IsSessionBroken(Guid sessionId) => false;

        public bool Switch(ISession session) => ReferenceEquals(session, Active);

        public Task DisconnectAsync(ISession session, CancellationToken ct) => throw new NotSupportedException();

        public Task<StoredDocument<ConnectionsDocument>> LoadAsync(CancellationToken ct) =>
            Task.FromResult(new StoredDocument<ConnectionsDocument>(new ConnectionsDocument { Profiles = Profiles }));

        public Task<ConnectionProfile> SaveProfileAsync(ConnectionProfile profile, string? password, bool savePassword, CancellationToken ct) => throw new NotSupportedException();

        public Task DeleteProfileAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();

        public Task<ConnectOutcome> ConnectAsync(ConnectionProfile profile, string? password, CancellationToken ct) => throw new NotSupportedException();

        public Task DisconnectAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<ConnectOutcome> ReconnectAsync(string? password, CancellationToken ct) => throw new NotSupportedException();

        public Task<PasswordChangeOutcome> ChangePasswordAsync(ConnectionProfile profile, string? oldPassword, string newPassword, CancellationToken ct) => throw new NotSupportedException();

        public Task<bool> HasStoredPasswordAsync(ConnectionProfile profile, CancellationToken ct) => throw new NotSupportedException();

        public Task<ConnectionProfile> ImportWalletAsync(string zipPath, string? walletAlias, CancellationToken ct) => throw new NotSupportedException();

        public Task<ConnectionTestOutcome> TestConnectionAsync(ConnectionProfile profile, string? password, CancellationToken ct) => throw new NotSupportedException();

        public Task<ConnectionProfile> DuplicateProfileAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();

        public Task UpdateFoldersAsync(Func<ConnectionsDocument, ConnectionsDocument> edit, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class MonitorFakeMonitor : IMonitorService
    {
        public Exception? Fail { get; set; }

        public Task<IReadOnlyList<SessionRow>> GetSessionsAsync(ISession monitor, SessionFilter f, CancellationToken ct)
        {
            if (Fail is { } ex)
            {
                throw ex;
            }

            IReadOnlyList<SessionRow> rows = [new(new SessionKey(1, 10, 100), "ACTIVE") { Username = "SCOTT" }];
            return Task.FromResult(rows);
        }

        public Task<BlockingTree> GetBlockingTreeAsync(ISession monitor, CancellationToken ct) => Task.FromResult(new BlockingTree([]));

        public Task<IReadOnlyList<TopSqlRow>> GetTopSqlAsync(ISession monitor, TopSqlCriteria c, CancellationToken ct)
        {
            if (Fail is { } ex)
            {
                throw ex;
            }

            IReadOnlyList<TopSqlRow> rows = [new(new CursorRef("sql1", 0, 1), "select 1 from dual") { Executions = 3, ElapsedSeconds = 2.5 }];
            return Task.FromResult(rows);
        }

        public Task<SessionDetail> GetSessionDetailAsync(ISession monitor, SessionKey target, CancellationToken ct) => throw new NotSupportedException();

        public Task KillAsync(ISession monitor, SessionKey target, bool immediate, CancellationToken ct) => throw new NotSupportedException();

        public Task<string?> SetTraceAsync(ISession monitor, SessionKey target, bool enable, TraceOptions options, CancellationToken ct) => throw new NotSupportedException();

        public Task<SqlDetail> GetSqlDetailAsync(ISession monitor, CursorRef cursor, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class MonitorFakeSession(ConnectionProfile profile, ServerCapabilities caps) : ISession
    {
        public bool Disposed { get; private set; }

        /// <summary>설정하면 DisposeAsync 가 이 작업이 끝날 때까지 끝나지 않는다(응답 없는 서버).</summary>
        public TaskCompletionSource? DisposeHang { get; set; }

        public Guid Id { get; } = Guid.NewGuid();

        public ConnectionProfile Profile { get; } = profile;

        public ServerInfo Server { get; } = new("23.26.0.0.0", "FREE", "h", "FREEPDB1", "FREE", "FREEPDB1", true, 1, caps);

        public SessionPurpose Purpose => SessionPurpose.Monitor;

        public string DbKey => "FREE/FREEPDB1/FT";

        public SessionState State => SessionState.Idle;

        public bool HasPendingTransaction => false;

        public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return DisposeHang is { } hang ? new ValueTask(hang.Task) : ValueTask.CompletedTask;
        }
    }
}
