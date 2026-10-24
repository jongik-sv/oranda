using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.ExecutionPlan;
using Oranda.Core.Tuning;
using CorePlan = Oranda.Core.Plans.ExecutionPlan;

namespace Oranda.App.Features.SqlMonitor;

/// <summary>AWR 계획 이력 탭의 상태.</summary>
public enum AwrPaneState
{
    NoSession,
    NoSql,

    /// <summary>Diagnostics Pack 선언 없음(서버로 보내지 않음).</summary>
    LicenseOff,
    Loading,

    /// <summary>AWR 에 이력이 없음.</summary>
    Empty,
    Loaded,
    Error,
}

/// <summary>
/// AWR 계획 이력(TN-13, Diagnostics Pack). snap 구간별 plan_hash·실행 수·경과/실행 그래프와 두 plan_hash 계획 비교(<see cref="PlanComparer"/>).
/// 선언이 없으면 dba_hist_* 를 읽지 않고 이유·켜는 방법을 보인다.
/// </summary>
public sealed partial class AwrHistoryViewModel : ObservableObject
{
    /// <summary>나쁜 계획으로 칠 배수(가장 빠른 plan_hash 의 평균 경과/실행 대비).</summary>
    public const double SlowFactor = 2;

    private readonly SqlMonitorViewModel _owner;
    private string? _loadedFor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLicenseOff), nameof(IsLoading), nameof(IsLoaded), nameof(HasMessage), nameof(CanCompare))]
    private AwrPaneState _state = AwrPaneState.NoSession;

    [ObservableProperty]
    private string _message = string.Empty;

    [ObservableProperty]
    private string _hintText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCompare), nameof(SelectedIndex))]
    private AwrRowItem? _selectedRow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasComparison))]
    private PlanComparison? _comparison;

    [ObservableProperty]
    private string _compareTitle = string.Empty;

    [ObservableProperty]
    private string _compareSummary = string.Empty;

    [ObservableProperty]
    private bool _compareHashDiffers;

    [ObservableProperty]
    private string _compareMessage = string.Empty;

    [ObservableProperty]
    private bool _isComparing;

    internal AwrHistoryViewModel(SqlMonitorViewModel owner)
    {
        _owner = owner;
        Gate(out _);
    }

    /// <summary>SQL 모니터(바로 가기 단추·팩 상태).</summary>
    public SqlMonitorViewModel Owner => _owner;

    /// <summary>snap × plan_hash 줄(snap 순).</summary>
    public ObservableCollection<AwrRowItem> Rows { get; } = [];

    /// <summary>plan_hash 범례(처음 나온 순).</summary>
    public ObservableCollection<AwrPlanLegendItem> Plans { get; } = [];

    /// <summary>비교 줄(<see cref="PlanComparer"/> 결과를 화면 줄로).</summary>
    public ObservableCollection<PlanCompareRowItem> CompareRows { get; } = [];

    public bool IsLicenseOff => State == AwrPaneState.LicenseOff;

    public bool IsLoading => State == AwrPaneState.Loading;

    public bool IsLoaded => State == AwrPaneState.Loaded;

    public bool HasMessage => State is AwrPaneState.NoSession or AwrPaneState.NoSql or AwrPaneState.Empty or AwrPaneState.Error;

    public bool HasComparison => Comparison is not null || CompareMessage.Length > 0;

    /// <summary>두 plan_hash 비교를 할 수 있는가(plan_hash 2개 이상).</summary>
    public bool CanCompare => IsLoaded && Plans.Count >= 2 && !IsComparing;

    /// <summary>그래프에서 강조할 줄 번호(없으면 -1).</summary>
    public int SelectedIndex => SelectedRow is { } r ? Rows.IndexOf(r) : -1;

    /// <summary>표 아래 줄: 「plan_hash 2개 · snap 14개」.</summary>
    public string SummaryText => Rows.Count == 0
        ? string.Empty
        : string.Create(CultureInfo.InvariantCulture, $"plan_hash {Plans.Count}개 · snap {Rows.Select(r => r.Row.SnapId).Distinct().Count()}개");

    /// <summary>느린 plan_hash 인가(가장 빠른 평균의 <see cref="SlowFactor"/> 배 이상).</summary>
    public bool IsSlowPlan(long planHash)
    {
        var best = Plans.Where(p => p.AverageMs is not null).Select(p => p.AverageMs!.Value).DefaultIfEmpty(double.NaN).Min();
        var mine = Plans.FirstOrDefault(p => p.PlanHash == planHash)?.AverageMs;
        return Plans.Count >= 2 && mine is { } m && !double.IsNaN(best) && m >= best * SlowFactor && m - best >= 1;
    }

    /// <summary>탭을 열 때: 아직 이 sql_id 를 읽지 않았으면 읽는다.</summary>
    public Task EnsureLoadedAsync()
    {
        var id = _owner.SelectedSqlId;
        return id is not null && string.Equals(id, _loadedFor, StringComparison.Ordinal) && State is AwrPaneState.Loaded or AwrPaneState.Empty
            ? Task.CompletedTask
            : LoadAsync();
    }

    /// <summary>AWR 계획 이력을 읽는다. Diagnostics 선언이 없으면 서버로 보내지 않는다.</summary>
    public async Task LoadAsync()
    {
        ClearComparison();
        if (!Gate(out var sqlId))
        {
            return;
        }

        State = AwrPaneState.Loading;
        IReadOnlyList<AwrPlanRow>? rows = null;
        Exception? failure = null;
        try
        {
            await _owner.RunTuningAsync(async (tuning, session) => rows = await tuning.GetAwrPlanHistoryAsync(session, sqlId, CancellationToken.None), ex => failure = ex, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            _owner.PostToUi(() => State = AwrPaneState.NoSql);
            return;
        }

        _owner.PostToUi(() =>
        {
            _loadedFor = sqlId;
            if (failure is not null)
            {
                Rows.Clear();
                Plans.Clear();
                Message = "AWR 이력을 읽지 못했습니다";
                HintText = SqlMonitorViewModel.Describe(failure);
                State = AwrPaneState.Error;
                _owner.StatusText = "AWR 계획 이력: " + HintText;
                return;
            }

            Apply(rows ?? []);
            if (Rows.Count == 0)
            {
                Message = "AWR 에 이 sql_id 의 이력이 없습니다";
                HintText = "snap 사이에 상위 SQL 로 잡히지 않았거나 AWR 보존 기간이 지났습니다. 지금 계획은 「실제 계획」·「자식 커서」 로 보세요.";
                State = AwrPaneState.Empty;
            }
            else
            {
                State = AwrPaneState.Loaded;
            }

            _owner.StatusText = $"AWR 계획 이력: {sqlId} snap×plan {Rows.Count}줄, plan_hash {Plans.Count}개";
        });
    }

    /// <summary>
    /// 두 plan_hash 비교. 기준 = 가장 빠른(평균 경과/실행) 다른 plan_hash, 대상 = 고른 줄의 plan_hash(없으면 가장 최근 것).
    /// 계획 단계는 dba_hist_sql_plan 에서 읽고 <see cref="PlanComparer.Compare"/> 로 맞춘다.
    /// </summary>
    public async Task CompareAsync()
    {
        if (!CanCompare || _owner.SelectedSqlId is not { } sqlId)
        {
            return;
        }

        var (baseHash, targetHash) = PickPair();
        IsComparing = true;
        OnPropertyChanged(nameof(CanCompare));
        ClearComparison();
        CorePlan? before = null;
        CorePlan? after = null;
        Exception? failure = null;
        try
        {
            await _owner.RunTuningAsync(async (tuning, session) =>
            {
                before = await tuning.GetAwrPlanAsync(session, sqlId, baseHash, CancellationToken.None);
                after = await tuning.GetAwrPlanAsync(session, sqlId, targetHash, CancellationToken.None);
            }, ex => failure = ex, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _owner.PostToUi(() =>
            {
                IsComparing = false;
                OnPropertyChanged(nameof(CanCompare));
            });
        }

        _owner.PostToUi(() => ShowComparison(baseHash, targetHash, before, after, failure));
    }

    /// <summary>비교 영역을 닫는다.</summary>
    public void ClearComparison()
    {
        Comparison = null;
        CompareRows.Clear();
        CompareTitle = string.Empty;
        CompareSummary = string.Empty;
        CompareMessage = string.Empty;
        CompareHashDiffers = false;
        OnPropertyChanged(nameof(HasComparison));
    }

    /// <summary>비교할 두 plan_hash(기준, 대상).</summary>
    public (long Base, long Target) PickPair()
    {
        var target = SelectedRow?.Row.PlanHashValue ?? Rows.LastOrDefault()?.Row.PlanHashValue ?? 0;
        var others = Plans.Where(p => p.PlanHash != target).ToList();
        var best = others.Where(p => p.AverageMs is not null).OrderBy(p => p.AverageMs).FirstOrDefault() ?? others.FirstOrDefault();
        return (best?.PlanHash ?? target, target);
    }

    internal void Reset()
    {
        _loadedFor = null;
        Rows.Clear();
        Plans.Clear();
        SelectedRow = null;
        ClearComparison();
        Gate(out _);
        if (State == AwrPaneState.Loading)
        {
            State = AwrPaneState.NoSql;
        }
    }

    internal void OnTargetChanged(bool visible)
    {
        var id = _owner.SelectedSqlId;
        if (id is not null && string.Equals(id, _loadedFor, StringComparison.Ordinal))
        {
            return;
        }

        if (id is null && State is AwrPaneState.Loading)
        {
            return;
        }

        _loadedFor = null;
        Rows.Clear();
        Plans.Clear();
        ClearComparison();
        if (visible && id is not null)
        {
            _ = LoadAsync();
        }
        else
        {
            Gate(out _);
        }
    }

    /// <summary>세션·sql_id·팩을 확인하고 맞지 않으면 그 상태를 세운다. 보낼 수 있으면 true.</summary>
    private bool Gate(out string sqlId)
    {
        sqlId = _owner.SelectedSqlId ?? string.Empty;
        if (_owner.MonitorSession is null)
        {
            Message = "모니터 세션이 열려 있지 않습니다";
            HintText = "SQL 모니터를 열면 고른 SQL 의 AWR 계획 이력을 볼 수 있습니다.";
            State = AwrPaneState.NoSession;
            return false;
        }

        if (_owner.DiagnosticsOff() is { } off)
        {
            Message = "AWR 계획 이력은 Diagnostics Pack 이 필요합니다";
            HintText = off.Reason + " " + off.HowTo + " 팩 없이 보려면 「대기 분해」 의 지금 표본이나 「자식 커서」 를 쓰세요.";
            State = AwrPaneState.LicenseOff;
            return false;
        }

        if (sqlId.Length == 0)
        {
            Message = "SQL 을 고르세요";
            HintText = "위 목록에서 SQL 을 고르면 snap 구간별 plan_hash 와 경과/실행을 읽습니다(dba_hist_sqlstat).";
            State = AwrPaneState.NoSql;
            return false;
        }

        return true;
    }

    private void Apply(IReadOnlyList<AwrPlanRow> rows)
    {
        Rows.Clear();
        Plans.Clear();
        var index = new Dictionary<long, int>();
        foreach (var r in rows.OrderBy(r => r.SnapId).ThenBy(r => r.PlanHashValue))
        {
            if (!index.TryGetValue(r.PlanHashValue, out var i))
            {
                i = index.Count;
                index[r.PlanHashValue] = i;
            }

            Rows.Add(new AwrRowItem(r, i));
        }

        foreach (var (hash, i) in index.OrderBy(p => p.Value))
        {
            var mine = rows.Where(r => r.PlanHashValue == hash).ToList();
            var execs = mine.Sum(r => r.Executions);
            double? avg = execs > 0 ? mine.Sum(r => r.ElapsedMicros) / 1000d / execs : null;
            Plans.Add(new AwrPlanLegendItem(hash, i, avg, execs));
        }

        SelectedRow = null;
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(CanCompare));
    }

    private void ShowComparison(long baseHash, long targetHash, CorePlan? before, CorePlan? after, Exception? failure)
    {
        var pair = string.Create(CultureInfo.InvariantCulture, $"{baseHash} → {targetHash}");
        if (failure is not null || before is null || after is null)
        {
            CompareTitle = "두 plan_hash 비교: " + pair;
            CompareMessage = failure is null ? "비교를 멈췄습니다." : "AWR 계획 단계를 읽지 못했습니다: " + SqlMonitorViewModel.Describe(failure);
            OnPropertyChanged(nameof(HasComparison));
            return;
        }

        if (before.Steps.Count == 0 || after.Steps.Count == 0)
        {
            CompareTitle = "두 plan_hash 비교: " + pair;
            CompareMessage = "AWR(dba_hist_sql_plan)에 한쪽 계획 단계가 남아 있지 않습니다.";
            OnPropertyChanged(nameof(HasComparison));
            return;
        }

        var cmp = PlanComparer.Compare(before with { PlanHashValue = baseHash }, after with { PlanHashValue = targetHash });
        foreach (var row in cmp.Rows)
        {
            CompareRows.Add(new PlanCompareRowItem(row));
        }

        CompareHashDiffers = cmp.Count(PlanDiffKind.Same) != cmp.Rows.Count;
        CompareTitle = CompareHashDiffers
            ? $"두 plan_hash 비교: {pair} — 계획이 다릅니다"
            : $"두 plan_hash 비교: {pair} — 단계가 같습니다(수치만 다름)";
        CompareSummary = string.Create(CultureInfo.InvariantCulture,
            $"기준 {baseHash}(평균 {AwrRowItem.Ms(Average(baseHash))}) · 대상 {targetHash}(평균 {AwrRowItem.Ms(Average(targetHash))}) · 같음 {cmp.Count(PlanDiffKind.Same)} · 바뀜 {cmp.Count(PlanDiffKind.Changed)} · 추가 {cmp.Count(PlanDiffKind.Added)} · 빠짐 {cmp.Count(PlanDiffKind.Removed)}")
            + (cmp.Positional ? " (단계가 많아 자리 순서로만 맞춤)" : string.Empty);
        Comparison = cmp;
        _owner.StatusText = "AWR 계획 비교: " + pair;
    }

    private double? Average(long hash) => Plans.FirstOrDefault(p => p.PlanHash == hash)?.AverageMs;

    partial void OnCompareMessageChanged(string value) => OnPropertyChanged(nameof(HasComparison));
}
