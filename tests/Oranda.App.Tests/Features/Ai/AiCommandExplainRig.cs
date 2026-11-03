using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Oranda.App.Features.Ai.Chat;
using Oranda.App.Features.Ai.Tuning;
using Oranda.App.Features.Ai.Tuning.Commands;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Extensibility;
using Oranda.Core.Text;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>SQL 설명·실행 계획 해설 시험이 함께 쓰는 틀: 진짜 명령 + 가짜 편집기·드라이버·가림. 실제 CLI·모델은 부르지 않는다.</summary>
internal sealed class AiCommandExplainRig
{
    public const string Cursor = "SELECT * FROM tb_order WHERE stat_cd = 'SECRET' /* 메모본문 */";

    public static readonly ConnectionProfile Dev = new("DEV-ERP", "scott", "h/s", Id: Guid.Parse("66666666-6666-6666-6666-666666666666"), Environment: ConnectionEnvironment.Development, AiAccess: AiAccessLevel.QueryRows);

    private static readonly AiContext Full = new("DEV-ERP", ConnectionEnvironment.Development, AiAccessLevel.QueryRows);

    private AiTuningFlow? _flow;

    public ScriptDriver Driver { get; } = new();

    public AiChatViewModel Chat { get; } = new();

    public FakeEditor Editor { get; } = new();

    public MaskerFake? Masker { get; set; } = new();

    public bool Mask { get; set; }

    /// <summary>새 탭으로 열린 것(제안 반영이 없으므로 늘 비어야 한다).</summary>
    public List<(string Title, string Text)> Tabs { get; } = [];

    public List<AgentSession> Sessions { get; } = [];

    public IEnumerable<string> AssistantTexts => Chat.Entries.OfType<ChatAssistantEntry>().Select(e => e.Text);

    public AiTuningFlow Flow => _flow ??= new AiTuningFlow(
        Editor, Driver, Chat, (t, x, _) => Tabs.Add((t, x)),
        sessionFactory: (c, v) => Make(c, v),
        generalSessionFactory: (c, v) => Make(c, v),
        refreshContext: (context, _) => Task.FromResult<AiContext?>(context with { AccessLevel = AiAccessLevel.QueryRows }), // 접속의 지금 수준으로 되돌아온다(launcher 와 같다)
        masker: Masker,
        maskRequired: _ => Mask,
        previewEdit: Editor.Preview);

    public static SqlStatement Statement(string text) => new(text, new TextRange(10, text.Length), StatementKind.Query);

    /// <summary>시작 준비가 읽는 환경. <paramref name="statement"/> 가 null 이면 커서 문장 없음.</summary>
    public AiCommandStartEnv Env(string? statement = Cursor, ConnectionProfile? profile = null, Guid? tabId = null) =>
        new(new EmptyServices(), Editor, profile ?? Dev, Full, statement is null ? AiStatementSource.NoStatement : AiStatementSource.Found,
            statement is null ? null : Statement(statement), tabId, Mask);

    /// <summary>명령의 시작 준비 → 흐름 시작(런처가 하는 순서). 거부되면 흐름을 시작하지 않고 거부 글을 돌려준다.</summary>
    public async Task<string?> RunAsync(AiCommandSpec spec, string? statement = Cursor, ConnectionProfile? profile = null, Guid? tabId = null)
    {
        var preparation = await spec.PrepareAsync(Env(statement, profile, tabId), CancellationToken.None);
        if (preparation.Target is not { } target)
        {
            return preparation.RefusalText;
        }

        await Flow.StartCommandAsync(spec, target, Full);
        return null;
    }

    private AgentSession Make(AiContext context, LiteralVault? vault)
    {
        var session = new AgentSession(new AgentRunScope("run-" + (Sessions.Count + 1), Dev, context) { Literals = vault });
        Sessions.Add(session);
        return session;
    }

    internal sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    internal sealed class FakeEditor : IEditorContext
    {
        public List<TextEdit> Proposed { get; } = [];

        public EditorSnapshot? GetActiveDocument() => null;

        public SqlStatement? GetCurrentStatement() => Statement(Cursor);

        public Task<bool> ProposeEditAsync(TextEdit edit, CancellationToken ct) => Task.FromResult(true);

        public Task<string?> Preview(TextEdit edit, string before, CancellationToken ct)
        {
            Proposed.Add(edit); // 비교 창이 열렸다는 기록(자유 글 명령은 열지 않는다)
            return Task.FromResult<string?>(edit.NewText);
        }
    }

    internal sealed class ScriptDriver : IAiDriver
    {
        public List<AiRequest> Requests { get; } = [];

        /// <summary>돌려줄 답 글(없으면 「답 n」). 렌더 점검이 실제 모양의 답을 넣는다.</summary>
        public string? Answer { get; set; }

        public AiDriverCapabilities Capabilities { get; } = new(false, true, true, false, true);

        public async IAsyncEnumerable<AiEvent> RunAsync(AiRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            Requests.Add(request);
            yield return new AiStarted("s-" + Requests.Count);
            await Task.Yield();
            yield return new AiFinal(Answer ?? "답 " + Requests.Count, null, "s-" + Requests.Count);
        }
    }

    /// <summary>작은따옴표 문자열·주석을 가리는 시험용 가림.</summary>
    internal sealed class MaskerFake : ISqlLiteralMasker
    {
        public bool FailMask { get; set; }

        public LiteralVault CreateVault(string statementText) => new();

        public SqlMaskResult Mask(string sql, LiteralVault vault) =>
            FailMask ? SqlMaskResult.Failed : SqlMaskResult.Ok(Regex.Replace(sql, @"'[^']*'|/\*(?!\+)[\s\S]*?\*/|--[^\n]*", m => m.Value[0] == '\'' ? vault.Add(m.Value) : vault.AddComment(m.Value)));

        public SqlRestoreResult Restore(string sql, LiteralVault vault) =>
            vault.TryGetStatement(sql, out var original) ? SqlRestoreResult.Ok(original) : SqlRestoreResult.Ok(sql);

        public string MaskPredicate(string predicate, LiteralVault vault) => predicate;
    }
}
