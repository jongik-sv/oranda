using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Security;
using Oranda.Core.Settings;
using Oranda.Core.Storage;
using Oranda.Platform.Ai;
using Oranda.Platform.Ai.Bridge;

namespace Oranda.Platform.Tests.Ai;

internal sealed class FakeRunTokens : IAgentRunTokens
{
    private readonly Dictionary<string, AgentSession> _byToken = [];

    public List<string> Issued { get; } = [];

    public List<string> Revoked { get; } = [];

    public string Issue(AgentSession session)
    {
        var token = AgentRunToken.Generate();
        _byToken[token] = session;
        Issued.Add(token);
        return token;
    }

    public AgentSession? Resolve(string token) => _byToken.GetValueOrDefault(token);

    public void Revoke(string token)
    {
        Revoked.Add(token);
        if (_byToken.Remove(token, out var session))
        {
            session.Revoke();
        }
    }
}

internal sealed class FakeGate : IAgentGate
{
    public string? LastTool { get; private set; }

    public JsonNode? LastArguments { get; private set; }

    public AgentSession? LastSession { get; private set; }

    public bool Throw { get; set; }

    public bool ThrowCancel { get; set; }

    public bool HugeResult { get; set; }

    public bool QuoteResult { get; set; }

    public TaskCompletionSource? Block { get; set; }

    /// <summary><see cref="Block"/> 에서 기다리기 시작하면 끝난다(도구 실행 자리를 잡았다는 신호).</summary>
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public event EventHandler<AgentToolActivityEventArgs>? ToolCompleted;

    public void Raise(AgentToolActivityEventArgs e) => ToolCompleted?.Invoke(this, e);

    public bool HasSubscribers => ToolCompleted is not null;

    public IReadOnlyList<AgentToolInfo> ListTools(AgentSession session) =>
        [new AgentToolInfo("describe_object", "describe", new JsonObject { ["type"] = "object" })];

    public async Task<AgentGateResponse> CallToolAsync(AgentSession session, string toolName, JsonNode? arguments, CancellationToken ct)
    {
        if (Block is { } block)
        {
            Entered.TrySetResult();
            await block.Task.WaitAsync(ct);
        }

        if (Throw)
        {
            throw new InvalidOperationException("secret detail");
        }

        if (ThrowCancel)
        {
            throw new OperationCanceledException("gate internal timeout");
        }

        (LastSession, LastTool, LastArguments) = (session, toolName, arguments);
        var text = HugeResult ? new string('x', AgentRpc.MaxMessageBytes) : QuoteResult ? new string('"', 1_000_000) : "{\"ok\":true}";
        return new AgentGateResponse(new AgentCallToolResult(false, text), AiToolJudgement.Allowed);
    }
}

/// <summary>
/// 설정 저장소 가짜. 실제 <c>SettingsStore</c> 처럼 읽을 때마다 현재 값을 돌려주고 저장하면 그 값으로 바뀐다(실행마다 설정을 다시 읽는 경로를 흉내 낸다).
/// 읽기 전용 문서(미래 버전 파일 등)면 저장이 조용히 사라지고, 취소된 토큰으로 읽으면 취소 예외가 난다. 실제 저장소와 같게 <c>SettingsChanged</c> 는 발행하지 않는다
/// (<c>ai</c> 구역 변경 알림은 프로필 관리 창이 직접 발행한다, 설계 §6.5).
/// </summary>
internal sealed class FakeSettingsStore(AppSettings? initial = null) : ISettingsStore, IAiSettingsResetSignal
{
    public AppSettings Current { get; set; } = initial ?? new AppSettings();

    /// <summary>true 면 <see cref="SaveAsync"/> 가 값을 바꾸지 않는다(횟수만 센다).</summary>
    public bool IsReadOnly { get; set; }

    public int LoadCount { get; private set; }

    public int SaveCount { get; private set; }

    public Exception? LoadFailure { get; set; }

    /// <summary>true 면 다음 <see cref="TryTakeReset"/> 가 한 번 true 를 돌려준다(손상 복구 뒤 첫 실행을 흉내 낸다).</summary>
    public bool ResetPending { get; set; }

    public bool TryTakeReset()
    {
        var pending = ResetPending;
        ResetPending = false;
        return pending;
    }

    public Task<StoredDocument<AppSettings>> LoadAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        LoadCount++;
        return LoadFailure is { } failure
            ? Task.FromException<StoredDocument<AppSettings>>(failure)
            : Task.FromResult(new StoredDocument<AppSettings>(Current, IsReadOnly));
    }

    public Task SaveAsync(AppSettings settings, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        SaveCount++;
        if (!IsReadOnly)
        {
            Current = settings;
        }

        return Task.CompletedTask;
    }
}

/// <summary>자격 증명 저장소 가짜. 키 이름과 비밀 값을 메모리에 둔다. 없는 키 삭제는 오류가 아니다(실제 구현과 같다).</summary>
internal sealed class FakeCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, byte[]> _items = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Keys => _items.Keys;

    public List<string> Deleted { get; } = [];

    public Task SaveAsync(string key, ReadOnlyMemory<byte> secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _items[key] = secret.ToArray();
        return Task.CompletedTask;
    }

    public Task<byte[]?> LoadAsync(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return Task.FromResult(_items.TryGetValue(key, out var value) ? value.ToArray() : null);
    }

    public Task DeleteAsync(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Deleted.Add(key);
        _items.Remove(key);
        return Task.CompletedTask;
    }

    /// <summary>시험 편의: 저장된 비밀을 글로 읽는다. 없으면 null.</summary>
    public string? LoadText(string key) => _items.TryGetValue(key, out var value) ? Encoding.UTF8.GetString(value) : null;

    public Task SaveTextAsync(string key, string secret) => SaveAsync(key, Encoding.UTF8.GetBytes(secret));
}

/// <summary>AI 다리 끝점 가짜. null 이면 다리가 열려 있지 않은 상태다.</summary>
internal sealed class FixedBridgeEndpoint(string? pipeName) : IAgentBridgeEndpoint
{
    public string? PipeName { get; } = pipeName;
}

/// <summary>정해 둔 사건을 내는 드라이버 가짜. 받은 요청을 기록한다.</summary>
internal sealed class ScriptedAiDriver(params AiEvent[] events) : IAiDriver
{
    public List<AiRequest> Requests { get; } = [];

    public AiDriverCapabilities Capabilities { get; } = new(Skills: false, Tools: true, Resume: true, LocalOnly: false, Streaming: true, Images: true);

    public async IAsyncEnumerable<AiEvent> RunAsync(AiRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        Requests.Add(request);
        foreach (var e in events)
        {
            ct.ThrowIfCancellationRequested();
            yield return e;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }
}

/// <summary>API 경로 드라이버 가짜. 라우터가 넘긴 요청과 전송 대상(<see cref="AiRoute"/>)을 기록한다.</summary>
internal sealed class ScriptedApiDriver(params AiEvent[] events) : IApiAiDriver
{
    public List<AiRequest> Requests { get; } = [];

    public List<AiRoute> Routes { get; } = [];

    public AiDriverCapabilities Capabilities { get; } = new(Skills: false, Tools: true, Resume: true, LocalOnly: false, Streaming: true, Images: true);

    public IAsyncEnumerable<AiEvent> RunAsync(AiRequest request, CancellationToken ct) =>
        throw new InvalidOperationException("라우터는 전송 대상을 함께 주는 오버로드로만 부른다.");

    public async IAsyncEnumerable<AiEvent> RunAsync(AiRequest request, AiRoute route, [EnumeratorCancellation] CancellationToken ct)
    {
        Requests.Add(request);
        Routes.Add(route);
        foreach (var e in events)
        {
            ct.ThrowIfCancellationRequested();
            yield return e;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }
}
