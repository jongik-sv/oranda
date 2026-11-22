using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Sessions;
using Oranda.Core.Storage;
using Oranda.Platform.Ai.Bridge;
using Oranda.Platform.Storage;
using Oranda.Testing;

namespace Oranda.App.Tests.Features.Ai.AgentCli;

/// <summary>
/// X1 Unix 실측: 앱과 같은 조립(<see cref="AppDataPaths"/> 를 받는 공개 생성자 = 기본 소켓 이름 규칙)으로 다리를 열고 실제 <c>orandactl</c> 이 붙는가.
/// 긴 <c>ORANDA_DATA_DIR</c> 이면 소켓이 임시 폴더의 짧은 경로로 가고, 연결 파일이 그 경로를 알려 준다.
/// umask 가 달라도 연결 파일 0600·폴더 0700 이다. Windows 에서는 할 일이 없어 그냥 통과한다.
/// </summary>
[Collection(AgentCliSerial.Name)] // umask 는 프로세스 전체 값이라 다른 시험과 겹치지 않게 차례로 돈다.
public sealed class UnixAgentCliTests : IDisposable
{
    private const UnixFileMode OwnerRw = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OwnerRwx = OwnerRw | UnixFileMode.UserExecute;

    // macOS 의 TMPDIR(/var/folders/…/T/) 아래 GUID 폴더는 그것만으로 run/agent.sock 이 103바이트를 넘어 「짧은 데이터 폴더」 가 되지 않으므로 /tmp 아래에 둔다
    // (절대 경로 접두는 Path.Combine 이 임시 폴더 대신 그대로 쓴다).
    private readonly TempDirectory _root = OperatingSystem.IsMacOS() ? new("/tmp/ox1-") : new("oranda-x1-");

    public void Dispose() => _root.Dispose();

    [Fact]
    public async Task Long_data_directory_moves_the_socket_to_the_short_path_and_orandactl_still_connects()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // 데이터 폴더 아래 run/agent.sock 이 107바이트를 넘게 만든다.
        var data = Path.Combine(_root.Path, new string('d', 60), new string('e', 60));
        Directory.CreateDirectory(data);
        Assert.True(Encoding.UTF8.GetByteCount(AgentPipeName.Unix(data)) > AgentSocketPath.LimitFor(OperatingSystem.IsMacOS()));

        await using var app = new UnixApp(data);

        var socket = app.PipeName;
        Assert.True(IsFallback(socket), socket); // 대체 경로($XDG_RUNTIME_DIR/oranda 또는 <tmp>/oranda-<uid>[-무작위])
        Assert.True(File.Exists(socket), socket);
        Assert.Equal(OwnerRwx, File.GetUnixFileMode(Path.GetDirectoryName(socket)!));

        var endpoint = AgentEndpointFile.TryParse(await File.ReadAllTextAsync(AppDataLocation.EndpointFile(data, Environment.ProcessId)));
        Assert.NotNull(endpoint);
        Assert.Equal(AgentEndpoint.UnixKind, endpoint.Endpoint.Kind);
        Assert.Equal(socket, endpoint.Endpoint.Name);

        var status = await RunOrandactlAsync(data, "status");
        Assert.True(status.ExitCode == 0, status.ToString());
        Assert.Equal("x1", (string?)status.Json["label"]);

        var tabs = await RunOrandactlAsync(data, "tabs");
        Assert.True(tabs.ExitCode == 0, tabs.ToString());
        Assert.Equal("주문", (string?)tabs.Json["tabs"]![0]!["title"]);
    }

    [Fact]
    public async Task Short_data_directory_keeps_the_socket_under_run()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var data = Path.Combine(_root.Path, "d");
        await using var app = new UnixApp(data);

        Assert.StartsWith(Path.Combine(data, "run") + Path.DirectorySeparatorChar, app.PipeName);
        var status = await RunOrandactlAsync(data, "status");
        Assert.True(status.ExitCode == 0, status.ToString());
    }

    [Theory]
    [InlineData(0b000_000_000)] // 000: 아무것도 빼지 않는 umask
    [InlineData(0b000_010_010)] // 022: 흔한 기본
    [InlineData(0b000_000_010)] // 002: 사용자 개인 그룹 배포판 기본
    [InlineData(0b000_111_111)] // 077: 엄격
    public async Task Endpoint_file_is_0600_and_folders_0700_under_any_umask(int mask)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var data = Path.Combine(_root.Path, "u" + Convert.ToString(mask, 8));
        var previous = Umask((uint)mask);
        try
        {
            await using var app = new UnixApp(data);

            var file = AppDataLocation.EndpointFile(data, Environment.ProcessId);
            Assert.Equal(OwnerRw, File.GetUnixFileMode(file));
            Assert.Equal(OwnerRwx, File.GetUnixFileMode(Path.GetDirectoryName(file)!)); // run/agents
            Assert.Equal(OwnerRwx, File.GetUnixFileMode(Path.GetDirectoryName(app.PipeName)!)); // run (소켓 폴더)

            // 기한 연장(파일 다시 쓰기)도 같은 모드를 지킨다.
            app.Grants.Extend(app.Issue.Grant.GrantId, TimeSpan.FromMinutes(5));
            Assert.Equal(OwnerRw, File.GetUnixFileMode(file));
        }
        finally
        {
            _ = Umask(previous);
        }
    }

    [Fact]
    public async Task Revoking_removes_the_file_and_orandactl_reports_no_endpoint()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var data = Path.Combine(_root.Path, "r");
        await using var app = new UnixApp(data);
        app.Grants.Revoke(app.Issue.Grant.GrantId);

        var after = await RunOrandactlAsync(data, "tabs");

        Assert.Equal(AgentCliExit.Unreachable, after.ExitCode);
        Assert.Equal(AgentErrorCodes.NoEndpoint, (string?)after.Json["code"]);
    }

    /// <summary>A4 의 대체 소켓 폴더 중 하나인가.</summary>
    private static bool IsFallback(string socket)
    {
        var folder = Path.GetDirectoryName(socket)!;
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        return (runtime is { Length: > 0 } && folder == Path.Combine(runtime, "oranda"))
            || Path.GetFileName(folder).StartsWith("oranda-", StringComparison.Ordinal) && Path.GetDirectoryName(folder) == Path.TrimEndingDirectorySeparator(Path.GetTempPath());
    }

    [DllImport("libc", EntryPoint = "umask")]
    private static extern uint Umask(uint mask);

    private static async Task<CliRun> RunOrandactlAsync(string dataDirectory, params string[] args)
    {
        var start = new ProcessStartInfo(AgentCliRig.FindOrandactl())
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        start.Environment["ORANDA_DATA_DIR"] = dataDirectory;
        start.Environment["ORANDA_CLIENT"] = "x1-test";
        start.Environment.Remove("ORANDA_RUN_TOKEN");
        start.Environment.Remove("ORANDA_INSTANCE");
        start.Environment.Remove("CLAUDECODE");

        using var process = Process.Start(start) ?? throw new InvalidOperationException("orandactl 을 띄우지 못했습니다.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("orandactl 이 끝나지 않았습니다: " + string.Join(' ', args));
        }

        return new CliRun(process.ExitCode, await stdout, await stderr);
    }

    /// <summary>앱과 같은 공개 생성자로 연 다리 + 허가 하나(편집기 도구는 가짜 둘).</summary>
    private sealed class UnixApp : IAsyncDisposable
    {
        private readonly AgentBridgeHost _host;

        public UnixApp(string dataDirectory)
        {
            Directory.CreateDirectory(dataDirectory);
            var profile = new ConnectionProfile("dev-erp", "scott", "h/s", Id: Guid.NewGuid(), AiAccess: AiAccessLevel.Metadata);
            Grants = new AgentGrantStore();
            var gate = new AgentGate(new NoSessions(), new NoExtractor(), [], new MemoryAudit(), grants: Grants)
            {
                AppHandlers =
                [
                    new FixedHandler(AppToolNames.GetStatus, inv => new JsonObject { ["label"] = inv.Grant.Label }),
                    new FixedHandler(AppToolNames.ListTabs, _ => new JsonObject { ["tabs"] = new JsonArray(new JsonObject { ["tab_id"] = "t1", ["title"] = "주문" }) }),
                ],
            };
            _host = new AgentBridgeHost(gate, new AgentRunTokenStore(), new AppDataPaths(dataDirectory), Grants, gate);
            PipeName = _host.Start();
            Issue = Grants.Create(new AgentGrantRequest(
                [new AgentGrantTarget(profile, AgentGrantPolicy.DefaultCap(profile.Environment))],
                AgentGrantPolicy.DefaultLifetime,
                Label: "x1"));
        }

        public string PipeName { get; }

        public AgentGrantStore Grants { get; }

        public AgentGrantIssue Issue { get; }

        public async ValueTask DisposeAsync()
        {
            await _host.DisposeAsync();
            Grants.Dispose();
            if (IsFallback(PipeName) && File.Exists(PipeName))
            {
                File.Delete(PipeName); // 대체 경로의 소켓 파일만 지운다(폴더는 다른 실행과 같이 쓴다).
            }
        }
    }

    private sealed class FixedHandler(string toolName, Func<AppToolInvocation, JsonNode> respond) : IAppToolHandler
    {
        public string ToolName { get; } = toolName;

        public ValueTask<AgentToolOutput> ExecuteAsync(AppToolInvocation invocation, CancellationToken ct) =>
            ValueTask.FromResult(AgentToolOutput.Ok(respond(invocation)));
    }

    private sealed class NoSessions : IAgentSessionSource
    {
        public AgentSession? Find(string runId) => null;

        public ValueTask<ISession> AcquireDbSessionAsync(AgentSession session, CancellationToken ct) =>
            throw new InvalidOperationException("DB 도구는 이 시험에서 부르지 않는다.");
    }

    private sealed class NoExtractor : IStatementReferenceExtractor
    {
        public ValueTask<AgentStatementInfo> AnalyzeAsync(ISession db, string sql, string? defaultSchema, CancellationToken ct) =>
            ValueTask.FromResult(new AgentStatementInfo(StatementKind.Query, false, [], 1));

        public ValueTask<AgentObjectRef?> ResolveAsync(ISession db, string name, string? defaultSchema, CancellationToken ct) =>
            ValueTask.FromResult<AgentObjectRef?>(null);

        public ValueTask<IReadOnlyList<AgentObjectRef>> GetRelatedAsync(ISession db, IReadOnlyList<AgentObjectRef> objects, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<AgentObjectRef>>([]);
    }
}
