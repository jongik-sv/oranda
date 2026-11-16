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
    /// <summary>오른쪽 상세 탭이 보이는 내용을 읽는다(고른 세션·탭이 바뀌었을 때만).</summary>
    public Task LoadDetailAsync(CancellationToken ct = default)
    {
        var tab = DetailTabIndex;
        var key = SelectedKey;
        if (_detailLoaded is { } done && done.Key == key && done.Tab == tab)
        {
            return Task.CompletedTask;
        }

        _detailLoaded = (key, tab);
        return tab switch
        {
            SqlTab => LoadSqlAsync(ct),
            RowTab => LoadLockedRowAsync(ct),
            BindTab => LoadBindsAsync(ct),
            _ => Task.CompletedTask,
        };
    }

    /// <summary>방치 트랜잭션 탭에서 고른 세션(그 탭이 앞에 있을 때만).</summary>
    public IdleTransactionItem? SelectedIdle =>
        TopTabIndex == IdleTab && SelectedKey is { } k ? IdleTransactions.FirstOrDefault(i => i.Key == k) : null;

    private Task LoadSqlAsync(CancellationToken ct)
    {
        if (SelectedIdle is { } idle)
        {
            return LoadIdleSqlAsync(idle, ct);
        }

        var (holder, waiter) = SelectedPair;
        IdleCallout = null;
        if (holder is null)
        {
            HolderSql = WaiterSql = null;
            HolderCaption = WaiterCaption = string.Empty;
            HolderHasNoCurrentSql = false;
            SqlMessage = "세션을 고르면 보유자·대기자 SQL 이 여기에 보입니다.";
            NotifySql();
            return Task.CompletedTask;
        }

        return RunAsync(async (monitor, session) =>
        {
            var holderText = await SqlTextAsync(monitor, session, holder.CurrentOrPreviousSqlId, ct);
            var waiterText = waiter is null ? null : await SqlTextAsync(monitor, session, waiter.Node.SqlId, ct);
            PostToUi(() =>
            {
                HolderCaption = $"보유자 · {holder.KeyText} {holder.Username} · {holder.StatusText}"
                    + (holder.Facts.TransactionStart is { } t && _snapshot is { } s
                        ? " " + LockText.Seconds((long)Math.Max(0, (s.CollectedAt - t).TotalSeconds)) : string.Empty);
                HolderHasNoCurrentSql = string.IsNullOrEmpty(holder.Node.SqlId);
                HolderSql = holderText is null ? null : Header(holder.CurrentOrPreviousSqlId) + holderText;
                WaiterCaption = waiter is null ? string.Empty : $"대기자 · {waiter.KeyText} {waiter.Username} · {waiter.WaitText}";
                WaiterSql = waiterText is null || waiter is null ? null : Header(waiter.Node.SqlId) + waiterText;
                SqlMessage = HolderSql is null && WaiterSql is null ? "SQL 을 찾지 못했습니다(커서가 공유 풀에서 나갔을 수 있습니다)." : string.Empty;
                NotifySql();
            });
        }, ct);
    }

    /// <summary>방치 트랜잭션: 「2·0644 SCOTT · INACTIVE 1,018 초 · 트랜잭션 17 분」 + 도구 안내 + 마지막 SQL(prev_sql_id).</summary>
    private Task<bool> LoadIdleSqlAsync(IdleTransactionItem idle, CancellationToken ct) => RunAsync(async (monitor, session) =>
    {
        var text = await SqlTextAsync(monitor, session, idle.Row.PrevSqlId, ct);
        PostToUi(() =>
        {
            HolderCaption = $"{LockText.Key(idle.Key)} {idle.Row.Username ?? "-"} · INACTIVE {LockText.Seconds(idle.Row.IdleSeconds)} · 트랜잭션 {LockText.Elapsed(idle.TransactionSeconds)}";
            HolderHasNoCurrentSql = false;
            IdleCallout = idle.Advice;
            HolderSql = text is null ? null : "-- prev_sql_id " + idle.Row.PrevSqlId + "\n" + text;
            WaiterCaption = string.Empty;
            WaiterSql = null;
            SqlMessage = HolderSql is null ? "마지막 SQL 을 찾지 못했습니다(커서가 공유 풀에서 나갔을 수 있습니다)." : string.Empty;
            NotifySql();
        });
    }, ct);

    private static string Header(string? sqlId) => "-- " + sqlId + "\n";

    private static async Task<string?> SqlTextAsync(IMonitorService monitor, ISession session, string? sqlId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(sqlId))
        {
            return null;
        }

        try
        {
            return (await monitor.GetSqlDetailAsync(session, new CursorRef(sqlId), ct)).FullText;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !MonitorAccessState.IsPrivilegeError(ex) && ex is not NotSupportedException)
        {
            return null;
        }
    }

    private Task LoadLockedRowAsync(CancellationToken ct)
    {
        var (_, waiter) = SelectedPair;
        void Reset(string message)
        {
            LockedRowId = null;
            LockedRowSelect = null;
            LockedRowCaption = string.Empty;
            LockedRowCells = [];
            LockedRowMessage = message;
            OnPropertyChanged(nameof(HasLockedRow));
        }

        if (waiter is null)
        {
            Reset("대기 세션을 고르면 기다리는 행이 여기에 보입니다.");
            return Task.CompletedTask;
        }

        if (!LockText.IsRowLockWait(waiter.Node.Event))
        {
            Reset($"행 잠금 대기가 아닙니다({waiter.Node.Event ?? "대기 없음"}).");
            return Task.CompletedTask;
        }

        return RunAsync(async (monitor, session) =>
        {
            var row = await monitor.GetLockedRowAsync(session, waiter.Key, ct);
            PostToUi(() =>
            {
                if (row is null)
                {
                    Reset(LockedRowMissingReason(session.Profile.Dbms));
                    return;
                }

                // 값을 못 읽으면(권한 없음·표가 아님·행 없음) 조회는 열 「오류」 하나에 이유를 담아 돌려준다(TSK-05-02 약속).
                if (row.ColumnNames is [LockedRowErrorColumn])
                {
                    Reset($"{row.Owner}.{row.Table} 의 잠긴 행({row.RowId}) 값을 읽지 못했습니다: {(row.Values.Count > 0 ? row.Values[0] : null) ?? "이유 모름"}");
                    return;
                }

                LockedRowId = row.RowId;
                LockedRowCaption = LockedRowText(row) + $" · 대기자 {waiter.KeyText}";
                LockedRowCells = [.. row.ColumnNames.Select((c, i) => (IReadOnlyList<object?>)[c, i < row.Values.Count ? row.Values[i] ?? "(null)" : string.Empty])];
                // 이 DBMS·버전에 문장이 없으면 null — 「SELECT 복사」만 빠지고 행 값은 그대로 보인다.
                LockedRowSelect = CatalogRouting.Text(_services, session, "app.locked_row_select", ("table", $"\"{row.Owner}\".\"{row.Table}\""), ("rowid", row.RowId));
                LockedRowMessage = string.Empty;
                OnPropertyChanged(nameof(HasLockedRow));
            });
        }, ct);
    }

    /// <summary>
    /// 잠긴 행을 못 찾았을 때의 이유. Tibero 는 잠긴 행 위치를 제공하지 않으므로(DbmsFeature 값 없이 DBMS 로 판정) 그 사실을 알리고,
    /// 그 밖(Oracle)은 예전 문구(대기가 끝났거나 row_wait_obj# 가 없음)를 그대로 쓴다.
    /// </summary>
    public static string LockedRowMissingReason(Oranda.Core.Connection.Dbms dbms) => dbms == Oranda.Core.Connection.Dbms.Oracle
        ? "잠긴 행을 찾지 못했습니다(대기가 끝났거나 row_wait_obj# 가 없습니다)."
        : $"{dbms} 는 잠긴 행 위치를 제공하지 않습니다.";

    /// <summary>「MES.LOT_HIST · 파일 7 · 블록 2495 · 행 2」(파일·블록·행이 없으면 표 이름만).</summary>
    public static string LockedRowText(LockedRowInfo row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var text = row.Owner + "." + row.Table;
        if (row.FileNumber is { } f)
        {
            text += string.Create(CultureInfo.InvariantCulture, $" · 파일 {f}");
        }

        if (row.BlockNumber is { } b)
        {
            text += string.Create(CultureInfo.InvariantCulture, $" · 블록 {b}");
        }

        if (row.RowNumber is { } r)
        {
            text += string.Create(CultureInfo.InvariantCulture, $" · 행 {r}");
        }

        return text;
    }

    private Task LoadBindsAsync(CancellationToken ct)
    {
        var (_, waiter) = SelectedPair;
        if (waiter is null)
        {
            BindCaption = string.Empty;
            BindCells = [];
            BindMessage = "대기 세션을 고르면 그 SQL 의 캡처된 바인드가 여기에 보입니다.";
            OnPropertyChanged(nameof(HasBinds));
            return Task.CompletedTask;
        }

        return RunAsync(async (monitor, session) =>
        {
            var binds = await monitor.GetWaitingBindsAsync(session, new WaitingBindTarget { Session = waiter.Key }, ct);
            PostToUi(() =>
            {
                BindCaption = $"대기자 {waiter.KeyText} · {waiter.Node.SqlId ?? "-"}";
                BindCells = [.. binds.Select(b => (IReadOnlyList<object?>)[b.Name, b.Position.ToString(CultureInfo.InvariantCulture), b.DataType ?? string.Empty, b.Value ?? "(null)",
                    b.CapturedAt?.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? string.Empty])];
                BindMessage = binds.Count == 0 ? "캡처된 바인드가 없습니다." : string.Empty;
                OnPropertyChanged(nameof(HasBinds));
            });
        }, ct);
    }

    /// <summary>DDL 잠금 탭(LK-09). 권한이 없으면 그 탭만 자물쇠 + 이유.</summary>
    public Task LoadDdlLocksAsync(CancellationToken ct = default)
    {
        if (_ddlLoaded)
        {
            return Task.CompletedTask;
        }

        return RunAsync(async (monitor, session) =>
        {
            try
            {
                var rows = await monitor.GetDdlLocksAsync(session, ct);
                PostToUi(() => ApplyDdl(rows, null, session));
                _ddlLoaded = true;
            }
            catch (Exception ex) when (MonitorAccessState.IsPrivilegeError(ex))
            {
                PostToUi(() => ApplyDdl([], ex, session));
                _ddlLoaded = true;
            }
        }, ct);
    }

    private void ApplyDdl(IReadOnlyList<DdlLockRow> rows, Exception? denied, ISession session)
    {
        DdlLocks.Clear();
        foreach (var r in rows)
        {
            DdlLocks.Add(new DdlLockItem(r));
        }

        if (denied is not null)
        {
            DdlNotice = "DDL 잠금은 dba_ddl_locks 권한이 필요합니다.";
            DdlGrantSql = CatalogRouting.Text(_services, session, "app.ddl_locks_grant", ("user", session.Profile.User.ToUpperInvariant())); // 문장이 없으면 null(GRANT 안내만 빠진다)
            DdlSummary = "권한 없음 · " + MonitorRows.FirstLine(denied.Message);
        }
        else
        {
            DdlNotice = null;
            DdlGrantSql = null;
            var excl = DdlLocks.Where(d => d.IsExclusive).Select(d => d.Key).Distinct().ToList();
            var waiting = DdlLocks.Count(d => d.IsWaiting);
            DdlSummary = DdlLocks.Count == 0
                ? "DDL·라이브러리 잠금이 없습니다 · dba_ddl_locks(권한이 없거나 뷰가 없는 DB 도 빈 목록)"
                : waiting > 0 && excl.Count > 0
                    ? $"Exclusive 를 잡은 {string.Join(", ", excl.Select(LockText.Key))} 이(가) {waiting} 세션을 막고 있습니다 · dba_ddl_locks"
                    : $"{DdlLocks.Count}행 · dba_ddl_locks";
        }

        Keys?.Set("canQueryDdlLocks", denied is null && _session is not null);
        OnPropertyChanged(nameof(HasDdlLocks));
        OnPropertyChanged(nameof(DdlDenied));
    }
}
