using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Features.Ai.Chat;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Execution;
using Oranda.Core.Extensibility;
using Oranda.Core.Text;

namespace Oranda.App.Features.Ai.Tuning;

/// <summary>
/// 「이 문장 튜닝」 실행 흐름(u5): 시작 확인 창의 선택으로 <see cref="AiRequest"/> 를 만들고, <see cref="IAiDriver"/> 사건을 대화 창에 옮기고,
/// 끝의 구조 결과(<see cref="TuningResult"/>)를 반영한다. 반영 규칙은 둘뿐이다.
/// <list type="bullet">
/// <item>재작성 후보 → <see cref="IEditorContext.ProposeEditAsync"/> 로만(차이 미리보기에서 사용자가 받아들일 때만 바뀐다).</item>
/// <item>인덱스 CREATE 문안 → 새 탭에 글로만 넣는다. 실행하지 않는다.</item>
/// </list>
/// 멈춤은 진행 중인 요청을 취소하고, 이어 말하기는 앞 요청의 <c>ResumeId</c> 로 다음 요청을 보낸다.
/// </summary>
/// <param name="editor">활성 편집기(문장 읽기와 제안 넣기).</param>
/// <param name="driver">AI 호출 경로.</param>
/// <param name="chat">대화 창 모델.</param>
/// <param name="openTab">글을 새 탭으로 연다(제목, 글).</param>
/// <param name="refreshContext">이어 말하기 때 접속의 지금 설정으로 문맥을 다시 만든다(옛 허용 수준으로 새 요청이 열리지 않게). null 을 돌려주면 이어 말하지 않는다.</param>
/// <param name="sessionFactory">도구 호출의 실행 범위를 만든다(문맥, 이 대화의 값 사전). null 이거나 null 을 돌려주면 도구 없이 보낸다.</param>
/// <param name="masker">SQL 글의 리터럴을 가리고 되돌리는 구현. 가림이 필요한데 null 이면 시작하지 않는다.</param>
/// <param name="maskRequired">이 문맥의 접속에서 리터럴 가림이 필요한가(운영 접속이거나 설정이 켜짐). null 이면 가리지 않는다.</param>
/// <param name="tabs">
/// 시작한 탭을 기억해 그 탭 기준으로 제안을 넣고, 인덱스 후보 탭을 대화당 하나만 열고 바꾼다. null 이면 활성 탭의 문장으로 비교하고 인덱스 후보를 매번 <paramref name="openTab"/> 으로 연다.
/// </param>
/// <param name="showComparison">적용한 제안을 보기만 하는 비교 창(제목, 원본, 적용한 글). null 이면 적용한 카드에 「비교 보기」 가 없다.</param>
public sealed partial class AiTuningFlow(
    IEditorContext editor,
    IAiDriver driver,
    AiChatViewModel chat,
    Action<string, string> openTab,
    Func<AiContext, LiteralVault?, AgentSession?>? sessionFactory = null,
    Func<AiContext, CancellationToken, Task<AiContext?>>? refreshContext = null,
    ISqlLiteralMasker? masker = null,
    Func<AiContext, bool>? maskRequired = null,
    IEditorTabs? tabs = null,
    Func<string, string, string, Task>? showComparison = null)
{
    /// <summary>도구를 부르는 횟수 예산(프롬프트로 알린다).</summary>
    public const int ToolBudget = 12;

    /// <summary>가림이 필요한데 가림 구현이 없을 때 대화 창에 남기는 안내.</summary>
    public const string MaskerMissingText = "이 접속은 SQL 의 리터럴 값을 가려서 보내야 하는데 가림 기능을 쓸 수 없어 튜닝을 시작하지 않았습니다.";

    /// <summary>문장을 가리지 못했을 때 대화 창에 남기는 안내.</summary>
    public const string MaskFailedText = "문장의 리터럴 값을 가리지 못해(닫히지 않은 문자열 등) 튜닝을 시작하지 않았습니다. 문장을 고친 뒤 다시 시도하세요.";

    private AiContext? _context;
    private TuningStartChoice? _choice;
    private SqlStatement? _target;
    private LiteralVault? _vault;
    private Guid? _startTabId;
    private Guid? _indexTabId;
    private CancellationTokenSource? _running;
    private TuningResult? _lastResult;
    private int _generation;

    /// <summary>대화 창의 멈춤·이어 말하기를 이 흐름에 잇는다(창당 한 번).</summary>
    public void Attach()
    {
        chat.StopRequested += (_, _) => Stop();
        chat.FollowUpRequested += async (_, text) =>
        {
            try
            {
                await FollowUpAsync(text).ConfigureAwait(true);
            }
#pragma warning disable CA1031 // 이벤트 처리기라 예외를 밖으로 내면 앱 전역 미처리 예외가 된다: 대화 창에 알린다.
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                chat.AddAssistantText("이어 말하는 중 오류가 났습니다: " + ex.Message);
            }
        };
    }

    /// <summary>마지막으로 반영한 구조 결과(시험·이어 말하기 확인용).</summary>
    public TuningResult? LastResult => _lastResult;

    /// <summary>
    /// 대화가 새로 확정될 때마다 늘어난다(시작이 문장 없음·가림 불가로 거부되면 그대로). 호출하는 쪽이 이 값으로 「이번 시작이 앞 대화를 바꿨는지」 를 안다:
    /// 거부된 시작이 대화에 딸린 상태(접속 프로필 등)를 바꿔 두면 앞 대화의 이어 말하기가 엉뚱한 접속으로 간다.
    /// </summary>
    public int Generation => _generation;

    /// <summary>요청이 진행 중이다(다시 시작하거나 이어 말할 수 없다).</summary>
    public bool IsRunning => _running is not null;

    /// <summary>진행 중인 요청을 멈춘다.</summary>
    public void Stop() => _running?.Cancel();

    /// <summary>
    /// 새 튜닝을 시작한다. 시작할 문장이 없으면(커서가 문장 밖) 대화 창에 안내만 남기고 끝낸다.
    /// </summary>
    public async Task StartAsync(TuningStartChoice choice, AiContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(choice);
        ArgumentNullException.ThrowIfNull(context);
        if (IsRunning)
        {
            chat.AddAssistantText("튜닝이 진행 중입니다. 멈춤을 누른 뒤 다시 시작하세요.");
            return;
        }

        var statement = editor.GetCurrentStatement();
        if (statement is null)
        {
            chat.AddAssistantText("튜닝할 문장을 찾지 못했습니다. 커서를 문장 안에 두고 다시 시도하세요.");
            return;
        }

        // 가림이 필요하면 대화 하나에 쓸 사전을 만들고 문장을 가린다. 못 하면 원래 글을 보내지 않고 시작을 거부한다.
        LiteralVault? vault = null;
        var promptStatement = statement.Text;
        if (maskRequired?.Invoke(context) == true)
        {
            if (masker is null)
            {
                chat.AddAssistantText(MaskerMissingText);
                return;
            }

            try
            {
                vault = masker.CreateVault(statement.Text);
                var masked = masker.Mask(statement.Text, vault);
                if (!masked.Succeeded)
                {
                    chat.AddAssistantText(MaskFailedText);
                    return;
                }

                vault.RememberStatement(masked.Text, statement.Text);
                promptStatement = masked.Text;
            }
#pragma warning disable CA1031 // 가림 구현의 어떤 실패든 원래 글을 보내는 쪽으로 이어지면 안 된다: 시작을 거부한다.
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                chat.AddAssistantText(MaskFailedText);
                return;
            }
        }

        _choice = choice;
        _target = statement;
        _vault = vault;
        _startTabId = tabs is null ? null : editor.GetActiveDocument()?.TabId; // 문장을 읽은 탭: 튜닝하는 사이 다른 탭이 활성이 돼도 이 탭 기준으로 제안을 넣는다
        _indexTabId = null; // 인덱스 후보 탭은 대화 하나에 하나다
        _generation++;
        _context = context with { StatementText = statement.Text, AllowRiskyFunctions = choice.RunRiskyFunctions }; // 원문: 범위 검사·추출기가 쓴다(LLM 으로 가지 않는다)
        chat.StartConversation("튜닝 — " + Shorten(promptStatement, 40));
        await RunAsync(BuildPrompt(choice, promptStatement, vault), resumeId: null, userText: null, ct).ConfigureAwait(true);
    }

    /// <summary>앞 대화에 이어 말한다(<see cref="AiChatViewModel.ResumeId"/> 가 없으면 새 대화로 보낸다).</summary>
    public async Task FollowUpAsync(string text, CancellationToken ct = default)
    {
        if (_context is null || _choice is null)
        {
            return;
        }

        if (IsRunning)
        {
            return;
        }

        if (refreshContext is not null)
        {
            var fresh = await refreshContext(_context, ct).ConfigureAwait(true);
            if (fresh is null || fresh.AccessLevel == AiAccessLevel.Off)
            {
                chat.AddAssistantText("이 접속의 AI 허용 수준이 바뀌어 이어 말할 수 없습니다. 「AI 튜닝」 으로 다시 시작하세요.");
                return;
            }

            // 이어 말하는 사이 접속이 운영으로 바뀌었는데 이 대화는 사전 없이 시작했다면, 앞서 보낸 글에 원래 값이 이미 실려 있다: 새로 시작하게 한다.
            if (_vault is null && maskRequired?.Invoke(fresh) == true)
            {
                chat.AddAssistantText("이 접속은 이제 리터럴 값을 가려서 보내야 해 이어 말할 수 없습니다. 「AI 튜닝」 으로 다시 시작하세요.");
                return;
            }

            _context = fresh with { AllowRiskyFunctions = _choice!.RunRiskyFunctions }; // 대화마다 고른 값을 이어 말하기에도 쓴다
        }

        // 이어 말하기 글은 가리지 않고(사용자가 쓴 글) 자리표시를 그대로 쓰라는 안내만 덧붙인다.
        var prompt = text + Environment.NewLine + Environment.NewLine + ResponseStyle + Environment.NewLine + KeepCommentsRule
            + (_vault is null ? string.Empty : Environment.NewLine + PlaceholderReminder(_vault));
        await RunAsync(prompt, chat.ResumeId, text, ct).ConfigureAwait(true);
    }

    /// <summary>시작 확인 창의 선택과 대상 문장으로 요청 글을 만든다(문장은 씨앗, 나머지는 도구로 가져오게 한다). <paramref name="vault"/> 가 있으면 문장은 이미 가려진 글이고 자리표시 안내가 붙는다.</summary>
    public static string BuildPrompt(TuningStartChoice choice, string statement, LiteralVault? vault = null)
    {
        ArgumentNullException.ThrowIfNull(choice);
        var text = new StringBuilder();
        text.AppendLine("Tune this Oracle SQL statement. Use the Oranda tools to inspect the plan, tables, indexes and statistics; do not guess.");
        text.AppendLine(CultureInfo.InvariantCulture, $"Call at most {ToolBudget} tools. Answer in Korean. Return the structured result.");
        text.AppendLine(choice.Benchmark
            ? "You may compare candidates with benchmark_queries and compare_results."
            : "Do not benchmark: do not run candidate statements.");
        text.AppendLine(choice.IndexDdl
            ? "Index CREATE statements may be suggested as text only; they are never executed."
            : "Do not suggest indexes; leave index_suggestions empty.");
        text.AppendLine(ResponseStyle);
        text.AppendLine(KeepCommentsRule);
        if (vault is not null)
        {
            text.AppendLine(PlaceholderReminder(vault));
        }

        text.AppendLine();
        text.AppendLine("<statement>");
        text.AppendLine(statement);
        text.AppendLine("</statement>");
        return text.ToString();
    }

    /// <summary>
    /// 응답 규칙(처음 요청과 이어 말하기 모두). 결론 한 줄(굵게) → 원문·제안 수치 표 → 근거 블릿 → 제안 → 뺀 후보 → 다음 → 확인할 것(인용 한 줄).
    /// 대화 창이 결론 줄+표를 결론 상자로, 인용을 주의 상자로 그린다. 본문 SQL 은 구조 결과의 제안 항목에만 둔다.
    /// 도구·함수 이름은 쓰지 않고 사람 말(벤치마크·실행 계획·튜닝 정보)로, 지표 이름도 한국어로 쓴다.
    /// </summary>
    internal const string ResponseStyle =
        "Response style (applies to every answer, including follow-ups; the summary field is this chat answer in Markdown): write in Korean and be brief. "
        + "Use this order and skip a part that has nothing to say. "
        + "1) The conclusion as one bold line, for example **keep_last 재작성 · 37% 빨라짐**; a second plain line may add how it was measured. "
        + "2) Right after it, a Markdown table of the numbers with three columns: 지표 | 원문 | 제안; name the metrics in Korean (경과, 논리 읽기, 결과 행, 계획 해시 and so on, not elapsed or logical reads); put numbers in the table, not in sentences. "
        + "3) ## 근거: 3 to 5 bullets, each about 40 characters on one line, ending in the noun style 「~함」 or 「~음」, quoting plan or statistics numbers. "
        + "4) ## 제안: one line per rewrite or index; put the full SQL only in the structured result, not in the chat text. "
        + "5) ## 뺀 후보: candidates you measured and dropped, one line each with why. "
        + "6) ## 다음: what could be tried next, marked 미측정 if not measured. "
        + "7) One line starting with > that says what the user must check before applying. "
        + "Use bold only for the conclusion line. No [name] tags, no long remarks in parentheses, no preamble or closing remarks, "
        + "do not narrate each tool call and do not repeat yourself. "
        + "Never write tool or function names (get_*, benchmark_queries, explain_plan and so on) in the answer; say what was done in plain words such as 벤치마크, 실행 계획, 튜닝 정보.";

    /// <summary>재작성 SQL 에 원래 주석을 남기라는 규칙(처음 요청과 이어 말하기 모두). 가린 대화면 주석 자리표시를 그대로 두라는 말이 <see cref="PlaceholderReminder"/> 에 붙는다.</summary>
    internal const string KeepCommentsRule =
        "Keep every comment of the original statement in each rewrite's sql, unchanged and next to the code it describes; do not drop, merge or reword comments.";

    /// <summary>카드 없는 재작성 후보 한 줄: 「- `이름` · 근거 · 측정」.</summary>
    private static string RewriteLine(TuningRewrite rewrite) =>
        $"- `{OneLine(rewrite.Label)}` · {OneLine(rewrite.Rationale)}" + (string.IsNullOrWhiteSpace(rewrite.Benchmark) ? string.Empty : " · " + OneLine(rewrite.Benchmark));

    /// <summary>
    /// 벤치마크 글의 첫 「원문 → 제안」 경과(ms·s·초)에서 변화율(예 「−37%」). 벤치마크가 없거나 읽을 수 없으면 null(카드에 안 보인다).
    /// </summary>
    internal static string? ChangeText(string? benchmark)
    {
        if (string.IsNullOrWhiteSpace(benchmark) || ElapsedPair().Match(benchmark) is not { Success: true } m
            || !double.TryParse(m.Groups[1].Value.Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.Float, CultureInfo.InvariantCulture, out var before)
            || !double.TryParse(m.Groups[3].Value.Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.Float, CultureInfo.InvariantCulture, out var after))
        {
            return null;
        }

        before *= m.Groups[2].Value == "ms" ? 1 : 1000;
        after *= m.Groups[4].Value == "ms" ? 1 : 1000;
        if (before <= 0)
        {
            return null;
        }

        var percent = (int)Math.Round((after - before) / before * 100, MidpointRounding.AwayFromZero);
        return percent switch
        {
            < 0 => string.Create(CultureInfo.InvariantCulture, $"−{-percent}%"),
            > 0 => string.Create(CultureInfo.InvariantCulture, $"+{percent}%"),
            _ => "0%",
        };
    }

    [GeneratedRegex(@"(\d[\d,]*(?:\.\d+)?)\s*(ms|s|초)\s*(?:→|->)\s*(?:[^\d\s→]{1,8}\s*)?(\d[\d,]*(?:\.\d+)?)\s*(ms|s|초)", RegexOptions.CultureInvariant)]
    private static partial Regex ElapsedPair();

    /// <summary>리터럴 값을 자리표시로 가렸다는 안내. 값은 싣지 않고 접두만 쓴다. 가린 주석이 있으면 주석 자리표시도 그대로 두라고 덧붙인다.</summary>
    private static string PlaceholderReminder(LiteralVault vault) =>
        $"Literal values in the SQL were replaced with placeholders such as {vault.Placeholder(1)}. Use the same placeholders unchanged when you pass SQL to the tools and when you write SQL in your answer; never guess the original values."
        + (vault.CommentCount == 0
            ? string.Empty
            : $" Comments were replaced with placeholders such as {LiteralVault.CommentPlaceholder(1, lineComment: false)} and {LiteralVault.CommentPlaceholder(1, lineComment: true)}; keep each comment placeholder exactly as written, in the same place, in every SQL you write.");

    private async Task RunAsync(string prompt, string? resumeId, string? userText, CancellationToken ct)
    {
        var context = _context!;
        var choice = _choice!;
        var vault = _vault; // 결과를 반영하는 동안(미리보기 창) 새 대화가 시작돼도 이 대화의 사전·문장을 쓴다
        var target = _target!;
        var startTab = _startTabId;
        var userLine = resumeId is null
            ? "이 문장 튜닝" + (choice.Benchmark ? " · 벤치마크 켬" : string.Empty) + (choice.RunRiskyFunctions ? " · 위험 함수도 실행" : string.Empty)
            : userText ?? prompt;
        chat.BeginTurn(userLine);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _running = cts;
        var session = sessionFactory?.Invoke(context, vault);
        var request = new AiRequest(
            prompt, context, resumeId, TuningResult.Schema(), choice.ModelId == "default" ? null : choice.ModelId, session,
            choice.EffortId == TuningStartInfo.DefaultModelId ? null : choice.EffortId);
        JsonElement? result = null;
        try
        {
            await chat.RunAsync(Capture(driver.RunAsync(request, cts.Token), r => result = r, cts.Token), cts.Token).ConfigureAwait(true);
        }
        finally
        {
            _running = null;
            session?.Revoke();
        }

        if (result is { } structured && !cts.IsCancellationRequested)
        {
            await ApplyResultAsync(structured, choice, vault, target, startTab, ct).ConfigureAwait(true);
        }
    }

    /// <summary>사건을 그대로 흘려 보내면서 끝의 구조 결과를 붙잡는다.</summary>
    private static async IAsyncEnumerable<AiEvent> Capture(IAsyncEnumerable<AiEvent> events, Action<JsonElement?> onResult, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var e in events.WithCancellation(ct).ConfigureAwait(false))
        {
            if (e is AiFinal { Result: { } r })
            {
                onResult(r);
            }

            yield return e;
        }
    }

    private async Task ApplyResultAsync(JsonElement structured, TuningStartChoice choice, LiteralVault? vault, SqlStatement target, Guid? startTab, CancellationToken ct)
    {
        TuningResult result;
        try
        {
            result = TuningResult.Parse(structured);
        }
        catch (JsonException ex)
        {
            chat.AddAssistantText("튜닝 결과의 모양을 읽지 못했습니다: " + ex.Message);
            return;
        }

        _lastResult = result;
        chat.AddAssistantText(result.Summary);

        // 편집기·탭으로 가는 글은 자리표시를 원래 값으로 되돌린 뒤에만 넣는다. 되돌리지 못하면(모르는 자리표시) 넣지 않고 알린다.
        var carded = false;
        if (result.Rewrites.Count > 0 && TryRestore(result.Rewrites[0].Sql, $"재작성 후보 「{result.Rewrites[0].Label}」", vault, out var restoredSql))
        {
            // 비교 창을 열었으면 닫은 뒤에도 대화 창 카드에서 같은 제안을 다시 열 수 있게 남긴다(c6). 카드 글은 AI 가 보낸 라벨·근거 그대로다.
            var rewrite = result.Rewrites[0];
            var proposal = rewrite with { Sql = restoredSql };

            // AI 가 원래 주석(가린 대화면 주석 자리표시)을 빠뜨렸으면 제안은 그대로 두고 몇 개 빠졌는지만 알린다.
            if (MissingComments(target.Text, restoredSql) is var missing and > 0)
            {
                chat.AddAssistantText($"주석 {missing}개 빠짐: 재작성 후보 「{rewrite.Label}」 에 원래 문장의 주석 {missing}개가 없습니다. 제안은 그대로 두었으니 비교 창에서 확인하세요.");
            }

            // 받아들인 뒤 그 자리의 글(비교 창에서 고친 것까지)을 기억해 두고 「비교 보기」 로 원본과 견준다.
            string? applied = null;
            async Task<bool?> Propose(CancellationToken token)
            {
                var answer = await ProposeRewriteAsync(proposal, target, startTab, token).ConfigureAwait(true);
                if (answer == true)
                {
                    applied = AppliedText(target, startTab) ?? MatchTerminator(target.Text, proposal.Sql);
                }

                return answer;
            }

            Func<Task>? view = showComparison is null ? null
                : () => showComparison("AI 튜닝 — " + rewrite.Label + " (적용함)", target.Text, applied ?? MatchTerminator(target.Text, proposal.Sql));
            if (await Propose(ct).ConfigureAwait(true) is { } accepted)
            {
                chat.AddProposal(rewrite.Label, OneLine(rewrite.Rationale), accepted, () => Propose(CancellationToken.None), ChangeText(rewrite.Benchmark), view);
                carded = true;
            }
        }

        // 카드가 없는 후보는 블릿 한 줄씩(「[이름] 긴 글 (수치)」 꼬리표 대신).
        var rest = result.Rewrites.Skip(carded ? 1 : 0).ToList();
        if (rest.Count > 0)
        {
            chat.AddAssistantText((carded ? "## 다른 후보" : "## 재작성 후보") + "\n\n" + string.Join('\n', rest.Select(RewriteLine)));
        }

        if (choice.IndexDdl && result.IndexSuggestions.Count > 0)
        {
            List<TuningIndexSuggestion> restored = [];
            foreach (var suggestion in result.IndexSuggestions)
            {
                if (TryRestore(suggestion.Ddl, $"인덱스 후보 {suggestion.Table}", vault, out var ddl))
                {
                    restored.Add(suggestion with { Ddl = ddl });
                }
            }

            if (restored.Count > 0)
            {
                ShowIndexCandidates(BuildIndexText(restored, vault is not null), restored.Count);
            }
        }
    }

    private const string IndexTabTitle = "AI 인덱스 후보";

    /// <summary>
    /// 인덱스 후보 글을 탭에 넣는다. 대화 하나에 탭 하나만 쓴다: 열려 있으면 그 탭의 글만 바꾸고(활성 탭은 그대로),
    /// 글이 같으면 건드리지 않으며, 사용자가 닫았으면 새로 연다. 실행은 하지 않는다.
    /// </summary>
    private void ShowIndexCandidates(string text, int count)
    {
        if (tabs is null)
        {
            openTab(IndexTabTitle, text);
            chat.AddAssistantText($"인덱스 후보 {count}개를 새 탭에 넣었습니다. 실행은 하지 않았습니다.");
            return;
        }

        if (_indexTabId is { } existing && tabs.GetText(existing) is { } current)
        {
            if (current == text)
            {
                chat.AddAssistantText("인덱스 후보가 앞서 넣은 탭과 같아 그대로 두었습니다.");
                return;
            }

            if (tabs.ReplaceText(existing, text))
            {
                chat.AddAssistantText($"인덱스 후보 {count}개로 「{IndexTabTitle}」 탭의 내용을 바꾸었습니다. 실행은 하지 않았습니다.");
                return;
            }
        }

        _indexTabId = tabs.OpenText(IndexTabTitle, text);
        chat.AddAssistantText($"인덱스 후보 {count}개를 새 탭에 넣었습니다. 실행은 하지 않았습니다.");
    }

    /// <summary>
    /// 가린 대화라면 <paramref name="sql"/> 의 자리표시를 되돌린다. 사전이 없으면(가리지 않은 대화) 그대로 통과한다.
    /// 모르는 자리표시(<c>literal_unknown</c>)이거나 되돌리는 중 실패하면 false 이고 대화 창에 알린다.
    /// </summary>
    private bool TryRestore(string sql, string what, LiteralVault? vault, out string restored)
    {
        restored = sql;
        if (vault is null)
        {
            return true;
        }

        try
        {
            var result = masker?.Restore(sql, vault);
            if (result is { Succeeded: true })
            {
                restored = result.Text;
                return true;
            }

            var unknown = result?.UnknownPlaceholder;
            chat.AddAssistantText(unknown is null
                ? $"{what} 의 자리표시를 되돌리지 못해 넣지 않았습니다."
                : $"{what} 에 모르는 자리표시 {unknown} 이 있어 되돌리지 못하고 넣지 않았습니다(literal_unknown).");
        }
#pragma warning disable CA1031 // 되돌림이 어떤 이유로 실패해도 가린 글을 편집기에 넣지 않고 알리기만 한다.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            chat.AddAssistantText($"{what} 의 자리표시를 되돌리지 못해 넣지 않았습니다.");
        }

        return false;
    }

    /// <summary>
    /// 원래 문장의 주석 중 <paramref name="proposal"/>(되돌린 제안)에 없는 수. 버리는 사전 하나로 두 글을 가려 같은 주석이 같은 자리표시가 되게 한 뒤
    /// 자리표시 수를 비교한다(가린 대화·안 가린 대화 공통, 이 사전은 어디로도 보내지 않는다). 셀 수 없으면 0 이다(알리지 않는다).
    /// </summary>
    private int MissingComments(string original, string proposal)
    {
        if (masker is null)
        {
            return 0;
        }

        try
        {
            var scratch = masker.CreateVault(original);
            var before = masker.Mask(original, scratch);
            var after = masker.Mask(proposal, scratch);
            if (!before.Succeeded || !after.Succeeded)
            {
                return 0;
            }

            var kept = LiteralVault.FindCommentPlaceholders(after.Text).CountBy(p => p).ToDictionary();
            return LiteralVault.FindCommentPlaceholders(before.Text).CountBy(p => p)
                .Sum(pair => Math.Max(0, pair.Value - kept.GetValueOrDefault(pair.Key)));
        }
#pragma warning disable CA1031 // 주석 셈은 알림용이다: 어떤 실패든 제안 반영을 막지 않는다.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return 0;
        }
    }

    /// <summary>재작성 제안을 시작 탭의 문장에 비교 창으로 보인다. 받아들이면 true, 거절하면 false, 열지 못했으면(탭이 닫힘·문장이 바뀜) null.</summary>
    private async Task<bool?> ProposeRewriteAsync(TuningRewrite rewrite, SqlStatement target, Guid? startTab, CancellationToken ct)
    {
        if (tabs is not null && startTab is { } tabId)
        {
            // 시작한 탭의 글로 비교한다(인덱스 후보 탭이 열려 활성 탭이 바뀌어도 문장은 그대로다). 비교 창은 활성 탭에 적용되므로 시작 탭을 활성으로 돌린다.
            if (tabs.GetText(tabId) is null)
            {
                chat.AddAssistantText("튜닝을 시작한 탭이 닫혀 제안을 편집기에 넣지 않았습니다.");
                return null;
            }

            // 옛 범위의 글만 같은지 보면 문장 뒤에 이어 친 글(WHERE …)을 놓친다: 그 자리의 문장이 글과 범위까지 같아야 한다.
            var current = tabs.GetStatementAt(tabId, target.Range.Start);
            if (current is null || current.Text != target.Text || current.Range != target.Range)
            {
                chat.AddAssistantText("튜닝하는 사이 문장이 바뀌어 제안을 편집기에 넣지 않았습니다.");
                return null;
            }

            if (tabs.ActiveTabId != tabId && !tabs.Activate(tabId))
            {
                chat.AddAssistantText("튜닝을 시작한 탭이 닫혀 제안을 편집기에 넣지 않았습니다.");
                return null;
            }
        }
        else
        {
            var current = editor.GetCurrentStatement();

            // 튜닝하는 동안 사용자가 문장을 고쳤으면 옛 범위에 덮어쓰지 않는다.
            if (current is null || current.Text != target.Text || current.Range != target.Range)
            {
                chat.AddAssistantText("튜닝하는 사이 문장이 바뀌어 제안을 편집기에 넣지 않았습니다.");
                return null;
            }
        }

        var accepted = await editor.ProposeEditAsync(new TextEdit(target.Range, MatchTerminator(target.Text, rewrite.Sql)) { Description = "AI 튜닝 — " + rewrite.Label }, ct).ConfigureAwait(true);
        chat.AddAssistantText(accepted ? "제안을 받아들였습니다. Ctrl+Z 한 번으로 되돌릴 수 있습니다." : "제안을 받아들이지 않았습니다.");
        return accepted;
    }

    /// <summary>받아들인 직후 원래 문장 자리에 있는 글(시작 탭 기준, 없으면 활성 편집기의 문장). 읽지 못하면 null.</summary>
    private string? AppliedText(SqlStatement target, Guid? startTab) =>
        tabs is not null && startTab is { } tabId ? tabs.GetStatementAt(tabId, target.Range.Start)?.Text : editor.GetCurrentStatement()?.Text;

    /// <summary>끝의 <c>;</c> 를 원문과 같게 맞춘다(원문 범위가 <c>;</c> 를 포함하지 않으면 뺀다).</summary>
    private static string MatchTerminator(string original, string replacement)
    {
        var text = replacement.Trim();
        var originalHasSemicolon = original.TrimEnd().EndsWith(';');
        var replacementHasSemicolon = text.EndsWith(';');
        return originalHasSemicolon == replacementHasSemicolon ? text
            : originalHasSemicolon ? text + ";"
            : text.TrimEnd(';').TrimEnd();
    }

    private static string BuildIndexText(IReadOnlyList<TuningIndexSuggestion> suggestions, bool masked)
    {
        var text = new StringBuilder();
        text.AppendLine("-- AI 가 제안한 인덱스 후보입니다. Oranda 는 실행하지 않았습니다. 검토한 뒤 직접 실행하세요.");
        if (masked)
        {
            text.AppendLine("-- 설명 줄의 :v1 같은 표시는 가려서 보낸 리터럴 값의 자리표시입니다. CREATE 문의 값은 원래대로 되돌렸습니다.");
        }
        foreach (var s in suggestions)
        {
            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture, $"-- {s.Table} ({string.Join(", ", s.Columns)})");
            text.AppendLine("-- " + OneLine(s.Rationale));
            if (!string.IsNullOrWhiteSpace(s.Risk))
            {
                text.AppendLine("-- 위험: " + OneLine(s.Risk));
            }

            text.AppendLine(s.Ddl.Trim());
        }

        return text.ToString();
    }

    private static string OneLine(string value) => value.ReplaceLineEndings(" ").Trim();

    private static string Shorten(string value, int max)
    {
        var line = OneLine(value);
        return line.Length <= max ? line : line[..max] + "…";
    }
}
