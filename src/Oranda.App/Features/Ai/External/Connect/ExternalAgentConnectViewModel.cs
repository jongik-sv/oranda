using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Connection;

namespace Oranda.App.Features.Ai.External.Connect;

/// <summary>연결 창이 쓰는 부품. 앱에서는 <see cref="ExternalAgentConnectCommands"/> 가 컨테이너에서 모은다.</summary>
/// <param name="LoadProfiles">저장된 접속 목록. 이 창은 접속의 AI 허용 수준을 바꾸지 않는다(수준은 설정 「AI」 탭 ① 한곳, ai-settings-one-place).</param>
/// <param name="Grants">허가 저장소(A2).</param>
/// <param name="StartBridge">다리를 연다(이미 열려 있으면 아무 일도 하지 않는다). 없으면 부르지 않는다.</param>
/// <param name="Bind">상태 표시줄 띠를 허가에 잇는다.</param>
/// <param name="Label">창 이름(연결 파일의 <c>label</c>).</param>
public sealed record ExternalAgentConnectServices(
    Func<CancellationToken, Task<IReadOnlyList<ConnectionProfile>>> LoadProfiles,
    IAgentGrants Grants,
    Action? StartBridge = null,
    Action? Bind = null,
    Func<string>? Label = null,
    TimeProvider? Time = null);

/// <summary>연결 창의 접속 한 줄.</summary>
public sealed partial class ExternalAgentConnectionRow : ObservableObject
{
    public ExternalAgentConnectionRow(ConnectionProfile profile)
    {
        Profile = profile;
        _isChecked = Usable && AgentGrantPolicy.CheckedByDefault(profile.Environment);
    }

    public ConnectionProfile Profile { get; private set; }

    public string Alias => Profile.Name;

    public bool IsProduction => Profile.Environment == ConnectionEnvironment.Production;

    /// <summary>운영 행 빨간 표시(§3.2): 고를 수 있는 운영 접속.</summary>
    public bool IsProductionMarked => IsProduction && Usable;

    /// <summary>접속의 AI 허용 수준이 「끔」이 아니라 고를 수 있다.</summary>
    public bool Usable => AiAccessPolicy.Effective(Profile) != AiAccessLevel.Off;

    public string EnvironmentText => Profile.Environment switch
    {
        ConnectionEnvironment.Development => ExternalAgentText.T("개발", "Development"),
        ConnectionEnvironment.Test => ExternalAgentText.T("시험", "Test"),
        ConnectionEnvironment.Production => ExternalAgentText.T("운영", "Production"),
        _ => Profile.Environment.ToString(),
    };

    /// <summary>접속의 지금 AI 허용 수준(접속 편집 「AI」 쪽과 같은 값).</summary>
    public AiAccessLevel CurrentLevel => AiAccessPolicy.Effective(Profile);

    /// <summary>외부 연결에서 쓰일 수준 = min(접속 실효 수준, 환경 외부 상한). 운영 상한은 허가 정책 스위치가 정한다(§2.2).</summary>
    public AiAccessLevel ExternalLevel => AgentGrantPolicy.LevelFor(Profile, AgentGrantPolicy.DefaultCap(Profile.Environment));

    /// <summary>
    /// 줄의 수준 글(글자만): 실효 수준 = min(접속 수준, 외부 상한)(<see cref="AiConnectionAccess"/>). 운영 줄은 실제 수준 뒤에 「(고르려면 접속 이름을 다시 입력)」, 외부 상한이 실제로 낮췄으면 「(외부 상한 · …)」.
    /// 수준을 바꾸는 곳은 설정 「AI」 탭 ① 하나다(이 창은 「이번 연결에 쓸 접속」 만 고른다).
    /// </summary>
    public string LevelText => LevelName(ExternalLevel) + (!IsProductionMarked ? string.Empty
        : ExternalLevel < CurrentLevel ? (SetInTable ? ExternalAgentText.CappedLevelNote : ExternalAgentText.ProductionCappedLevelNote)
        : SetInTable ? string.Empty
        : ExternalAgentText.ProductionLevelNote);

    /// <summary>
    /// 설정 「AI」 탭 ① 표에서 이 접속의 수준을 직접 정해 두었다(저장 값이 환경 기본값이 아니고 끔도 아님). 운영 수준을 올릴 때 그 표에서 이미 접속 이름을 다시 입력했으므로
    /// 연결 창에서는 다시 묻지 않는다(사용자 요청 2026-11-24). 기본값 그대로인 운영 접속은 지금처럼 묻는다.
    /// </summary>
    public bool SetInTable => Profile.AiAccess is not null && CurrentLevel != AiAccessLevel.Off;

    /// <summary>운영 행 경고 「운영 접속 — LLM 이 SELECT 를 자동 실행합니다」: 운영이고 허가 수준이 조회 실행 이상(스위치를 끄면 Metadata 라 숨는다).</summary>
    public bool ShowsRunWarning => IsProductionMarked && ExternalLevel >= AiAccessLevel.Query;

    /// <summary>운영 행 경고 한 줄 더 「운영 값이 가리지 않고 나갑니다」: 고른 운영 접속 + 로컬 모델 + 로컬 모델이 운영도 덮는 정책.</summary>
    public bool ShowsUnmaskedWarning => IsProductionMarked && IsChecked && LocalModel && ExternalAgentConnectPolicy.LocalModelCoversProduction;

    public static string RunWarningText => ExternalAgentText.ProductionRunsWarning;

    public static string UnmaskedWarningText => ExternalAgentText.ProductionUnmaskedWarning;

    /// <summary>연결 창의 「로컬 모델」 값(창이 넣는다).</summary>
    internal bool LocalModel
    {
        get => _localModel;
        set
        {
            if (SetProperty(ref _localModel, value))
            {
                OnPropertyChanged(nameof(ShowsUnmaskedWarning));
            }
        }
    }

    private bool _localModel;

    [ObservableProperty]
    private bool _isChecked;

    /// <summary>운영 접속을 고를 때 다시 입력하는 접속 이름(SAFE-08 방식).</summary>
    [ObservableProperty]
    private string _confirmName = string.Empty;

    public bool NeedsConfirmation => IsProduction && IsChecked && !SetInTable;

    public bool Confirmed => !NeedsConfirmation || string.Equals(ConfirmName, Alias, StringComparison.Ordinal);

    public string ConfirmPlaceholder => ExternalAgentText.IsEnglish ? "Type " + Alias + " exactly" : Alias + " 를 그대로 입력";

    internal static string LevelName(AiAccessLevel level) => level switch
    {
        AiAccessLevel.Off => ExternalAgentText.T("끔", "Off"),
        AiAccessLevel.Metadata => ExternalAgentText.T("메타데이터", "Metadata"),
        AiAccessLevel.Plan => ExternalAgentText.T("계획", "Plan"),
        AiAccessLevel.Query => ExternalAgentText.T("조회 실행", "Query"),
        AiAccessLevel.QueryRows => ExternalAgentText.T("조회 + 행 값", "Query + rows"),
        _ => level.ToString(),
    };

    internal void Replace(ConnectionProfile profile)
    {
        Profile = profile;
        OnPropertyChanged(nameof(Profile));
        OnPropertyChanged(nameof(CurrentLevel));
        OnPropertyChanged(nameof(ExternalLevel));
        OnPropertyChanged(nameof(SetInTable));
        OnPropertyChanged(nameof(NeedsConfirmation));
        OnPropertyChanged(nameof(Confirmed));
        RaiseLevel();
    }

    partial void OnIsCheckedChanged(bool value)
    {
        OnPropertyChanged(nameof(NeedsConfirmation));
        OnPropertyChanged(nameof(Confirmed));
        OnPropertyChanged(nameof(ShowsUnmaskedWarning));
    }

    private void RaiseLevel()
    {
        OnPropertyChanged(nameof(LevelText));
        OnPropertyChanged(nameof(ShowsRunWarning));
    }

    partial void OnConfirmNameChanged(string value) => OnPropertyChanged(nameof(Confirmed));
}

/// <summary>
/// 「외부 에이전트 연결」 창(설계서 §3.1, 결정 E-4·E-6·E-7). 접속을 골라 허가를 만들고(토큰은 다리가 연결 파일에만 쓴다),
/// 연결된 동안은 상태·「끊기」·「연장」을 보인다.
/// <list type="bullet">
/// <item>개발만 기본 체크, 시험은 체크 가능, 운영은 기본 체크 해제이고 체크하면 접속 이름을 다시 입력해야 「연결」이 켜진다(SAFE-08).
/// 운영 수준은 허가 정책 스위치(§2.2)가 정한다: 끄면 「메타데이터」, 켜면 조회 실행 — 그때 행에 빨간 경고가 붙는다(D5, §3.2).</item>
/// <item>접속의 AI 허용 수준은 바꾸지 않는다(ai-settings-one-place): 줄마다 실효 수준(min(접속 수준, 외부 상한))을 글자로만 보이고,
/// 바꾸려면 「수준 바꾸기: 설정 &gt; AI 의 접속 표 열기」 로 간다. 예전 「조회 허용」(개발·시험을 조회+행 값으로 올림)은 그 표에서 한다.</item>
/// <item>「로컬 모델(가리지 않음)」(기본 끔, D4): 켜면 경고를 보이고, 운영과 함께면 「운영 값이 가리지 않고 나갑니다」를 더한다.</item>
/// <item>「8시간 뒤 끊기」(기본 켬): 끄면 앱을 끌 때까지.</item>
/// </list>
/// </summary>
public sealed partial class ExternalAgentConnectViewModel : ObservableObject, IDisposable
{
    private readonly ExternalAgentConnectServices _services;
    private readonly TimeProvider _time;

    [ObservableProperty]
    private bool _expireIn8Hours = true;

    [ObservableProperty]
    private bool _allowUnboundTabText = true;

    /// <summary>「로컬 모델(가리지 않음)」(D4, 기본 끔).</summary>
    [ObservableProperty]
    private bool _localModel;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _error;

    public ExternalAgentConnectViewModel(ExternalAgentConnectServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _time = services.Time ?? TimeProvider.System;
        _services.Grants.Changed += OnGrantChanged;
    }

    public ObservableCollection<ExternalAgentConnectionRow> Rows { get; } = [];

    public AgentGrantView? Grant => _services.Grants.Active is { Count: > 0 } active ? active[0] : null;

    public bool IsConnected => Grant is not null;

    /// <summary>「연결」을 누를 수 있다: 바쁘지 않고, 고른 운영 접속은 모두 이름을 맞게 다시 입력했다. 접속을 하나도 고르지 않아도 된다(편집기 도구만).</summary>
    public bool CanConnect => !IsBusy && Rows.All(r => r.Confirmed);

    /// <summary>「연결됨 · Claude Code · 17:12 에 끊김」.</summary>
    public string StatusText
    {
        get
        {
            if (Grant is not { } grant)
            {
                return ExternalAgentText.T("연결 안 됨", "Not connected");
            }

            var parts = new List<string> { ExternalAgentText.T("연결됨", "Connected") };
            parts.Add(grant.Clients.Count > 0 ? string.Join(", ", grant.Clients) : ExternalAgentText.T("아직 붙은 CLI 없음", "no CLI attached yet"));
            parts.Add(grant.ExpiresAt is { } at
                ? EndsAt(at.ToOffset(_time.LocalTimeZone.GetUtcOffset(at)).ToString("HH:mm", CultureInfo.InvariantCulture))
                : ExternalAgentText.T("앱을 끌 때까지", "until the app closes"));
            var count = grant.Connections.Count.ToString(CultureInfo.InvariantCulture);
            parts.Add(ExternalAgentText.IsEnglish ? count + " connection(s)" : "접속 " + count + "개");
            return string.Join(" · ", parts);
        }
    }

    public bool CanExtend => Grant is { ExpiresAt: not null };

    /// <summary>고른 접속에 운영이 있다.</summary>
    public bool IncludesProduction => Rows.Any(r => r.IsChecked && r.IsProductionMarked);

    /// <summary>「로컬 모델」 경고를 보인다.</summary>
    public bool ShowsLocalModelWarning => LocalModel;

    /// <summary>「운영 값이 가리지 않고 나갑니다」가 붙은 행이 있다.</summary>
    public bool ShowsProductionUnmasked => Rows.Any(r => r.ShowsUnmaskedWarning);

    public static string LocalModelText => ExternalAgentText.LocalModel;

    public static string LocalModelWarningText => ExternalAgentText.LocalModelWarning;

    /// <summary>「쓸 접속」 설명: 운영 조회가 꺼져 있으면 「운영은 메타데이터만」, 켜져 있으면 「운영은 기본으로 고르지 않음」.</summary>
    public static string ConnectionsCaption => ExternalAgentConnectPolicy.ProductionRuns
        ? ExternalAgentText.ConnectionsCaptionProductionRuns
        : ExternalAgentText.ConnectionsCaptionMetadataOnly;

    /// <summary>표 머리 「에이전트가 볼 수 있는 접속」.</summary>
    public static string ConnectionsTitle => ExternalAgentText.ConnectionsTitle;

    /// <summary>표 아래 링크 「수준 바꾸기: 설정 &gt; AI 의 접속 표 열기」.</summary>
    public static string LevelsLinkText => ExternalAgentText.LevelsLink;

    /// <summary>파란 띠: 이 창은 이번 연결에 쓸 접속만 고른다.</summary>
    public static string OnlyChooseNote => ExternalAgentText.OnlyChooseNote;

    private static string EndsAt(string time) => ExternalAgentText.IsEnglish ? "ends at " + time : time + " 에 끊김";

    public async Task LoadAsync(CancellationToken ct = default)
    {
        foreach (var row in Rows)
        {
            row.PropertyChanged -= OnRowChanged;
        }

        Rows.Clear();
        var profiles = await _services.LoadProfiles(ct).ConfigureAwait(true);
        foreach (var profile in profiles.OrderBy(p => p.Environment).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var row = new ExternalAgentConnectionRow(profile) { LocalModel = LocalModel };
            row.PropertyChanged += OnRowChanged;
            Rows.Add(row);
        }

        Raise();
    }

    /// <summary>「연결」: 고른 접속으로 허가를 만든다(접속 수준은 바꾸지 않는다). 이미 연결돼 있으면 옛 허가를 폐기하고 새 토큰을 준다.</summary>
    public Task<bool> ConnectAsync(CancellationToken ct = default) => Task.FromResult(Connect(ct));

    private bool Connect(CancellationToken ct)
    {
        if (!CanConnect)
        {
            return false;
        }

        ct.ThrowIfCancellationRequested();
        IsBusy = true;
        Error = null;
        try
        {
            var picked = Rows.Where(r => r.IsChecked && r.Usable).ToList();

            var targets = picked.Select(r => new AgentGrantTarget(r.Profile, AgentGrantPolicy.DefaultCap(r.Profile.Environment))).ToList();
            _services.StartBridge?.Invoke();
            _services.Bind?.Invoke();
            _services.Grants.Create(new AgentGrantRequest(
                targets,
                ExpireIn8Hours ? AgentGrantPolicy.DefaultLifetime : null,
                AllowUnboundTabText,
                _services.Label?.Invoke() ?? string.Empty,
                LocalModel));
            return true;
        }
#pragma warning disable CA1031 // 저장·연결 실패는 창에 보이고 창을 닫지 않는다.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            Error = ex.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
            Raise();
        }
    }

    /// <summary>「끊기」: 허가와 하위 세션을 폐기한다. 다리가 연결 파일을 지운다.</summary>
    public void Disconnect()
    {
        if (Grant is { } grant)
        {
            _services.Grants.Revoke(grant.GrantId);
        }

        Raise();
    }

    /// <summary>「연장」: 지금부터 다시 8시간.</summary>
    public void Extend()
    {
        if (Grant is { ExpiresAt: not null } grant)
        {
            _services.Grants.Extend(grant.GrantId, AgentGrantPolicy.DefaultLifetime);
        }

        Raise();
    }

    public void Dispose()
    {
        _services.Grants.Changed -= OnGrantChanged;
        foreach (var row in Rows)
        {
            row.PropertyChanged -= OnRowChanged;
        }
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanConnect));

    partial void OnLocalModelChanged(bool value)
    {
        foreach (var row in Rows)
        {
            row.LocalModel = value;
        }

        OnPropertyChanged(nameof(ShowsLocalModelWarning));
        OnPropertyChanged(nameof(ShowsProductionUnmasked));
    }

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(IncludesProduction));
        OnPropertyChanged(nameof(ShowsProductionUnmasked));
    }

    private void OnGrantChanged(object? sender, AgentGrantChanged e) => Oranda.App.Shell.UiThread.PostIfAppRunning(Raise);

    private void Raise()
    {
        OnPropertyChanged(nameof(Grant));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(CanExtend));
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(IncludesProduction));
        OnPropertyChanged(nameof(ShowsProductionUnmasked));
    }
}

/// <summary>연결 창이 보는 운영·로컬 모델 정책 값. 모두 <see cref="AgentGrantPolicy"/> 스위치·함수를 그대로 읽는다(§2.2, E-22).</summary>
internal static class ExternalAgentConnectPolicy
{
    /// <summary>운영 접속에서도 문장을 실행한다(<see cref="AgentGrantPolicy.ExecutesIn"/> 운영).</summary>
    public static bool ProductionRuns => AgentGrantPolicy.ExecutesIn(ConnectionEnvironment.Production);

    /// <summary>「로컬 모델」 허가면 운영도 가리지 않는다(<see cref="AgentGrantPolicy.LocalModelCoversProduction"/>).</summary>
    public static bool LocalModelCoversProduction => AgentGrantPolicy.LocalModelCoversProduction;

    /// <summary>「조회 허용」이 이 환경의 접속 수준을 올려도 된다 = 그 환경에서 실행이 허용된다(<see cref="AgentGrantPolicy.ExecutesIn"/>).</summary>
    public static bool MayRaise(ConnectionEnvironment environment) => AgentGrantPolicy.ExecutesIn(environment);
}
