using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Ai.Setup;
using Oranda.Core.Settings;

namespace Oranda.App.Features.Ai.Settings;

/// <summary>CLI 연결 시험 한 번의 결과. 실패면 <see cref="Kind"/> 와 쉬운 글(<see cref="Message"/>).</summary>
public sealed record AiCliTestOutcome(bool Success, TimeSpan Elapsed, AiSetupErrorKind? Kind = null, string? Message = null);

/// <summary>
/// 시스템 CLI(Codex·Antigravity·opencode)·기타 CLI 의 「연결 시험」(ai-settings-one-place 2026-11-24): 저장된 설정의 대상에 짧은 질문 하나를 보내 답이 오는지만 본다.
/// DB·접속 문맥은 보내지 않는다(접속 없는 문맥, 가림 필요 없음). 실패는 쉬운 말로: 찾지 못함·로그인 필요·시간 초과·그 밖.
/// 앱의 AI 라우터(<see cref="IAiDriver"/>)를 그대로 지나므로 실제 쓸 때와 같은 길이다.
/// </summary>
public static class AiCliConnectionTest
{
    /// <summary>보내는 질문(한 낱말 답).</summary>
    public const string Prompt = "This is a connection test from Oranda. Reply with the single word OK.";

    /// <summary>답을 기다리는 시간.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    /// <summary>저장된 설정이 고른 CLI 로 시험한다. 시스템·기타 CLI 가 아니면 실패(<see cref="AiSetupErrorKind.Unknown"/>).</summary>
    public static async Task<AiCliTestOutcome> RunAsync(IAiDriver driver, AiSettings saved, bool english, CancellationToken ct, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(saved);
        var active = AiActiveProfile.Find(saved);
        var name = TargetName(active) ?? "CLI";
        AiProfileExpectation expected;
        switch (active.Kind)
        {
            case AiActiveKind.SystemCli:
                expected = new AiProfileExpectation(null, SystemCliId: active.SystemCliId);
                break;
            case AiActiveKind.CustomCli:
                expected = new AiProfileExpectation(null, SystemCliId: active.SystemCliId, CustomCliCommandHash: AiCustomCliTarget.CommandHash(active.CustomCli!.Command ?? string.Empty));
                break;
            default:
                return new AiCliTestOutcome(false, TimeSpan.Zero, AiSetupErrorKind.Unknown, Text(english, "이 대상은 CLI 연결 시험을 하지 않습니다.", "This target is not tested as a CLI."));
        }

        var request = new AiRequest(Prompt, new AiContext(null, null, AiAccessLevel.Off), Expected: expected, MaskingRequired: false);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout ?? Timeout);
        var clock = Stopwatch.StartNew();
        var answered = false;
        try
        {
            await foreach (var e in driver.RunAsync(request, limit.Token).ConfigureAwait(true))
            {
                switch (e)
                {
                    case AiError error:
                        return Failed(error, name, english, clock.Elapsed);
                    case AiTextDelta { Text.Length: > 0 }:
                        answered = true;
                        break;
                    case AiFinal final when !string.IsNullOrWhiteSpace(final.Text) || final.Result is not null:
                        answered = true;
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new AiCliTestOutcome(false, clock.Elapsed, AiSetupErrorKind.Timeout,
                Text(english, $"{name} 이(가) {(int)(timeout ?? Timeout).TotalSeconds}초 안에 답하지 않았습니다. 잠시 뒤 다시 해 보세요.",
                    $"{name} did not answer within {(int)(timeout ?? Timeout).TotalSeconds} seconds. Try again in a moment."));
        }

        return answered
            ? new AiCliTestOutcome(true, clock.Elapsed)
            : new AiCliTestOutcome(false, clock.Elapsed, AiSetupErrorKind.Unknown, Text(english, $"{name} 이(가) 빈 답을 보냈습니다.", $"{name} sent an empty answer."));
    }

    /// <summary>카드에 보일 대상 이름(시스템 CLI 표시 이름·기타 CLI 이름). 그 밖은 null.</summary>
    public static string? TargetName(AiActiveProfile active) => active.Kind switch
    {
        AiActiveKind.SystemCli => active.SystemCli?.DisplayName,
        AiActiveKind.CustomCli => string.IsNullOrWhiteSpace(active.CustomCli?.Name) ? active.CustomCli?.Id : active.CustomCli!.Name,
        _ => null,
    };

    /// <summary>실패 종류의 쉬운 글(대상 이름을 넣는다 — 「Claude Code」 로 적지 않는다).</summary>
    public static string KindText(AiSetupErrorKind kind, string target, bool english) => kind switch
    {
        AiSetupErrorKind.CliNotFound => Text(english, $"이 PC 에서 {target} 을(를) 찾지 못했습니다. 설치했는지, 실행 명령이 맞는지 확인하세요.",
            $"{target} was not found on this PC. Check that it is installed and the command is right."),
        AiSetupErrorKind.NotLoggedIn => Text(english, $"{target} 에 로그인되어 있지 않습니다. 터미널에서 {target} 에 로그인한 뒤 다시 시험하세요.",
            $"You are not signed in to {target}. Sign in to {target} in a terminal, then test again."),
        AiSetupErrorKind.Timeout => Text(english, $"{target} 이(가) 제시간에 답하지 않았습니다. 잠시 뒤 다시 해 보세요.", $"{target} did not answer in time. Try again in a moment."),
        AiSetupErrorKind.Network => AiSetupErrorText.For(kind, english),
        _ => Text(english, $"{target} 에서 답을 받지 못했습니다. AI 를 한 번 써 보고 오류 글을 담당자에게 보내 주세요.",
            $"No answer came from {target}. Try AI once and send the error text to your admin."),
    };

    private static AiCliTestOutcome Failed(AiError error, string name, bool english, TimeSpan elapsed)
    {
        var kind = KindOf(error);
        return new AiCliTestOutcome(false, elapsed, kind, KindText(kind, name, english));
    }

    /// <summary>오류 사건의 종류: 코드로 먼저, 로그인·시간 초과는 글로도 알아본다(CLI 마다 코드가 다르다).</summary>
    internal static AiSetupErrorKind KindOf(AiError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var code = error.Code ?? string.Empty;
        if (code == AiSetupErrorCodes.CliNotFound)
        {
            return AiSetupErrorKind.CliNotFound;
        }

        var message = error.Message ?? string.Empty;
        if (code == AiSetupErrorCodes.CliNotLoggedIn || Contains(message, "log in", "login", "logged in", "sign in", "unauthorized", "authenticate", "로그인"))
        {
            return AiSetupErrorKind.NotLoggedIn;
        }

        return code.Contains("timeout", StringComparison.OrdinalIgnoreCase) || Contains(message, "timed out", "timeout", "시간")
            ? AiSetupErrorKind.Timeout
            : AiSetupErrorKind.Unknown;
    }

    private static bool Contains(string text, params string[] words)
    {
        foreach (var word in words)
        {
            if (text.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string Text(bool english, string ko, string en) => english ? en : ko;
}
