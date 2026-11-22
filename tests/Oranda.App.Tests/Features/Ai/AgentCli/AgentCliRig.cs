using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Sessions;
using Oranda.Platform.Ai.Bridge;
using Oranda.Testing;

namespace Oranda.App.Tests.Features.Ai.AgentCli;

/// <summary>
/// 끝 왕복 시험대(A5): 실제 <see cref="AgentBridgeHost"/> · 실제 <see cref="AgentGate"/> · 실제 <see cref="AgentGrantStore"/> 에 처리자를 꽂고,
/// 빌드된 실제 <c>orandactl</c> 프로세스를 임시 데이터 폴더(<c>ORANDA_DATA_DIR</c>)로 띄워 연결 파일로 붙게 한다.
/// 처리자는 넣는 대로 쓴다 — 지금은 가짜, C·D·E 처리자가 머지되면 그 처리자를 넣어 같은 왕복을 돌린다.
/// DB 는 쓰지 않는다(DB 세션은 가짜, 이름 풀기는 <c>OWNER.NAME</c> 을 그대로 돌려준다).
/// </summary>
internal sealed class AgentCliRig : IAsyncDisposable
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(60);

    private readonly TempDirectory _data = new("oranda-a5-");
    private readonly AgentBridgeHost _host;

    public AgentCliRig(
        IEnumerable<IAppToolHandler> appHandlers,
        IEnumerable<IAgentToolHandler>? dbHandlers = null,
        IReadOnlyList<ConnectionProfile>? profiles = null,
        string label = "A5 rig")
    {
        Directory.CreateDirectory(_data);
        Profiles = profiles ?? [new ConnectionProfile("dev-erp", "scott", "h/s", Id: Guid.NewGuid(), AiAccess: AiAccessLevel.QueryRows)];
        Grants = new AgentGrantStore();
        Gate = new AgentGate(new ProfileSessions(), new NameEchoExtractor(), dbHandlers ?? [], Audit, grants: Grants)
        {
            AppHandlers = appHandlers,
        };
        var unique = Guid.NewGuid().ToString("N")[..10];
        _host = new AgentBridgeHost(Gate, new AgentRunTokenStore(), (_, i) => $"oranda-a5-{unique}" + (i is null ? string.Empty : "-" + i), Grants, Gate, _data);
        PipeName = _host.Start();
        Issue = Grants.Create(new AgentGrantRequest(
            [.. Profiles.Select(p => new AgentGrantTarget(p, AgentGrantPolicy.DefaultCap(p.Environment)))],
            AgentGrantPolicy.DefaultLifetime,
            Label: label));
    }

    public IReadOnlyList<ConnectionProfile> Profiles { get; }

    public AgentGrantStore Grants { get; }

    public AgentGate Gate { get; }

    public MemoryAudit Audit { get; } = new();

    public AgentGrantIssue Issue { get; }

    public string PipeName { get; }

    public string DataDirectory => _data.Path;

    /// <summary><c>orandactl</c> 을 한 번 띄운다. 표준 입력은 <paramref name="stdin"/>(없으면 빈 입력).</summary>
    public async Task<CliRun> RunAsync(string[] args, string? stdin = null, string client = "a5-test")
    {
        var start = new ProcessStartInfo(FindOrandactl())
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

        // 바깥 환경(개발자 PC 의 실제 Oranda·Claude Code)과 섞이지 않게 한다.
        start.Environment["ORANDA_DATA_DIR"] = _data.Path;
        start.Environment["ORANDA_CLIENT"] = client;
        start.Environment.Remove("ORANDA_RUN_TOKEN");
        start.Environment.Remove("ORANDA_INSTANCE");
        start.Environment.Remove("CLAUDECODE");

        using var process = Process.Start(start) ?? throw new InvalidOperationException("orandactl 을 띄우지 못했습니다.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin);
        }

        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(ProcessTimeout);
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

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        Grants.Dispose();
        _data.Dispose();
    }

    /// <summary>개발 빌드 orandactl(App 이 빌드 순서로 함께 빌드한다).</summary>
    public static string FindOrandactl()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Oranda.sln")))
        {
            directory = directory.Parent;
        }

        var exe = OperatingSystem.IsWindows() ? "orandactl.exe" : "orandactl";
        var path = Path.Combine(directory!.FullName, "src", "Oranda.Agent.Cli", "bin", "Debug", "net10.0", exe);
        return File.Exists(path) ? path : throw new FileNotFoundException("orandactl 을 먼저 빌드하세요.", path);
    }

    /// <summary>하위 세션의 접속으로 가짜 DB 세션을 준다.</summary>
    private sealed class ProfileSessions : IAgentSessionSource
    {
        public AgentSession? Find(string runId) => null;

        public ValueTask<ISession> AcquireDbSessionAsync(AgentSession session, CancellationToken ct) =>
            ValueTask.FromResult<ISession>(new FakeSession(session.Profile));
    }

    /// <summary>이름을 대문자 <c>OWNER.NAME</c> 으로 그대로 푸는 추출기(문장은 SELECT 1개로 본다).</summary>
    private sealed class NameEchoExtractor : IStatementReferenceExtractor
    {
        public ValueTask<AgentStatementInfo> AnalyzeAsync(ISession db, string sql, string? defaultSchema, CancellationToken ct) =>
            ValueTask.FromResult(new AgentStatementInfo(StatementKind.Query, false, [], 1));

        public ValueTask<AgentObjectRef?> ResolveAsync(ISession db, string name, string? defaultSchema, CancellationToken ct)
        {
            var parts = name.Trim().ToUpperInvariant().Split('.');
            return ValueTask.FromResult<AgentObjectRef?>(new AgentObjectRef(parts.Length == 2 ? parts[0] : "SCOTT", parts[^1], "TABLE"));
        }

        public ValueTask<IReadOnlyList<AgentObjectRef>> GetRelatedAsync(ISession db, IReadOnlyList<AgentObjectRef> objects, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<AgentObjectRef>>([]);
    }
}

/// <summary><c>orandactl</c> 한 번의 결과.</summary>
internal sealed record CliRun(int ExitCode, string Stdout, string Stderr)
{
    /// <summary>표준 출력의 JSON(한 개). 못 읽으면 예외와 함께 출력 전부를 보인다.</summary>
    public JsonObject Json => JsonNode.Parse(Stdout)?.AsObject() ?? throw new InvalidOperationException("stdout: " + Stdout + " stderr: " + Stderr);

    public override string ToString() => $"exit={ExitCode} stdout={Stdout} stderr={Stderr}";
}

/// <summary>감사 한 줄을 메모리에 모은다.</summary>
internal sealed class MemoryAudit : IAgentAuditStore
{
    private readonly List<AgentAuditEntry> _entries = [];

    public IReadOnlyList<AgentAuditEntry> Entries
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }
    }

    public ValueTask AppendAsync(AgentAuditEntry entry, CancellationToken ct)
    {
        lock (_entries)
        {
            _entries.Add(entry);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<AgentAuditEntry>> ListRecentAsync(int limit, Guid? profileId, CancellationToken ct) =>
        ValueTask.FromResult(Entries);
}
