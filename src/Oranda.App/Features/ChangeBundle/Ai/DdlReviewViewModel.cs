using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.Ai.Agent;
using Oranda.App.Features.Ai.Kit;
using Oranda.App.Shell.Settings;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Settings;

namespace Oranda.App.Features.ChangeBundle.Ai;

/// <summary>검토가 쓰는 앱 서비스. 셸 = <see cref="From"/>, 시험은 직접 채운다.</summary>
/// <param name="Driver">AI 호출 경로(DI 의 라우터).</param>
/// <param name="Settings">지금 AI 설정(누를 때마다 읽는다).</param>
public sealed record DdlReviewServices(Func<IAiDriver?> Driver, Func<AiSettings?> Settings)
{
    /// <summary>감사 기록 저장소.</summary>
    public IAgentAuditStore? Audit { get; init; }

    /// <summary>실행 범위 등록(도구 연결이 실행 id 로 찾는다).</summary>
    public Func<AgentSession, AgentSession>? TrackSession { get; init; }

    /// <summary>SQL 리터럴 가림.</summary>
    public ISqlLiteralMasker? Masker { get; init; }

    /// <summary>채팅창 동작(「채팅에서 이어 보기」). null 이면 그 단추를 숨긴다.</summary>
    public AiChatHost? Chat { get; init; }

    /// <summary>셸 서비스에서 모은다. AI 설정은 누를 때마다 지금 값을 읽는다.</summary>
    public static DdlReviewServices From(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return new DdlReviewServices(
            () => services.GetService(typeof(IAiDriver)) as IAiDriver,
            () => (services.GetService(typeof(SettingsService)) as SettingsService)?.Current.Ai)
        {
            Audit = services.GetService(typeof(IAgentAuditStore)) as IAgentAuditStore,
            TrackSession = services.GetService(typeof(IAgentSessionSource)) is AppAgentSessionSource source ? source.Track : null, // AI 튜닝·파티션 추천과 같다
            Masker = services.GetService(typeof(ISqlLiteralMasker)) as ISqlLiteralMasker,
            Chat = AiChatHost.For(services),
        };
    }
}

/// <summary>
/// 「AI 검토」 입구 단추 + 검토 띠(설계 §4.1·§2.1 「바로 처리」). 주 모델·도구 씀(<c>get_dependents</c>·<c>get_table_stats</c>).
/// 실행 단추를 막지 않는 조언이고, 앱 검사 줄은 그대로 두고 AI 는 덧붙인다. 받는 곳·가림·감사·실행 범위는 Kit 의 한 번짜리 경로(<see cref="AiOneShotRun"/>)를 그대로 쓴다.
/// </summary>
public sealed partial class DdlReviewViewModel : ObservableObject
{
    /// <summary>AI 검토에 필요한 최소 허용 수준.</summary>
    public const AiAccessLevel RequiredLevel = AiAccessLevel.Metadata;

    public const string EntryLabel = "AI 검토";

    public const string EnabledTip = "실행하기 전에 잠금·공간·시간·무효화·되돌리기를 AI 로 점검합니다(실행을 막지 않습니다)";

    public const string NoScriptText = "검토할 문장이 없습니다.";

    public const string NoDriverText = "AI 경로를 쓸 수 없습니다(이 빌드에 없습니다).";

    private readonly Func<DdlReviewInput?> _input;
    private readonly DdlReviewServices _services;
    private readonly Func<AiStripRequest, CancellationToken, Task<bool>>? _confirm;
    private bool _confirmed;

    /// <param name="input">화면의 지금 상태(누를 때마다 다시 읽는다). 검토할 것이 없으면 null.</param>
    /// <param name="services">앱 서비스.</param>
    /// <param name="confirm">보내기 직전 확인(받는 곳·보낼 칸, 셸은 창 위 확인 창). null 이면 묻지 않는다. 이 검토(창 하나)에서 처음 한 번만 묻는다.</param>
    public DdlReviewViewModel(Func<DdlReviewInput?> input, DdlReviewServices services, Func<AiStripRequest, CancellationToken, Task<bool>>? confirm = null)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _confirm = confirm;
        Strip = new AiInlineStripViewModel(DdlReviewPrompt.Title, PrepareAsync, services.Chat) { Confirm = confirm is null ? null : ConfirmOnceAsync };
        Strip.PropertyChanged += OnStripChanged;
        Entry = new AiEntryButton(EntryLabel, Evaluate, () => Strip.RequestCommand.ExecuteAsync(null));
    }

    /// <summary>검토 띠.</summary>
    public AiInlineStripViewModel Strip { get; }

    /// <summary>「AI 검토」 단추.</summary>
    public AiEntryButton Entry { get; }

    /// <summary>답의 위험도(틀대로 읽지 못했으면 null).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRisk), nameof(RiskText), nameof(IsHighRisk))]
    private DdlRisk? _risk;

    public bool HasRisk => Risk is not null;

    public bool IsHighRisk => Risk == DdlRisk.High;

    public string RiskText => Risk is { } risk ? DdlReviewPrompt.RiskText(risk) : string.Empty;

    /// <summary>화면이 문맥이 바뀔 때(접속·문장·설정) 부른다.</summary>
    public void Refresh() => Entry.Refresh();

    /// <summary>확인 창 글: 받는 곳 · 접속 · 보낼 칸 · 보내지 않는 칸.</summary>
    public static string ConfirmMessage(AiStripRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var route = request.Run.Route;
        var lines = new List<string>
        {
            "실행 전 검토를 AI 로 보냅니다(실행은 하지 않습니다).",
            "받는 곳: " + route.RecipientText,
            "접속: " + (route.Context.ConnectionName ?? "(이름 없음)") + " · 허용 수준 「" + AiContextBundle.LevelLabel(route.Context.AccessLevel) + "」",
            "보내는 것: " + string.Join(", ", request.Bundle.Items.Select(i => i.Label)) + (request.Bundle.Masked ? " (SQL 리터럴은 가림)" : string.Empty),
        };
        if (request.Bundle.Omitted.Count > 0)
        {
            lines.Add("보내지 않음: " + string.Join(", ", request.Bundle.Omitted.Select(o => o.Label + "(" + o.ReasonText + ")")));
        }

        lines.Add("AI 는 의존 객체·통계 조회 도구를 바꾸는 객체에만 씁니다.");
        return string.Join(Environment.NewLine, lines);
    }

    private AiEntryState Evaluate()
    {
        var settings = _services.Settings();
        var configured = settings is { Driver: not AiDriverKind.None };
        if (_input() is not { } input)
        {
            return AiEntryRule.Evaluate(configured, AiAccessLevel.Metadata, RequiredLevel, NoScriptText, EnabledTip);
        }

        var level = AiAccessPolicy.Effective(input.Profile);
        var reason = string.IsNullOrWhiteSpace(input.Script) ? NoScriptText : null;
        return AiEntryRule.Evaluate(configured, level, RequiredLevel, reason, EnabledTip);
    }

    private Task<AiStripPreparation> PrepareAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var settings = _services.Settings();
        if (settings is not { Driver: not AiDriverKind.None })
        {
            return Task.FromResult(AiStripPreparation.Refuse(AiEntryRule.NotConfiguredText));
        }

        if (_services.Driver() is not { } driver)
        {
            return Task.FromResult(AiStripPreparation.Refuse(NoDriverText));
        }

        if (_input() is not { } input || string.IsNullOrWhiteSpace(input.Script))
        {
            return Task.FromResult(AiStripPreparation.Refuse(NoScriptText));
        }

        // 주 모델(설계 §4.1 — 잘못 짚으면 비용이 크다). 주 모델은 크기 올림이 없으므로 등급 그대로.
        var route = AiKitRoute.For(input.Profile, settings, AiTier.Main);
        if (route.Context.AccessLevel < RequiredLevel)
        {
            return Task.FromResult(AiStripPreparation.Refuse(route.Context.AccessLevel == AiAccessLevel.Off ? AiEntryRule.AccessOffText : AiEntryRule.LevelTooLowText(RequiredLevel)));
        }

        var targets = input.Targets;
        var track = _services.TrackSession;
        var run = new AiOneShotRun(new AiOneShotSetup(driver, route, DdlReviewPrompt.Feature)
        {
            Audit = _services.Audit,
            TrackSession = session =>
            {
                var widened = DdlReviewPrompt.Widen(session, targets); // 도구는 바꾸는 객체만 본다
                return track?.Invoke(widened) ?? widened;
            },
        });
        var bundle = DdlReviewPrompt.BuildBundle(route, input, _services.Masker);
        var request = new AiStripRequest(run, bundle, DdlReviewPrompt.Instruction(input, route.Context.AccessLevel), DdlReviewPrompt.Template);
        return Task.FromResult(AiStripPreparation.Ready(request));
    }

    private async Task<bool> ConfirmOnceAsync(AiStripRequest request, CancellationToken ct)
    {
        if (_confirmed || _confirm is not { } confirm)
        {
            return true;
        }

        _confirmed = await confirm(request, ct).ConfigureAwait(true);
        return _confirmed;
    }

    private void OnStripChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AiInlineStripViewModel.State) or nameof(AiInlineStripViewModel.Answer))
        {
            Risk = Strip.State == AiStripState.Done ? DdlReviewPrompt.RiskOf(Strip.Answer) : null;
        }
    }
}
