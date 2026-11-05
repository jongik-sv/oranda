using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Oranda.App.Features.Ai.Chat;
using Oranda.App.Features.Ai.Tuning;
using Oranda.App.Features.Ai.Tuning.Commands;
using Oranda.Core.Ai.Access;
using Oranda.Core.Settings;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Extensibility;
using Oranda.Core.Text;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>
/// AI 명령 공용 기반(cmd-base): 가짜 계약 둘(자유 글·구조 결과)로 실제 명령 없이 확인한다. 가림, 보낼 곳이 아닌 문장 거부, 수준 상한(이어 말하기 뒤에도),
/// 레포트 조건, 대화 종류의 수명, 제안 반영 이름표, 튜닝으로 새지 않는 것.
/// </summary>
public class AiCommandFlowTests
{
    private const string Cursor = "SELECT * FROM tb_order WHERE stat_cd = 'SECRET' /* 메모본문 */";

    private const string ResultJson = """
        {
          "summary": "고쳤습니다.",
          "rewrites": [ { "label": "ORDER 수정", "sql": "SELECT o.* FROM tb_order o WHERE o.stat_cd = '10';", "rationale": "근거" } ],
          "index_suggestions": [ { "table": "TB_ORDER", "columns": ["STAT_CD"], "ddl": "CREATE INDEX ix_o ON tb_order (stat_cd);", "rationale": "r" } ],
          "evidence": []
        }
        """;

    private static readonly ConnectionProfile Dev = new("DEV-ERP", "scott", "h/s", Id: Guid.Parse("66666666-6666-6666-6666-666666666666"), Environment: ConnectionEnvironment.Development, AiAccess: AiAccessLevel.QueryRows);

    private static readonly AiContext Full = new("DEV-ERP", ConnectionEnvironment.Development, AiAccessLevel.QueryRows);

    private sealed class FreeSpec : AiCommandSpec
    {
        public override string Id => "test.free";

        public override string ConversationTitle => "자유 글";

        public override string UserLine => "자유 글 명령";

        public override AiAccessLevel AccessCeiling => AiAccessLevel.Metadata;

        public override string ResponseRules => "RULES-FREE";

        public Func<AiCommandStartEnv, AiCommandPreparation>? OnPrepare { get; init; }

        public override Task<AiCommandPreparation> PrepareAsync(AiCommandStartEnv env, CancellationToken ct) =>
            Task.FromResult(OnPrepare?.Invoke(env) ?? AiCommandPreparation.Ready(new AiCommandTarget(env.CursorStatement!)));

        public override string BuildInstruction(AiCommandPromptInput input) => "FREE-INSTRUCTION";
    }

    private sealed class StructuredSpec : AiCommandSpec
    {
        public override string Id => "test.structured";

        public override string ConversationTitle => "구조 결과";

        public override string UserLine => "구조 결과 명령";

        public override AiAccessLevel AccessCeiling => AiAccessLevel.Plan;

        public override JsonElement? OutputSchema => TuningResult.Schema();

        public override string ResponseRules => "RULES-FIX";

        public override string TabSuffix => " (수정)";

        public override string ProposalLabel => "AI 수정 — ";

        public override Task<AiCommandPreparation> PrepareAsync(AiCommandStartEnv env, CancellationToken ct) =>
            Task.FromResult(AiCommandPreparation.Ready(new AiCommandTarget(env.CursorStatement!)));

        public override string BuildInstruction(AiCommandPromptInput input) => "FIX-INSTRUCTION";
    }

    private sealed class FakeEditor(string statementText) : IEditorContext
    {
        public string StatementText { get; set; } = statementText;

        public List<TextEdit> Proposed { get; } = [];

        public EditorSnapshot? GetActiveDocument() => null;

        public SqlStatement? GetCurrentStatement() => Statement(StatementText);

        public Task<bool> ProposeEditAsync(TextEdit edit, CancellationToken ct) => Task.FromResult(true);

        public Task<string?> Preview(TextEdit edit, string before, CancellationToken ct)
        {
            Proposed.Add(edit);
            return Task.FromResult<string?>(edit.NewText);
        }
    }

    private static SqlStatement Statement(string text) => new(text, new TextRange(10, text.Length), StatementKind.Query);

    private sealed class ScriptDriver : IAiDriver
    {
        public List<AiRequest> Requests { get; } = [];

        public JsonElement? Result { get; set; }

        public AiDriverCapabilities Capabilities { get; } = new(false, true, true, false, true);

        public async IAsyncEnumerable<AiEvent> RunAsync(AiRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            Requests.Add(request);
            yield return new AiStarted("s-" + Requests.Count);
            await Task.Yield();
            yield return new AiFinal("답 " + Requests.Count, request.OutputSchema is null ? null : Result, "s-" + Requests.Count);
        }
    }

    /// <summary>작은따옴표 문자열·주석을 가리는 시험용 가림. 따옴표가 홀수 개인 글(닫히지 않은 문자열)은 실제 렉서처럼 가리지 못한다.</summary>
    private sealed class FakeMasker : ISqlLiteralMasker
    {
        public bool FailMask { get; set; }

        public LiteralVault CreateVault(string statementText) => new();

        public SqlMaskResult Mask(string sql, LiteralVault vault) =>
            FailMask || sql.Count(c => c == '\'') % 2 == 1
                ? SqlMaskResult.Failed
                : SqlMaskResult.Ok(Regex.Replace(sql, @"'[^']*'|/\*(?!\+)[\s\S]*?\*/|--[^\n]*", m => m.Value[0] == '\'' ? vault.Add(m.Value) : vault.AddComment(m.Value)));

        public SqlRestoreResult Restore(string sql, LiteralVault vault) =>
            vault.TryGetStatement(sql, out var original) ? SqlRestoreResult.Ok(original) : SqlRestoreResult.Ok(sql);

        public string MaskPredicate(string predicate, LiteralVault vault) => predicate;
    }

    /// <summary>짧은 글(앞부분)과 전체 글을 서로 다른 모양으로 가리는 가림(실제 구현이 DATE 키워드·주석 한가운데서 그런 것처럼).</summary>
    private sealed class ShapeShiftingMasker : ISqlLiteralMasker
    {
        public LiteralVault CreateVault(string statementText) => new();

        public SqlMaskResult Mask(string sql, LiteralVault vault) => SqlMaskResult.Ok(sql.Length < 20 ? "[" + sql + "]" : sql);

        public SqlRestoreResult Restore(string sql, LiteralVault vault) => SqlRestoreResult.Ok(sql);

        public string MaskPredicate(string predicate, LiteralVault vault) => predicate;
    }

    private sealed class Rig
    {
        public ScriptDriver Driver { get; } = new();

        public AiChatViewModel Chat { get; } = new();

        public FakeEditor Editor { get; } = new(Cursor);

        public FakeMasker? Masker { get; set; } = new();

        public bool Mask { get; set; }

        public AiSettings? Settings { get; set; }

        public List<(string Title, string Text)> Tabs { get; } = [];

        public List<(string Kind, AgentSession Session, LiteralVault? Vault)> Sessions { get; } = [];

        private AiTuningFlow? _flow;

        /// <summary>처음 쓸 때 만든다(초기화 식으로 정한 Mask·Masker 를 본 뒤).</summary>
        public AiTuningFlow Flow => _flow ??= new AiTuningFlow(
            Editor, Driver, Chat, (t, x, _) => Tabs.Add((t, x)),
            sessionFactory: (c, v) => MakeSession("tuning", c, v),
            generalSessionFactory: (c, v) => MakeSession("general", c, v),
            refreshContext: (context, _) => Task.FromResult<AiContext?>(context with { AccessLevel = AiAccessLevel.QueryRows }), // 접속의 지금 수준으로 되돌아온다(launcher 와 같다)
            masker: Masker,
            maskRequired: context => AiAccessPolicy.MaskLiterals(production: false, setting: Mask, context.LocalAi),
            previewEdit: Editor.Preview,
            settings: Settings is null ? null : () => Settings);

        private AgentSession MakeSession(string kind, AiContext context, LiteralVault? vault)
        {
            var session = new AgentSession(new AgentRunScope("run-" + (Sessions.Count + 1), Dev, context) { Literals = vault });
            Sessions.Add((kind, session, vault));
            return session;
        }

        public Task StartTuning() => Flow.StartAsync(new TuningStartChoice(true, true, "default"), Full);

        public Task StartCommand(AiCommandSpec spec, string? text = null, string? sqlId = null) =>
            Flow.StartCommandAsync(spec, new AiCommandTarget(Statement(text ?? Cursor), sqlId), Full);

        public IEnumerable<string> AssistantTexts => Chat.Entries.OfType<ChatAssistantEntry>().Select(e => e.Text);
    }

    [Fact]
    public async Task A_command_sent_to_local_ai_keeps_literals_and_comment_bodies_even_where_masking_is_on()
    {
        var id = Guid.NewGuid();
        var profile = new AiLlmProfile { Id = id, Name = "로컬", Preset = AiProfilePreset.Custom, Format = AiApiFormat.OpenAi, BaseUrl = "http://192.0.2.10:8080/v1", Model = "m", LocalAiConfirmedUrl = "http://192.0.2.10:8080/v1" };
        var rig = new Rig { Mask = true, Settings = new AiSettings { Profiles = [profile], ActiveProfileId = id } };
        rig.Driver.Result = JsonDocument.Parse(ResultJson).RootElement.Clone();

        await rig.StartCommand(new FreeSpec());

        var request = Assert.Single(rig.Driver.Requests);
        Assert.Contains("SECRET", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("메모본문", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("가리지 않고 보냈습니다", rig.AssistantTexts.First(), StringComparison.Ordinal);
        Assert.Null(Assert.Single(rig.Sessions).Vault);
        Assert.True(request.Context.LocalAi);
        Assert.True(request.Expected!.LocalAi);
    }

    [Fact]
    public async Task Masked_command_prompt_has_no_literal_or_comment_body_and_the_first_line_says_it_was_masked()
    {
        var rig = new Rig { Mask = true };
        rig.Driver.Result = JsonDocument.Parse(ResultJson).RootElement.Clone();

        await rig.StartCommand(new FreeSpec());

        var request = Assert.Single(rig.Driver.Requests);
        Assert.DoesNotContain("SECRET", request.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("메모본문", request.Prompt, StringComparison.Ordinal);
        Assert.Contains(":v1", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("placeholders such as :v1", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("FREE-INSTRUCTION", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("RULES-FREE", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("<statement>", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("가려서 보냈습니다", rig.AssistantTexts.First(), StringComparison.Ordinal); // 대화 창 첫 줄
        Assert.NotNull(Assert.Single(rig.Sessions).Vault); // 도구 결과도 같은 사전으로 가린다
        Assert.Equal(Cursor, request.Context.StatementText); // 원문은 범위 검사용 문맥에만(LLM 으로 가지 않는다)

        var plain = new Rig();
        await plain.StartCommand(new FreeSpec());
        Assert.Contains("SECRET", Assert.Single(plain.Driver.Requests).Prompt, StringComparison.Ordinal);
        Assert.Contains("가리지 않고 보냈습니다", plain.AssistantTexts.First(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_masking_failure_or_a_missing_masker_sends_nothing_and_leaves_the_running_conversation_alone()
    {
        var failing = new Rig { Mask = true };
        failing.Masker!.FailMask = true;
        await failing.StartCommand(new FreeSpec());
        Assert.Empty(failing.Driver.Requests);
        Assert.Contains(AiTuningFlow.CommandMaskFailedText, failing.AssistantTexts);
        Assert.Null(failing.Flow.Command);
        Assert.False(failing.Chat.IsTuning);

        var missing = new Rig { Mask = true, Masker = null };
        await missing.StartCommand(new FreeSpec());
        Assert.Empty(missing.Driver.Requests);
        Assert.Contains(AiTuningFlow.CommandMaskerMissingText, missing.AssistantTexts);

        // 진행 중이던 앞 명령 대화는 거부된 새 시작(명령·튜닝 모두)이 바꾸지 않는다.
        var rig = new Rig();
        var first = new StructuredSpec();
        await rig.StartCommand(first);
        var generation = rig.Flow.Generation;
        var entries = rig.Chat.Entries.Count;
        rig.Mask = true;
        rig.Masker!.FailMask = true;

        await rig.StartCommand(new FreeSpec());
        await rig.StartTuning();

        Assert.Same(first, rig.Flow.Command);
        Assert.Equal(generation, rig.Flow.Generation);
        Assert.Single(rig.Driver.Requests);
        Assert.True(rig.Chat.IsTuning);
        Assert.Equal(entries + 2, rig.Chat.Entries.Count); // 앞 줄은 그대로, 거부 안내만 둘 늘었다
    }

    [Fact]
    public async Task Access_off_sends_nothing()
    {
        var rig = new Rig();

        await rig.Flow.StartCommandAsync(new FreeSpec(), new AiCommandTarget(Statement(Cursor)), Full with { AccessLevel = AiAccessLevel.Off });

        Assert.Empty(rig.Driver.Requests);
        Assert.Contains(AiTuningFlow.CommandAccessOffText, rig.AssistantTexts);
    }

    [Fact]
    public async Task The_access_ceiling_closes_benchmark_tools_and_holds_after_a_follow_up_refreshes_the_connection_level()
    {
        var rig = new Rig();
        await rig.StartCommand(new FreeSpec());

        var first = Assert.Single(rig.Driver.Requests);
        Assert.Equal(AiAccessLevel.Metadata, first.Context.AccessLevel);
        Assert.Equal(AiAccessLevel.Metadata, first.Session!.Level); // 게이트가 쓰는 수준: 접속은 QueryRows 라도 벤치마크 도구가 닫힌다
        Assert.Null(first.OutputSchema);
        Assert.Equal("general", rig.Sessions[0].Kind); // 튜닝의 바인드 값을 싣는 세션이 아니다

        await rig.Flow.FollowUpAsync("더 설명해 줘"); // refreshContext 가 접속의 지금 수준(QueryRows)을 돌려준다

        Assert.Equal(2, rig.Driver.Requests.Count);
        var second = rig.Driver.Requests[1];
        Assert.Equal("s-1", second.ResumeId);
        Assert.Equal(AiAccessLevel.Metadata, second.Context.AccessLevel);
        Assert.Equal(AiAccessLevel.Metadata, second.Session!.Level);
        Assert.Contains("RULES-FREE", second.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Response style", second.Prompt, StringComparison.Ordinal); // 튜닝의 응답 규칙이 아니다
        Assert.Null(second.OutputSchema);
        Assert.Equal("general", rig.Sessions[1].Kind);

        // 같은 흐름의 튜닝은 상한이 없다.
        await rig.StartTuning();
        await rig.Flow.FollowUpAsync("더");
        Assert.Equal(AiAccessLevel.QueryRows, rig.Driver.Requests[^1].Context.AccessLevel);
        Assert.Equal(AiAccessLevel.QueryRows, rig.Driver.Requests[^1].Session!.Level);
        Assert.Contains("Response style", rig.Driver.Requests[^1].Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_tuning_makes_a_report_and_a_command_does_not_inherit_it()
    {
        var rig = new Rig();
        rig.Driver.Result = JsonDocument.Parse(ResultJson).RootElement.Clone();

        await rig.StartTuning();
        Assert.True(rig.Chat.CanSaveReport);

        await rig.StartCommand(new StructuredSpec()); // 새 대화가 앞 레포트 재료를 지운다
        Assert.False(rig.Chat.CanSaveReport);

        await rig.StartCommand(new FreeSpec());
        Assert.False(rig.Chat.CanSaveReport);

        await rig.StartTuning();
        Assert.True(rig.Chat.CanSaveReport); // 튜닝 레포트는 그대로
    }

    [Fact]
    public async Task A_structured_command_uses_its_schema_label_and_tab_suffix_and_never_the_index_path_while_free_text_applies_nothing()
    {
        var rig = new Rig();
        rig.Driver.Result = JsonDocument.Parse(ResultJson).RootElement.Clone();

        await rig.StartCommand(new StructuredSpec());

        var request = Assert.Single(rig.Driver.Requests);
        Assert.Equal(TuningResult.Schema().GetRawText(), request.OutputSchema!.Value.GetRawText());
        Assert.StartsWith("AI 수정 — ", Assert.Single(rig.Editor.Proposed).Description, StringComparison.Ordinal);
        var tab = Assert.Single(rig.Tabs); // 인덱스 후보 탭은 열리지 않는다
        Assert.Equal("SQL (수정)", tab.Title);
        Assert.DoesNotContain(rig.AssistantTexts, t => t.Contains("인덱스 후보", StringComparison.Ordinal));
        Assert.Contains("구조 결과 명령", rig.Chat.Entries.OfType<ChatUserEntry>().First().Text, StringComparison.Ordinal);

        var free = new Rig();
        free.Driver.Result = JsonDocument.Parse(ResultJson).RootElement.Clone();
        await free.StartCommand(new FreeSpec());
        Assert.Null(Assert.Single(free.Driver.Requests).OutputSchema);
        Assert.Empty(free.Tabs);
        Assert.Empty(free.Editor.Proposed);
        Assert.Null(free.Flow.LastResult);
    }

    [Fact]
    public async Task Flow_notices_in_the_apply_path_name_the_command_and_not_the_tuning()
    {
        var command = new Rig();
        command.Driver.Result = JsonDocument.Parse(ResultJson).RootElement.Clone();
        command.Editor.StatementText = "SELECT 1 FROM dual"; // 반영하기 전에 문장이 바뀐 것을 흉내 낸다

        await command.StartCommand(new StructuredSpec());

        Assert.Contains("구조 결과 중에 문장이 바뀌어", Assert.Single(command.AssistantTexts, t => t.Contains("문장이 바뀌어", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Empty(command.Tabs);
        Assert.DoesNotContain(command.AssistantTexts, t => t.Contains("튜닝", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_conversation_kind_never_leaks_between_tuning_commands_and_general_chat()
    {
        var rig = new Rig();
        rig.Driver.Result = JsonDocument.Parse(ResultJson).RootElement.Clone();

        await rig.StartCommand(new FreeSpec());
        Assert.IsType<FreeSpec>(rig.Flow.Command);

        await rig.StartCommand(new StructuredSpec()); // 명령 → 다른 명령: 앞 상한(Metadata)을 물려받지 않는다
        Assert.Equal(AiAccessLevel.Plan, rig.Driver.Requests[^1].Context.AccessLevel);
        await rig.StartCommand(new FreeSpec()); // 반대로 뒤 명령의 상한(Plan)이 아니라 자기 상한
        Assert.Equal(AiAccessLevel.Metadata, rig.Driver.Requests[^1].Context.AccessLevel);

        await rig.StartTuning(); // 명령 → 튜닝: 상한·스키마·레포트 조건을 물려받지 않는다
        Assert.Null(rig.Flow.Command);
        var tuning = rig.Driver.Requests[^1];
        Assert.Equal(AiAccessLevel.QueryRows, tuning.Context.AccessLevel);
        Assert.Equal(TuningResult.Schema().GetRawText(), tuning.OutputSchema!.Value.GetRawText());
        Assert.Contains("Tune this Oracle SQL statement", tuning.Prompt, StringComparison.Ordinal);
        Assert.Equal("tuning", rig.Sessions[^1].Kind);
        Assert.True(rig.Chat.CanSaveReport);

        await rig.StartCommand(new FreeSpec()); // 튜닝 → 명령
        Assert.IsType<FreeSpec>(rig.Flow.Command);
        Assert.False(rig.Chat.CanSaveReport);

        rig.Chat.StartNewConversation(); // 대화 비우기: 일반 대화
        Assert.Null(rig.Flow.Command);
        await rig.Flow.FollowUpAsync("그냥 질문");
        var general = rig.Driver.Requests[^1];
        Assert.Null(general.OutputSchema);
        Assert.Contains("general conversation", general.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("RULES-FREE", general.Prompt, StringComparison.Ordinal);
        rig.Chat.StartConversation("수동으로 튜닝 대화처럼 되돌림"); // 일반 대화를 거친 뒤에는 앞 명령이 되살아나지 않는다
        Assert.Null(rig.Flow.Command);
    }

    [Fact]
    public async Task The_target_statement_comes_from_the_target_not_the_cursor_and_the_sql_id_reaches_the_context()
    {
        var rig = new Rig();
        rig.Editor.StatementText = "SELECT cursor_col FROM dual";

        await rig.StartCommand(new FreeSpec(), "SELECT err_col FROM broken", sqlId: "7h35uxf5uhmm1");

        var request = Assert.Single(rig.Driver.Requests);
        Assert.Contains("SELECT err_col FROM broken", request.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("cursor_col", request.Prompt, StringComparison.Ordinal);
        Assert.Equal("SELECT err_col FROM broken", request.Context.StatementText);
        Assert.Equal("7h35uxf5uhmm1", request.Context.SqlId);
    }

    [Fact]
    public void Masked_offset_follows_the_masked_text_and_fails_closed()
    {
        var masker = new FakeMasker();
        const string sql = "SELECT 'AB' x, y FROM t";
        var vault = masker.CreateVault(sql);
        var masked = masker.Mask(sql, vault).Text;
        var target = new AiCommandTarget(Statement(sql));
        var input = new AiCommandPromptInput(target, masked, vault, masker, Full);
        var original = sql.IndexOf("y FROM", StringComparison.Ordinal);

        Assert.Equal(masked.IndexOf("y FROM", StringComparison.Ordinal), input.MaskedOffset(original));
        Assert.Null(input.MaskedOffset(sql.IndexOf('A', StringComparison.Ordinal))); // 문자열 한가운데: 가릴 수 없다
        Assert.Null(input.MaskedOffset(sql.Length + 1));
        Assert.Null(input.MaskedOffset(-1));
        Assert.Null(new AiCommandPromptInput(target, masked, vault, null, Full).MaskedOffset(original)); // 가림 구현이 없다

        // 앞부분과 전체의 가림 모양이 다르면(DATE 키워드·주석 한가운데 등) 어긋난 위치를 내지 않고 null.
        Assert.Null(new AiCommandPromptInput(target, masked, vault, new ShapeShiftingMasker(), Full).MaskedOffset(original));

        var plain = new AiCommandPromptInput(target, sql, null, masker, Full);
        Assert.False(plain.Masked);
        Assert.Equal(original, plain.MaskedOffset(original)); // 가리지 않는 대화는 그대로
    }

    // ── 실제 AiTuningLauncher 를 지나는 경로 ──

    private sealed class Services(Dictionary<Type, object> map) : IServiceProvider
    {
        public object? GetService(Type serviceType) => map.GetValueOrDefault(serviceType);
    }

    private sealed class LauncherRig
    {
        /// <param name="sqlDocumentActive">활성 문서가 SQL 작업 공간 문서인가.</param>
        /// <param name="tabConnection">활성 탭의 접속 id(null 이면 접속 표시 없음 = 지금 접속의 탭).</param>
        public LauncherRig(bool sqlDocumentActive = true, Guid? tabConnection = null)
        {
            var bus = new EventBus();
            Oranda.App.Features.Execution.ExecutionState.For(bus).FocusPending(Dev);
            Launcher = new AiTuningLauncher(new Services(new()
            {
                [typeof(IEventBus)] = bus,
                [typeof(IEditorContext)] = new LauncherEditor(tabConnection),
                [typeof(IAiDriver)] = Driver,
            }))
            {
                ChatOverride = () => Task.FromResult<AiChatViewModel?>(Chat),
                SqlDocumentActiveProvider = () => sqlDocumentActive,
                SessionTracker = session => session,
            };
        }

        public ScriptDriver Driver { get; } = new();

        public AiChatViewModel Chat { get; } = new();

        public AiTuningLauncher Launcher { get; }
    }

    private sealed class LauncherEditor(Guid? tabConnection) : IEditorContext
    {
        public EditorSnapshot? GetActiveDocument() => new(Guid.NewGuid(), Cursor, new TextRange(0, 0), 0) { ConnectionProfileId = tabConnection };

        public SqlStatement? GetCurrentStatement() => Statement(Cursor);

        public Task<bool> ProposeEditAsync(TextEdit edit, CancellationToken ct) => Task.FromResult(false);
    }

    [Fact]
    public async Task A_command_on_a_document_that_is_not_the_current_sql_tab_sends_nothing_and_starts_no_conversation()
    {
        var rig = new LauncherRig(sqlDocumentActive: false);

        await rig.Launcher.RunCommandAsync(new FreeSpec(), CancellationToken.None);

        Assert.Equal(AiTuningLauncher.CommandOtherDocumentText, Assert.Single(rig.Chat.Entries.OfType<ChatAssistantEntry>()).Text);
        Assert.Empty(rig.Driver.Requests);
        Assert.False(rig.Chat.IsTuning);
    }

    [Fact]
    public async Task A_command_on_a_tab_of_another_connection_sends_nothing_while_the_shown_connections_own_tab_passes()
    {
        var other = new LauncherRig(tabConnection: Guid.NewGuid()); // 보이는 접속(Dev)과 다른 접속의 SQL 탭

        await other.Launcher.RunCommandAsync(new FreeSpec(), CancellationToken.None);

        Assert.Equal(AiTuningLauncher.CommandOtherDocumentText, Assert.Single(other.Chat.Entries.OfType<ChatAssistantEntry>()).Text);
        Assert.Empty(other.Driver.Requests);
        Assert.False(other.Chat.IsTuning);

        var own = new LauncherRig(tabConnection: Dev.Id); // 같은 접속의 탭은 막히지 않는다
        await own.Launcher.RunCommandAsync(new FreeSpec(), CancellationToken.None);
        Assert.Single(own.Driver.Requests);
    }

    [Fact]
    public async Task A_follow_up_of_a_masked_command_conversation_carries_no_original_literal_or_comment_body()
    {
        var rig = new Rig { Mask = true };
        await rig.StartCommand(new FreeSpec());

        await rig.Flow.FollowUpAsync("조건을 더 자세히 설명해 줘");

        Assert.Equal(2, rig.Driver.Requests.Count);
        var followUp = rig.Driver.Requests[1];
        Assert.DoesNotContain("SECRET", followUp.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("메모본문", followUp.Prompt, StringComparison.Ordinal);
        Assert.Contains("placeholders such as :v1", followUp.Prompt, StringComparison.Ordinal); // 같은 사전의 자리표시를 그대로 쓰게 한다
        Assert.Contains("RULES-FREE", followUp.Prompt, StringComparison.Ordinal);
        Assert.Same(rig.Sessions[0].Vault, rig.Sessions[1].Vault); // 도구 결과도 같은 사전으로 가린다
        Assert.NotNull(rig.Sessions[1].Vault);
    }

    [Fact]
    public async Task A_refusal_from_the_command_is_shown_and_nothing_is_sent()
    {
        var rig = new LauncherRig();

        await rig.Launcher.RunCommandAsync(new FreeSpec { OnPrepare = _ => AiCommandPreparation.Refuse("보낼 수 없는 이유") }, CancellationToken.None);

        Assert.Equal("보낼 수 없는 이유", Assert.Single(rig.Chat.Entries.OfType<ChatAssistantEntry>()).Text);
        Assert.Empty(rig.Driver.Requests);
        Assert.False(rig.Chat.IsTuning);
    }

    [Fact]
    public async Task Through_the_real_launcher_the_ceiling_survives_the_context_refresh_of_a_follow_up()
    {
        var rig = new LauncherRig();

        await rig.Launcher.RunCommandAsync(new FreeSpec(), CancellationToken.None);

        var first = Assert.Single(rig.Driver.Requests);
        Assert.Equal(AiAccessLevel.Metadata, first.Session!.Level);
        Assert.Equal("DEV-ERP", first.Context.ConnectionName);
        Assert.True(rig.Chat.IsTuning);

        rig.Chat.Draft = "이어서";
        rig.Chat.SendCommand.Execute(null); // 런처의 RefreshContextAsync 가 접속의 수준(QueryRows)으로 되돌린다
        for (var i = 0; i < 100 && rig.Driver.Requests.Count < 2; i++)
        {
            await Task.Delay(20);
        }

        Assert.Equal(2, rig.Driver.Requests.Count);
        var second = rig.Driver.Requests[1];
        Assert.Equal(AiAccessLevel.Metadata, second.Context.AccessLevel);
        Assert.Equal(AiAccessLevel.Metadata, second.Session!.Level);
        Assert.Equal("s-1", second.ResumeId);
    }
}
