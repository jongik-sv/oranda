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
/// <param name="LoadProfiles">저장된 접속 목록.</param>
/// <param name="SaveProfile">「조회 허용」으로 접속의 AI 허용 수준을 올려 저장한다(기존 접속 설정에 저장, 새 칸 없음, E-7).</param>
/// <param name="Grants">허가 저장소(A2).</param>
/// <param name="StartBridge">다리를 연다(이미 열려 있으면 아무 일도 하지 않는다). 없으면 부르지 않는다.</param>
/// <param name="Bind">상태 표시줄 띠를 허가에 잇는다.</param>
/// <param name="Label">창 이름(연결 파일의 <c>label</c>).</param>
public sealed record ExternalAgentConnectServices(
    Func<CancellationToken, Task<IReadOnlyList<ConnectionProfile>>> LoadProfiles,
    Func<ConnectionProfile, CancellationToken, Task<ConnectionProfile>> SaveProfile,
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

    /// <summary>외부 연결에서 쓰일 수준(운영은 메타데이터만).</summary>
    public AiAccessLevel ExternalLevel => AgentGrantPolicy.LevelFor(Profile, AgentGrantPolicy.DefaultCap(Profile.Environment));

    public string LevelText => IsProduction && Usable
        ? ExternalAgentText.T("메타데이터만", "Metadata only")
        : LevelName(ExternalLevel);

    [ObservableProperty]
    private bool _isChecked;

    /// <summary>운영 접속을 고를 때 다시 입력하는 접속 이름(SAFE-08 방식).</summary>
    [ObservableProperty]
    private string _confirmName = string.Empty;

    public bool NeedsConfirmation => IsProduction && IsChecked;

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
        OnPropertyChanged(nameof(LevelText));
    }

    partial void OnIsCheckedChanged(bool value)
    {
        OnPropertyChanged(nameof(NeedsConfirmation));
        OnPropertyChanged(nameof(Confirmed));
    }

    partial void OnConfirmNameChanged(string value) => OnPropertyChanged(nameof(Confirmed));
}

/// <summary>
/// 「외부 에이전트 연결」 창(설계서 §3.1, 결정 E-4·E-6·E-7). 접속을 골라 허가를 만들고(토큰은 다리가 연결 파일에만 쓴다),
/// 연결된 동안은 상태·「끊기」·「연장」을 보인다.
/// <list type="bullet">
/// <item>개발만 기본 체크, 시험은 체크 가능, 운영은 체크하면 「메타데이터만」이고 접속 이름을 다시 입력해야 「연결」이 켜진다.</item>
/// <item>「조회 허용」: 고른 개발·시험 접속의 AI 허용 수준을 「조회 + 행 값」으로 올려 접속 설정에 저장한다. 내리지는 않는다.</item>
/// <item>「8시간 뒤 끊기」(기본 켬): 끄면 앱을 끌 때까지.</item>
/// </list>
/// </summary>
public sealed partial class ExternalAgentConnectViewModel : ObservableObject, IDisposable
{
    private readonly ExternalAgentConnectServices _services;
    private readonly TimeProvider _time;

    [ObservableProperty]
    private bool _allowQueryRows;

    [ObservableProperty]
    private bool _expireIn8Hours = true;

    [ObservableProperty]
    private bool _allowUnboundTabText = true;

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
            var row = new ExternalAgentConnectionRow(profile);
            row.PropertyChanged += OnRowChanged;
            Rows.Add(row);
        }

        AllowQueryRows = Rows.Where(r => r.IsChecked && !r.IsProduction).ToList() is { Count: > 0 } picked
            && picked.All(r => r.CurrentLevel == AiAccessLevel.QueryRows);
        Raise();
    }

    /// <summary>「연결」: (「조회 허용」이면 먼저 접속 수준을 저장하고) 허가를 만든다. 이미 연결돼 있으면 옛 허가를 폐기하고 새 토큰을 준다.</summary>
    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        if (!CanConnect)
        {
            return false;
        }

        IsBusy = true;
        Error = null;
        try
        {
            var picked = Rows.Where(r => r.IsChecked && r.Usable).ToList();
            if (AllowQueryRows)
            {
                foreach (var row in picked.Where(r => !r.IsProduction && r.CurrentLevel < AiAccessLevel.QueryRows))
                {
                    row.Replace(await _services.SaveProfile(row.Profile with { AiAccess = AiAccessLevel.QueryRows }, ct).ConfigureAwait(true));
                }
            }

            var targets = picked.Select(r => new AgentGrantTarget(r.Profile, AgentGrantPolicy.DefaultCap(r.Profile.Environment))).ToList();
            _services.StartBridge?.Invoke();
            _services.Bind?.Invoke();
            _services.Grants.Create(new AgentGrantRequest(
                targets,
                ExpireIn8Hours ? AgentGrantPolicy.DefaultLifetime : null,
                AllowUnboundTabText,
                _services.Label?.Invoke() ?? string.Empty));
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

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => OnPropertyChanged(nameof(CanConnect));

    private void OnGrantChanged(object? sender, AgentGrantChanged e) => Oranda.App.Shell.UiThread.PostIfAppRunning(Raise);

    private void Raise()
    {
        OnPropertyChanged(nameof(Grant));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(CanExtend));
        OnPropertyChanged(nameof(CanConnect));
    }
}
