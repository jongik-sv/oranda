using System.IO.Compression;
using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.App.Tests.Shell;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Core.Storage;
using Oranda.Platform.Storage;

namespace Oranda.App.Tests.Features.ConnectionManager;

// 소유: TSK-03-04. 접속 관리자 화면(모델·서비스·지갑·창) 시험. 통합 시험은 ConnectionManagerIntegrationTests.

internal sealed class MemoryCredentials : ICredentialStore
{
    public Dictionary<string, byte[]> Items { get; } = new(StringComparer.Ordinal);

    public Task SaveAsync(string key, ReadOnlyMemory<byte> secret)
    {
        Items[key] = secret.ToArray();
        return Task.CompletedTask;
    }

    /// <summary>설정하면 읽기가 이 예외로 실패한다(키체인 접근 거부 등).</summary>
    public Exception? LoadFailure { get; set; }

    public Task<byte[]?> LoadAsync(string key) => LoadFailure is { } fail
        ? Task.FromException<byte[]?>(fail)
        : Task.FromResult(Items.TryGetValue(key, out var v) ? (byte[]?)v.ToArray() : null);

    public Task DeleteAsync(string key)
    {
        Items.Remove(key);
        return Task.CompletedTask;
    }

    public string? Text(string key) => Items.TryGetValue(key, out var v) ? Encoding.UTF8.GetString(v) : null;
}

internal sealed class StubSession(ConnectionProfile profile, ServerInfo server, IEventBus events) : ISession
{
    public Guid Id { get; } = Guid.NewGuid();

    public ConnectionProfile Profile { get; } = profile;

    public ServerInfo Server { get; } = server;

    public SessionPurpose Purpose => SessionPurpose.Work;

    public string DbKey => "FREE/FREEPDB1/SCOTT";

    public SessionState State { get; private set; } = SessionState.Idle;

    public bool HasPendingTransaction => false;

    public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;

    public Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>설정하면 DisposeAsync 가 이 신호를 받을 때까지 호출 스레드를 막는다(응답 없는 서버의 ODP Dispose 흉내). 안전장치로 10초 뒤엔 풀린다.</summary>
    public ManualResetEventSlim? DisposeGate { get; set; }

    public ValueTask DisposeAsync()
    {
        DisposeGate?.Wait(TimeSpan.FromSeconds(10));
        State = SessionState.Closed;
        events.Publish(new SessionClosed(Id));
        return ValueTask.CompletedTask;
    }
}

internal sealed class StubSessionManager(IEventBus events) : ISessionManager
{
    private readonly List<ISession> _sessions = [];

    public IReadOnlyList<ISession> Sessions => _sessions;

    /// <summary>접속 때 던질 예외를 고른다(인자 = 프로필, 비밀번호). null 이면 성공.</summary>
    public Func<ConnectionProfile, string, Exception?> Behavior { get; set; } = (_, _) => null;

    public List<(ConnectionProfile Profile, string Password)> Calls { get; } = [];

    /// <summary>설정하면 이후 열리는 세션의 DisposeAsync 가 이 신호를 기다린다.</summary>
    public ManualResetEventSlim? DisposeGate { get; set; }

    public Task<ISession> OpenAsync(ConnectionProfile profile, SecureCredential credential, SessionPurpose purpose, CancellationToken ct)
    {
        var password = credential.Secret.ToString();
        Calls.Add((profile, password));
        if (Behavior(profile, password) is { } failure)
        {
            return Task.FromException<ISession>(failure);
        }

        var info = new ServerInfo("23.26.0.0.0", "FREE", "localhost", "FREEPDB1", "FREE", "FREEPDB1", true, 1,
            new ServerCapabilities(false, true, true, false, false), DbUniqueName: "FREE", SessionUser: profile.User.ToUpperInvariant());
        var session = new StubSession(profile, info, events) { DisposeGate = DisposeGate };
        _sessions.Add(session);
        return Task.FromResult<ISession>(session);
    }
}

internal sealed class StubPasswordChanger : IPasswordChanger
{
    public List<(string Old, string New)> Calls { get; } = [];

    public Exception? Failure { get; set; }

    public Task ChangeAsync(ConnectionProfile profile, string oldPassword, string newPassword, CancellationToken ct)
    {
        Calls.Add((oldPassword, newPassword));
        return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
    }
}

/// <summary>실제 <c>ConnectionStore</c>(임시 폴더) + 가짜 비밀 저장소·세션 관리자로 서비스를 만든다.</summary>
internal sealed class ServiceFixture : IDisposable
{
    public ServiceFixture()
    {
        Directory = Path.Combine(Path.GetTempPath(), "oranda-cm-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        Container = AppComposition.Create(new AppCompositionOptions { DataDirectory = Directory });
        Events = Container.GetRequiredService<IEventBus>();
        Store = Container.GetRequiredService<IConnectionStore>();
        Paths = Container.GetRequiredService<IAppDataPaths>();
        Sessions = new StubSessionManager(Events);
        Keys = new ContextKeyService();
        Changer = new StubPasswordChanger();
        Tester = new StubConnectionTester();
        Service = new ConnectionService(Store, Credentials, Sessions, Paths, Events, Changer, Keys, Tester);
    }

    public string Directory { get; }

    public Oranda.App.Shell.Composition.ServiceContainer Container { get; }

    public IEventBus Events { get; }

    public IConnectionStore Store { get; }

    public IAppDataPaths Paths { get; }

    public MemoryCredentials Credentials { get; } = new();

    public StubSessionManager Sessions { get; }

    public ContextKeyService Keys { get; }

    public StubPasswordChanger Changer { get; }

    /// <summary>접속 시험용 가짜 도구(ConnectionManagerUxTests).</summary>
    public StubConnectionTester Tester { get; }

    public ConnectionService Service { get; }

    public string ConnectionsJson => File.ReadAllText(Paths.ConnectionsFile);

    public void Dispose()
    {
        Service.Dispose();
        Container.Dispose();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public sealed class ConnectionManagerTests
{
    private static ConnectionProfile Profile(string name = "dev", string user = "scott") =>
        new(name, user, "localhost:1521/FREEPDB1");

    // ---------- 명령 등록 ----------

    [Fact]
    public void Frame_exists()
    {
        Assert.Equal("Oranda.App.Features.ConnectionManager", typeof(ConnectionManagerCommands).Namespace);
    }

    [Fact]
    public void All_five_connection_commands_are_registered_with_commands_md_enablement()
    {
        var registry = ShellTestHelpers.CreateCommandRegistry();
        foreach (var id in ConnectionManagerCommands.Ids)
        {
            Assert.NotNull(registry.Find(id));
        }

        Assert.Equal(5, ConnectionManagerCommands.Ids.Length);
        Assert.Equal("hasSession", registry.Find("oranda.conn.disconnect")!.Enablement);
        Assert.Equal("hasSession", registry.Find("oranda.conn.changePassword")!.Enablement);
        Assert.Equal("sessionBroken", registry.Find("oranda.conn.reconnect")!.Enablement);
        Assert.Equal("sqlDocActive || plsqlDocActive", registry.Find("oranda.conn.switchSession")!.Enablement);
        Assert.Null(registry.Find("oranda.conn.manage")!.Enablement);
    }

    // ---------- 접속 방식 문자열 ----------

    [Theory]
    [InlineData(false, "db.example.com", "1521", "orcl", "db.example.com:1521/orcl")]
    [InlineData(true, "adb.example.com", "1522", "svc.adb.oraclecloud.com", "tcps://adb.example.com:1522/svc.adb.oraclecloud.com")]
    [InlineData(false, "h", "", "s", "h/s")]
    public void Host_port_service_composes_and_parses_back(bool tls, string host, string port, string service, string expected)
    {
        Assert.Equal(expected, DataSourceComposer.Compose(tls, host, port, service));
        Assert.True(DataSourceComposer.TryParseHostPort(expected, out var t, out var h, out var p, out var s));
        Assert.Equal((tls, host, port, service), (t, h, p, s));
        Assert.Equal(ConnectionMode.HostPortService, DataSourceComposer.Detect(expected));
    }

    [Theory]
    [InlineData("db.example.com:1521/orcl?connect_timeout=5", ConnectionMode.EasyConnect)]
    [InlineData("(DESCRIPTION=(ADDRESS=(PROTOCOL=tcp)(HOST=h)(PORT=1521))(CONNECT_DATA=(SERVICE_NAME=s)))", ConnectionMode.EasyConnect)]
    [InlineData("h1:1521,h2:1521/svc", ConnectionMode.EasyConnect)]
    [InlineData("MYDB_HIGH", ConnectionMode.TnsAlias)]
    [InlineData("", ConnectionMode.HostPortService)]
    public void Detect_picks_mode_from_stored_string(string dataSource, ConnectionMode expected)
    {
        Assert.Equal(expected, DataSourceComposer.Detect(dataSource));
    }

    // ---------- 색 규칙 ----------

    [Theory]
    [InlineData("#FF0000", true)]
    [InlineData("#C42B1C", true)]
    [InlineData("#E81123", true)]
    [InlineData("#FF6B5E", true)]
    [InlineData("#107C10", false)]
    [InlineData("#3366CC", false)]
    [InlineData("#FF8800", false)]
    [InlineData("#808080", false)]
    [InlineData("#C08080", false)]
    [InlineData("nope", false)]
    public void Red_family_colors_are_detected(string hex, bool red)
    {
        Assert.Equal(red, LabelColorRules.IsRedFamily(hex));
    }

    // ---------- 편집기 ----------

    [Fact]
    public void Editor_builds_host_port_service_profile_with_tls_proxy_nls_and_environment()
    {
        var vm = ProfileEditorViewModel.ForNew();
        vm.Name = " ADB 개발 ";
        vm.Group = "고객사";
        vm.Environment = ConnectionEnvironment.Test;
        vm.ColorText = "#3366cc";
        vm.Host = "adb.example.com";
        vm.Port = "1522";
        vm.Service = "svc_high";
        vm.UseTls = true;
        vm.User = "scott";
        vm.Auth = AuthMode.SysDba;
        vm.ProxyUser = "app";
        vm.ReadOnly = true;
        vm.LicensePack = LicensePack.Diagnostics;
        vm.KeepAliveSeconds = 120;
        vm.NlsDateFormat = "YYYY-MM-DD";

        var p = vm.TryBuild(out var errors);
        Assert.Empty(errors);
        Assert.NotNull(p);
        Assert.Equal("ADB 개발", p.Name);
        Assert.Equal("tcps://adb.example.com:1522/svc_high", p.DataSource);
        Assert.Equal("#3366CC", p.Color);
        Assert.Equal(ConnectionEnvironment.Test, p.Environment);
        Assert.Equal(AuthMode.SysDba, p.Auth);
        Assert.Equal("app", p.ProxyUser);
        Assert.True(p.ReadOnly);
        Assert.Equal(LicensePack.Diagnostics, p.LicensePack);
        Assert.Equal(120, p.KeepAliveSeconds);
        Assert.Equal("YYYY-MM-DD", p.Nls?.DateFormat);
        Assert.Equal("고객사", p.Group);
        Assert.NotEqual(Guid.Empty, p.Id);
    }

    [Fact]
    public void Editor_builds_easy_connect_and_tns_alias_with_tns_directory()
    {
        var ez = ProfileEditorViewModel.ForNew();
        ez.Name = "ez";
        ez.User = "u";
        ez.Mode = ConnectionMode.EasyConnect;
        ez.EasyConnect = " h1:1521,h2:1521/svc?connect_timeout=5 ";
        Assert.Equal("h1:1521,h2:1521/svc?connect_timeout=5", ez.TryBuild(out _)!.DataSource);

        var tns = ProfileEditorViewModel.ForNew();
        tns.Name = "tns";
        tns.User = "u";
        tns.Mode = ConnectionMode.TnsAlias;
        tns.TnsAlias = "MYDB";
        tns.TnsAdmin = "C:/net/admin";
        var p = tns.TryBuild(out _)!;
        Assert.Equal("MYDB", p.DataSource);
        Assert.Equal("C:/net/admin", p.TnsAdmin);
    }

    [Fact]
    public void Editor_rejects_red_color_bad_port_blank_fields_and_secrets_in_connection_string()
    {
        var vm = ProfileEditorViewModel.ForNew();
        Assert.Null(vm.TryBuild(out var blank));
        Assert.NotEmpty(blank);

        vm.Name = "x";
        vm.User = "u";
        vm.Host = "h";
        vm.Service = "s";
        vm.ColorText = "#FF0000";
        Assert.Null(vm.TryBuild(out var red));
        Assert.Contains(red, e => e.Contains("빨강", StringComparison.Ordinal));

        vm.ColorText = string.Empty;
        vm.Port = "99999";
        Assert.Null(vm.TryBuild(out var port));
        Assert.Contains(port, e => e.Contains("포트", StringComparison.Ordinal));

        vm.Port = "1521";
        vm.Mode = ConnectionMode.EasyConnect;
        vm.EasyConnect = "h/s;password=tiger";
        Assert.Null(vm.TryBuild(out var secret));
        Assert.Contains(secret, e => e.Contains("비밀번호", StringComparison.Ordinal));
    }

    [Fact]
    public void Production_ignores_custom_color_and_disables_the_picker()
    {
        var vm = ProfileEditorViewModel.ForNew();
        vm.Name = "prod";
        vm.User = "u";
        vm.Host = "h";
        vm.Service = "s";
        vm.ColorText = "#3366CC";
        vm.Environment = ConnectionEnvironment.Production;
        Assert.False(vm.CanPickColor);
        Assert.Null(vm.TryBuild(out _)!.Color);
    }

    [Fact]
    public void Editor_round_trips_a_saved_profile_and_detects_mode()
    {
        var original = new ConnectionProfile("a", "scott", "tcps://h:1522/s", ReadOnly: true, Group: "g", CredentialRef: "oranda:conn:x");
        var vm = ProfileEditorViewModel.From(original);
        Assert.Equal(ConnectionMode.HostPortService, vm.Mode);
        Assert.True(vm.UseTls);
        Assert.Equal("1522", vm.Port);
        Assert.True(vm.SavePassword);
        Assert.Equal(original with { KeepAliveSeconds = 0 }, vm.TryBuild(out _));
    }

    // ---------- 서비스: 저장·비밀번호 ----------

    [Fact]
    public async Task Password_goes_only_to_credential_store_never_to_connections_json()
    {
        using var fx = new ServiceFixture();
        var saved = await fx.Service.SaveProfileAsync(Profile(), "S3cret!pw", true, CancellationToken.None);

        Assert.Equal($"oranda:conn:{saved.Id}", saved.CredentialRef);
        Assert.Equal("S3cret!pw", fx.Credentials.Text(saved.CredentialRef!));
        Assert.DoesNotContain("S3cret!pw", fx.ConnectionsJson, StringComparison.Ordinal);
        Assert.Contains(saved.CredentialRef!, fx.ConnectionsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_without_password_keeps_the_stored_one_and_unchecking_save_deletes_it()
    {
        using var fx = new ServiceFixture();
        var saved = await fx.Service.SaveProfileAsync(Profile(), "pw1", true, CancellationToken.None);
        saved = await fx.Service.SaveProfileAsync(saved with { Name = "renamed" }, null, true, CancellationToken.None);
        Assert.Equal("pw1", fx.Credentials.Text(saved.CredentialRef!));

        var unsaved = await fx.Service.SaveProfileAsync(saved, null, false, CancellationToken.None);
        Assert.Null(unsaved.CredentialRef);
        Assert.Empty(fx.Credentials.Items);
    }

    [Fact]
    public async Task Invalid_profile_is_not_saved_and_its_password_is_not_stored()
    {
        using var fx = new ServiceFixture();
        var bad = Profile() with { DataSource = "h/s;pwd=x" };
        await Assert.ThrowsAsync<ConnectionValidationException>(() => fx.Service.SaveProfileAsync(bad, "pw", true, CancellationToken.None));
        Assert.Empty(fx.Credentials.Items);
    }

    [Fact]
    public async Task Duplicate_name_is_rejected()
    {
        using var fx = new ServiceFixture();
        await fx.Service.SaveProfileAsync(Profile(), null, false, CancellationToken.None);
        await Assert.ThrowsAsync<ConnectionValidationException>(() => fx.Service.SaveProfileAsync(Profile(), null, false, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_removes_profile_and_stored_password()
    {
        using var fx = new ServiceFixture();
        var saved = await fx.Service.SaveProfileAsync(Profile(), "pw", true, CancellationToken.None);
        await fx.Service.DeleteProfileAsync(saved.Id, CancellationToken.None);
        Assert.Empty((await fx.Service.LoadAsync(CancellationToken.None)).Value.Profiles);
        Assert.Empty(fx.Credentials.Items);
    }

    // ---------- 서비스: 접속 ----------

    [Fact]
    public async Task Connect_uses_stored_password_sets_context_keys_and_records_last_connected()
    {
        using var fx = new ServiceFixture();
        var saved = await fx.Service.SaveProfileAsync(Profile(), "pw", true, CancellationToken.None);

        var outcome = await fx.Service.ConnectAsync(saved, null, CancellationToken.None);

        Assert.True(outcome.IsConnected);
        Assert.Equal("pw", fx.Sessions.Calls.Single().Password);
        Assert.NotNull(fx.Service.ActiveSession);
        Assert.True(fx.Keys.Get("hasSession"));
        Assert.False(fx.Keys.Get("sessionBroken"));
        var stored = (await fx.Service.LoadAsync(CancellationToken.None)).Value.Profiles.Single();
        Assert.NotNull(stored.LastConnectedAt);
    }

    [Fact]
    public async Task Unreadable_stored_password_asks_for_one_instead_of_failing()
    {
        // macOS 에서 키체인 접근 허용 창을 「거부」하면 읽기가 OSStatus -128 로 실패한다. 앱이 죽지 않고 비밀번호를 물어야 한다.
        using var fx = new ServiceFixture();
        var saved = await fx.Service.SaveProfileAsync(Profile(), "tiger", true, CancellationToken.None);
        fx.Credentials.LoadFailure = new InvalidOperationException("키체인 읽기 실패(OSStatus -128).");

        var outcome = await fx.Service.ConnectAsync(saved, null, CancellationToken.None);

        Assert.Equal(ConnectStatus.PasswordRequired, outcome.Status);
        Assert.Contains("저장된 비밀번호를 읽지 못했습니다", outcome.Message, StringComparison.Ordinal);
        Assert.Contains("-128", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(ConnectStatus.Connected, (await fx.Service.ConnectAsync(saved, "tiger", CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Connect_without_stored_password_asks_for_one()
    {
        using var fx = new ServiceFixture();
        var saved = await fx.Service.SaveProfileAsync(Profile(), null, false, CancellationToken.None);
        var outcome = await fx.Service.ConnectAsync(saved, null, CancellationToken.None);
        Assert.Equal(ConnectStatus.PasswordRequired, outcome.Status);
        Assert.Empty(fx.Sessions.Calls);
        Assert.True((await fx.Service.ConnectAsync(saved, "typed", CancellationToken.None)).IsConnected);
    }

    [Fact]
    public async Task Unsupported_server_shows_version_and_reason_and_does_not_become_active()
    {
        using var fx = new ServiceFixture();
        fx.Sessions.Behavior = (_, _) => new UnsupportedServerException("11.2.0.4.0", "Oranda 는 Oracle 19.x·23.x 만 지원합니다. 이 서버는 11 계열입니다.");
        var outcome = await fx.Service.ConnectAsync(Profile(), "pw", CancellationToken.None);

        Assert.Equal(ConnectStatus.Unsupported, outcome.Status);
        Assert.Equal("11.2.0.4.0", outcome.ServerVersion);
        Assert.Contains("11.2.0.4.0", outcome.Message, StringComparison.Ordinal);
        Assert.Contains("19.x", outcome.Message, StringComparison.Ordinal);
        Assert.Null(fx.Service.ActiveSession);
        Assert.False(fx.Keys.Get("hasSession"));
    }

    [Fact]
    public async Task Proxy_profile_is_sent_to_the_driver_as_proxy_bracket_user_but_stored_as_is()
    {
        using var fx = new ServiceFixture();
        var saved = await fx.Service.SaveProfileAsync(Profile(user: "scott") with { ProxyUser = "app" }, "pw", true, CancellationToken.None);
        await fx.Service.ConnectAsync(saved, null, CancellationToken.None);

        Assert.Equal("app[scott]", fx.Sessions.Calls.Single().Profile.User);
        Assert.Null(fx.Sessions.Calls.Single().Profile.ProxyUser);
        var stored = (await fx.Service.LoadAsync(CancellationToken.None)).Value.Profiles.Single();
        Assert.Equal(("scott", "app"), (stored.User, stored.ProxyUser));
    }

    [Fact]
    public async Task Disconnect_closes_session_and_clears_keys_then_broken_event_enables_reconnect()
    {
        using var fx = new ServiceFixture();
        var saved = await fx.Service.SaveProfileAsync(Profile(), "pw", true, CancellationToken.None);
        await fx.Service.ConnectAsync(saved, null, CancellationToken.None);
        var first = fx.Service.ActiveSession!;

        fx.Events.Publish(new SessionBroken(first.Id, false, null));
        Assert.True(fx.Service.IsBroken);
        Assert.True(fx.Keys.Get("sessionBroken"));
        Assert.False(fx.Keys.Get("hasSession"));

        var again = await fx.Service.ReconnectAsync(null, CancellationToken.None);
        Assert.True(again.IsConnected);
        Assert.NotSame(first, fx.Service.ActiveSession);
        Assert.False(fx.Keys.Get("sessionBroken"));
        Assert.True(fx.Keys.Get("hasSession"));

        await fx.Service.DisconnectAsync(CancellationToken.None);
        Assert.Null(fx.Service.ActiveSession);
        Assert.False(fx.Keys.Get("hasSession"));
    }

    [Fact]
    public async Task Change_password_updates_the_credential_store_only()
    {
        using var fx = new ServiceFixture();
        var saved = await fx.Service.SaveProfileAsync(Profile(), "old", true, CancellationToken.None);

        var result = await fx.Service.ChangePasswordAsync(saved, null, "new!1", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(("old", "new!1"), fx.Changer.Calls.Single());
        Assert.Equal("new!1", fx.Credentials.Text(saved.CredentialRef!));
        Assert.DoesNotContain("new!1", fx.ConnectionsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_password_change_keeps_stored_password()
    {
        using var fx = new ServiceFixture();
        var saved = await fx.Service.SaveProfileAsync(Profile(), "old", true, CancellationToken.None);
        fx.Changer.Failure = new InvalidOperationException("ORA-28003: password verification for the specified password failed");
        var result = await fx.Service.ChangePasswordAsync(saved, null, "weak", CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Contains("ORA-28003", result.Message, StringComparison.Ordinal);
        Assert.Equal("old", fx.Credentials.Text(saved.CredentialRef!));
    }

    // ---------- 모델 흐름(묻기·만료·지원 안 함) ----------

    [Fact]
    public async Task Viewmodel_asks_password_when_not_stored_then_connects_and_requests_close()
    {
        using var fx = new ServiceFixture();
        var saved = await fx.Service.SaveProfileAsync(Profile(), null, false, CancellationToken.None);
        var vm = new ConnectionManagerViewModel(fx.Service) { AskPassword = _ => Task.FromResult<string?>("typed") };
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;
        await vm.LoadAsync();
        vm.Selected = vm.Tree.Single();

        await vm.ConnectCommand.ExecuteAsync(null);

        Assert.True(closed);
        Assert.Equal("typed", fx.Sessions.Calls.Last().Password);
        Assert.Equal(saved.Id, fx.Service.LastProfile!.Id);
    }

    [Fact]
    public async Task Expired_password_opens_change_dialog_then_reconnects_with_new_password()
    {
        using var fx = new ServiceFixture();
        var saved = await fx.Service.SaveProfileAsync(Profile(), "old", true, CancellationToken.None);
        fx.Sessions.Behavior = (_, pw) => pw == "old" ? new InvalidOperationException("ORA-28001: the password has expired") : null;
        var vm = new ConnectionManagerViewModel(fx.Service)
        {
            AskChangePassword = (_, _) => Task.FromResult<(string? Old, string New)?>((null, "brand-new")),
        };

        var outcome = await vm.ConnectWithPromptsAsync(saved);

        Assert.True(outcome!.IsConnected);
        Assert.Equal(("old", "brand-new"), fx.Changer.Calls.Single());
        Assert.Equal("brand-new", fx.Sessions.Calls.Last().Password);
        Assert.Equal("brand-new", fx.Credentials.Text(saved.CredentialRef!));
    }

    [Fact]
    public async Task Expired_password_with_cancelled_dialog_stays_disconnected()
    {
        using var fx = new ServiceFixture();
        var saved = await fx.Service.SaveProfileAsync(Profile(), "old", true, CancellationToken.None);
        fx.Sessions.Behavior = (_, _) => new InvalidOperationException("ORA-28001: the password has expired");
        var vm = new ConnectionManagerViewModel(fx.Service)
        {
            AskChangePassword = (_, _) => Task.FromResult<(string? Old, string New)?>(null),
        };

        Assert.Null(await vm.ConnectWithPromptsAsync(saved));
        Assert.Null(fx.Service.ActiveSession);
        Assert.True(vm.StatusIsError);
    }

    [Fact]
    public async Task Unsupported_version_is_shown_in_status_with_version_and_reason()
    {
        using var fx = new ServiceFixture();
        var saved = await fx.Service.SaveProfileAsync(Profile(), "pw", true, CancellationToken.None);
        fx.Sessions.Behavior = (_, _) => new UnsupportedServerException("12.2.0.1.0", "Oranda 는 Oracle 19.x·23.x 만 지원합니다. 이 서버는 12 계열입니다.");
        var vm = new ConnectionManagerViewModel(fx.Service);

        var outcome = await vm.ConnectWithPromptsAsync(saved);

        Assert.Equal(ConnectStatus.Unsupported, outcome!.Status);
        Assert.True(vm.StatusIsError);
        Assert.Contains("12.2.0.1.0", vm.Status, StringComparison.Ordinal);
        Assert.Contains("지원", vm.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_command_reports_validation_errors_in_status()
    {
        using var fx = new ServiceFixture();
        var vm = new ConnectionManagerViewModel(fx.Service);
        await vm.LoadAsync();
        vm.AddNewCommand.Execute(null);
        vm.Editor!.Name = "x";
        vm.Editor.User = "u";
        vm.Editor.Mode = ConnectionMode.EasyConnect;
        vm.Editor.EasyConnect = "scott/tiger@h/s";

        Assert.False(await vm.SaveCurrentAsync());
        Assert.True(vm.StatusIsError);
        Assert.Empty((await fx.Service.LoadAsync(CancellationToken.None)).Value.Profiles);
    }

    [Fact]
    public async Task Add_edit_delete_updates_tree_with_groups_and_environment_chip()
    {
        using var fx = new ServiceFixture();
        var vm = new ConnectionManagerViewModel(fx.Service) { Confirm = _ => Task.FromResult(true) };
        await vm.LoadAsync();

        vm.AddNewCommand.Execute(null);
        vm.Editor!.Name = "운영";
        vm.Editor.Group = "고객사";
        vm.Editor.User = "u";
        vm.Editor.Host = "h";
        vm.Editor.Service = "s";
        vm.Editor.Environment = ConnectionEnvironment.Production;
        vm.Editor.Password = "pw";
        Assert.True(await vm.SaveCurrentAsync());

        var folder = Assert.Single(vm.Tree);
        Assert.True(folder.IsFolder);
        var node = Assert.Single(folder.Children);
        Assert.Equal("운영", node.EnvironmentLabel);
        Assert.Equal("#C42B1C", node.EnvironmentColor);
        Assert.Contains("운영 접속", node.AccessibleName, StringComparison.Ordinal);
        Assert.Same(node, vm.Selected);
        Assert.Empty(vm.Editor!.Password);

        vm.Editor.Name = "운영2";
        Assert.True(await vm.SaveCurrentAsync());
        Assert.Equal("운영2", vm.Tree.Single().Children.Single().Title);

        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.Empty(vm.Tree);
        Assert.Empty(fx.Credentials.Items);
    }

    // ---------- 지갑 ----------

    private static string MakeWalletZip(string dir, bool autoLogin = true, string name = "Wallet_MYDB.zip", string alias = "mydb", string? extraEntry = null)
    {
        var path = Path.Combine(dir, name);
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        void Add(string entry, string text)
        {
            using var w = new StreamWriter(zip.CreateEntry(entry).Open(), new UTF8Encoding(false));
            w.Write(text);
        }

        Add(
            "tnsnames.ora",
            $"""
            # comment_alias = (x)
            {alias}_high = (description=(address=(protocol=tcps)(port=1522)(host=h.example.com))(connect_data=(service_name=s_high.adb.oraclecloud.com))(security=(ssl_server_dn_match=yes)))
            {alias}_low = (description=(address=(protocol=tcps)(port=1522)(host=h.example.com))(connect_data=(service_name=s_low.adb.oraclecloud.com)))
            """);
        Add("sqlnet.ora", "WALLET_LOCATION = (SOURCE = (METHOD = file) (METHOD_DATA = (DIRECTORY=\"?/network/admin\")))\nSSL_SERVER_DN_MATCH=yes\n");
        if (autoLogin)
        {
            Add("cwallet.sso", "binary-ish");
        }

        Add("ewallet.p12", "p12");
        Add("readme.txt", "ignored");
        if (extraEntry is not null)
        {
            Add(extraEntry, "evil");
        }

        return path;
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oranda-wz-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Wallet_inspect_lists_aliases_prefers_high_and_suggests_name()
    {
        var dir = NewTempDir();
        try
        {
            var info = WalletImporter.Inspect(MakeWalletZip(dir));
            Assert.Equal(["mydb_high", "mydb_low"], info.Aliases);
            Assert.Equal("mydb_high", info.DefaultAlias);
            Assert.True(info.HasAutoLoginWallet);
            Assert.Equal("MYDB", info.SuggestedName);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Wallet_extract_rewrites_wallet_directory_ignores_unknown_files_and_blocks_path_traversal()
    {
        var dir = NewTempDir();
        try
        {
            var zip = MakeWalletZip(dir, extraEntry: "../escape.ora");
            var target = Path.Combine(dir, "out");
            var written = WalletImporter.Extract(zip, target);

            Assert.Contains("cwallet.sso", written);
            Assert.DoesNotContain("readme.txt", written);
            Assert.False(File.Exists(Path.Combine(dir, "escape.ora")));
            var sqlnet = File.ReadAllText(Path.Combine(target, "sqlnet.ora"));
            Assert.Contains(target, sqlnet, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("?/network/admin", sqlnet, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Wallet_without_tnsnames_or_not_a_zip_is_rejected_with_message()
    {
        var dir = NewTempDir();
        try
        {
            var notZip = Path.Combine(dir, "x.zip");
            File.WriteAllText(notZip, "hello");
            Assert.Throws<WalletFormatException>(() => WalletImporter.Inspect(notZip));

            var empty = Path.Combine(dir, "empty.zip");
            using (var fs = File.Create(empty))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                zip.CreateEntry("other.txt");
            }

            var ex = Assert.Throws<WalletFormatException>(() => WalletImporter.Inspect(empty));
            Assert.Contains("tnsnames.ora", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Wallet_import_creates_profile_with_tns_alias_and_wallet_folder_inside_data_dir()
    {
        using var fx = new ServiceFixture();
        var zip = MakeWalletZip(fx.Directory);

        var p = await fx.Service.ImportWalletAsync(zip, null, CancellationToken.None);

        Assert.Equal("MYDB", p.Name);
        Assert.Equal("mydb_high", p.DataSource);
        Assert.Equal(p.TnsAdmin, p.WalletLocation);
        Assert.StartsWith(Path.Combine(fx.Directory, "wallets"), p.WalletLocation!, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(p.WalletLocation!, "cwallet.sso")));
        Assert.Null(p.CredentialRef);
        Assert.Single((await fx.Service.LoadAsync(CancellationToken.None)).Value.Profiles);

        // 같은 지갑을 또 가져와도 이름이 겹치지 않는다.
        var second = await fx.Service.ImportWalletAsync(zip, "mydb_low", CancellationToken.None);
        Assert.Equal("MYDB (2)", second.Name);
        Assert.Equal("mydb_low", second.DataSource);

        // 지우면 풀어 놓은 지갑 폴더도 지운다.
        await fx.Service.DeleteProfileAsync(p.Id, CancellationToken.None);
        Assert.False(Directory.Exists(p.WalletLocation));
        Assert.True(Directory.Exists(second.WalletLocation));
    }

    [Fact]
    public async Task Wallet_without_auto_login_file_is_refused()
    {
        using var fx = new ServiceFixture();
        var zip = MakeWalletZip(fx.Directory, autoLogin: false);
        await Assert.ThrowsAsync<WalletFormatException>(() => fx.Service.ImportWalletAsync(zip, null, CancellationToken.None));
        Assert.Empty((await fx.Service.LoadAsync(CancellationToken.None)).Value.Profiles);
    }

    [Fact]
    public async Task Dropping_a_wallet_creates_profile_asks_password_and_connects()
    {
        using var fx = new ServiceFixture();
        var zip = MakeWalletZip(fx.Directory);
        var vm = new ConnectionManagerViewModel(fx.Service) { AskPassword = _ => Task.FromResult<string?>("admin-pw") };
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;
        await vm.LoadAsync();

        await vm.ImportWalletAsync(zip);

        Assert.True(closed);
        var sent = fx.Sessions.Calls.Single().Profile;
        Assert.StartsWith("(description=", sent.DataSource, StringComparison.Ordinal);
        Assert.Contains("MY_WALLET_DIRECTORY=", sent.DataSource, StringComparison.Ordinal);
        Assert.Contains("s_high.adb.oraclecloud.com", sent.DataSource, StringComparison.Ordinal);
        Assert.Equal("admin-pw", fx.Sessions.Calls.Single().Password);
        Assert.Equal("MYDB", vm.Selected!.Title);
        Assert.NotNull(fx.Service.ActiveSession);
    }

    [Fact]
    public async Task Dropping_a_broken_wallet_shows_error_and_creates_nothing()
    {
        using var fx = new ServiceFixture();
        var bad = Path.Combine(fx.Directory, "Wallet_BAD.zip");
        await File.WriteAllTextAsync(bad, "not a zip");
        var vm = new ConnectionManagerViewModel(fx.Service);
        await vm.LoadAsync();
        await vm.ImportWalletAsync(bad);
        Assert.True(vm.StatusIsError);
        Assert.Empty(vm.Tree);
    }

    // ---------- TNS 별칭 풀기 ----------

    private const string SampleTns = """
        # 주석 줄 (skip_me = (DESCRIPTION=(x)))
        FIRST =
          (DESCRIPTION =
            (ADDRESS = (PROTOCOL = TCP)(HOST = h1)(PORT = 1521))
            (CONNECT_DATA = (SERVICE_NAME = svc1))
          )

        adb_high = (description= (retry_count=20)(address=(protocol=tcps)(port=1522)(host=h2))(connect_data=(service_name=s2))(security=(ssl_server_cert_dn="CN=adb.example.com, OU=Oracle BMCS US, O=Oracle Corporation")))
        """;

    [Fact]
    public void Tns_finder_reads_multiline_entry_ignoring_comments_and_case()
    {
        Assert.True(TnsResolver.TryFind(SampleTns, "first", out var d));
        Assert.Equal("(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=h1)(PORT=1521))(CONNECT_DATA=(SERVICE_NAME=svc1)))", d);
        Assert.False(TnsResolver.TryFind(SampleTns, "skip_me", out _));
        Assert.False(TnsResolver.TryFind(SampleTns, "missing", out _));
    }

    [Fact]
    public void Tns_finder_keeps_spaces_inside_quotes()
    {
        Assert.True(TnsResolver.TryFind(SampleTns, "ADB_HIGH", out var d));
        Assert.Contains("\"CN=adb.example.com, OU=Oracle BMCS US, O=Oracle Corporation\"", d, StringComparison.Ordinal);
        Assert.DoesNotContain(" (", d, StringComparison.Ordinal);
    }

    [Fact]
    public void Wallet_directory_goes_into_existing_or_new_security_section_once()
    {
        TnsResolver.TryFind(SampleTns, "adb_high", out var withSecurity);
        var a = TnsResolver.WithWalletDirectory(withSecurity, "C:/w");
        Assert.Contains("(security=(MY_WALLET_DIRECTORY=C:/w)(ssl_server_cert_dn=", a, StringComparison.Ordinal);
        Assert.Equal(a, TnsResolver.WithWalletDirectory(a, "C:/other"));

        TnsResolver.TryFind(SampleTns, "first", out var without);
        var b = TnsResolver.WithWalletDirectory(without, "C:/w");
        Assert.EndsWith("(SECURITY=(MY_WALLET_DIRECTORY=C:/w)))", b, StringComparison.Ordinal);
    }

    [Fact]
    public void Driver_profile_resolves_alias_so_tns_admin_is_not_needed_process_wide()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "tnsnames.ora"), SampleTns);
            var profile = new ConnectionProfile("t", "u", "first", TnsAdmin: dir);

            var driver = ConnectionService.ToDriverProfile(profile);

            Assert.StartsWith("(DESCRIPTION=", driver.DataSource, StringComparison.Ordinal);
            Assert.Null(driver.TnsAdmin);
            Assert.Equal("first", profile.DataSource);

            // 별칭이 없거나 EZConnect 문자열이면 그대로 둔다.
            Assert.Equal("nope", ConnectionService.ToDriverProfile(profile with { DataSource = "nope" }).DataSource);
            var ez = ConnectionService.ToDriverProfile(profile with { DataSource = "h:1521/s" });
            Assert.Equal("h:1521/s", ez.DataSource);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------- 창(Headless) ----------

    [Fact]
    public async Task Manager_window_shows_tree_and_editor_and_fits_960x600()
    {
        using var fx = new ServiceFixture();
        await fx.Service.SaveProfileAsync(Profile("개발"), "pw", true, CancellationToken.None);
        await fx.Service.SaveProfileAsync(Profile("운영") with { Environment = ConnectionEnvironment.Production, Group = "고객사" }, null, false, CancellationToken.None);
        var vm = new ConnectionManagerViewModel(fx.Service);
        await vm.LoadAsync();

        await HeadlessHost.Run(() =>
        {
            var window = new ConnectionManagerWindow { DataContext = vm, Width = 960, Height = 600 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var tree = window.FindControl<TreeView>("ConnectionTree")!;
            Assert.Equal(2, tree.ItemCount);
            Assert.NotNull(window.FindControl<Button>("ConnectButton"));
            Assert.NotNull(window.FindControl<Button>("SaveButton"));
            Assert.False(window.FindControl<Button>("DeleteButton")!.IsEffectivelyEnabled);

            vm.Selected = vm.Tree.First(n => !n.IsFolder);
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.FindControl<Button>("DeleteButton")!.IsEffectivelyEnabled);
            var editor = window.FindControl<ProfileEditorView>("EditorView")!;
            Assert.True(editor.IsVisible);
            Assert.Equal("개발", editor.FindControl<TextBox>("NameBox")!.Text);

            // 접속 방식에 따라 입력칸이 바뀐다.
            vm.Editor!.Mode = ConnectionMode.TnsAlias;
            Dispatcher.UIThread.RunJobs();
            Assert.True(editor.FindControl<TextBox>("TnsAliasBox")!.IsEffectivelyVisible);
            Assert.False(editor.FindControl<TextBox>("HostBox")!.IsEffectivelyVisible);

            Assert.True(window.Bounds.Width <= 960 + 0.5 && window.Bounds.Height <= 600 + 0.5);
            window.Close();
        });
    }

    [Fact]
    public async Task Manage_command_runs_in_the_real_shell_and_registers_the_connection_service()
    {
        using var fx = new ServiceFixture();
        await HeadlessHost.Run(async () =>
        {
            using var shell = new ShellHarness();
            var registry = shell.Get<Oranda.Core.Shell.ICommandRegistry>();
            var keys = shell.Get<ContextKeyService>();

            Assert.False(registry.CanExecute("oranda.conn.disconnect", keys));
            Assert.True(registry.CanExecute("oranda.conn.manage", keys));
            Assert.True(await registry.ExecuteAsync("oranda.conn.disconnect", FakeContextKeys.AllTrue));

            Assert.NotNull(shell.Services.GetService<IConnectionService>());
            Dispatcher.UIThread.RunJobs();
        });
    }
}

/// <summary>
/// 통합 시험(Category=Integration): 실제 26ai 컨테이너에 접속한다. 환경 변수가 없으면 아무것도 하지 않고 통과한다.
/// ORANDA_IT_HOST, ORANDA_IT_PORT, ORANDA_IT_SERVICE, ORANDA_IT_USER, ORANDA_IT_PASSWORD (일반 사용자),
/// ORANDA_IT_ADMIN_USER, ORANDA_IT_ADMIN_PASSWORD (만료·프록시 시험용 계정을 만든다, 선택).
/// </summary>
[Trait("Category", "Integration")]
public sealed class ConnectionManagerIntegrationTests : IDisposable
{
    private readonly ServiceFixture _fx = new();
    private readonly ConnectionService _service;

    public ConnectionManagerIntegrationTests()
    {
        _service = new ConnectionService(
            _fx.Store,
            _fx.Credentials,
            _fx.Container.GetRequiredService<ISessionManager>(),
            _fx.Paths,
            _fx.Events,
            keys: _fx.Keys);
    }

    private static bool Configured =>
        Env("HOST") is not null && Env("SERVICE") is not null && Env("USER") is not null && Env("PASSWORD") is not null;

    private static string Host => Env("HOST")!;

    private static string Port => Env("PORT") ?? "1521";

    private static string Svc => Env("SERVICE")!;

    public void Dispose()
    {
        _service.Dispose();
        _fx.Dispose();
    }

    private static string? Env(string name) => Environment.GetEnvironmentVariable("ORANDA_IT_" + name);

    [Fact]
    public async Task Connects_to_26ai_by_easy_connect_host_port_service_and_tns_alias()
    {
        if (!Configured)
        {
            return;
        }

        var tnsDir = Path.Combine(_fx.Directory, "tns");
        Directory.CreateDirectory(tnsDir);
        await File.WriteAllTextAsync(
            Path.Combine(tnsDir, "tnsnames.ora"),
            $"ORANDA_IT = (DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST={Host})(PORT={Port}))(CONNECT_DATA=(SERVICE_NAME={Svc})))\n");

        var editor = ProfileEditorViewModel.ForNew();
        editor.Name = "hostport";
        editor.User = Env("USER")!;
        editor.Host = Host;
        editor.Port = Port;
        editor.Service = Svc;
        var hostPort = editor.TryBuild(out var errors);
        Assert.Empty(errors);

        var profiles = new[]
        {
            new ConnectionProfile("easyconnect", Env("USER")!, $"{Host}:{Port}/{Svc}"),
            hostPort!,
            new ConnectionProfile("tnsalias", Env("USER")!, "ORANDA_IT", TnsAdmin: tnsDir),
        };

        foreach (var p in profiles)
        {
            var saved = await _service.SaveProfileAsync(p, Env("PASSWORD"), true, CancellationToken.None);
            var outcome = await _service.ConnectAsync(saved, null, CancellationToken.None);

            Assert.True(outcome.IsConnected, $"{p.Name}: {outcome.Message}");
            Assert.StartsWith("23.", outcome.ServerVersion, StringComparison.Ordinal);
            Assert.Equal(Env("USER")!.ToUpperInvariant(), outcome.Session!.Server.SessionUser);
            Assert.Contains("26ai", outcome.Message, StringComparison.Ordinal);
            await _service.DisconnectAsync(CancellationToken.None);
        }

        Assert.DoesNotContain(Env("PASSWORD")!, _fx.ConnectionsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wrong_password_reports_ora_error_without_throwing()
    {
        if (!Configured)
        {
            return;
        }

        var saved = await _service.SaveProfileAsync(new ConnectionProfile("bad", Env("USER")!, $"{Host}:{Port}/{Svc}"), null, false, CancellationToken.None);
        var outcome = await _service.ConnectAsync(saved, "definitely-wrong-1", CancellationToken.None);
        Assert.Equal(ConnectStatus.Failed, outcome.Status);
        Assert.Contains("ORA-01017", outcome.Message, StringComparison.Ordinal);
        Assert.Null(_service.ActiveSession);
    }

    [Fact]
    public async Task Dropped_wallet_zip_creates_profile_and_connects()
    {
        if (!Configured)
        {
            return;
        }

        // 실물 ADB 지갑이 없으므로 같은 모양의 zip(tnsnames.ora, sqlnet.ora, cwallet.sso)을 만들고 별칭이 로컬 컨테이너를 가리키게 한다.
        var zipPath = Path.Combine(_fx.Directory, "Wallet_LOCAL26.zip");
        using (var fs = File.Create(zipPath))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            void Add(string name, string text)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                w.Write(text);
            }

            Add("tnsnames.ora", $"local26_high = (DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST={Host})(PORT={Port}))(CONNECT_DATA=(SERVICE_NAME={Svc})))\n");
            Add("sqlnet.ora", "WALLET_LOCATION = (SOURCE = (METHOD = file) (METHOD_DATA = (DIRECTORY=\"?/network/admin\")))\nSSL_SERVER_DN_MATCH=yes\n");
            Add("cwallet.sso", "placeholder");
        }

        var vm = new ConnectionManagerViewModel(_service);
        await vm.LoadAsync();
        await vm.ImportWalletAsync(zipPath, thenConnect: false);
        Assert.Equal("LOCAL26", vm.Selected!.Title);
        Assert.Equal("local26_high", vm.Selected.Profile!.DataSource);

        // 지갑의 사용자는 기본 ADMIN 이다. 이 컨테이너의 계정으로 바꿔 저장한 뒤 접속한다.
        vm.Editor!.User = Env("USER")!;
        vm.Editor.Password = Env("PASSWORD")!;
        vm.Editor.SavePassword = true;
        Assert.True(await vm.SaveCurrentAsync());
        var outcome = await vm.ConnectWithPromptsAsync(vm.Selected!.Profile!);

        Assert.True(outcome!.IsConnected, outcome.Message);
        Assert.StartsWith("23.", outcome.ServerVersion, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Expired_password_is_detected_changed_and_reconnected()
    {
        if (!Configured || Env("ADMIN_USER") is null)
        {
            return;
        }

        var user = "ORANDA_EXP_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var oldPw = "Old" + Guid.NewGuid().ToString("N")[..10] + "#1";
        var newPw = "New" + Guid.NewGuid().ToString("N")[..10] + "#1";
        await Admin($"CREATE USER {user} IDENTIFIED BY \"{oldPw}\" PASSWORD EXPIRE", $"GRANT CREATE SESSION TO {user}");
        try
        {
            var saved = await _service.SaveProfileAsync(new ConnectionProfile("expired", user, $"{Host}:{Port}/{Svc}"), oldPw, true, CancellationToken.None);
            var first = await _service.ConnectAsync(saved, null, CancellationToken.None);
            Assert.Equal(ConnectStatus.PasswordExpired, first.Status);

            var vm = new ConnectionManagerViewModel(_service)
            {
                AskChangePassword = (_, _) => Task.FromResult<(string? Old, string New)?>((null, newPw)),
            };
            var outcome = await vm.ConnectWithPromptsAsync(saved);

            Assert.True(outcome!.IsConnected, outcome.Message);
            Assert.Equal(newPw, _fx.Credentials.Text(saved.CredentialRef!));
            Assert.DoesNotContain(newPw, _fx.ConnectionsJson, StringComparison.Ordinal);
        }
        finally
        {
            await _service.DisconnectAsync(CancellationToken.None);
            await Admin($"DROP USER {user} CASCADE");
        }
    }

    [Fact]
    public async Task Proxy_user_connects_as_target_user()
    {
        if (!Configured || Env("ADMIN_USER") is null)
        {
            return;
        }

        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var proxy = "ORANDA_PX_" + suffix;
        var target = "ORANDA_TG_" + suffix;
        var pw = "Px" + Guid.NewGuid().ToString("N")[..10] + "#1";
        await Admin(
            $"CREATE USER {target} IDENTIFIED BY \"{pw}x\"",
            $"GRANT CREATE SESSION TO {target}",
            $"CREATE USER {proxy} IDENTIFIED BY \"{pw}\"",
            $"GRANT CREATE SESSION TO {proxy}",
            $"ALTER USER {target} GRANT CONNECT THROUGH {proxy}");
        try
        {
            var saved = await _service.SaveProfileAsync(
                new ConnectionProfile("proxy", target, $"{Host}:{Port}/{Svc}", ProxyUser: proxy), pw, true, CancellationToken.None);
            var outcome = await _service.ConnectAsync(saved, null, CancellationToken.None);

            Assert.True(outcome.IsConnected, outcome.Message);
            Assert.Equal(target, outcome.Session!.Server.SessionUser);
        }
        finally
        {
            await _service.DisconnectAsync(CancellationToken.None);
            await Admin($"DROP USER {proxy} CASCADE", $"DROP USER {target} CASCADE");
        }
    }

    private static async Task Admin(params string[] statements)
    {
        var profile = new ConnectionProfile("admin", Env("ADMIN_USER")!, $"{Host}:{Port}/{Svc}");
        await using var conn = new global::Oracle.ManagedDataAccess.Client.OracleConnection(
            Oranda.Oracle.Connection.ConnectionProfileExtensions.BuildConnectionString(profile, Env("ADMIN_PASSWORD")!));
        await conn.OpenAsync();
        foreach (var sql in statements)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
