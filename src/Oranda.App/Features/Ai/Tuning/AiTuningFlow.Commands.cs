using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Features.Ai.Tuning.Commands;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Settings;

namespace Oranda.App.Features.Ai.Tuning;

/// <summary>
/// AI 명령(SQL 설명·실행 계획 해설·오류 고치기)의 시작. 흐름 객체는 튜닝과 같은 하나를 쓰고(대화 창당 하나라는 전제: 멈춤 연결·<c>IsRunning</c>),
/// 명령별 내용은 <see cref="AiCommandSpec"/> 이 정한다. 가림·보낼 곳·이어 말하기·제안 반영은 튜닝 경로를 그대로 재사용한다.
/// </summary>
public sealed partial class AiTuningFlow
{
    /// <summary>가림이 필요한데 가림 구현이 없을 때 대화 창에 남기는 안내(명령).</summary>
    public const string CommandMaskerMissingText = "이 접속은 SQL 의 리터럴 값을 가려서 보내야 하는데 가림 기능을 쓸 수 없어 보내지 않았습니다.";

    /// <summary>문장을 가리지 못했을 때 대화 창에 남기는 안내(명령).</summary>
    public const string CommandMaskFailedText = "문장의 리터럴 값을 가리지 못해(닫히지 않은 문자열 등) 보내지 않았습니다. 문장을 고친 뒤 다시 시도하세요.";

    /// <summary>접속의 AI 허용 수준이 「끔」 이라 명령을 시작하지 않았을 때의 안내.</summary>
    public const string CommandAccessOffText = "이 접속은 AI 허용 수준이 「끔」 이라 보내지 않았습니다. 접속 편집에서 허용 수준을 바꾼 뒤 다시 시도하세요.";

    /// <summary>진행 중이라 새로 시작하지 않았을 때의 안내(명령).</summary>
    public const string CommandBusyText = "AI 작업이 진행 중입니다. 멈춤을 누른 뒤 다시 시작하세요.";

    /// <summary>
    /// 명령 하나를 새 대화로 시작한다. 보내지 않기로 한 경우(진행 중·허용 수준 끔·문장 비음·가림 불가)는 대화 창에 이유만 남기고 아무것도 바꾸지 않는다 —
    /// 진행 중인 앞 대화의 종류(<see cref="Command"/>)·문맥·사전·세대는 시작이 확정된 뒤에만 바뀐다.
    /// </summary>
    /// <param name="spec">명령 계약.</param>
    /// <param name="commandTarget">시작 준비(<see cref="AiCommandSpec.PrepareAsync"/>)가 정한 대상. 편집기의 현재 문장을 다시 읽지 않는다.</param>
    /// <param name="context">접속의 지금 설정으로 만든 문맥(실효 허용 수준). 명령의 상한은 이 안에서 건다.</param>
    public async Task StartCommandAsync(AiCommandSpec spec, AiCommandTarget commandTarget, AiContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(commandTarget);
        ArgumentNullException.ThrowIfNull(context);
        if (IsRunning || _preparing)
        {
            chat.AddAssistantText(CommandBusyText);
            return;
        }

        var statement = commandTarget.Statement;
        if (string.IsNullOrWhiteSpace(statement.Text))
        {
            chat.AddAssistantText("보낼 문장이 비어 있어 보내지 않았습니다.");
            return;
        }

        if (context.AccessLevel == AiAccessLevel.Off)
        {
            chat.AddAssistantText(CommandAccessOffText);
            return;
        }

        // 튜닝과 같은 가림 경로. 못 하면 원래 글을 보내지 않고 시작을 거부한다.
        if (!TryMaskStatement(context, statement, spec.MaskerMissingText ?? CommandMaskerMissingText, spec.MaskFailedText ?? CommandMaskFailedText, out var vault, out var promptStatement))
        {
            return;
        }

        var choice = new TuningStartChoice(Benchmark: false, IndexDdl: false, TuningStartInfo.DefaultModelId); // 벤치마크·인덱스 없음, 모델·effort 는 설정을 따른다
        var capped = context with
        {
            StatementText = statement.Text, // 원문: 범위 검사·추출기가 쓴다(LLM 으로 가지 않는다)
            SqlId = commandTarget.SqlId,
            AllowRiskyFunctions = false,
            AccessLevel = Cap(spec, context.AccessLevel),
        };

        _command = spec;
        _route = CurrentTarget();
        _choice = choice;
        _target = statement;
        _vault = vault;
        _promptStatement = promptStatement;
        _startedAt = DateTimeOffset.Now;
        _startTabId = tabs is null ? null : commandTarget.TabId ?? editor.GetActiveDocument()?.TabId;
        _indexTabId = null;
        _tuneTabId = null;
        _generation++;
        _reportTurns.Clear();
        _context = capped;
        chat.StartConversation(spec.ConversationTitle + " — " + Shorten(promptStatement, 40));
        chat.AddAssistantText(SentLine(spec, promptStatement, vault is not null)); // 첫 줄: 무엇을 보냈고 가렸는지
        chat.IsBusy = true;
        try
        {
            var input = new AiCommandPromptInput(commandTarget, promptStatement, vault, masker, capped);
            await RunAsync(BuildCommandPrompt(spec, input, JoinSyntax), resumeId: null, userText: null, images: [], ct).ConfigureAwait(true);
        }
        finally
        {
            chat.IsBusy = false;
        }
    }

    /// <summary>
    /// 처음 요청 글: 명령의 지시 → 응답 규칙 → (가린 대화면) 자리표시 안내 → 문장 블록. 문장 블록과 자리표시 안내는 명령이 빠뜨릴 수 없게 흐름이 붙인다.
    /// </summary>
    internal static string BuildCommandPrompt(AiCommandSpec spec, AiCommandPromptInput input, AiJoinSyntax joinSyntax = AiJoinSyntax.Oracle)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(input);
        var text = new StringBuilder();
        text.AppendLine(spec.BuildInstruction(input));
        text.AppendLine(CommandRules(spec, joinSyntax));
        if (input.Vault is not null)
        {
            text.AppendLine(PlaceholderReminder(input.Vault));
        }

        text.AppendLine();
        text.AppendLine("<statement>");
        text.AppendLine(input.PromptStatement);
        text.AppendLine("</statement>");
        return text.ToString();
    }

    /// <summary>명령의 응답 규칙 + (재작성을 내는 명령이면) 설정 문법의 조인 규칙. 처음 요청과 이어 말하기가 같이 쓴다.</summary>
    private static string CommandRules(AiCommandSpec spec, AiJoinSyntax joinSyntax) =>
        spec.JoinRule(joinSyntax) is { Length: > 0 } rule ? spec.ResponseRules + " " + rule : spec.ResponseRules;

    private static string SentLine(AiCommandSpec spec, string promptStatement, bool masked)
    {
        var line = $"보낸 문장: 「{Shorten(promptStatement, 60)}」 · "
            + (masked ? "리터럴 값과 주석은 가려서 보냈습니다." : "가리지 않고 보냈습니다.");
        return spec.SentNote(masked) is { Length: > 0 } note ? line + " " + note : line;
    }
}
