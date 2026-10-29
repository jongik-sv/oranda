using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
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
public sealed class AiTuningFlow(
    IEditorContext editor,
    IAiDriver driver,
    AiChatViewModel chat,
    Action<string, string> openTab,
    Func<AiContext, LiteralVault?, AgentSession?>? sessionFactory = null,
    Func<AiContext, CancellationToken, Task<AiContext?>>? refreshContext = null,
    ISqlLiteralMasker? masker = null,
    Func<AiContext, bool>? maskRequired = null)
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
        _generation++;
        _context = context with { StatementText = statement.Text }; // 원문: 범위 검사·추출기가 쓴다(LLM 으로 가지 않는다)
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

            _context = fresh;
        }

        // 이어 말하기 글은 가리지 않고(사용자가 쓴 글) 자리표시를 그대로 쓰라는 안내만 덧붙인다.
        var prompt = _vault is null ? text : text + Environment.NewLine + Environment.NewLine + PlaceholderReminder(_vault);
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

    /// <summary>리터럴 값을 자리표시로 가렸다는 안내. 값은 싣지 않고 접두만 쓴다.</summary>
    private static string PlaceholderReminder(LiteralVault vault) =>
        $"Literal values in the SQL were replaced with placeholders such as {vault.Placeholder(1)}. Use the same placeholders unchanged when you pass SQL to the tools and when you write SQL in your answer; never guess the original values.";

    private async Task RunAsync(string prompt, string? resumeId, string? userText, CancellationToken ct)
    {
        var context = _context!;
        var choice = _choice!;
        var vault = _vault; // 결과를 반영하는 동안(미리보기 창) 새 대화가 시작돼도 이 대화의 사전·문장을 쓴다
        var target = _target!;
        var userLine = resumeId is null ? "이 문장 튜닝" + (choice.Benchmark ? " · 벤치마크 켬" : string.Empty) : userText ?? prompt;
        chat.BeginTurn(userLine);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _running = cts;
        var session = sessionFactory?.Invoke(context, vault);
        var request = new AiRequest(prompt, context, resumeId, TuningResult.Schema(), choice.ModelId == "default" ? null : choice.ModelId, session);
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
            await ApplyResultAsync(structured, choice, vault, target, ct).ConfigureAwait(true);
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

    private async Task ApplyResultAsync(JsonElement structured, TuningStartChoice choice, LiteralVault? vault, SqlStatement target, CancellationToken ct)
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
        foreach (var rewrite in result.Rewrites)
        {
            chat.AddAssistantText($"[{rewrite.Label}] {rewrite.Rationale}" + (string.IsNullOrWhiteSpace(rewrite.Benchmark) ? string.Empty : $" ({rewrite.Benchmark})"));
        }

        // 편집기·탭으로 가는 글은 자리표시를 원래 값으로 되돌린 뒤에만 넣는다. 되돌리지 못하면(모르는 자리표시) 넣지 않고 알린다.
        if (result.Rewrites.Count > 0 && TryRestore(result.Rewrites[0].Sql, $"재작성 후보 「{result.Rewrites[0].Label}」", vault, out var restoredSql))
        {
            await ProposeRewriteAsync(result.Rewrites[0] with { Sql = restoredSql }, target, ct).ConfigureAwait(true);
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
                openTab("AI 인덱스 후보", BuildIndexText(restored, vault is not null));
                chat.AddAssistantText($"인덱스 후보 {restored.Count}개를 새 탭에 넣었습니다. 실행은 하지 않았습니다.");
            }
        }
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

    private async Task ProposeRewriteAsync(TuningRewrite rewrite, SqlStatement target, CancellationToken ct)
    {
        var current = editor.GetCurrentStatement();

        // 튜닝하는 동안 사용자가 문장을 고쳤으면 옛 범위에 덮어쓰지 않는다.
        if (current is null || current.Text != target.Text || current.Range != target.Range)
        {
            chat.AddAssistantText("튜닝하는 사이 문장이 바뀌어 제안을 편집기에 넣지 않았습니다.");
            return;
        }

        var accepted = await editor.ProposeEditAsync(new TextEdit(target.Range, MatchTerminator(target.Text, rewrite.Sql)) { Description = "AI 튜닝 — " + rewrite.Label }, ct).ConfigureAwait(true);
        chat.AddAssistantText(accepted ? "제안을 받아들였습니다. Ctrl+Z 한 번으로 되돌릴 수 있습니다." : "제안을 받아들이지 않았습니다.");
    }

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
