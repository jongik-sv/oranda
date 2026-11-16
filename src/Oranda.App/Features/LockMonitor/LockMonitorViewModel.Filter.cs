using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.ExecutionPlan;
using Oranda.App.Features.SessionMonitor;
using Oranda.App.Shell;
using Oranda.App.Shell.Features;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Language.Errors;
using Oranda.Core.History;
using Oranda.Core.Monitoring;
using Oranda.Core.Monitoring.Providers;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;
using Oranda.Core.Settings;
using Oranda.Core.Shell;

namespace Oranda.App.Features.LockMonitor;

public sealed partial class LockMonitorViewModel
{
    private void UpdateLockCaption()
    {
        var mine = SelectedKey is { } k ? Locks.Count(l => l.Key == k) : 0;
        LockCaption = string.Create(CultureInfo.InvariantCulture, $"{Locks.Count}행")
            + (mine > 0 ? $" · 고른 세션 {LockText.Key(SelectedKey!.Value)} 의 잠금 {mine}행 ▶" : string.Empty);
    }

    private void RebuildInstances(IReadOnlyList<LockRow> rows)
    {
        var have = InstanceChoices.Skip(1).ToList();
        foreach (var inst in rows.Select(r => r.Session.InstanceId).Distinct().OrderBy(i => i))
        {
            var text = string.Create(CultureInfo.InvariantCulture, $"인스턴스 {inst}");
            if (!have.Contains(text))
            {
                InstanceChoices.Add(text);
            }
        }
    }

    private void RebuildTreeRows()
    {
        var visible = new List<LockTreeItem>();
        foreach (var item in _allTree)
        {
            if (IsHidden(item))
            {
                continue;
            }

            visible.Add(item);
        }

        var lines = PlanTreeLines.Compute([.. visible.Select(v => v.Depth)]);
        TreeRows.Clear();
        for (var i = 0; i < visible.Count; i++)
        {
            visible[i].TreeLines = lines[i];
            TreeRows.Add(visible[i]);
        }

        bool IsHidden(LockTreeItem item)
        {
            for (var p = item.Parent; p is not null; p = p.Parent)
            {
                if (!p.IsExpanded)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>트리 가지 접기·펴기(▾·▸). 접은 상태는 새로 고침 뒤에도 남는다.</summary>
    public void ToggleExpanded(LockTreeItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.HasChildren)
        {
            return;
        }

        item.IsExpanded = !item.IsExpanded;
        if (item.IsExpanded)
        {
            _collapsed.Remove(item.Key);
        }
        else
        {
            _collapsed.Add(item.Key);
        }

        RebuildTreeRows();
    }

    private void ClearData()
    {
        _snapshot = null;
        _allTree = [];
        TreeRows.Clear();
        Locks.Clear();
        Impacts.Clear();
        Hints.Clear();
        DdlLocks.Clear();
        IdleTransactions.Clear();
        Deadlocks.Clear();
        Cycles = [];
        CycleText = null;
        OnPropertyChanged(nameof(HasIdleTransactions));
        OnPropertyChanged(nameof(HasDeadlocks));
        OnPropertyChanged(nameof(ShowNoDeadlocks));
        SelectedKey = null;
    }

    // ---- 거르기 ------------------------------------------------------------------------------

    /// <summary>「3 초 이상」.</summary>
    public void SetMinWaitThree() => MinWaitSeconds = 3;

    /// <summary>「거르기 풀기」: 최소 대기·사용자·스키마·인스턴스를 비우고 다시 읽는다(대기·차단만은 그대로).</summary>
    public Task ClearFiltersAsync(CancellationToken ct = default)
    {
        _suspendFilterRefresh = true;
        try
        {
            MinWaitSeconds = 0;
            UsernameFilter = null;
            SchemaFilter = null;
            SelectedInstanceIndex = 0;
        }
        finally
        {
            _suspendFilterRefresh = false;
        }

        return RefreshAsync(ct);
    }

    /// <summary>「모든 잠금 보기」: 대기·차단만을 끄고 다시 읽는다.</summary>
    public Task ShowAllLocksAsync(CancellationToken ct = default)
    {
        _suspendFilterRefresh = true;
        try
        {
            WaitingOnly = false;
            MinWaitSeconds = 0;
        }
        finally
        {
            _suspendFilterRefresh = false;
        }

        return RefreshAsync(ct);
    }

    private bool _suspendFilterRefresh;

    partial void OnMinWaitSecondsChanged(int value)
    {
        if (value < 0)
        {
            MinWaitSeconds = 0;
            return;
        }

        FilterChanged();
    }

    partial void OnWaitingOnlyChanged(bool value) => FilterChanged();

    partial void OnSelectedInstanceIndexChanged(int value) => FilterChanged();

    private void FilterChanged()
    {
        if (!_suspendFilterRefresh && _session is not null && _snapshot is not null)
        {
            _ = RefreshAsync();
        }
    }

    // ---- 선택·탭 -----------------------------------------------------------------------------

    partial void OnSelectedKeyChanged(SessionKey? value)
    {
        UpdateLockCaption();
        UpdateKeys();
        if (_session is not null)
        {
            _ = LoadDetailAsync();
        }
    }

    partial void OnDetailTabIndexChanged(int value)
    {
        if (_session is not null)
        {
            _ = LoadDetailAsync();
        }
    }

    partial void OnTopTabIndexChanged(int value)
    {
        UpdateStatusText();
        if (_session is null)
        {
            return;
        }

        switch (value)
        {
            case DdlTab:
                _ = LoadDdlLocksAsync();
                break;
            case IdleTab:
                _idleLoaded = false;
                _ = LoadIdleTransactionsAsync();
                break;
            case DeadlockTab:
                _ = LoadDeadlocksAsync();
                break;
        }

        // SQL 탭 내용이 위쪽 탭(방치 트랜잭션 여부)에 따라 달라진다. 방치 탭은 목록을 읽은 뒤에 다시 읽는다.
        _detailLoaded = null;
        if (value != IdleTab)
        {
            _ = LoadDetailAsync();
        }
    }

    partial void OnErrorTextChanged(string? value) => OnPropertyChanged(nameof(HasError));

    partial void OnHolderSqlChanged(string? value) => OnPropertyChanged(nameof(HasHolderSql));

    partial void OnWaiterSqlChanged(string? value) => OnPropertyChanged(nameof(HasWaiterSql));

    private void NotifySql()
    {
        OnPropertyChanged(nameof(HasHolderSql));
        OnPropertyChanged(nameof(HasWaiterSql));
    }
}
