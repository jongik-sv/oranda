using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Oranda.Core.Ai.Access;
using Oranda.Core.Connection;

namespace Oranda.App.Features.Ai.Settings;

/// <summary>허용 수준 한 칸(제목·부제·눌림·사용 가능). 값은 <see cref="ConnectionAiAccessViewModel"/> 이 맞춘다.</summary>
public sealed partial class AiAccessLevelItem(AiAccessLevel level, string label, string sub) : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isEnabled = true;

    public AiAccessLevel Level { get; } = level;

    public string Label { get; } = label;

    public string Sub { get; } = sub;
}

/// <summary>
/// 접속 편집 「AI」 쪽(시안 5): 허용 수준 5칸과 EXPLAIN PLAN 허용.
/// 수준의 기본값·상한·이름 재입력 규칙은 Core 의 <see cref="AiAccessPolicy"/> 가 정하고, 이 모델은 다시 계산하지 않고 그 결과를 보인다.
/// 편집기 쪽이 환경 라벨을 바꾸면 <see cref="Environment"/> 로 알린다(상한이 따라 바뀐다).
/// </summary>
public sealed partial class ConnectionAiAccessViewModel : ObservableObject
{
    public const string ProductionNotice = "운영 접속은 기본이 「조회 실행」 이고(SELECT 만 실행, 행 값은 보내지 않음) 그보다 높일 수 없습니다. 「끔」 으로 내리면 에이전트 접속 목록에도 나오지 않고, 직접 수준을 올릴 때는 접속 이름을 다시 입력합니다. SQL 의 리터럴 값은 항상 가려서 보내고, EXPLAIN PLAN 은 기본으로 켜져 있습니다(끌 수 있음).";

    private static readonly (AiAccessLevel Level, string Label, string Sub, string Sends, string Tools)[] Defs =
    [
        (AiAccessLevel.Off, "끔", "AI 를 쓰지 않음", "아무것도 보내지 않습니다. 에이전트 접속 목록에도 나오지 않습니다.", "—"),
        (AiAccessLevel.Metadata, "메타데이터", "이름·형·주석", "객체 이름·형·주석·PK/FK·인덱스, PL/SQL 원문, 편집기 SQL 글", "list_objects · describe_object · get_ddl · get_source · validate_sql · propose_edit"),
        (AiAccessLevel.Plan, "계획", "+ 계획·통계", "위에 더해 실행 계획·표/열 통계·자식 커서 (바인드 값은 가림)", "+ explain_plan · get_cursor_plan · get_table_stats · suggest_tuning"),
        (AiAccessLevel.Query, "조회 실행", "+ SELECT 실행", "위에 더해 SELECT 결과의 행 수·열 형 (행 값은 보내지 않음)", "+ run_query · benchmark_queries · compare_results"),
        (AiAccessLevel.QueryRows, "조회 + 행 값", "+ 결과 행", "위에 더해 결과 행 값 (민감 컬럼 가림 뒤, 처음 한 번 동의)", "+ run_query 행 값 · fetch_more"),
    ];

    private readonly ConnectionProfile _before;
    private AiAccessLevel? _chosen;
    private bool? _explainChosen;

    [ObservableProperty]
    private ConnectionEnvironment _environment;

    [ObservableProperty]
    private string _confirmName = string.Empty;

    /// <summary>다시 입력해야 하는 접속 이름. 저장할 접속의 이름이라 편집기에서 이름 칸이 바뀌면 따라 바뀐다(새 접속은 처음에 비어 있다).</summary>
    [ObservableProperty]
    private string _connectionName = string.Empty;

    public ConnectionAiAccessViewModel(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _before = profile;
        _connectionName = profile.Name;
        _chosen = profile.AiAccess;
        _explainChosen = profile.AllowExplainPlan;
        _environment = profile.Environment;
        Levels = [.. Defs.Select(d => new AiAccessLevelItem(d.Level, d.Label, d.Sub))];
        Refresh();
    }

    public IReadOnlyList<AiAccessLevelItem> Levels { get; }

    /// <summary>지금 화면 선택을 담은 프로필(환경 라벨은 <see cref="Environment"/>). 저장은 <see cref="Result"/> 를 쓴다.</summary>
    public ConnectionProfile Draft => _before with { Environment = Environment, AiAccess = _chosen, AllowExplainPlan = _explainChosen };

    /// <summary>저장할 프로필: 한도를 넘는 값은 한도로 줄인다.</summary>
    public ConnectionProfile Result => AiAccessPolicy.Clamp(Draft);

    /// <summary>지금 효력이 있는 수준.</summary>
    public AiAccessLevel Level => AiAccessPolicy.Effective(Draft);

    public AiAccessLevel MaxLevel => AiAccessPolicy.MaxLevel(Environment);

    public bool ExplainAllowed
    {
        get => AiAccessPolicy.ExplainPlanEnabled(Draft);
        set
        {
            if (value != ExplainAllowed)
            {
                _explainChosen = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>저장하려면 접속 이름을 다시 입력해야 한다(<see cref="AiAccessPolicy.NeedsNameConfirmation"/>).</summary>
    public bool NeedsNameConfirmation => AiAccessPolicy.NeedsNameConfirmation(_before, Draft);

    /// <summary>저장할 수 있다: 이름 재입력이 필요 없거나, 필요하고 접속 이름과 같게 입력했다.</summary>
    public bool CanSave => !NeedsNameConfirmation || (ConnectionName.Length > 0 && string.Equals(ConfirmName, ConnectionName, StringComparison.Ordinal));

    public string SendsText => Defs[(int)Level].Sends;

    public string ToolsText => Defs[(int)Level].Tools;

    public bool IsProduction => Environment == ConnectionEnvironment.Production;

    public string? ProductionText => IsProduction ? ProductionNotice : null;

    public string ConfirmPlaceholder => ConnectionName.Length > 0 ? $"{ConnectionName} 를 그대로 입력" : "접속 이름을 먼저 입력하세요";

    partial void OnEnvironmentChanged(ConnectionEnvironment value) => Refresh();

    partial void OnConfirmNameChanged(string value) => OnPropertyChanged(nameof(CanSave));

    partial void OnConnectionNameChanged(string value)
    {
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(ConfirmPlaceholder));
    }

    [RelayCommand]
    private void SelectLevel(AiAccessLevel level)
    {
        if (level > MaxLevel)
        {
            return;
        }

        _chosen = level;
        Refresh();
    }

    private void Refresh()
    {
        foreach (var item in Levels)
        {
            item.IsSelected = item.Level == Level;
            item.IsEnabled = item.Level <= MaxLevel;
        }

        OnPropertyChanged(nameof(Level));
        OnPropertyChanged(nameof(MaxLevel));
        OnPropertyChanged(nameof(ExplainAllowed));
        OnPropertyChanged(nameof(NeedsNameConfirmation));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(IsProduction));
        OnPropertyChanged(nameof(ProductionText));
        OnPropertyChanged(nameof(SendsText));
        OnPropertyChanged(nameof(ToolsText));
    }
}
