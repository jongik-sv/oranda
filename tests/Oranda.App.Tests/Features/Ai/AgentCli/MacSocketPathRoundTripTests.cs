using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Connection;
using Oranda.Core.Storage;
using Oranda.Platform.Ai.Bridge;
using Oranda.Platform.Storage;
using Oranda.Testing;

namespace Oranda.App.Tests.Features.Ai.AgentCli;

/// <summary>
/// macOS 실측(agent-cli §3.4·§10-4, 레인 m-mac): 실제 기본 이름 규칙(<see cref="AgentBridgeHost"/> 공개 생성자 → 데이터 폴더 아래 소켓,
/// 한도를 넘으면 <c>&lt;tmp&gt;/oranda-&lt;uid&gt;/</c>)으로 연 다리에 실제 <c>orandactl</c> 이 연결 파일만 보고 붙는가.
/// <see cref="AgentCliRig"/> 는 상대 이름 파이프를 써서 이 경로를 지나지 않는다. macOS 밖에서는 할 일이 없어 그냥 통과한다.
/// </summary>
[Collection(AgentCliSerial.Name)]
public sealed class MacSocketPathRoundTripTests
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(60);

    private static readonly UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    [Theory]
    [InlineData(103, true)]
    [InlineData(104, false)]
    public void Macos_socket_path_limit_is_103_bytes(int length, bool opens)
    {
        // sun_path 104바이트에 끝 NUL 이 들어가므로 경로는 103바이트까지(AgentSocketPath.LimitFor(macOS: true)).
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var root = "/tmp/om-" + Guid.NewGuid().ToString("N")[..8];
        var path = root + "/" + new string('m', length - root.Length - "/".Length - "/agent.sock".Length) + "/agent.sock";
        Assert.Equal(length, Encoding.UTF8.GetByteCount(path));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            Exception? error = null;
            try
            {
                using var server = new NamedPipeServerStream(path, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            }
            catch (Exception ex)
            {
                error = ex;
            }

            Assert.True(opens == (error is null), $"length={length} error={error}");
            Assert.Equal(103, AgentSocketPath.LimitFor(macOS: true));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Default_data_folder_socket_is_found_through_the_endpoint_file()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        // macOS 의 TMPDIR(/var/folders/…/T/) 아래 임시 폴더는 그것만으로 103바이트를 넘으므로 짧은 /tmp 아래에 둔다.
        var data = "/tmp/om-" + Guid.NewGuid().ToString("N")[..8];
        Directory.CreateDirectory(data);
        try
        {
            var endpoint = await RoundTripAsync(data);

            Assert.Equal(AgentEndpoint.UnixKind, endpoint.Kind);
            Assert.Equal(AgentPipeName.Unix(data, null), endpoint.Name);
            AssertCleanedUp(data, endpoint);
            Assert.Equal(OwnerOnly, File.GetUnixFileMode(Path.Combine(data, "run")));
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Fact]
    public async Task Long_data_folder_falls_back_to_the_short_temp_socket()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        using var data = new TempDirectory("oranda-mac-");
        var longData = Path.Combine(data, new string('L', 60), new string('M', 60));
        Directory.CreateDirectory(longData);
        Assert.True(Encoding.UTF8.GetByteCount(AgentPipeName.Unix(longData, null)) > AgentSocketPath.LimitFor(macOS: true));

        var endpoint = await RoundTripAsync(longData);

        var shortDirectory = Path.Combine(Path.GetTempPath(), "oranda-" + CurrentUser.GetKey());
        Assert.Equal(AgentEndpoint.UnixKind, endpoint.Kind);
        Assert.Equal(shortDirectory, Path.GetDirectoryName(endpoint.Name));
        Assert.Matches(@"^agent(-\d+)?\.sock$", Path.GetFileName(endpoint.Name));
        Assert.True(Encoding.UTF8.GetByteCount(endpoint.Name) <= AgentSocketPath.LimitFor(macOS: true), endpoint.Name);
        Assert.Equal(OwnerOnly, File.GetUnixFileMode(shortDirectory));
        AssertCleanedUp(longData, endpoint);
    }

    /// <summary>다리를 끄면 연결 파일과 소켓 파일이 남지 않는다(남으면 다음 실행이 FirstPipeInstance 에 실패해 -pid 이름으로 밀린다).</summary>
    private static void AssertCleanedUp(string dataDirectory, AgentEndpoint endpoint)
    {
        Assert.False(File.Exists(AppDataLocation.EndpointFile(dataDirectory, Environment.ProcessId)));
        Assert.False(File.Exists(endpoint.Name), endpoint.Name);
    }

    /// <summary>공개 생성자로 다리를 열고 허가를 낸 뒤 orandactl status 가 붙는지 본다. 연결 파일의 통로를 돌려준다.</summary>
    [SupportedOSPlatform("macos")]
    private static async Task<AgentEndpoint> RoundTripAsync(string dataDirectory)
    {
        var grants = new AgentGrantStore();
        var gate = new AgentGate(new NoSessions(), new NoExtractor(), [], new MemoryAudit(), grants: grants)
        {
            AppHandlers = [new StatusHandler()],
        };
        var host = new AgentBridgeHost(gate, new AgentRunTokenStore(), new AppDataPaths(dataDirectory), grants, gate);
        try
        {
            var pipe = host.Start();
            grants.Create(new AgentGrantRequest(
                [new AgentGrantTarget(new ConnectionProfile("dev-erp", "scott", "h/s", Id: Guid.NewGuid(), AiAccess: AiAccessLevel.QueryRows), AiAccessLevel.QueryRows)],
                AgentGrantPolicy.DefaultLifetime,
                Label: "mac"));

            var path = AppDataLocation.EndpointFile(dataDirectory, Environment.ProcessId);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            Assert.Equal(OwnerOnly, File.GetUnixFileMode(Path.GetDirectoryName(path)!));
            var file = AgentEndpointFile.TryParse(await File.ReadAllTextAsync(path))!;
            Assert.Equal(pipe, file.Endpoint.Name);
            Assert.True(File.Exists(pipe), pipe); // 소켓 파일이 실제로 있다

            var run = await RunOrandactlAsync(dataDirectory, "status");
            Assert.True(run.ExitCode == 0, run.ToString());
            Assert.Equal("mac", (string?)run.Json["label"]);
            return file.Endpoint;
        }
        finally
        {
            await host.DisposeAsync();
            grants.Dispose();
        }
    }

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
        start.Environment["ORANDA_CLIENT"] = "mac-test";
        start.Environment.Remove("ORANDA_RUN_TOKEN");
        start.Environment.Remove("ORANDA_INSTANCE");
        start.Environment.Remove("ORANDA_PIPE");
        start.Environment.Remove("CLAUDECODE");

        using var process = Process.Start(start) ?? throw new InvalidOperationException("orandactl 을 띄우지 못했습니다.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
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

    private sealed class StatusHandler : IAppToolHandler
    {
        public string ToolName => AppToolNames.GetStatus;

        public ValueTask<AgentToolOutput> ExecuteAsync(AppToolInvocation invocation, CancellationToken ct) =>
            ValueTask.FromResult(AgentToolOutput.Ok(new JsonObject { ["label"] = invocation.Grant.Label }));
    }

    private sealed class NoSessions : IAgentSessionSource
    {
        public AgentSession? Find(string runId) => null;

        public ValueTask<Oranda.Core.Sessions.ISession> AcquireDbSessionAsync(AgentSession session, CancellationToken ct) =>
            throw new InvalidOperationException("이 시험은 DB 를 쓰지 않는다.");
    }

    private sealed class NoExtractor : IStatementReferenceExtractor
    {
        public ValueTask<AgentStatementInfo> AnalyzeAsync(Oranda.Core.Sessions.ISession db, string sql, string? defaultSchema, CancellationToken ct) =>
            throw new InvalidOperationException("이 시험은 DB 를 쓰지 않는다.");

        public ValueTask<AgentObjectRef?> ResolveAsync(Oranda.Core.Sessions.ISession db, string name, string? defaultSchema, CancellationToken ct) =>
            throw new InvalidOperationException("이 시험은 DB 를 쓰지 않는다.");

        public ValueTask<IReadOnlyList<AgentObjectRef>> GetRelatedAsync(Oranda.Core.Sessions.ISession db, IReadOnlyList<AgentObjectRef> objects, CancellationToken ct) =>
            throw new InvalidOperationException("이 시험은 DB 를 쓰지 않는다.");
    }
}
