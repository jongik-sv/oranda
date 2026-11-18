using System.Globalization;
using System.Text;
using Oranda.App.Features.Ai.Chat;
using Oranda.App.Features.Ai.Tuning;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Connection;
using Oranda.Core.Settings;

namespace Oranda.App.Features.Ai.Kit;

/// <summary>
/// 받는 곳과 가림 판단(요청을 만들 때 한 번 정한다). AI 튜닝·파티션 추천과 같은 규칙을 그대로 쓴다: 받는 곳 = 설정의 활성 대상(<see cref="AiConversationTarget"/>),
/// 라우터 기대값 = <see cref="AiConversationTarget.ToExpectation"/>, 가림 = <see cref="AiAccessPolicy.MaskLiterals(ConnectionProfile, bool, bool)"/>.
/// </summary>
/// <param name="Target">받는 곳.</param>
/// <param name="Context">요청 문맥(접속 이름·환경·실효 허용 수준·로컬 AI).</param>
/// <param name="MaskRequired">이 접속의 SQL 을 가려 보내야 한다.</param>
/// <param name="Profile">이 접속(실행 범위에 싣는다).</param>
/// <param name="ModelLabel">보낼 모델 이름(띠·카드 머리에 작게 보인다). 라우터가 <see cref="AiModelChosen"/> 으로 알려 주면 그 이름으로 바뀐다.</param>
/// <param name="Tier">요청 등급(설계 §1.2). 받는 곳·가림은 이 등급의 대상으로 계산한다.</param>
public sealed record AiKitRoute(AiConversationTarget Target, AiContext Context, bool MaskRequired, ConnectionProfile Profile, string ModelLabel, AiTier Tier = AiTier.Main)
{
    /// <summary>받는 곳 한 줄(이름 · 주소).</summary>
    public string RecipientText => Target.RecipientUrl is { } url ? Target.Label + " · " + url : Target.Label;

    /// <summary>
    /// 접속·설정·등급으로 만든다. 받는 곳은 등급의 대상(<see cref="AiConversationTarget.Choose"/>: 가벼운 일 모델이 비어 있으면 주 모델, 추정 입력이 가벼운 일 모델 한도의 70% 를 넘으면
    /// 주 모델로 올림)이고, 확인·가림도 실제로 고른 그 대상으로 계산한다(가벼운 일 모델이 로컬 AI 면 가리지 않고, 주 모델이 유료면 가린다).
    /// 크기 올림은 확인 전에 여기서 정한다(라우터는 등급을 바꾸지 않는다).
    /// </summary>
    /// <param name="estimatedTokens">보낼 글의 추정 토큰(<see cref="EstimateFor"/>). 0 이면 올리지 않는다.</param>
    public static AiKitRoute For(ConnectionProfile profile, AiSettings settings, AiTier tier = AiTier.Main, int estimatedTokens = 0)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(settings);
        var target = AiConversationTarget.Choose(settings, tier, estimatedTokens);
        var chosen = settings.ForTier(target.Tier);
        var context = new AiContext(profile.Name, profile.Environment, AiAccessPolicy.Effective(profile), LocalAi: target.LocalAi);
        var mask = AiAccessPolicy.MaskLiterals(profile, chosen.MaskLiterals, target.LocalAi);
        var label = AiTierRouting.ModelLabel(AiActiveProfile.Find(settings, target.Tier), chosen.Model);
        return new AiKitRoute(target, context, mask, profile, target.LocalAi ? label + " · 로컬" : label, target.Tier);
    }

    /// <summary>보낼 글(꾸러미에 넣을 원문들)의 추정 토큰: 글 + 지시·규칙 몫.</summary>
    public static int EstimateFor(params string?[] texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        return texts.Sum(AiTierRouting.EstimateTokens) + AiTierRouting.PromptOverheadTokens;
    }

    /// <summary>요청이 원한 등급(크기 올림 전).</summary>
    public AiTier RequestedTier => Target.RequestedTier;

    /// <summary>가벼운 일을 입력 크기 때문에 주 모델로 올렸다.</summary>
    public bool Escalated => Target.Escalated;

    /// <summary>꾸러미를 이 받는 곳의 규칙(허용 수준·로컬 AI·가림)으로 모으기 시작한다.</summary>
    public AiContextBundleBuilder Bundle(string title, ISqlLiteralMasker? masker) =>
        new(title, Context.AccessLevel, Context.LocalAi, MaskRequired, masker);
}

/// <summary>한 번짜리 요청의 결과.</summary>
/// <param name="Text">받은 글(끝 글 또는 조각을 이은 글).</param>
/// <param name="ResumeId">이어 묻기에 쓸 id(없으면 null).</param>
/// <param name="Error">실패 글(가린 뒤). 성공이면 null.</param>
/// <param name="ErrorCode">실패 코드(<c>profile_changed</c> 등).</param>
public sealed record AiOneShotResult(string Text, string? ResumeId, string? Error, string? ErrorCode = null)
{
    public bool Succeeded => Error is null;

    /// <summary>답한 모델 이름(라우터의 <see cref="AiModelChosen"/>, 알리지 않았으면 경로의 이름).</summary>
    public string ModelLabel { get; init; } = string.Empty;

    /// <summary>가벼운 일 요청을 입력 크기 때문에 주 모델로 올려 보냈다.</summary>
    public bool Escalated { get; init; }

    /// <summary>가벼운 일 모델이 실패했다: 화면이 「주 모델로 다시 보내기」 를 붙인다(AE-1, 몰래 넘기지 않는다).</summary>
    public bool RetryOnMain { get; init; }
}

/// <summary>한 번짜리 요청의 재료.</summary>
/// <param name="Driver">AI 호출 경로.</param>
/// <param name="Route">받는 곳과 가림 판단.</param>
/// <param name="Feature">기능 이름(감사 기록의 도구 이름 <c>ai_kit:&lt;Feature&gt;</c>).</param>
public sealed record AiOneShotSetup(IAiDriver Driver, AiKitRoute Route, string Feature)
{
    /// <summary>감사 기록 저장소(없으면 남기지 않음).</summary>
    public IAgentAuditStore? Audit { get; init; }

    /// <summary>새 실행 범위를 등록한다(앱 = <c>AppAgentSessionSource.Track</c>). null = 등록하지 않음.</summary>
    public Func<AgentSession, AgentSession>? TrackSession { get; init; }

    /// <summary>모델 이름(null = 설정 값).</summary>
    public string? Model { get; init; }

    /// <summary>추론 노력(null = 설정 값).</summary>
    public string? Effort { get; init; }
}

/// <summary>
/// 도구를 쓰지 않는 한 번짜리 요청(설계 §0-2): 앱이 모은 꾸러미를 한 번에 보내고 글만 받는다. 보내기 전에 감사 기록을 남기고, 요청마다 새 실행 범위를 싣고 끝나면 폐기한다
/// (드라이버가 늘 요구한다). 확인한 곳과 실제 전송처가 같게 <see cref="AiRequest.Expected"/>·<see cref="AiRequest.MaskingRequired"/> 를 채운다. AI 는 아무것도 실행하지 않는다.
/// </summary>
public sealed class AiOneShotRun(AiOneShotSetup setup)
{
    /// <summary>감사 기록 도구 이름의 앞글.</summary>
    public const string AuditToolPrefix = "ai_kit:";

    public AiOneShotSetup Setup { get; } = setup ?? throw new ArgumentNullException(nameof(setup));

    public AiKitRoute Route => Setup.Route;

    /// <summary>
    /// 보내고 끝까지 받는다. 사건은 <paramref name="onEvent"/> 로도 넘긴다(채팅창이 그대로 그린다). 실패는 <see cref="AiOneShotResult.Error"/>, 멈춤은 <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <param name="prompt">보낼 글.</param>
    /// <param name="resumeId">이어 묻기 id(새 요청이면 null).</param>
    /// <param name="bundle">감사 기록에 칸 수·뺀 칸 수를 적을 꾸러미(없으면 null).</param>
    public async Task<AiOneShotResult> AskAsync(string prompt, string? resumeId, IAiContextBundle? bundle, Action<AiEvent>? onEvent, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(prompt);
        await AuditAsync(prompt, bundle, ct).ConfigureAwait(true);
        var session = NewSession(bundle?.Vault);
        var request = new AiRequest(
            prompt,
            Route.Context,
            resumeId,
            Model: Setup.Model,
            Session: session,
            Effort: Setup.Effort,
            Expected: Route.Target.ToExpectation(), // 확인한 곳과 실제 전송처가 같게(profile_changed)
            MaskingRequired: Route.MaskRequired, // 값을 가려 보낼 수 없는 기타 CLI 의 라우터만 읽는다
            Tier: Route.Tier, // 확인한 대상의 등급(이어 묻기도 같은 등급, 라우터는 바꾸지 않는다)
            RequestedTier: Route.RequestedTier);
        var text = new StringBuilder();
        var label = Route.ModelLabel;
        var escalated = Route.Escalated;
        var retryOnMain = false;
        string? finalText = null;
        string? resume = resumeId;
        string? error = null;
        string? code = null;
        try
        {
            await foreach (var e in Setup.Driver.RunAsync(request, ct).WithCancellation(ct).ConfigureAwait(true))
            {
                onEvent?.Invoke(e);
                switch (e)
                {
                    case AiStarted started:
                        resume = started.ResumeId ?? resume;
                        break;
                    case AiTextDelta delta:
                        text.Append(delta.Text);
                        break;
                    case AiFinal final:
                        finalText = final.Text;
                        resume = final.ResumeId ?? resume;
                        break;
                    case AiModelChosen chosen:
                        label = string.IsNullOrWhiteSpace(chosen.Label) ? label : chosen.Label;
                        escalated = escalated || chosen.Escalated;
                        break;
                    case AiError fail:
                        error = AiErrorText.Describe(fail);
                        code = fail.Code;
                        retryOnMain = fail.RetryOnMain && Route.Tier == AiTier.Light;
                        break;
                }
            }
        }
        finally
        {
            session.Revoke(); // 성공·실패·취소 모두: 실행 범위를 남기지 않는다
        }

        ct.ThrowIfCancellationRequested();
        var raw = string.IsNullOrEmpty(finalText) ? text.ToString() : finalText;
        return new AiOneShotResult(raw, resume, error, code) { ModelLabel = label, Escalated = escalated, RetryOnMain = retryOnMain };
    }

    /// <summary>감사 한 줄: 기능·글자 수·칸 수·가림·받는 곳만(본문은 담지 않는다).</summary>
    public static AgentAuditEntry BuildAuditEntry(AiKitRoute route, string feature, string prompt, IAiContextBundle? bundle, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(prompt);
        return new AgentAuditEntry
        {
            OccurredAt = now,
            ProfileId = route.Profile.Id == Guid.Empty ? null : route.Profile.Id,
            ProfileName = route.Context.ConnectionName,
            Client = "oranda-ai-kit",
            RunId = Guid.NewGuid().ToString("N"),
            Tool = AuditToolPrefix + feature,
            Arguments = string.Create(CultureInfo.InvariantCulture,
                $"chars={prompt.Length}; items={bundle?.Items.Count ?? 0}; omitted={bundle?.Omitted.Count ?? 0}; masked={(route.MaskRequired ? "yes" : "no")}; tier={(route.Tier == AiTier.Light ? "light" : "main")}; model={route.ModelLabel}; to={route.Target.RecipientUrl ?? route.Target.Label}"),
            AccessLevel = route.Context.AccessLevel,
            Verdict = AiToolJudgement.Allowed,
            ResultChars = prompt.Length,
        };
    }

    private AgentSession NewSession(LiteralVault? vault)
    {
        var session = new AgentSession(new AgentRunScope(Guid.NewGuid().ToString("N"), Route.Profile, Route.Context)
        {
            MaskLiteralsRequired = Route.MaskRequired,
            Literals = vault,
        });
        return Setup.TrackSession?.Invoke(session) ?? session;
    }

    private async Task AuditAsync(string prompt, IAiContextBundle? bundle, CancellationToken ct)
    {
        if (Setup.Audit is not { } store)
        {
            return;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await store.AppendAsync(BuildAuditEntry(Route, Setup.Feature, prompt, bundle, DateTimeOffset.UtcNow), timeout.Token)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            System.Diagnostics.Trace.TraceWarning("AI 감사 기록 실패: {0}", AiErrorText.Redact(ex.GetType().Name));
        }
    }
}
