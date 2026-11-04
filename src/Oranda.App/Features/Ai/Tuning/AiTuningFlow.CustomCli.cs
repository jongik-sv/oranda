using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Features.Ai.Tuning.Commands;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Settings;

namespace Oranda.App.Features.Ai.Tuning;

/// <summary>
/// AI 명령을 기타 CLI(DB 도구 없이 글만 주고받음)로 보낼 때의 준비: 명령이 기타 CLI 로 나가도 되는지 확인하고, 도구를 대신해 앱이 문맥을 미리 읽어 프롬프트에 글로 넣는다.
/// 읽기는 도구와 같은 <see cref="IAgentGate"/> 를 지나므로 허용 수준·감사·취소·제한 시간이 그대로 적용된다. 읽지 못하면 조용히 빈 문맥으로 보내지 않고 안내한 뒤 닫는다.
/// </summary>
public sealed partial class AiTuningFlow
{
    /// <summary>미리 읽은 문맥 한 덩어리의 글자 수 상한(넘으면 앞부분만 보내고 잘렸다고 글에 적는다).</summary>
    internal const int PreloadSectionMaxChars = 12_000;

    /// <summary>미리 읽은 문맥 전체의 글자 수 상한.</summary>
    internal const int PreloadTotalMaxChars = 20_000;

    /// <summary>선택 읽기에 실패했을 때 문맥 블록에 남기는 오류 글의 상한.</summary>
    private const int PreloadFailureMaxChars = 400;

    /// <summary>문맥을 읽을 길(관문·실행 범위)이 없어 보내지 않았을 때의 안내.</summary>
    public const string PreloadUnavailableText = "기타 CLI 로 보내려면 앱이 먼저 DB 정보를 읽어야 하는데 읽을 수단이 없어 보내지 않았습니다.";

    /// <summary>미리 읽는 도중 멈춤을 눌렀을 때의 안내.</summary>
    public const string PreloadStoppedText = "정보를 읽는 도중 멈춰 보내지 않았습니다.";

    /// <summary>접속 설정이 바뀌어 읽는 도중 실행 범위가 회수됐을 때의 안내.</summary>
    public const string PreloadRevokedText = "접속 설정이 바뀌거나 실행 범위가 회수되어 정보 읽기를 접고 보내지 않았습니다.";

    /// <summary>읽기가 제한 시간 안에 끝나지 않았을 때의 안내.</summary>
    public const string PreloadTimedOutText = "정보를 읽는 데 시간이 너무 걸려 보내지 않았습니다.";

    /// <summary>미리 읽기 전체의 제한 시간(시험에서 줄인다).</summary>
    internal TimeSpan PreloadTimeout { get; set; } = TimeSpan.FromMinutes(2);

    private bool _preloadStopRequested; // 사용자가 멈춤을 눌렀다(아니면 취소 원인은 제한 시간)

    private CancellationTokenSource? _preloading; // 문맥 미리 읽기 중이면 그 취소 원천(Stop 이 취소한다)

    /// <summary>미리 읽기를 마친 결과: 읽은 덩어리와, 뒤이은 실행이 이어 쓸 실행 범위(미리 읽을 것이 없으면 null).</summary>
    private sealed record CustomCliPreload(AgentSession? Session, IReadOnlyList<AiPreloadedSection> Sections, IReadOnlyList<string> Notices);

    /// <summary>
    /// 이 명령을 기타 CLI 로 보내도 되는지 확인한다. 쓸 수 없는 줄·지원하지 않는 명령·가림 대상 접속이면 이유를 대화 창에 남기고 false.
    /// 가림이 필요 없다고 확인된 경우만 통과한다(판단 쪽이 없으면 닫는다).
    /// </summary>
    private bool CustomCliCommandAllowed(AiCommandSpec spec, AiConversationTarget target, AiContext context)
    {
        if (AiTuningCustomCli.IsUnavailable(target))
        {
            chat.AddAssistantText(AiTuningCustomCli.UnavailableText);
            return false;
        }

        if (!spec.SupportsCustomCli)
        {
            chat.AddAssistantText(AiTuningCustomCli.CommandUnsupportedText);
            return false;
        }

        if (MaskingRequiredFor(context, vault: null))
        {
            chat.AddAssistantText(AiTuningCustomCli.MaskingBlockedText);
            return false;
        }

        return true;
    }

    /// <summary>
    /// 명령이 정한 도구 호출을 게이트로 차례로 읽는다. 필수 읽기가 실패(거부·실행 실패·예외·제한 시간·취소·실행 범위 회수)하면 안내하고 null 을 돌려주며 실행 범위를 회수한다.
    /// 선택 읽기의 거부·실패 결과는 「읽지 못함」 으로 문맥에 적고 계속한다(그 사실이 대화 창에도 남는다). 호출하는 쪽이 대화 상태를 바꾸기 전에 부른다.
    /// 읽는 동안 「멈춤」 을 쓸 수 있고(<see cref="AiChatViewModel.IsPreparing"/>), 전체에 <see cref="PreloadTimeout"/> 이 걸린다.
    /// </summary>
    private async Task<CustomCliPreload?> PreloadCustomCliAsync(AiCommandSpec spec, AiCommandPromptInput input, CancellationToken ct)
    {
        var calls = spec.CustomCliPreloads(input);
        if (calls.Count == 0)
        {
            return new CustomCliPreload(null, [], []);
        }

        var session = gate is null ? null : (generalSessionFactory ?? sessionFactory)?.Invoke(input.Context, null);
        if (gate is null || session is null)
        {
            chat.AddAssistantText(PreloadUnavailableText);
            session?.Revoke();
            return null;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(PreloadTimeout);
        _preloading = cts;
        _preloadStopRequested = false;
        _preparing = true; // 읽는 동안 겹쳐 시작하지 않는다
        chat.IsBusy = true; // 새 대화로 바뀌지 않게
        chat.IsPreparing = true; // 아직 실행 전이지만 멈춤은 쓸 수 있게
        var done = false;
        try
        {
            var sections = new List<AiPreloadedSection>();
            var notices = new List<string>(); // 새 대화가 시작되면 앞 줄이 지워지므로, 대화가 시작된 뒤에 보일 안내를 모아 둔다
            var remaining = PreloadTotalMaxChars;
            foreach (var call in calls)
            {
                if (session.IsRevoked)
                {
                    chat.AddAssistantText(PreloadRevokedText);
                    return null;
                }

                AgentGateResponse response;
                try
                {
                    response = await gate.CallToolAsync(session, call.Tool, call.Arguments.DeepClone(), cts.Token).ConfigureAwait(true);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw; // 호출한 쪽의 취소: 명령 경로가 그대로 끝낸다
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    chat.AddAssistantText(PreloadInterruptedText());
                    return null;
                }
#pragma warning disable CA1031 // 게이트가 던진 그 밖의 예외도 읽기 실패다: 예외 글에는 DB·내부 정보가 섞일 수 있어 화면에 내지 않는다.
                catch (Exception)
#pragma warning restore CA1031
                {
                    chat.AddAssistantText(PreloadFailedText(call.Label, null, failed: true));
                    return null;
                }

                // 게이트는 처리자가 끝난 뒤 감사 기록을 쓰는 동안 취소를 보지 않는다: 멈춤·시간 초과·회수가 그 사이에 왔으면 읽은 글을 보내지 않는다.
                ct.ThrowIfCancellationRequested();
                if (session.IsRevoked)
                {
                    chat.AddAssistantText(PreloadRevokedText);
                    return null;
                }

                if (cts.IsCancellationRequested)
                {
                    chat.AddAssistantText(PreloadInterruptedText());
                    return null;
                }

                if (response.Result.IsError)
                {
                    var failed = response.Judgement == AiToolJudgement.Failed; // 거부(Denied)와 실행 실패(DB 오류·처리자 예외·시간 초과)를 구분한다
                    var reason = response.Note ?? response.Judgement.ToString();
                    if (call.Required)
                    {
                        chat.AddAssistantText(PreloadFailedText(call.Label, reason, failed));
                        return null;
                    }

                    var failure = Clip(response.Result.Text, PreloadFailureMaxChars).Text;
                    notices.Add(OptionalReadFailedText(call.Label, reason, failed));
                    sections.Add(new AiPreloadedSection(call.Label, string.Empty, 0, failure.Length == 0 ? reason : failure));
                    continue;
                }

                var clipped = Clip(response.Result.Text, Math.Min(PreloadSectionMaxChars, Math.Max(remaining, 0)));
                remaining -= clipped.Text.Length;
                sections.Add(new AiPreloadedSection(call.Label, clipped.Text, clipped.OriginalLength));
            }

            done = true;
            return new CustomCliPreload(session, sections, notices);
        }
        finally
        {
            _preloading = null;
            _preparing = false;
            chat.IsPreparing = false;
            chat.IsBusy = false;
            if (!done)
            {
                session.Revoke(); // 보내지 않기로 했거나 예외가 났다: 열어 둔 실행 범위를 남기지 않는다
            }
        }
    }

    /// <summary>멈춤을 눌렀거나 제한 시간이 지나 읽기를 접었을 때의 안내(제한 시간이 원인이면 그렇게 적는다).</summary>
    private string PreloadInterruptedText() => _preloadStopRequested ? PreloadStoppedText : PreloadTimedOutText;

    private static string PreloadFailedText(string label, string? reason, bool failed) =>
        reason is null
            ? string.Create(CultureInfo.CurrentCulture, $"기타 CLI 로 보내기 전에 필요한 정보({label})를 읽다가 실패해 보내지 않았습니다. 자세한 원인은 보이지 않습니다.")
            : failed
                ? string.Create(CultureInfo.CurrentCulture, $"기타 CLI 로 보내기 전에 필요한 정보({label})를 읽다가 실패해 보내지 않았습니다. 실패 사유: {reason}.")
                : string.Create(CultureInfo.CurrentCulture, $"기타 CLI 로 보내기 전에 필요한 정보({label})를 읽지 못해 보내지 않았습니다. 관문의 거부 사유: {reason}.");

    private static string OptionalReadFailedText(string label, string reason, bool failed) =>
        failed
            ? string.Create(CultureInfo.CurrentCulture, $"{label}을(를) 읽다가 실패했습니다(사유: {reason}). 읽지 못했다는 사실을 글에 적고 나머지만으로 보냅니다.")
            : string.Create(CultureInfo.CurrentCulture, $"관문이 읽기를 거부했습니다({label}, 사유: {reason}). 읽지 못했다는 사실을 글에 적고 나머지만으로 보냅니다.");

    /// <summary>글자 수 상한 안으로 자른다. 원래 글자 수를 함께 돌려줘 호출한 쪽이 잘렸는지 안다.</summary>
    internal static (string Text, int OriginalLength) Clip(string text, int max)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Length <= max ? (text, text.Length) : (text[..Math.Max(max, 0)], text.Length);
    }

    /// <summary>
    /// 기타 CLI 용 처음 요청 글: 명령의 지시 → 응답 규칙 → 문장 블록 → 미리 읽은 문맥 블록. 기타 CLI 는 값을 가리지 않는 대화만 받으므로 자리표시 안내는 없다.
    /// 문맥 블록은 데이터로만 쓰게 감싸고 닫는 표지를 지운다(도구 결과·DB 글이 지시와 섞이지 않게). 잘렸으면 블록 안에 잘렸다고 적는다.
    /// </summary>
    internal static string BuildCustomCliCommandPrompt(AiCommandSpec spec, AiCommandPromptInput input, IReadOnlyList<AiPreloadedSection> preloaded, AiJoinSyntax joinSyntax = AiJoinSyntax.Oracle)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(preloaded);
        var text = new StringBuilder();
        text.AppendLine(spec.BuildCustomCliInstruction(input, preloaded));
        text.AppendLine(CommandRules(spec, joinSyntax));
        text.AppendLine();
        text.AppendLine("<statement>");
        text.AppendLine(input.PromptStatement);
        text.AppendLine("</statement>");
        foreach (var section in preloaded)
        {
            text.AppendLine();
            text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"<context name=\"{section.Label}\">"));
            if (!section.Read)
            {
                text.AppendLine("(not available: the app could not read this. " + Strip(section.Failure) + ")");
            }
            else
            {
                text.AppendLine(Strip(section.Text));
                if (section.Truncated)
                {
                    text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"[truncated: showing the first {section.Text.Length} of {section.OriginalLength} characters]"));
                }
            }

            text.AppendLine("</context>");
        }

        return text.ToString();
    }

    /// <summary>닫는 표지를 지운다. 지운 자리가 이어 붙어 새 표지가 되는 글(<c>&lt;/con&lt;/context&gt;text&gt;</c>)도 막도록 더 지울 것이 없을 때까지 되풀이한다.</summary>
    private static string Strip(string? text)
    {
        var current = text ?? string.Empty;
        while (true)
        {
            var next = current.Replace("</context>", string.Empty, StringComparison.OrdinalIgnoreCase).Replace("</statement>", string.Empty, StringComparison.OrdinalIgnoreCase);
            if (next.Length == current.Length)
            {
                return next;
            }

            current = next;
        }
    }

    /// <summary>기타 CLI 로 보낸 명령의 대화 창 첫 줄: 보낸 문장, 가리지 않았다는 사실, 미리 읽어 함께 보낸 정보.</summary>
    private static string CustomCliSentLine(AiCommandSpec spec, string promptStatement, IReadOnlyList<AiPreloadedSection> preloaded)
    {
        var line = $"보낸 문장: 「{Shorten(promptStatement, 60)}」 · 기타 CLI 는 값을 가려 보낼 수 없어 가림 대상 접속에는 쓰지 않으며, 이 문장은 가리지 않고 보냈습니다.";
        var read = preloaded.Where(p => p.Read && p.Text.Length > 0).ToList();
        if (read.Count > 0)
        {
            line += " 앱이 미리 읽어 함께 보낸 정보: " + string.Join(", ", read.Select(p =>
                string.Create(CultureInfo.CurrentCulture, $"{p.Label} {p.Text.Length:N0}자") + (p.Truncated ? "(일부 잘림)" : string.Empty))) + ".";
        }

        return spec.CustomCliSentNote is { Length: > 0 } note ? line + " " + note : line;
    }
}
