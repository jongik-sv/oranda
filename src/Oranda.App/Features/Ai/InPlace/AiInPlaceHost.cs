using System.Text;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Oranda.App.Features.Ai.Agent;
using Oranda.App.Features.Ai.Kit;
using Oranda.App.Features.Ai.Settings;
using Oranda.App.Features.Ai.Tuning;
using Oranda.App.Shell;
using Oranda.App.Shell.Settings;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Connection;
using Oranda.Core.Settings;

namespace Oranda.App.Features.Ai.InPlace;

/// <summary>띠 한 번의 재료: 이 요청의 접속과 입력 크기(받는 곳 고르기·크기 올림). 보낼 수 없으면 <see cref="Refusal"/>.</summary>
/// <param name="Profile">접속(허용 수준·가림의 기준).</param>
/// <param name="EstimatedTokens">입력 추정 토큰(<see cref="AiTierRouting.EstimateTokens"/>, 지시 글 몫은 이 형이 더한다).</param>
/// <param name="Refusal">화면 조건이 맞지 않는 까닭. 맞으면 null.</param>
public sealed record AiInPlaceSource(ConnectionProfile? Profile, int EstimatedTokens, string? Refusal = null);

/// <summary>
/// 바로 처리 기능(락 풀이·일지 요약·원복 점검 등)이 화면에 AI 띠를 붙일 때 같이 쓰는 앱 쪽 연결(설계 §2.1).
/// 받는 곳 = <see cref="AiConversationTarget.Choose"/>(등급 + 크기 올림)로 고른 대상, 경고 창 = 받는 곳·가림·보낼 정보 미리 보기,
/// 감사·실행 범위 = 기존 AI 경로(<see cref="AiOneShotRun"/>). 시험은 <see cref="Settings"/>·<see cref="Driver"/>·<see cref="Confirm"/> 를 바꾼다.
/// </summary>
public sealed class AiInPlaceHost
{
    private readonly IServiceProvider? _services;

    public AiInPlaceHost(IServiceProvider? services)
    {
        _services = services;
        Settings = () => Service<SettingsService>()?.Current.Ai;
        Driver = Service<IAiDriver>;
        _masker = new Lazy<ISqlLiteralMasker?>(Service<ISqlLiteralMasker>);
        ChatHost = services is null ? null : AiChatHost.For(services);
        Confirm = ConfirmWithDialogAsync;
    }

    /// <summary>AI 설정.</summary>
    public Func<AiSettings?> Settings { get; init; }

    /// <summary>AI 드라이버.</summary>
    public Func<IAiDriver?> Driver { get; init; }

    private readonly Lazy<ISqlLiteralMasker?> _masker = new(() => null);

    /// <summary>SQL 리터럴 가림기(누를 때 처음 찾는다 — 화면을 만들 때 지연 어셈블리를 훑지 않게).</summary>
    public ISqlLiteralMasker? Masker
    {
        get => _masker.Value;
        init => _masker = new Lazy<ISqlLiteralMasker?>(value);
    }

    /// <summary>채팅창 동작(「채팅에서 이어 보기」·「더 깊게」). null 이면 숨긴다.</summary>
    public AiChatHost? ChatHost { get; init; }

    /// <summary>보내기 직전 경고 창. 기본 = 주 창 위 확인 창.</summary>
    public Func<AiStripRequest, CancellationToken, Task<bool>> Confirm { get; init; }

    /// <summary>AI 모델이 「선택 안 함」 이 아니다.</summary>
    public bool Configured => Settings() is { Driver: not AiDriverKind.None };

    /// <summary>입구 단추 상태(설정 → 허용 수준 → 최소 수준 → 화면 조건).</summary>
    public AiEntryState Entry(ConnectionProfile? profile, AiAccessLevel required, string? screenReason = null, string? enabledTip = null) =>
        AiEntryRule.Evaluate(Configured, profile is null ? AiAccessLevel.Off : AiAccessPolicy.Effective(profile), required, screenReason, enabledTip);

    /// <summary>
    /// 띠를 만든다. 등급이 가벼운 일이면 「주 모델로 다시 보내기」 가 붙는다.
    /// <paramref name="source"/> 는 누를 때마다 화면의 지금 상태를 읽고, <paramref name="prepare"/> 는 고른 경로로 요청을 모은다.
    /// </summary>
    public AiInlineStripViewModel CreateStrip(string title, string feature, AiTier tier, Func<AiInPlaceSource> source, Func<AiOneShotRun, AiStripPreparation> prepare)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        return CreateStrip(title, feature, tier, source, (run, _) => Task.FromResult(prepare(run)));
    }

    /// <inheritdoc cref="CreateStrip(string, string, AiTier, Func{AiInPlaceSource}, Func{AiOneShotRun, AiStripPreparation})"/>
    public AiInlineStripViewModel CreateStrip(string title, string feature, AiTier tier, Func<AiInPlaceSource> source, Func<AiOneShotRun, CancellationToken, Task<AiStripPreparation>> prepare)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(prepare);
        if (tier == AiTier.Light)
        {
            return new AiInlineStripViewModel(title, (t, ct) => PrepareAsync(feature, t ?? tier, source, prepare, ct), ChatHost) { Confirm = Confirm };
        }

        return new AiInlineStripViewModel(title, ct => PrepareAsync(feature, tier, source, prepare, ct), ChatHost) { Confirm = Confirm };
    }

    /// <summary>「더 깊게」 에서 채팅창을 열지 못했을 때.</summary>
    public const string DeeperUnavailableText = "AI 채팅창을 열 수 없어 「더 깊게」 를 보내지 않았습니다.";

    /// <summary>
    /// 「더 깊게」: 주 모델 경로로 지금 문맥을 다시 모으고, 경고 창을 거친 뒤 채팅창에 새 대화를 열어 보낸다(띠의 대화는 넘기지 않는다).
    /// 보내지 않았으면 까닭을, 보냈으면 null 을 돌려준다.
    /// </summary>
    public async Task<string?> OpenDeeperAsync(string title, string feature, Func<AiInPlaceSource> source, Func<AiOneShotRun, CancellationToken, Task<AiStripPreparation>> prepare, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(prepare);
        if (ChatHost is not { } chatHost)
        {
            return DeeperUnavailableText;
        }

        var preparation = await PrepareAsync(feature, AiTier.Main, source, prepare, ct).ConfigureAwait(true);
        if (preparation.Request is not { } request)
        {
            return preparation.RefusalText;
        }

        if (!await Confirm(request, ct).ConfigureAwait(true))
        {
            return AiInlineStripViewModel.CancelledText;
        }

        var conversation = await AiChatOpener.OpenAsync(chatHost, chat => new AiDeeperConversation(chat, title + " · 더 깊게", request) { ConfirmText = chatHost.ConfirmText }).ConfigureAwait(true);
        if (conversation is null)
        {
            return DeeperUnavailableText;
        }

        await conversation.StartAsync("「" + title + "」 더 깊게 — 화면에서 모은 정보를 주 모델로 보냈습니다", ct).ConfigureAwait(true);
        return null;
    }

    /// <summary>받는 곳을 골라 한 번짜리 실행을 만든다. 쓸 수 없으면 null 과 까닭.</summary>
    public AiOneShotRun? Run(ConnectionProfile? profile, AiTier tier, int estimatedTokens, string feature, out string? refusal)
    {
        refusal = null;
        if (Settings() is not { Driver: not AiDriverKind.None } settings || Driver() is not { } driver)
        {
            refusal = AiEntryRule.NotConfiguredText;
            return null;
        }

        if (profile is null)
        {
            refusal = NoConnectionText;
            return null;
        }

        var target = AiConversationTarget.Choose(settings, tier, Math.Max(0, estimatedTokens) + AiTierRouting.PromptOverheadTokens);
        var route = AiKitRoute.For(profile, settings, target.Tier);
        return new AiOneShotRun(new AiOneShotSetup(driver, route, feature)
        {
            Audit = Service<IAgentAuditStore>(),
            TrackSession = Service<IAgentSessionSource>() is AppAgentSessionSource tracker ? tracker.Track : null,
        });
    }

    /// <summary>서비스 하나를 찾는다. 등록되지 않았거나 찾다 실패하면 null(AI 띠는 덤이라 화면을 막지 않는다).</summary>
    private T? Service<T>()
        where T : class
    {
        try
        {
            return _services?.GetService(typeof(T)) as T;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>접속을 모를 때.</summary>
    public const string NoConnectionText = "이 화면의 접속을 알 수 없어 보내지 않았습니다.";

    private async Task<AiStripPreparation> PrepareAsync(string feature, AiTier tier, Func<AiInPlaceSource> source, Func<AiOneShotRun, CancellationToken, Task<AiStripPreparation>> prepare, CancellationToken ct)
    {
        var now = source();
        if (now.Refusal is { } refusal)
        {
            return AiStripPreparation.Refuse(refusal);
        }

        return Run(now.Profile, tier, now.EstimatedTokens, feature, out var why) is { } run
            ? await prepare(run, ct).ConfigureAwait(true)
            : AiStripPreparation.Refuse(why!);
    }

    /// <summary>경고 창에 보일 글: 받는 곳 · 가림 · 보낼 정보(꾸러미 미리 보기 그대로).</summary>
    public static string ConfirmText(AiStripRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var route = request.Run.Route;
        var text = new StringBuilder();
        text.Append("받는 곳: ").AppendLine(route.RecipientText);
        text.AppendLine(request.Bundle.Masked ? "SQL 리터럴 값은 가려서 보냅니다." : "SQL 글은 가리지 않고 보냅니다.");
        text.AppendLine();
        text.AppendLine(request.Bundle.PreviewText);
        return text.ToString().TrimEnd();
    }

    private async Task<bool> ConfirmWithDialogAsync(AiStripRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var owner = Service<ShellController>()?.MainWindow
            ?? (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        return owner is not null && await AiConfirmDialog.ShowAsync(owner, ConfirmText(request), "보내기", "취소").ConfigureAwait(true);
    }
}
