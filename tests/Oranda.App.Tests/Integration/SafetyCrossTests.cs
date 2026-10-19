using System.Globalization;
using System.Reflection;
using System.Text;
using Oracle.ManagedDataAccess.Client;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.DbTools;
using Oranda.App.Features.Execution;
using Oranda.App.Features.ExtensionTest;
using Oranda.App.Features.GridEdit;
using Oranda.App.Features.HistoryScripts;
using Oranda.App.Features.LobRowView;
using Oranda.App.Features.PlsqlEditor;
using Oranda.App.Features.PlsqlRun;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell.Composition;
using Oranda.App.Tests.Features.Execution;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.App.Tests.Shell;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Extensibility;
using Oranda.Core.Grid;
using Oranda.Core.History;
using Oranda.Core.Language;
using Oranda.Core.Metadata;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;
using Oranda.Core.Storage;
using Oranda.Platform.Diagnostics;

namespace Oranda.App.Tests.Integration;

// 소유: TSK-11-04. 보안·안전장치 교차 시험(앱 쪽). 같은 정책 관문을 쓰는 앱 기능 경로(편집기 실행·그리드 편집·LOB 저장·스키마 관리·
// PL/SQL 편집·PL/SQL 실행·확장 패널·이력 다시 실행)가 운영 라벨·읽기 전용 프로필에서 같은 결과를 내는지, 비밀번호가 데이터 폴더·이력·
// 충돌 보고 어디에도 평문으로 남지 않는지, 외부 전송이 동의 없이 일어나지 않는지 본다.
// DB 시험은 상주 컨테이너를 쓴다: ORANDA_IT_HOST, ORANDA_IT_PORT(1521), ORANDA_IT_SERVICE, ORANDA_IT_ADMIN_USER, ORANDA_IT_ADMIN_PASSWORD.
// 환경 변수가 없으면 DB 시험은 아무것도 안 하고 끝난다(「돌렸다」 로 세지 않는다). ORANDA_IT_REQUIRE=1 이면 없을 때 실패한다.

/// <summary>DB 없이 도는 시험: 비밀 가림·충돌 보고·외부 전송 동의·평문 검색의 파일 쪽.</summary>
public sealed class SafetyCrossNoDbTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-sc-" + Guid.NewGuid().ToString("N"));

    public SafetyCrossNoDbTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 임시 폴더 정리 실패는 무시한다.
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Oranda.sln")) && !File.Exists(Path.Combine(dir.FullName, "Oranda.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("저장소 루트를 찾지 못했다.");
    }

    // ---- 비밀 가림(이력·오류 메시지) ---------------------------------------------------------------

    private static readonly Oranda.Language.Redaction.SecretRedactor Probe = new();

    public static IEnumerable<object[]> RedactedForms() =>
    [
        ["ALTER USER scott IDENTIFIED BY \"Zx1#secretvalue\"", "Zx1#secretvalue"],
        ["alter user scott identified by Zx1secretvalue", "Zx1secretvalue"],
        ["CREATE USER u IDENTIFIED BY \"p w;1\" DEFAULT TABLESPACE users", "p w;1"],
        ["ALTER USER scott IDENTIFIED BY \"NewZx9secret\" REPLACE \"OldZx9secret\"", "NewZx9secret"],
        ["ALTER USER scott IDENTIFIED BY \"NewZx9secret\" REPLACE \"OldZx9secret\"", "OldZx9secret"],
        ["ALTER USER scott IDENTIFIED BY VALUES 'S:ABCDEF0123456789'", "S:ABCDEF0123456789"],
        ["CREATE DATABASE LINK l CONNECT TO u IDENTIFIED BY \"LinkZx9secret\" USING 'x'", "LinkZx9secret"],
        ["CONNECT scott/Zx1secretvalue@db", "Zx1secretvalue"],
        ["conn scott/\"Zx1#secret value\"", "Zx1#secret value"],
        ["-- note\n  CONNECT scott/Zx1secretvalue", "Zx1secretvalue"],
        ["PASSWORD Zx1secretvalue", "Zx1secretvalue"],
        ["select 1 from dual;\nALTER USER x IDENTIFIED BY \"Zx1secretvalue\"", "Zx1secretvalue"],
    ];

    [Theory]
    [MemberData(nameof(RedactedForms))]
    public void Secret_redactor_removes_the_password_in_every_known_form(string sql, string secret)
    {
        var redacted = Probe.Redact(sql);

        Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
        Assert.Contains("***", redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pwd")]
    [InlineData("PASSWORD")]
    [InlineData("db_secret")]
    [InlineData("api_token")]
    [InlineData("userPass")]
    public void Sensitive_bind_names_are_masked(string name) => Assert.True(Probe.IsSensitiveBindName(name));

    [Theory]
    [InlineData("id")]
    [InlineData("name")]
    [InlineData("amount")]
    public void Ordinary_bind_names_are_kept(string name) => Assert.False(Probe.IsSensitiveBindName(name));

    /// <summary>
    /// 관측(결함 DEF-02 후보, 낮음): 비밀 가림은 IDENTIFIED BY·CONNECT·PASSWORD 만 본다. SQL*Plus <c>DEFINE</c>·<c>ACCEPT</c> 값과 문자열 안의 비밀은 가리지 않는다.
    /// 지금 동작을 고정한다. 고치면 이 단정을 뒤집는다.
    /// </summary>
    [Fact]
    public void Observed_define_and_string_literal_secrets_are_not_masked()
    {
        Assert.Contains("Zx1secretvalue", Probe.Redact("DEFINE pw = 'Zx1secretvalue'"), StringComparison.Ordinal);
        Assert.Contains("Zx1secretvalue", Probe.Redact("SELECT 'Zx1secretvalue' FROM dual"), StringComparison.Ordinal);
    }

    // ---- 충돌 보고 ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("User Id=scott;Password=Zx1secretvalue;Data Source=h:1521/s")]
    [InlineData("password=Zx1secretvalue")]
    [InlineData("PWD = Zx1secretvalue; x=1")]
    [InlineData("Data Source=h;secret=Zx1secretvalue,other")]
    [InlineData("token=Zx1secretvalue")]
    public void Crash_report_masks_plain_connection_string_secrets(string text)
    {
        var path = new CrashReportWriter(_dir).Write(new InvalidOperationException("연결 실패: " + text), "1.0.0");

        var body = File.ReadAllText(path);
        Assert.DoesNotContain("Zx1secretvalue", body, StringComparison.Ordinal);
        Assert.Contains("***", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// 실제 연결 문자열 조립기가 비밀번호를 어떻게 쓰는지 확인한다(따옴표가 붙는 경우가 있는지).
    /// 결과는 아래 「관측」 시험이 고정한다.
    /// </summary>
    public static string ConnectionStringFor(string password) =>
        Oranda.Oracle.Connection.ConnectionProfileExtensions.BuildConnectionString(new ConnectionProfile("p", "scott", "h:1521/s"), password);

    [Fact]
    public void Observed_quoted_password_in_a_connection_string_is_not_masked_in_the_crash_report()
    {
        // 비밀번호에 세미콜론이 있으면 ODP.NET 조립기가 값을 따옴표로 감싼다. 충돌 보고의 가림 정규식은 따옴표로 시작하는 값을 못 잡는다(결함 DEF-01).
        var password = "Zx1;secretvalue";
        var cs = ConnectionStringFor(password);
        Assert.Contains("\"", cs, StringComparison.Ordinal);

        var path = new CrashReportWriter(_dir).Write(new InvalidOperationException("연결 실패: " + cs), "1.0.0");

        // 현재 동작: 비밀번호가 보고서에 그대로 남는다. 고치면 이 단정을 DoesNotContain 으로 뒤집는다.
        Assert.Contains(password, File.ReadAllText(path), StringComparison.Ordinal);
    }

    // ---- 외부 전송 동의 -----------------------------------------------------------------------------

    private sealed class FixedConsent(ConsentDecision decision) : IOutboundConsent
    {
        public List<OutboundRequest> Requests { get; } = [];

        public Task<ConsentDecision> RequestAsync(OutboundRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(decision);
        }
    }

    private string WriteReport() => new CrashReportWriter(_dir).Write(new InvalidOperationException("boom"), "1.0.0");

    [Fact]
    public async Task Crash_report_is_not_sent_when_the_user_declines()
    {
        var consent = new FixedConsent(ConsentDecision.Denied);
        var sent = 0;
        var sender = new CrashReportSender(consent, new Uri("https://reports.invalid/x"), (_, _, _) =>
        {
            sent++;
            return Task.CompletedTask;
        });

        var result = await sender.SendAsync(WriteReport(), default);

        Assert.Equal(CrashReportSendResult.NotSent, result);
        Assert.Equal(0, sent);
        Assert.Single(consent.Requests);
    }

    [Theory]
    [InlineData(ConsentDecision.AllowedOnce)]
    [InlineData(ConsentDecision.AllowedAlways)]
    public async Task Crash_report_sends_exactly_the_previewed_content_after_consent(ConsentDecision decision)
    {
        var consent = new FixedConsent(decision);
        var payloads = new List<string>();
        var sender = new CrashReportSender(consent, new Uri("https://reports.invalid/x"), (_, body, _) =>
        {
            payloads.Add(body);
            return Task.CompletedTask;
        });
        var report = WriteReport();

        var result = await sender.SendAsync(report, default);

        Assert.Equal(CrashReportSendResult.Sent, result);
        var request = Assert.Single(consent.Requests);
        Assert.Equal(OutboundPurpose.CrashReport, request.Purpose);
        Assert.Equal(payloads.Single(), request.PayloadPreview); // 미리 보여 준 것과 보낸 것이 같다
        Assert.Equal(await File.ReadAllTextAsync(report), request.PayloadPreview);
        Assert.Equal([report], request.Attachments);
    }

    [Fact]
    public async Task Extension_panel_blocks_outbound_when_there_is_no_consent_service()
    {
        var sent = 0;
        var vm = new ExtensionTestViewModel { Consent = null, Transport = (_, _) => { sent++; return Task.CompletedTask; } };

        var allowed = await vm.SendOutboundAsync(default);

        Assert.False(allowed);
        Assert.Equal(0, sent);
    }

    [Fact]
    public async Task Extension_panel_does_not_send_when_the_user_declines_and_sends_once_when_allowed()
    {
        var denied = new FixedConsent(ConsentDecision.Denied);
        var sent = new List<OutboundRequest>();
        Task Transport(OutboundRequest r, CancellationToken _)
        {
            sent.Add(r);
            return Task.CompletedTask;
        }

        Assert.False(await new ExtensionTestViewModel { Consent = denied, Transport = Transport }.SendOutboundAsync(default));
        Assert.Empty(sent);

        var allowed = new FixedConsent(ConsentDecision.AllowedOnce);
        Assert.True(await new ExtensionTestViewModel { Consent = allowed, Transport = Transport }.SendOutboundAsync(default));
        Assert.Single(sent);
        Assert.Equal(OutboundPurpose.Extension, sent[0].Purpose);
        Assert.Equal(ExtensionTestViewModel.TestDestination, sent[0].Destination);
    }

    /// <summary>
    /// 관측(결함 DEF-03 후보, 중간): 확장 패널이 동의 창에 보여 주고 보내는 본문은 비밀 가림을 거치지 않는다.
    /// 편집기 선택에 <c>IDENTIFIED BY "비밀번호"</c> 가 있으면 그대로 나간다(사용자가 미리보기를 보고 동의하긴 한다). 지금 동작을 고정한다.
    /// </summary>
    [Fact]
    public async Task Observed_extension_outbound_payload_is_not_redacted()
    {
        var consent = new FixedConsent(ConsentDecision.AllowedOnce);
        var vm = new ExtensionTestViewModel { Consent = consent, Transport = (_, _) => Task.CompletedTask };
        vm.SourceText = "ALTER USER x IDENTIFIED BY \"Zx1secretvalue\"";

        await vm.SendOutboundAsync(default);

        Assert.Contains("Zx1secretvalue", Assert.Single(consent.Requests).PayloadPreview, StringComparison.Ordinal);
    }

    [Fact]
    public void No_product_assembly_references_a_network_client_library()
    {
        // 네트워크 라이브러리를 아예 참조하지 않으면 코드 경로에 따라 보낼 수 없다(Oracle 접속은 ODP.NET 이 맡는다).
        var assemblies = new[]
        {
            typeof(Oranda.App.App).Assembly,
            typeof(Oranda.Core.Execution.ExecutionPolicy).Assembly,
            typeof(CrashReportWriter).Assembly,
        };
        var banned = new[] { "System.Net.Http", "System.Net.WebClient", "System.Net.Sockets", "System.Net.Requests", "System.Net.WebSockets.Client" };

        foreach (var asm in assemblies)
        {
            var refs = asm.GetReferencedAssemblies().Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var name in banned)
            {
                Assert.DoesNotContain(name, refs);
            }
        }
    }

    [Fact]
    public void Product_sources_do_not_use_http_or_socket_apis_outside_the_oracle_driver()
    {
        var root = RepoRoot();
        var pattern = new[] { "HttpClient", "WebClient", "WebRequest", "TcpClient", "UdpClient", "new Socket(", "System.Net.Http" };
        var hits = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            hits.AddRange(pattern.Where(p => text.Contains(p, StringComparison.Ordinal)).Select(p => Path.GetRelativePath(root, file) + ": " + p));
        }

        Assert.Empty(hits);
    }

    // ---- 설정 파일(connections.json)에 비밀이 들어가는 길 ------------------------------------------------

    [Theory]
    [InlineData("h:1521/s;password=Zx1secretvalue")]
    [InlineData("h:1521/s?pwd=Zx1secretvalue")]
    [InlineData("scott/Zx1secretvalue@h:1521/s")]
    [InlineData("(DESCRIPTION=(ADDRESS=(HOST=h))(CONNECT_DATA=(SERVICE_NAME=s)));Password = Zx1secretvalue")]
    [InlineData("User Id=scott;PWD=Zx1secretvalue")]
    public void Profile_validation_rejects_a_password_inside_the_connection_string(string dataSource)
    {
        var errors = Oranda.Platform.Storage.ConnectionProfileValidator.Validate(new ConnectionProfile("p", "scott", dataSource));

        Assert.Contains(errors, e => e.Contains("비밀번호", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("scott/Zx1secretvalue")]
    [InlineData("scott/")]
    public void Profile_validation_rejects_a_slash_in_the_user_name(string user)
    {
        var errors = Oranda.Platform.Storage.ConnectionProfileValidator.Validate(new ConnectionProfile("p", user, "h:1521/s"));

        Assert.NotEmpty(errors);
    }

    /// <summary>
    /// 관측(결함 DEF-07 후보, 낮음): 검증은 연결 문자열·사용자 칸만 본다. 프록시 사용자·그룹·이름 칸에 「이름/비밀번호」 를 적어도 막지 않아 connections.json 에 그대로 저장된다.
    /// 지금 동작을 고정한다. 고치면 이 단정을 뒤집는다.
    /// </summary>
    [Fact]
    public void Observed_proxy_user_with_a_slash_secret_is_accepted_by_validation()
    {
        var errors = Oranda.Platform.Storage.ConnectionProfileValidator.Validate(
            new ConnectionProfile("p", "scott", "h:1521/s") { ProxyUser = "app/Zx1secretvalue" });

        Assert.Empty(errors);
    }

    /// <summary>
    /// 관측(결함 DEF-05, 중간): 외부 전송 동의 구현(IOutboundConsent)이 앱에 등록돼 있지 않고, 충돌 보고 작성·전송(CrashReportWriter·CrashReportSender)도
    /// 처리되지 않은 예외 훅에 연결돼 있지 않다. 동의 서비스가 없으면 확장 패널은 모든 전송을 막으므로 안전한 쪽이지만, 업데이트 확인·충돌 보고 동의 창은 아직 없다.
    /// </summary>
    [Fact]
    public void Observed_no_outbound_consent_service_is_registered_in_the_app()
    {
        using var container = AppComposition.Create(new AppCompositionOptions { DataDirectory = _dir });
        container.ScanDeferred();

        Assert.Null(container.GetService<IOutboundConsent>());
    }

    // ---- 평문 검색 ---------------------------------------------------------------------------------

    /// <summary>폴더 아래 모든 파일에서 비밀(UTF-8·UTF-16LE)을 찾는다. 찾은 (파일, 비밀) 목록.</summary>
    internal static List<string> Scan(string directory, params string[] secrets)
    {
        var hits = new List<string>();
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            byte[] bytes;
            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                bytes = new byte[fs.Length];
                fs.ReadExactly(bytes);
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var secret in secrets)
            {
                foreach (var enc in new[] { Encoding.UTF8, Encoding.Unicode })
                {
                    if (bytes.AsSpan().IndexOf(enc.GetBytes(secret)) >= 0)
                    {
                        hits.Add($"{Path.GetFileName(file)} ({enc.WebName}): {secret[..4]}…");
                    }
                }
            }
        }

        return hits;
    }

    [Fact]
    public async Task Saved_profile_history_and_crash_report_hold_no_plaintext_password()
    {
        const string password = "Zq9PlainCheck7788#a";
        const string other = "Zq9OtherCheck5566#b";
        using var container = AppComposition.Create(new AppCompositionOptions { DataDirectory = _dir });
        container.ScanDeferred();

        // 1) 접속 저장(비밀번호 저장 켬): connections.json 에는 CredentialRef 만, 비밀 저장소에는 암호화된 값만.
        var service = ConnectionManagerCommands.Resolve(container);
        // 비밀 저장소는 Windows 에서는 DPAPI 파일이다. macOS 키체인은 시험 자리에서 쓰지 않는다(접근 창이 뜰 수 있다).
        var persist = OperatingSystem.IsWindows();
        var profile = await service.SaveProfileAsync(new ConnectionProfile("sc", "scott", "h:1521/s"), password, savePassword: persist, default);
        Assert.Equal(persist, profile.CredentialRef is not null);

        // 2) 비밀이 든 SQL·오류 메시지가 이력에 들어간다(저장소가 가린다).
        var history = container.GetRequiredService<IHistoryStore>();
        await history.AddAsync(new HistoryEntry
        {
            ProfileId = profile.Id,
            ProfileName = "sc",
            ExecutedAt = DateTimeOffset.Now,
            SqlText = $"ALTER USER scott IDENTIFIED BY \"{other}\" REPLACE \"{password}\"",
            StatementKind = StatementKind.Ddl,
            Source = HistorySource.Editor,
            Status = HistoryStatus.Failed,
            ErrorCode = "ORA-01017",
            ErrorMessage = $"ORA-01017: invalid username/password\nCONNECT scott/{password}@db",
        }, default);
        await history.AddAsync(new HistoryEntry
        {
            ProfileId = profile.Id,
            ProfileName = "sc",
            ExecutedAt = DateTimeOffset.Now,
            SqlText = $"CONNECT scott/\"{password}\"",
            StatementKind = StatementKind.SqlPlus,
            Source = HistorySource.Script,
            Status = HistoryStatus.Succeeded,
        }, default);

        // 3) 충돌 보고(연결 문자열이 든 예외).
        var paths = container.GetRequiredService<IAppDataPaths>();
        new CrashReportWriter(paths.LogsDirectory).Write(new InvalidOperationException($"fail Password={password};User Id=scott"), "1.0.0");

        container.Dispose();
        SqliteConnectionPoolsClear();

        var hits = Scan(_dir, password, other);
        Assert.Empty(hits);
    }

    private static void SqliteConnectionPoolsClear() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
}

/// <summary>DB 쪽 시험 장치: 시험 사용자 하나를 만들고 끝나면 지운다.</summary>
public sealed class SafetyAppDbFixture : IAsyncLifetime
{
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    public bool Enabled { get; private set; }

    public string User => "ORANDA_SA_" + _suffix;

    public string Password { get; } = "Zq" + Guid.NewGuid().ToString("N")[..20] + "#7";

    public static string DataSource => $"{Env("HOST")}:{Env("PORT") ?? "1521"}/{Env("SERVICE")}";

    private static string? Env(string name) => Environment.GetEnvironmentVariable("ORANDA_IT_" + name);

    private static string AdminConnectionString => new OracleConnectionStringBuilder
    {
        DataSource = DataSource,
        UserID = Env("ADMIN_USER"),
        Password = Env("ADMIN_PASSWORD"),
        Pooling = false,
    }.ConnectionString;

    public async Task InitializeAsync()
    {
        Enabled = Env("HOST") is not null && Env("SERVICE") is not null && Env("ADMIN_USER") is not null && Env("ADMIN_PASSWORD") is not null;
        if (!Enabled)
        {
            if (Env("REQUIRE") == "1")
            {
                throw new InvalidOperationException("ORANDA_IT_HOST/SERVICE/ADMIN_USER/ADMIN_PASSWORD 가 없다.");
            }

            return;
        }

        await AdminAsync($"CREATE USER {User} IDENTIFIED BY \"{Password}\"");
        await AdminAsync($"GRANT CREATE SESSION, CREATE TABLE, CREATE PROCEDURE, UNLIMITED TABLESPACE TO {User}");
    }

    public async Task DisposeAsync()
    {
        if (!Enabled)
        {
            return;
        }

        await using var conn = new OracleConnection(AdminConnectionString);
        await conn.OpenAsync();
        var kills = new List<string>();
        await using (var q = conn.CreateCommand())
        {
            q.CommandText = "SELECT sid || ',' || serial# FROM v$session WHERE username = :u";
            q.Parameters.Add(new OracleParameter("u", User));
            await using var r = await q.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                kills.Add(r.GetString(0));
            }
        }

        foreach (var k in kills)
        {
            await TryAsync(conn, $"ALTER SYSTEM KILL SESSION '{k}' IMMEDIATE");
        }

        for (var i = 0; i < 20 && !await TryAsync(conn, $"DROP USER {User} CASCADE"); i++)
        {
            await Task.Delay(500);
        }
    }

    private static async Task<bool> TryAsync(OracleConnection conn, string sql)
    {
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
            return true;
        }
        catch (OracleException)
        {
            return false;
        }
    }

    public static async Task AdminAsync(string sql)
    {
        await using var conn = new OracleConnection(AdminConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task TryDropAsync(string user)
    {
        await using var conn = new OracleConnection(AdminConnectionString);
        await conn.OpenAsync();
        for (var i = 0; i < 20 && !await TryAsync(conn, $"DROP USER {user} CASCADE"); i++)
        {
            await Task.Delay(500);
        }
    }

    public async Task SetupAsync(string sql)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<string> ScalarAsync(string sql)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private async Task<OracleConnection> OpenAsync()
    {
        var conn = new OracleConnection(new OracleConnectionStringBuilder
        {
            DataSource = DataSource,
            UserID = User,
            Password = Password,
            Pooling = false,
        }.ConnectionString);
        await conn.OpenAsync();
        return conn;
    }
}

public enum AppSafeMode
{
    Dev,
    Prod,
    ReadOnly,
    ProdReadOnly,
}

/// <summary>실제 앱 조립(컨테이너)과 실제 DB 세션으로 앱 기능 경로를 돌리는 장치.</summary>
internal sealed class AppSafetyRig : IDisposable
{
    private readonly SafetyAppDbFixture _db;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-sa-" + Guid.NewGuid().ToString("N"));
    private readonly IExecutionDialogs _previousDialogs = ExecutionDialogs.Current;
    private readonly Dictionary<AppSafeMode, ISession> _sessions = [];

    public AppSafetyRig(SafetyAppDbFixture db)
    {
        _db = db;
        Directory.CreateDirectory(_dir);
        ExecutionDialogs.Current = Dialogs;
        Container = AppComposition.Create(new AppCompositionOptions { DataDirectory = _dir });
        Container.ScanDeferred();
        if (Container.GetService<IExecutionPolicy>() is null)
        {
            Container.AddInstance<IExecutionPolicy>(new ExecutionPolicy());
        }

        Exec = Container.GetRequiredService<IExecutionService>();
        Language = Container.GetRequiredService<ILanguageService>();
        Unique = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
    }

    public ServiceContainer Container { get; }

    public FakeDialogs Dialogs { get; } = new();

    public IExecutionService Exec { get; }

    public ILanguageService Language { get; }

    public string Unique { get; }

    public string Table => "T_" + Unique;

    public static ConnectionProfile ProfileFor(SafetyAppDbFixture db, AppSafeMode mode) => new(
        "sa-" + mode, db.User, SafetyAppDbFixture.DataSource,
        Environment: mode is AppSafeMode.Prod or AppSafeMode.ProdReadOnly ? ConnectionEnvironment.Production : ConnectionEnvironment.Development,
        ReadOnly: mode is AppSafeMode.ReadOnly or AppSafeMode.ProdReadOnly,
        Id: Guid.NewGuid());

    public async Task<ISession> SessionAsync(AppSafeMode mode)
    {
        if (_sessions.TryGetValue(mode, out var s))
        {
            return s;
        }

        using var cred = new SecureCredential(_db.Password);
        s = await Container.GetRequiredService<ISessionManager>().OpenAsync(ProfileFor(_db, mode), cred, SessionPurpose.Work, default);
        _sessions[mode] = s;
        return s;
    }

    public async Task CloseAsync()
    {
        foreach (var s in _sessions.Values)
        {
            await s.DisposeAsync();
        }

        _sessions.Clear();
    }

    public void Dispose()
    {
        ExecutionDialogs.Current = _previousDialogs;
        Container.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 임시 폴더 정리 실패는 무시한다.
        }
    }
}

/// <summary>앱 기능 경로 교차 시험(실제 Oracle). 모드 4가지(개발·운영·읽기 전용·운영+읽기 전용)를 모든 경로에 같은 기대로 적용한다.</summary>
[Trait("Category", "Integration")]
[Collection("GridEditStatics")]
public sealed class SafetyCrossTests : IClassFixture<SafetyAppDbFixture>, IAsyncLifetime, IDisposable
{
    private readonly SafetyAppDbFixture _db;
    private AppSafetyRig _rig = null!;

    public SafetyCrossTests(SafetyAppDbFixture db) => _db = db;

    private string T => _rig.Table;

    private string Proc => "P_" + _rig.Unique;

    public static IEnumerable<object[]> Modes() => Enum.GetValues<AppSafeMode>().Select(m => new object[] { m });

    public async Task InitializeAsync()
    {
        if (!_db.Enabled)
        {
            return;
        }

        _rig = new AppSafetyRig(_db);
        await _db.SetupAsync($"CREATE TABLE {T} (id NUMBER PRIMARY KEY, v VARCHAR2(20), c CLOB)");
        await _db.SetupAsync($"INSERT INTO {T} VALUES (1, 'a', 'orig')");
        await _db.SetupAsync($"CREATE PROCEDURE {Proc} (p_id IN NUMBER DEFAULT 7) AS BEGIN INSERT INTO {T} (id, v) VALUES (p_id, 'proc'); END;");
    }

    public async Task DisposeAsync()
    {
        if (_rig is null)
        {
            return;
        }

        await _rig.CloseAsync();
    }

    public void Dispose() => _rig?.Dispose();

    private static bool Ro(AppSafeMode m) => m is AppSafeMode.ReadOnly or AppSafeMode.ProdReadOnly;

    private string Rows() => $"SELECT COUNT(*) || '/' || NVL(MAX(v), '-') FROM {T}";

    // ---- 편집기 실행 -----------------------------------------------------------------------------

    private sealed class EditorRig : IDisposable
    {
        private readonly SqlEditorHost _previousHost = SqlEditorHost.Current;

        public EditorRig(AppSafetyRig rig)
        {
            Host = new SqlEditorHost();
            SqlEditorHost.Current = Host;
            Service = ConnectionManagerCommands.Resolve(rig.Container);
            Controller = ExecutionCommands.Resolve(rig.Container);
            Handler = (ExecutionCommandHandler)rig.Container.Create(typeof(ExecutionCommandHandler));
        }

        public SqlEditorHost Host { get; }

        public IConnectionService Service { get; }

        public ExecutionController Controller { get; }

        public ExecutionCommandHandler Handler { get; }

        public async Task<ResultTabsViewModel> RunAsync(string sql)
        {
            var tab = Host.NewDocument(sql);
            tab.Select(0, 0);
            await Handler.ExecuteAsync(new CommandContext("oranda.exec.runStatement", FakeContextKeys.AllTrue), default);
            return (ResultTabsViewModel)tab.ResultPane!;
        }

        public void Dispose() => SqlEditorHost.Current = _previousHost;
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public Task Editor_run_applies_the_policy_for_every_mode(AppSafeMode mode) => Ui.Run(async () =>
    {
        if (!_db.Enabled)
        {
            return;
        }

        using var editor = new EditorRig(_rig);
        var outcome = await editor.Service.ConnectAsync(AppSafetyRig.ProfileFor(_db, mode), _db.Password, default);
        Assert.True(outcome.IsConnected, outcome.Message);
        await HeadlessHost.WaitUntil(() => editor.Controller.State.HasSession);
        try
        {
            var before = await _db.ScalarAsync(Rows());
            _rig.Dialogs.ConfirmAnswer = false;

            var first = await editor.RunAsync($"UPDATE {T} SET v = 'e' WHERE id = 1");

            var item = Assert.Single(first.Tabs);
            switch (mode)
            {
                case AppSafeMode.ReadOnly or AppSafeMode.ProdReadOnly:
                    Assert.Equal("거부됨", item.Title);
                    Assert.Contains("읽기 전용", item.Message, StringComparison.Ordinal);
                    Assert.Empty(_rig.Dialogs.Confirms);
                    break;
                case AppSafeMode.Prod:
                    Assert.Equal(PolicyRule.ProductionChange, Assert.Single(_rig.Dialogs.Confirms).Decision.Rule);
                    Assert.Equal("취소되었습니다.", item.Message);
                    Assert.False(editor.Controller.State.TxnPending);
                    break;
                default:
                    Assert.Empty(_rig.Dialogs.Confirms);
                    Assert.True(editor.Controller.State.TxnPending);
                    await editor.Handler.ExecuteAsync(new CommandContext("oranda.txn.rollback", FakeContextKeys.AllTrue), default); // 되돌림
                    Assert.False(editor.Controller.State.TxnPending);
                    break;
            }

            Assert.Equal(before, await _db.ScalarAsync(Rows()));

            if (mode == AppSafeMode.Prod)
            {
                _rig.Dialogs.ConfirmAnswer = true;
                await editor.RunAsync($"UPDATE {T} SET v = 'e' WHERE id = 1");
                await HeadlessHost.WaitUntil(() => editor.Controller.State.TxnPending);
                await editor.Handler.ExecuteAsync(new CommandContext("oranda.txn.commit", FakeContextKeys.AllTrue), default);
                Assert.Equal("1/e", await _db.ScalarAsync(Rows()));
            }
        }
        finally
        {
            await editor.Service.DisconnectAsync(default);
        }
    });

    // ---- 그리드 편집 ------------------------------------------------------------------------------

    private async Task<GridEditSession> OpenGridAsync(ISession session)
    {
        var columns = new[]
        {
            new ResultColumn(0, "ID", "NUMBER", ColumnDataKind.Number),
            new ResultColumn(1, "V", "VARCHAR2", ColumnDataKind.Text),
        };
        object?[][] data = [[1m, "a"]];
        return await GridEditSession.OpenAsync(
            _rig.Container.GetRequiredService<IRowEditor>(), _rig.Container.GetRequiredService<IMetadataProvider>(), session,
            new SqlStatement($"SELECT id, v FROM {T}", default, StatementKind.Query) { Verb = "SELECT" },
            columns, (r, c) => data[r][c], () => data.Length, new DbObjectRef(_db.User, T, "TABLE"), default);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public Task Grid_edit_session_applies_the_policy_for_every_mode(AppSafeMode mode) => Ui.Run(async () =>
    {
        if (!_db.Enabled)
        {
            return;
        }

        var session = await _rig.SessionAsync(mode);
        var grid = await OpenGridAsync(session);
        var before = await _db.ScalarAsync(Rows());

        if (Ro(mode))
        {
            Assert.False(grid.CanEdit);
            Assert.Equal(EditBlockReason.ReadOnlyProfile, grid.Capability.Reason);
            Assert.False(grid.SetCell(0, 1, "x")); // 셀 값을 못 바꾼다
            Assert.Empty(grid.BuildChanges());
            Assert.Equal(before, await _db.ScalarAsync(Rows()));
            return;
        }

        Assert.True(grid.CanEdit);
        Assert.True(grid.SetCell(0, 1, "g"));
        _rig.Dialogs.ConfirmAnswer = false;
        var declined = await grid.ApplyAsync(commit: true, default);

        if (mode == AppSafeMode.Prod)
        {
            Assert.Equal(PolicyRule.ProductionChange, Assert.Single(_rig.Dialogs.Confirms).Decision.Rule);
            Assert.False(declined.Committed);
            Assert.NotEmpty(declined.Failures); // 되돌림: 거절하면 반영하지 않고 실패로 남는다
            await session.RollbackAsync(default);
            Assert.Equal(before, await _db.ScalarAsync(Rows()));
            _rig.Dialogs.ConfirmAnswer = true;
            var approved = await grid.ApplyAsync(commit: true, default);
            Assert.True(approved.Committed);
        }
        else
        {
            Assert.Empty(_rig.Dialogs.Confirms);
            Assert.True(declined.Committed);
        }

        Assert.Equal("g", await _db.ScalarAsync($"SELECT v FROM {T} WHERE id = 1"));
    });

    // ---- LOB 저장 ---------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task Lob_save_applies_the_policy_for_every_mode(AppSafeMode mode)
    {
        if (!_db.Enabled)
        {
            return;
        }

        var session = await _rig.SessionAsync(mode);
        var writer = new LobWriter(_rig.Exec);
        var target = new LobSaveTarget(new DbObjectRef(_db.User, T, "TABLE"), "C", new Dictionary<string, object?> { ["ID"] = 1m });
        var lobValue = "새 내용 é中文 'quote' ;--";
        _rig.Dialogs.ConfirmAnswer = false;

        var first = await writer.WriteAsync(session, target, new BindValue(BindDataType.Clob, lobValue), commit: true, default);

        if (Ro(mode))
        {
            Assert.False(first.Success);
            Assert.Contains("읽기 전용", first.Message, StringComparison.Ordinal);
            Assert.Empty(_rig.Dialogs.Confirms);
            Assert.Equal("orig", await _db.ScalarAsync($"SELECT c FROM {T} WHERE id = 1"));
            return;
        }

        if (mode == AppSafeMode.Prod)
        {
            Assert.False(first.Success);
            Assert.Equal(PolicyRule.ProductionChange, Assert.Single(_rig.Dialogs.Confirms).Decision.Rule);
            Assert.Equal("orig", await _db.ScalarAsync($"SELECT c FROM {T} WHERE id = 1")); // 되돌림: 거절하면 바뀌지 않는다
            await session.RollbackAsync(default); // SAVEPOINT 가 남긴 미커밋 표시는 롤백으로 끝낸다(DEF-04)
            Assert.False(session.HasPendingTransaction);
            _rig.Dialogs.ConfirmAnswer = true;
            first = await writer.WriteAsync(session, target, new BindValue(BindDataType.Clob, lobValue), commit: true, default);
        }

        Assert.True(first.Success, first.Message);
        Assert.True(first.Committed);
        Assert.Equal(lobValue, await _db.ScalarAsync($"SELECT c FROM {T} WHERE id = 1"));
    }

    [Fact]
    public async Task Lob_save_with_a_missing_key_row_changes_nothing_in_every_environment()
    {
        if (!_db.Enabled)
        {
            return;
        }

        var session = await _rig.SessionAsync(AppSafeMode.Dev);
        var target = new LobSaveTarget(new DbObjectRef(_db.User, T, "TABLE"), "C", new Dictionary<string, object?> { ["ID"] = 999m });

        var r = await new LobWriter(_rig.Exec).WriteAsync(session, target, new BindValue(BindDataType.Clob, "x"), commit: true, default);

        Assert.False(r.Success);
        Assert.Equal("orig", await _db.ScalarAsync($"SELECT c FROM {T} WHERE id = 1"));
        await session.RollbackAsync(default); // SAVEPOINT 의 미커밋 표시(DEF-04)를 끝낸다
        Assert.False(session.HasPendingTransaction);
    }

    // ---- 스키마 관리(Purge·Flashback·컴파일) --------------------------------------------------------------

    private ObjectSearchViewModel SearchVm(ISession session) => new(_rig.Container.GetRequiredService<ISchemaAdminProvider>(), _rig.Exec)
    {
        Session = session,
    };

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task Schema_purge_and_flashback_apply_the_policy_for_every_mode(AppSafeMode mode)
    {
        if (!_db.Enabled)
        {
            return;
        }

        await _db.SetupAsync($"CREATE TABLE {T}_B1 (x NUMBER)");
        await _db.SetupAsync($"DROP TABLE {T}_B1");
        await _db.SetupAsync($"CREATE TABLE {T}_B2 (x NUMBER)");
        await _db.SetupAsync($"DROP TABLE {T}_B2");
        var session = await _rig.SessionAsync(mode);
        var vm = SearchVm(session);
        await vm.LoadRecycleBinAsync();
        var purgeItem = vm.RecycleBin.Single(r => r.Item.OriginalName == T + "_B1").Item;
        var flashItem = vm.RecycleBin.Single(r => r.Item.OriginalName == T + "_B2").Item;
        var bin = (string name) => $"SELECT COUNT(*) FROM user_recyclebin WHERE original_name = '{name}'";
        _rig.Dialogs.ConfirmAnswer = false;

        await vm.PurgeAsync(purgeItem);
        await vm.FlashbackAsync(flashItem);

        if (Ro(mode))
        {
            Assert.False(vm.CanChange);
            Assert.Empty(_rig.Dialogs.Confirms);
            Assert.Equal("1", await _db.ScalarAsync(bin(T + "_B1")));
            Assert.Equal("1", await _db.ScalarAsync(bin(T + "_B2")));
            return;
        }

        // PURGE 는 되돌릴 수 없는 위험 명령이라 개발 접속에서도 묻는다. FLASHBACK 은 운영 라벨에서만 묻는다.
        var expected = mode == AppSafeMode.Prod ? 2 : 1;
        Assert.Equal(expected, _rig.Dialogs.Confirms.Count);
        Assert.Equal(mode == AppSafeMode.Prod ? PolicyRule.ProductionChange : PolicyRule.DangerousCommand, _rig.Dialogs.Confirms[0].Decision.Rule);
        Assert.Equal("1", await _db.ScalarAsync(bin(T + "_B1"))); // 거절한 Purge 는 안 일어났다
        var flashed = mode == AppSafeMode.Prod ? "0" : "1"; // 개발 접속의 되살리기는 묻지 않고 실행된다
        Assert.Equal(flashed, await _db.ScalarAsync($"SELECT COUNT(*) FROM user_tables WHERE table_name = '{T}_B2'"));

        _rig.Dialogs.ConfirmAnswer = true;
        await vm.PurgeAsync(purgeItem);
        await vm.FlashbackAsync(flashItem);
        Assert.Equal("0", await _db.ScalarAsync(bin(T + "_B1")));
        Assert.Equal("1", await _db.ScalarAsync($"SELECT COUNT(*) FROM user_tables WHERE table_name = '{T}_B2'"));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task Schema_compile_invalid_applies_the_policy_for_every_mode(AppSafeMode mode)
    {
        if (!_db.Enabled)
        {
            return;
        }

        var dep = T + "_D";
        await _db.SetupAsync($"CREATE TABLE {dep} (x NUMBER)");
        await _db.SetupAsync($"CREATE PROCEDURE {Proc}_I AS n NUMBER; BEGIN SELECT COUNT(*) INTO n FROM {dep}; END;");
        await _db.SetupAsync($"DROP TABLE {dep} PURGE");
        await _db.SetupAsync($"CREATE TABLE {dep} (x NUMBER)");
        Assert.Equal("INVALID", await _db.ScalarAsync($"SELECT status FROM user_objects WHERE object_name = '{Proc}_I'"));
        var session = await _rig.SessionAsync(mode);
        var vm = SearchVm(session);
        vm.Owner = _db.User;
        _rig.Dialogs.ConfirmAnswer = false;

        await vm.CompileInvalidAsync();

        if (Ro(mode))
        {
            Assert.False(vm.CanChange);
            Assert.Empty(_rig.Dialogs.Confirms);
            Assert.Equal("INVALID", await _db.ScalarAsync($"SELECT status FROM user_objects WHERE object_name = '{Proc}_I'"));
            return;
        }

        if (mode == AppSafeMode.Prod)
        {
            Assert.NotEmpty(_rig.Dialogs.Confirms);
            Assert.Equal(PolicyRule.ProductionChange, _rig.Dialogs.Confirms[0].Decision.Rule);
            Assert.Equal("INVALID", await _db.ScalarAsync($"SELECT status FROM user_objects WHERE object_name = '{Proc}_I'"));
            _rig.Dialogs.ConfirmAnswer = true;
            await vm.CompileInvalidAsync();
        }
        else
        {
            Assert.Empty(_rig.Dialogs.Confirms);
        }

        Assert.Equal("VALID", await _db.ScalarAsync($"SELECT status FROM user_objects WHERE object_name = '{Proc}_I'"));
    }

    // ---- PL/SQL 소스 편집·컴파일 ---------------------------------------------------------------------------

    private sealed class FixedSessionServices(IServiceProvider inner, ISession session) : IServiceProvider
    {
        private sealed class Manager(ISession session) : ISessionManager
        {
            public IReadOnlyList<ISession> Sessions { get; } = [session];

            public Task<ISession> OpenAsync(ConnectionProfile profile, SecureCredential credential, SessionPurpose purpose, CancellationToken ct) =>
                throw new NotSupportedException();
        }

        public object? GetService(Type serviceType) => serviceType == typeof(ISessionManager) ? new Manager(session) : inner.GetService(serviceType);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public Task Plsql_source_compile_applies_the_policy_for_every_mode(AppSafeMode mode) => Ui.Run(async () =>
    {
        if (!_db.Enabled)
        {
            return;
        }

        var name = "PC_" + _rig.Unique;
        var session = await _rig.SessionAsync(mode);
        var vm = new PlsqlEditorViewModel(new FixedSessionServices(_rig.Container, session));
        vm.Editor.LoadText($"CREATE OR REPLACE PROCEDURE {name} AS BEGIN NULL; END;", dirty: true);
        var exists = $"SELECT COUNT(*) FROM user_objects WHERE object_name = '{name}'";
        _rig.Dialogs.ConfirmAnswer = false;

        var first = await vm.CompileAsync(force: true);

        if (Ro(mode))
        {
            Assert.Equal(PlsqlCompileStatus.Blocked, first.Status);
            Assert.Empty(_rig.Dialogs.Confirms);
            Assert.Equal("0", await _db.ScalarAsync(exists));
            return;
        }

        if (mode == AppSafeMode.Prod)
        {
            Assert.NotEqual(PlsqlCompileStatus.Compiled, first.Status);
            Assert.Equal(PolicyRule.ProductionChange, Assert.Single(_rig.Dialogs.Confirms).Decision.Rule);
            Assert.Equal("0", await _db.ScalarAsync(exists));
            _rig.Dialogs.ConfirmAnswer = true;
            first = await vm.CompileAsync(force: true);
        }

        Assert.Equal(PlsqlCompileStatus.Compiled, first.Status);
        Assert.Equal("1", await _db.ScalarAsync(exists));
    });

    // ---- PL/SQL 실행 대화상자 ------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Modes))]
    public Task Plsql_run_dialog_applies_the_policy_for_every_mode(AppSafeMode mode) => Ui.Run(async () =>
    {
        if (!_db.Enabled)
        {
            return;
        }

        var session = await _rig.SessionAsync(mode);
        var vm = new PlsqlRunViewModel(new PlsqlRunDeps
        {
            Session = () => session,
            Metadata = () => _rig.Container.GetRequiredService<IMetadataProvider>(),
            Execution = () => _rig.Exec,
        });
        await vm.OpenAsync(new DbObjectRef(_db.User, Proc, "PROCEDURE"));
        Assert.True(vm.CanRun, vm.ProblemText);
        var rows = $"SELECT COUNT(*) FROM {T} WHERE id = 7";
        _rig.Dialogs.ConfirmAnswer = false;

        var ok = await vm.RunAsync();

        if (Ro(mode))
        {
            Assert.False(ok);
            Assert.True(vm.StatusIsError);
            Assert.Empty(_rig.Dialogs.Confirms);
            Assert.Equal("0", await _db.ScalarAsync(rows));
            return;
        }

        if (mode == AppSafeMode.Prod)
        {
            Assert.False(ok);
            Assert.Equal(PolicyRule.ProductionChange, Assert.Single(_rig.Dialogs.Confirms).Decision.Rule);
            Assert.False(session.HasPendingTransaction); // 되돌림
            Assert.Equal("0", await _db.ScalarAsync(rows));
            _rig.Dialogs.ConfirmAnswer = true;
            ok = await vm.RunAsync();
        }

        Assert.True(ok, vm.StatusText);
        await session.CommitAsync(default);
        Assert.Equal("1", await _db.ScalarAsync(rows));
    });

    // ---- 확장 패널 -----------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Modes))]
    public Task Extension_panel_run_applies_the_policy_for_every_mode(AppSafeMode mode) => Ui.Run(async () =>
    {
        if (!_db.Enabled)
        {
            return;
        }

        var session = await _rig.SessionAsync(mode);
        var host = new SqlEditorHost();
        var tab = host.NewDocument($"DELETE FROM {T} WHERE id = 1");
        tab.Select(0, 0);
        var vm = new ExtensionTestViewModel
        {
            Editor = new EditorContext { Host = host },
            Execution = _rig.Exec,
            SessionResolver = _ => session,
        };
        _rig.Dialogs.ConfirmAnswer = false;

        var result = await vm.RunCurrentStatementAsync(default);

        Assert.NotNull(result);
        if (Ro(mode))
        {
            Assert.Equal(ExecutionStatus.Denied, result.Status);
            Assert.Equal(PolicyRule.ReadOnlyProfile, result.Policy?.Rule);
            Assert.Empty(_rig.Dialogs.Confirms);
        }
        else if (mode == AppSafeMode.Prod)
        {
            Assert.Equal(ExecutionStatus.Cancelled, result.Status);
            Assert.Equal(PolicyRule.ProductionChange, Assert.Single(_rig.Dialogs.Confirms).Decision.Rule);
        }
        else
        {
            Assert.Equal(ExecutionStatus.Succeeded, result.Status);
            Assert.Empty(_rig.Dialogs.Confirms);
            await session.RollbackAsync(default);
        }

        Assert.Equal("1/a", await _db.ScalarAsync(Rows()));
    });

    // ---- 이력 다시 실행 --------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task History_rerun_applies_the_policy_for_every_mode(AppSafeMode mode)
    {
        if (!_db.Enabled)
        {
            return;
        }

        var session = await _rig.SessionAsync(mode);
        var sql = $"UPDATE {T} SET v = 'h' WHERE id = 1";
        _rig.Dialogs.ConfirmAnswer = false;

        var ok = await HistoryScriptsCommands.RerunAsync(_rig.Container, sql, session.Profile.Id, default);

        if (Ro(mode))
        {
            Assert.False(ok);
            Assert.Empty(_rig.Dialogs.Confirms);
            Assert.Equal("1/a", await _db.ScalarAsync(Rows()));
            return;
        }

        if (mode == AppSafeMode.Prod)
        {
            Assert.False(ok);
            Assert.Equal(PolicyRule.ProductionChange, Assert.Single(_rig.Dialogs.Confirms).Decision.Rule);
            Assert.Equal("1/a", await _db.ScalarAsync(Rows()));
            _rig.Dialogs.ConfirmAnswer = true;
            ok = await HistoryScriptsCommands.RerunAsync(_rig.Container, sql, session.Profile.Id, default);
        }

        Assert.True(ok);
        await session.CommitAsync(default);
        Assert.Equal("1/h", await _db.ScalarAsync(Rows()));
    }

    [Fact]
    public async Task History_rerun_of_a_multi_statement_text_stops_at_the_first_refusal_on_a_read_only_profile()
    {
        if (!_db.Enabled)
        {
            return;
        }

        var session = await _rig.SessionAsync(AppSafeMode.ReadOnly);

        var ok = await HistoryScriptsCommands.RerunAsync(_rig.Container,
            $"SELECT 1 FROM dual;\nDELETE FROM {T} WHERE id = 1;\nDROP TABLE {T}", session.Profile.Id, default);

        Assert.False(ok);
        Assert.Equal("1/a", await _db.ScalarAsync(Rows()));
        Assert.Equal("1", await _db.ScalarAsync($"SELECT COUNT(*) FROM user_tables WHERE table_name = '{T}'"));
    }

    // ---- 평문 검색(실제 접속) --------------------------------------------------------------------------------

    [Fact]
    public async Task Connecting_running_secret_statements_and_failing_leaves_no_plaintext_password_anywhere()
    {
        if (!_db.Enabled)
        {
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "oranda-sp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var scratchUser = "ORANDA_SP_" + NewSuffix();
        var scratchPassword = "Zq" + Guid.NewGuid().ToString("N")[..16] + "#5";
        await SafetyAppDbFixture.AdminAsync($"CREATE USER {scratchUser} IDENTIFIED BY \"{scratchPassword}\"");
        await SafetyAppDbFixture.AdminAsync($"GRANT CREATE SESSION TO {scratchUser}");
        var newPassword = "Zq" + Guid.NewGuid().ToString("N")[..16] + "#8";
        var wrongPassword = "Zq" + Guid.NewGuid().ToString("N")[..16] + "#6";
        try
        {
            using (var container = AppComposition.Create(new AppCompositionOptions { DataDirectory = dir }))
            {
                container.ScanDeferred();
                container.AddInstance<IExecutionPolicy>(new ExecutionPolicy());
                var saved = ExecutionDialogs.Current;
                ExecutionDialogs.Current = new FakeDialogs();
                try
                {
                    var persist = OperatingSystem.IsWindows(); // macOS 키체인은 시험 자리에서 쓰지 않는다
                    // 이력 기록기(시작 작업)를 먼저 켠다. 접속·실행 이벤트를 놓치지 않게.
                    var recorders = (container.GetService<IEnumerable<IStartupTask>>() ?? []).Where(t => t.GetType().Name == "HistoryRecorder").ToList();
                    Assert.NotEmpty(recorders);
                    foreach (var task in recorders)
                    {
                        await task.RunAsync(default);
                    }

                    var service = ConnectionManagerCommands.Resolve(container);
                    var profile = await service.SaveProfileAsync(AppSafetyRig.ProfileFor(_db, AppSafeMode.Dev) with { User = scratchUser }, scratchPassword, savePassword: persist, default);

                    // 틀린 비밀번호로 접속 실패: 사용자에게 보이는 메시지에 두 비밀번호가 없어야 한다.
                    var failed = await service.ConnectAsync(profile, wrongPassword, default);
                    Assert.False(failed.IsConnected);
                    Assert.DoesNotContain(wrongPassword, failed.Message, StringComparison.Ordinal);
                    Assert.DoesNotContain(scratchPassword, failed.Message, StringComparison.Ordinal);

                    var ok = await service.ConnectAsync(profile, persist ? null : scratchPassword, default); // Windows 는 저장된 비밀번호로 접속
                    Assert.True(ok.IsConnected, ok.Message);
                    var session = service.ActiveSession!;
                    var exec = container.GetRequiredService<IExecutionService>();
                    var language = container.GetRequiredService<ILanguageService>();
                    foreach (var text in new[]
                    {
                        $"ALTER USER {scratchUser} IDENTIFIED BY \"{newPassword}\" REPLACE \"{scratchPassword}\"",
                        $"CONNECT {scratchUser}/\"{newPassword}\"",
                        $"CREATE USER ZZ_{NewSuffix()} IDENTIFIED BY \"{wrongPassword}\"",
                        $"ALTER USER {scratchUser} IDENTIFIED BY {wrongPassword.Replace("#", "", StringComparison.Ordinal)}",
                    })
                    {
                        foreach (var stmt in language.Split(text, SplitOptions.Default))
                        {
                            var r = await exec.ExecuteAsync(session, stmt, new Dictionary<string, BindValue>(), new ExecutionOptions { Source = HistorySource.Editor }, default);
                            if (r.ResultSet is not null)
                            {
                                await r.ResultSet.DisposeAsync();
                            }
                        }
                    }

                    // 이력이 쓰이길 기다린다(기록은 비동기 줄에서 한다).
                    foreach (var task in recorders)
                    {
                        var drain = task.GetType().GetMethod("DrainAsync", BindingFlags.Instance | BindingFlags.NonPublic);
                        await (Task)drain!.Invoke(task, null)!;
                    }

                    var rows = await container.GetRequiredService<IHistoryStore>().SearchAsync(new HistoryQuery(), default);
                    Assert.True(rows.Count >= 3, "이력에 기록이 있어야 검색이 의미가 있다");
                    Assert.Contains(rows, r => r.SqlText.Contains("IDENTIFIED BY ***", StringComparison.OrdinalIgnoreCase));
                    Assert.DoesNotContain(rows, r => r.SqlText.Contains(scratchPassword, StringComparison.Ordinal) || r.SqlText.Contains(newPassword, StringComparison.Ordinal));
                    Assert.DoesNotContain(rows, r => (r.ErrorMessage ?? string.Empty).Contains(scratchPassword, StringComparison.Ordinal));

                    await service.DisconnectAsync(default);
                }
                finally
                {
                    ExecutionDialogs.Current = saved;
                }
            }

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Assert.Empty(SafetyCrossNoDbTests.Scan(dir, scratchPassword, newPassword, wrongPassword, wrongPassword.Replace("#", string.Empty, StringComparison.Ordinal)));
        }
        finally
        {
            await SafetyAppDbFixture.TryDropAsync(scratchUser);
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // 임시 폴더 정리 실패는 무시한다.
            }
        }
    }

    private static string NewSuffix() => Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
}
