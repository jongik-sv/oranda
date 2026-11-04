using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Oranda.App.Features.Ai.Chat;
using Oranda.App.Features.Ai.Tuning;
using Oranda.App.Features.Ai.Tuning.Commands;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Extensibility;
using Oranda.Core.Security;
using Oranda.Core.Settings;
using Oranda.Core.Storage;
using Oranda.Core.Text;
using Oranda.Platform.Ai;
using Oranda.Platform.Ai.Bridge;
using Oranda.Platform.Ai.Cli;
using Oranda.Platform.Storage;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>
/// 기타 CLI 흐름 시험이 함께 쓰는 틀: 진짜 <see cref="AiTuningFlow"/> + 가짜 편집기·드라이버·게이트. 보낼 곳은 <see cref="Settings"/> 가 정한다.
/// 설정을 모르는 흐름(<see cref="NoSettings"/>)과, 가짜 드라이버 대신 진짜 <see cref="RoutingAiDriver"/> 를 거치는 길(<see cref="UseRealRouter"/>)도 만든다.
/// 실제 CLI·모델·DB 는 부르지 않는다.
/// </summary>
internal sealed class AiCustomCliRig
{
    public static readonly AiCustomCli Mine = new() { Id = "c1", Name = "사내 CLI", Command = "mycli --quiet" };

    public static readonly AiContext Dev = new("DEV-ERP", ConnectionEnvironment.Development, AiAccessLevel.Query);

    public const string Cursor = "SELECT * FROM tb_order WHERE stat_cd = 'SECRET' /* 메모본문 */";

    private AiTuningFlow? _flow;

    public static AiSettings CustomSettings(bool mask = false) => new() { SystemCliId = AiCustomCliTarget.SelectionOf(Mine.Id), CustomClis = [Mine], MaskLiterals = mask };

    public static AiSettings ClaudeSettings() => new();

    public AiSettings Settings { get; set; } = CustomSettings();

    /// <summary>대화 도중 설정을 바꾼다: 흐름이 읽는 설정과 진짜 라우터가 읽는 저장소를 함께 바꾼다(한쪽만 바꾸면 실제 앱과 다른 상태가 된다).</summary>
    public void Use(AiSettings settings)
    {
        Settings = settings;
        Store.Current = new AppSettings { Ai = settings };
    }

    /// <summary>true 면 흐름이 설정을 모른다(<c>settings</c> 델리게이트가 없다): 보낼 곳이 비어 기대값이 null 로 간다.</summary>
    public bool NoSettings { get; set; }

    public AiCommandExplainRig.ScriptDriver Driver { get; } = new();

    public AiChatViewModel Chat { get; } = new();

    public AiCommandExplainRig.MaskerFake? Masker { get; set; } = new();

    /// <summary>가림이 필요한 접속인가(흐름의 <c>maskRequired</c> 와 일반 대화의 <c>Masking</c> 이 같은 값을 쓴다).</summary>
    public bool Mask { get; set; }

    public AiContext? Connected { get; set; } = Dev;

    public static readonly Guid DefaultConnectionId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public Guid ConnectionId { get; } = DefaultConnectionId;

    public ScriptedGate Gate { get; } = new();

    public List<AgentSession> Sessions { get; } = [];

    public List<AiImageSendNotice> TextConfirmed { get; } = [];

    public IEnumerable<string> AssistantTexts => Chat.Entries.OfType<ChatAssistantEntry>().Select(e => e.Text);

    /// <summary>진짜 라우터를 거치게 한다: 드라이버가 받는 것은 <see cref="CustomDriver"/> 이다.</summary>
    public bool UseRealRouter { get; set; }

    public CustomDriverFake CustomDriver { get; } = new();

    public MemoryStore Store { get; } = new();

    public bool SessionMissing { get; set; }

    public bool GateMissing { get; set; }

    public AiTuningFlow Flow => _flow ??= Build();

    private AiTuningFlow Build()
    {
        Store.Current = new AppSettings { Ai = Settings };
        IAiDriver driver = UseRealRouter ? NewRouter() : Driver;
        var flow = new AiTuningFlow(
            new Editor(), driver, Chat, (_, _, _) => { },
            sessionFactory: (c, v) => Make(c, v),
            generalSessionFactory: (c, v) => Make(c, v),
            refreshContext: (context, _) => Task.FromResult<AiContext?>(context),
            masker: Masker,
            maskRequired: _ => Mask,
            settings: NoSettings ? null : () => Settings,
            generalContext: _ => Task.FromResult(new AiGeneralScope(Connected, Mask, ConnectionId, true)),
            confirmText: (notice, _) =>
            {
                TextConfirmed.Add(notice);
                return Task.FromResult(true);
            },
            gate: GateMissing ? null : Gate);
        Chat.SettingsProvider = () => Settings;
        flow.Attach();
        return flow;
    }

    private AgentSession? Make(AiContext context, LiteralVault? vault)
    {
        if (SessionMissing)
        {
            return null;
        }

        var session = new AgentSession(new AgentRunScope("run-" + (Sessions.Count + 1), new ConnectionProfile("DEV-ERP", "scott", "h/s", AiAccess: AiAccessLevel.QueryRows), context) { Literals = vault });
        Sessions.Add(session);
        return session;
    }

    private RoutingAiDriver NewRouter()
    {
        var cli = new CliDriver(new NoTokens(), Gate, new NoBridge(), new AppDataPaths(Path.GetTempPath()), Store);
        return new RoutingAiDriver(Store, new NoCredentials(), cli, new CustomOnlyServices(CustomDriver));
    }

    public static SqlStatement Statement(string text) => new(text, new TextRange(10, text.Length), StatementKind.Query);

    /// <summary>명령의 시작 준비 → 흐름 시작(런처가 하는 순서). 거부되면 흐름을 시작하지 않고 거부 글을 돌려준다.</summary>
    public async Task<string?> RunCommandAsync(AiCommandSpec spec, string statement = Cursor, ConnectionProfile? profile = null, AiAccessLevel level = AiAccessLevel.Plan)
    {
        var context = Dev with { AccessLevel = level };
        var env = new AiCommandStartEnv(new NoServices(), new Editor(), profile ?? new ConnectionProfile("DEV-ERP", "scott", "h/s", Id: ConnectionId, AiAccess: AiAccessLevel.QueryRows), context,
            AiStatementSource.Found, Statement(statement), null, Mask);
        var preparation = await spec.PrepareAsync(env, CancellationToken.None);
        if (preparation.Target is not { } target)
        {
            return preparation.RefusalText;
        }

        await Flow.StartCommandAsync(spec, target, context);
        return null;
    }

    internal sealed class Editor : IEditorContext
    {
        // 지금 접속의 SQL 탭이 활성이다(일반 대화가 문장을 범위로 도구를 열 수 있는 상태). 비어 있으면 어느 종류든 문장을 싣지 않아 시험이 아무것도 가리지 못한다.
        public EditorSnapshot? GetActiveDocument() => new(Guid.NewGuid(), Cursor, new TextRange(0, 0), 0) { ConnectionProfileId = DefaultConnectionId };

        public SqlStatement? GetCurrentStatement() => Statement(Cursor);

        public Task<bool> ProposeEditAsync(TextEdit edit, CancellationToken ct) => Task.FromResult(false);
    }

    internal sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    /// <summary>도구 호출을 기록하고 정해 둔 답을 돌려주는 게이트. 답을 정하지 않은 도구는 성공하는 짧은 글을 낸다.</summary>
    internal sealed class ScriptedGate : IAgentGate
    {
        public List<(string Tool, JsonNode? Arguments, AgentSession Session)> Calls { get; } = [];

        public Dictionary<string, Func<AgentGateResponse>> Replies { get; } = [];

        /// <summary>호출을 이 일이 끝날 때까지 미룬다(취소·정지 시험).</summary>
        public TaskCompletionSource? Block { get; set; }

        public event EventHandler<AgentToolActivityEventArgs>? ToolCompleted;

        public IReadOnlyList<AgentToolInfo> ListTools(AgentSession session) => [];

        public async Task<AgentGateResponse> CallToolAsync(AgentSession session, string toolName, JsonNode? arguments, CancellationToken ct)
        {
            Calls.Add((toolName, arguments?.DeepClone(), session));
            if (Block is { } block)
            {
                await block.Task.WaitAsync(ct);
            }

            ToolCompleted?.Invoke(this, new AgentToolActivityEventArgs(session.Run.RunId, Calls.Count, toolName, AiToolJudgement.Allowed, 0, null));
            return Replies.TryGetValue(toolName, out var reply) ? reply() : Ok("{\"ok\":true}");
        }

        public static AgentGateResponse Ok(string text) => new(new AgentCallToolResult(false, text), AiToolJudgement.Allowed);

        /// <summary>진짜 게이트가 처리자 예외·DB 오류·시간 초과를 돌려주는 모양(던지지 않고 Failed 판단으로 낸다).</summary>
        public static AgentGateResponse Failure(string text, string note = "tool_exception") => new(new AgentCallToolResult(true, text), AiToolJudgement.Failed, note);

        public static AgentGateResponse Error(string text, string note = "denied") => new(new AgentCallToolResult(true, text), AiToolJudgement.Denied, note);
    }

    /// <summary>진짜 라우터 뒤에서 받은 요청을 기록하는 기타 CLI 드라이버.</summary>
    internal sealed class CustomDriverFake : ICustomCliAiDriver
    {
        public List<(AiRequest Request, AiCustomCli Row)> Calls { get; } = [];

        public async IAsyncEnumerable<AiEvent> RunAsync(AiRequest request, AiCustomCli cli, [EnumeratorCancellation] CancellationToken ct)
        {
            Calls.Add((request, cli));
            yield return new AiStarted("custom-" + Calls.Count);
            await Task.Yield();
            yield return new AiFinal("기타 CLI 답 " + Calls.Count, null, "custom-" + Calls.Count);
        }
    }

    internal sealed class MemoryStore : ISettingsStore
    {
        public AppSettings Current { get; set; } = new();

        public Task<StoredDocument<AppSettings>> LoadAsync(CancellationToken ct) => Task.FromResult(new StoredDocument<AppSettings>(Current));

        public Task SaveAsync(AppSettings settings, CancellationToken ct)
        {
            Current = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class CustomOnlyServices(ICustomCliAiDriver custom) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(ICustomCliAiDriver) ? custom : null;
    }

    private sealed class NoTokens : IAgentRunTokens
    {
        public string Issue(AgentSession session) => throw new InvalidOperationException("기타 CLI 는 토큰을 발급하지 않는다.");

        public AgentSession? Resolve(string token) => null;

        public void Revoke(string token)
        {
        }
    }

    private sealed class NoBridge : IAgentBridgeEndpoint
    {
        public string? PipeName => null;
    }

    private sealed class NoCredentials : ICredentialStore
    {
        public Task SaveAsync(string key, ReadOnlyMemory<byte> secret) => Task.CompletedTask;

        public Task<byte[]?> LoadAsync(string key) => Task.FromResult<byte[]?>(null);

        public Task DeleteAsync(string key) => Task.CompletedTask;
    }
}
