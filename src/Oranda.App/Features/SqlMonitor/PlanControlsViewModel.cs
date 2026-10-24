using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.SessionMonitor;
using Oranda.Core.Tuning;

namespace Oranda.App.Features.SqlMonitor;

/// <summary>계획 관리 탭의 상태.</summary>
public enum PlanControlsState
{
    NoSession,
    NoSql,
    Loading,

    /// <summary>걸린 것이 없음(권한이 없어도 빈 목록으로 온다).</summary>
    Empty,
    Loaded,

    /// <summary>dba_sql_* 를 읽을 권한이 없다고 서버가 알렸다.</summary>
    Denied,
    Error,
}

/// <summary>
/// 계획 관리 보기(TN-15, 보기만): 고른 sql_id 의 signature 에 걸린 SQL Profile·SQL Plan Baseline·SQL Patch.
/// 출처 dba_sql_profiles·dba_sql_plan_baselines·dba_sql_patches(팩 없음). 바꾸는 동작은 두지 않는다.
/// </summary>
public sealed partial class PlanControlsViewModel : ObservableObject
{
    private readonly SqlMonitorViewModel _owner;
    private string? _loadedFor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(IsLoaded), nameof(HasMessage), nameof(IsDenied))]
    private PlanControlsState _state = PlanControlsState.NoSession;

    [ObservableProperty]
    private string _message = string.Empty;

    [ObservableProperty]
    private string _hintText = string.Empty;

    [ObservableProperty]
    private string _grantSql = string.Empty;

    internal PlanControlsViewModel(SqlMonitorViewModel owner)
    {
        _owner = owner;
        Gate(out _);
    }

    /// <summary>SQL 모니터.</summary>
    public SqlMonitorViewModel Owner => _owner;

    public ObservableCollection<PlanControlItem> Items { get; } = [];

    public bool IsLoading => State == PlanControlsState.Loading;

    public bool IsLoaded => State == PlanControlsState.Loaded;

    public bool IsDenied => State == PlanControlsState.Denied;

    public bool HasMessage => State is PlanControlsState.NoSession or PlanControlsState.NoSql or PlanControlsState.Empty or PlanControlsState.Error;

    /// <summary>표 아래 줄(출처·보기만).</summary>
    public string SourceText => _loadedFor is null
        ? "dba_sql_plan_baselines · dba_sql_profiles · dba_sql_patches · 보기만"
        : $"sql_id {_loadedFor} · Profile {Count(SqlPlanControlKind.Profile)} · Baseline {Count(SqlPlanControlKind.Baseline)} · Patch {Count(SqlPlanControlKind.Patch)} · dba_sql_plan_baselines · dba_sql_profiles · dba_sql_patches · 보기만";

    public Task EnsureLoadedAsync()
    {
        var id = _owner.SelectedSqlId;
        return id is not null && string.Equals(id, _loadedFor, StringComparison.Ordinal) && State is PlanControlsState.Loaded or PlanControlsState.Empty
            ? Task.CompletedTask
            : LoadAsync();
    }

    /// <summary>고른 sql_id 의 Profile·Baseline·Patch 를 읽는다.</summary>
    public async Task LoadAsync()
    {
        if (!Gate(out var sqlId))
        {
            return;
        }

        State = PlanControlsState.Loading;
        IReadOnlyList<SqlPlanControlRow>? rows = null;
        Exception? failure = null;
        try
        {
            await _owner.RunTuningAsync(async (tuning, session) => rows = await tuning.GetSqlPlanControlsAsync(session, sqlId, CancellationToken.None), ex => failure = ex, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            _owner.PostToUi(() => State = PlanControlsState.NoSql);
            return;
        }

        _owner.PostToUi(() =>
        {
            _loadedFor = sqlId;
            Items.Clear();
            if (failure is not null)
            {
                if (MonitorAccessState.IsPrivilegeError(failure) && _owner.MonitorSession is { } s)
                {
                    Message = "dba_sql_* 를 읽을 권한이 없습니다";
                    HintText = $"계정 '{s.Profile.User}' 에 딕셔너리 조회 권한이 없어 Profile·Baseline·Patch 를 볼 수 없습니다. DBA 에게 아래 권한을 요청하세요.";
                    GrantSql = MonitorAccessState.GrantStatement(s.Profile.User);
                    State = PlanControlsState.Denied;
                }
                else
                {
                    Message = "계획 관리 정보를 읽지 못했습니다";
                    HintText = SqlMonitorViewModel.Describe(failure);
                    State = PlanControlsState.Error;
                }

                _owner.StatusText = "계획 관리: " + (State == PlanControlsState.Denied ? Message : HintText);
                OnPropertyChanged(nameof(SourceText));
                return;
            }

            foreach (var r in (rows ?? []).OrderBy(r => r.Kind).ThenByDescending(r => r.Created))
            {
                Items.Add(new PlanControlItem(r));
            }

            if (Items.Count == 0)
            {
                Message = "이 SQL 에 걸린 Profile·Baseline·Patch 가 없습니다";
                HintText = "옵티마이저가 계획을 그때그때 고릅니다. 계정에 dba_sql_* 조회 권한이 없어도 빈 목록으로 보입니다.";
                State = PlanControlsState.Empty;
            }
            else
            {
                State = PlanControlsState.Loaded;
            }

            _owner.StatusText = $"계획 관리: {sqlId} — Profile {Count(SqlPlanControlKind.Profile)}, Baseline {Count(SqlPlanControlKind.Baseline)}, Patch {Count(SqlPlanControlKind.Patch)}";
            OnPropertyChanged(nameof(SourceText));
        });
    }

    internal void Reset()
    {
        _loadedFor = null;
        Items.Clear();
        Gate(out _);
        OnPropertyChanged(nameof(SourceText));
    }

    internal void OnTargetChanged(bool visible)
    {
        var id = _owner.SelectedSqlId;
        if (id is not null && string.Equals(id, _loadedFor, StringComparison.Ordinal))
        {
            return;
        }

        if (id is null && State is PlanControlsState.Loading)
        {
            return;
        }

        _loadedFor = null;
        Items.Clear();
        OnPropertyChanged(nameof(SourceText));
        if (visible && id is not null)
        {
            _ = LoadAsync();
        }
        else
        {
            Gate(out _);
        }
    }

    private bool Gate(out string sqlId)
    {
        sqlId = _owner.SelectedSqlId ?? string.Empty;
        if (_owner.MonitorSession is null)
        {
            Message = "모니터 세션이 열려 있지 않습니다";
            HintText = "SQL 모니터를 열면 고른 SQL 에 걸린 Profile·Baseline·Patch 를 볼 수 있습니다.";
            State = PlanControlsState.NoSession;
            return false;
        }

        if (sqlId.Length == 0)
        {
            Message = "SQL 을 고르세요";
            HintText = "위 목록에서 SQL 을 고르면 그 문장의 signature 에 걸린 SQL Profile·Baseline·Patch 를 읽습니다(보기만).";
            State = PlanControlsState.NoSql;
            return false;
        }

        return true;
    }

    private int Count(SqlPlanControlKind kind) => Items.Count(i => i.Row.Kind == kind);
}
