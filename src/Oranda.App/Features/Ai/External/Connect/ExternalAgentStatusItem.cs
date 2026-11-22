using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Features.Localization;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.App.ViewModels;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Shell;
using Oranda.Platform.Ai.Skills;

namespace Oranda.App.Features.Ai.External.Connect;

/// <summary>
/// 상태 표시줄 「에이전트 연결됨 · Claude Code · 최근 호출 12초 전 · 7시간 40분 남음」 띠(설계서 §3.1).
/// 누르면 최근 호출 20개(도구·판단·크기·시각)와 「끊기」·「연장」(만료 10분 전부터, §6.4)을 보인다. 따로 패널을 두지 않는다(§6.5).
/// 설치된 스킬의 orandactl 경로가 바뀌면 연결이 없어도 「에이전트 스킬 다시 설치 필요」를 띄운다(결정 E-15).
/// 이름공간이 <c>Features.Ai</c> 아래라 기능 <c>aiTuning</c> 이 꺼지면 칸 자체를 만들지 않는다(다리도 그 기능에서만 열린다, E-21).
/// </summary>
[OrandaStatusBarItem("oranda.status.externalAgent", 11, CollapseRank = 0)]
public sealed class ExternalAgentStatusViewModel : ViewModelBase, IStatusBarItem, IDisposable
{
    /// <summary>최근 호출 목록 길이(§3.1).</summary>
    public const int MaxRecentCalls = 20;

    /// <summary>「연장」을 보이기 시작하는 남은 시간(§6.4).</summary>
    public static readonly TimeSpan ExtendWindow = TimeSpan.FromMinutes(10);

    private readonly IExternalAgentLink? _link;
    private readonly IAgentSkillInstaller? _skills;
    private readonly TimeProvider _time;
    private readonly Action<Action> _post;
    private ITimer? _ticker;
    private DateTimeOffset? _lastCallAt;
    private bool _wasConnected;
    private bool _skillReinstallNeeded;
    private bool _disposed;

    /// <summary>
    /// 앱 조립: 연결 원천·스킬 설치기는 이미 등록된 것만 꺼낸다(<see cref="ServiceContainer.GetRegistered{T}"/>).
    /// 모르는 형을 물어 시작 경로에서 지연 어셈블리를 깨우지 않게 한다.
    /// </summary>
    public ExternalAgentStatusViewModel(ServiceContainer? container = null, TimeProvider? time = null)
        : this(new ExternalAgentStatusParts(container?.GetRegistered<IExternalAgentLink>(), container?.GetRegistered<IAgentSkillInstaller>(), time, UiThread.PostIfAppRunning))
    {
    }

    /// <summary>
    /// 부품을 직접 넣는다. 인자를 한 덩어리로 받는 까닭: 컨테이너의 <see cref="ServiceContainer.Create"/> 는 비공개 생성자까지 보고
    /// 인자가 가장 많은 것을 고른다. 이 생성자가 앱 조립에 뽑히지 않게 인자 수를 적게 둔다.
    /// </summary>
    private ExternalAgentStatusViewModel(ExternalAgentStatusParts parts)
    {
        _link = parts.Link;
        _skills = parts.Skills;
        _time = parts.Time ?? TimeProvider.System;
        _post = parts.Post ?? UiThread.PostIfAppRunning;
        if (_link is not null)
        {
            _link.Changed += OnLinkChanged;
            _link.CallCompleted += OnCallCompleted;
        }

        UiText.WatchLanguage(OnLanguageChanged);

        // 스킬 파일 셋을 읽는 일은 시작 경로(UI 스레드) 밖에서 한다.
        SkillCheck = _skills is null ? Task.CompletedTask : Task.Run(CheckSkills);
        Sync();
    }

    /// <summary>시험용: 연결 원천·스킬 설치기·시계·UI 스레드로 넘기는 방법을 바꿔 넣는다.</summary>
    internal static ExternalAgentStatusViewModel For(IExternalAgentLink? link = null, IAgentSkillInstaller? skills = null, TimeProvider? time = null, Action<Action>? post = null)
        => new(new ExternalAgentStatusParts(link, skills, time, post));

    /// <summary>시작 때 띄운 스킬 확인(시험이 기다린다).</summary>
    internal Task SkillCheck { get; }

    /// <summary>최근 호출(새것이 앞).</summary>
    public ObservableCollection<ExternalAgentCallRow> RecentCalls { get; } = [];

    public bool IsConnected => _link?.Current is not null;

    /// <summary>허가에 운영 접속이 들어 있다: 띠를 경고색으로 그리고 글에 「운영 포함」을 넣는다(§3.2).</summary>
    public bool IncludesProduction => _link?.Current?.Production == true;

    public bool SkillReinstallNeeded => _skillReinstallNeeded;

    public bool IsVisible => IsConnected || SkillReinstallNeeded;

    /// <summary>만료 10분 전부터 참.</summary>
    public bool CanExtend => Left is { } left && left <= ExtendWindow;

    public string Text
    {
        get
        {
            if (_link?.Current is not { } state)
            {
                return SkillReinstallNeeded ? ReinstallNeededText : string.Empty;
            }

            var parts = new List<string> { UiText.Get("AgentLink.status.connected", "에이전트 연결됨") };
            if (state.Production)
            {
                parts.Add(ExternalAgentText.StatusProduction);
            }
            if (state.Clients.Count > 0)
            {
                parts.Add(string.Join(", ", state.Clients));
            }

            parts.Add(_lastCallAt is { } at
                ? Format("AgentLink.status.lastCall", "최근 호출 {0}", AgoText(Now - at))
                : UiText.Get("AgentLink.status.noCall", "호출 없음"));
            if (Left is { } left)
            {
                parts.Add(LeftText(left));
            }

            return string.Join(" · ", parts);
        }
    }

    public string ToolTip
    {
        get
        {
            var tip = IsConnected
                ? UiText.Get("AgentLink.status.tooltip", "밖의 LLM CLI 가 이 창에 연결되어 있습니다. 눌러서 최근 호출 보기·끊기")
                : string.Empty;
            if (SkillReinstallNeeded)
            {
                var notice = UiText.Get("AgentLink.status.reinstallTip", "orandactl 위치가 바뀌어 설치된 에이전트 스킬이 옛 경로를 가리킵니다. 눌러서 다시 설치");
                tip = tip.Length == 0 ? notice : tip + "\n" + notice;
            }

            return tip;
        }
    }

    public static string DisconnectText => UiText.Get("AgentLink.status.disconnect", "끊기");

    public static string ExtendText => UiText.Get("AgentLink.status.extend", "연장");

    public static string ReinstallText => UiText.Get("AgentLink.status.reinstall", "스킬 다시 설치");

    public static string RecentCallsTitle => UiText.Get("AgentLink.status.recentTitle", "최근 호출");

    public static string NoCallsText => UiText.Get("AgentLink.status.noCall", "호출 없음");

    private static string ReinstallNeededText => UiText.Get("AgentLink.status.reinstallNeeded", "에이전트 스킬 다시 설치 필요");

    private DateTimeOffset Now => _time.GetUtcNow();

    private TimeSpan? Left => _link?.Current?.ExpiresAt is { } expires ? expires - Now : null;

    public void Disconnect() => _link?.Disconnect();

    public void Extend() => _link?.Extend();

    /// <summary>「스킬 다시 설치」: Oranda 가 썼고 옛 본문인 곳만 다시 쓴다(남의 파일·안 깐 곳은 건드리지 않는다).</summary>
    public async Task ReinstallSkillsAsync()
    {
        if (_skills is not { } skills)
        {
            return;
        }

        await Task.Run(() =>
        {
            var outdated = skills.Inspect().Where(s => s.State == AgentSkillState.Outdated).Select(s => s.Target).ToList();
            if (outdated.Count > 0)
            {
                skills.Install(outdated);
            }
        }).ConfigureAwait(false);
        await Task.Run(CheckSkills).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_link is not null)
        {
            _link.Changed -= OnLinkChanged;
            _link.CallCompleted -= OnCallCompleted;
        }

        UiText.UnwatchLanguage(OnLanguageChanged);
        _ticker?.Dispose();
        _ticker = null;
    }

    /// <summary>「12초 전」·「5분 전」·「1시간 3분 전」.</summary>
    internal static string AgoText(TimeSpan ago)
    {
        if (ago < TimeSpan.Zero)
        {
            ago = TimeSpan.Zero;
        }

        if (ago < TimeSpan.FromMinutes(1))
        {
            return Format("AgentLink.status.agoSeconds", "{0}초 전", (int)ago.TotalSeconds);
        }

        if (ago < TimeSpan.FromHours(1))
        {
            return Format("AgentLink.status.agoMinutes", "{0}분 전", (int)ago.TotalMinutes);
        }

        return Format("AgentLink.status.agoHours", "{0}시간 {1}분 전", (int)ago.TotalHours, ago.Minutes);
    }

    /// <summary>「7시간 40분 남음」·「10분 남음」·「1분 미만 남음」. 분은 올림.</summary>
    internal static string LeftText(TimeSpan left)
    {
        if (left < TimeSpan.FromMinutes(1))
        {
            return UiText.Get("AgentLink.status.leftUnderMinute", "1분 미만 남음");
        }

        // 줄어드는 시간은 분을 올려 센다(7시간 39분 48초 = 「7시간 40분 남음」).
        var minutes = (int)Math.Ceiling(left.TotalMinutes);
        return minutes < 60
            ? Format("AgentLink.status.leftMinutes", "{0}분 남음", minutes)
            : Format("AgentLink.status.leftHours", "{0}시간 {1}분 남음", minutes / 60, minutes % 60);
    }

    internal static string Format(string key, string fallback, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, UiText.Get(key, fallback), args);

    private void CheckSkills()
    {
        bool needed;
        try
        {
            needed = _skills?.NeedsReinstall() ?? false;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            needed = false;
        }

        _post(() =>
        {
            _skillReinstallNeeded = needed;
            Raise();
        });
    }

    private void OnLinkChanged(object? sender, EventArgs e) => _post(Sync);

    private void OnCallCompleted(object? sender, ExternalAgentCall call)
    {
        var at = Now;
        _post(() =>
        {
            if (_disposed || !IsConnected)
            {
                return;
            }

            _lastCallAt = at;
            RecentCalls.Insert(0, new ExternalAgentCallRow(call, at.ToOffset(_time.LocalTimeZone.GetUtcOffset(at))));
            while (RecentCalls.Count > MaxRecentCalls)
            {
                RecentCalls.RemoveAt(RecentCalls.Count - 1);
            }

            Raise();
        });
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => _post(Raise);

    /// <summary>연결 상태를 따라 목록·시계를 맞춘다. 새 연결이 시작되면 지난 연결의 호출을 지운다.</summary>
    private void Sync()
    {
        if (_disposed)
        {
            return;
        }

        var connected = IsConnected;
        if (connected && !_wasConnected)
        {
            RecentCalls.Clear();
            _lastCallAt = null;
            _ticker ??= _time.CreateTimer(_ => _post(Raise), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
        else if (!connected && _wasConnected)
        {
            _ticker?.Dispose();
            _ticker = null;
        }

        _wasConnected = connected;
        Raise();
    }

    private void Raise()
    {
        if (_disposed)
        {
            return;
        }

        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IncludesProduction));
        OnPropertyChanged(nameof(SkillReinstallNeeded));
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(CanExtend));
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(ToolTip));
    }
}

/// <summary>띠 뷰모델의 부품 묶음.</summary>
internal sealed record ExternalAgentStatusParts(IExternalAgentLink? Link, IAgentSkillInstaller? Skills, TimeProvider? Time, Action<Action>? Post);

/// <summary>최근 호출 목록의 한 줄.</summary>
public sealed class ExternalAgentCallRow(ExternalAgentCall call, DateTimeOffset localAt)
{
    public string Tool { get; } = call.Tool;

    public string? Client { get; } = call.Client;

    public AiToolJudgement Judgement { get; } = call.Judgement;

    public string JudgementText { get; } = call.Judgement switch
    {
        AiToolJudgement.Allowed => UiText.Get("AgentLink.call.allowed", "허용"),
        AiToolJudgement.Masked => UiText.Get("AgentLink.call.masked", "가림"),
        AiToolJudgement.Denied => UiText.Get("AgentLink.call.denied", "거부"),
        AiToolJudgement.Failed => UiText.Get("AgentLink.call.failed", "실패"),
        _ => call.Judgement.ToString(),
    };

    public string SizeText { get; } = ExternalAgentStatusViewModel.Format("AgentLink.call.size", "{0:N0}자", call.Size);

    public string TimeText { get; } = localAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
}
