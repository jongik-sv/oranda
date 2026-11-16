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
    // ---- 세션 열기·닫기 ---------------------------------------------------------------------

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_session is not null)
        {
            await RefreshAsync(ct);
            return;
        }

        if (OpenSession is null)
        {
            State = LockViewState.NoConnection;
            StatusText = "접속 없음";
            return;
        }

        ISession? session;
        try
        {
            session = await OpenSession(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            State = LockViewState.NoConnection;
            StatusText = "모니터 세션을 열지 못했습니다: " + MonitorRows.FirstLine(ex.Message);
            return;
        }

        if (session is null)
        {
            State = LockViewState.NoConnection;
            StatusText = "접속 없음";
            return;
        }

        await AttachAsync(session, chosen: false, ct);
    }

    public async Task OpenWithOtherAccountAsync(CancellationToken ct = default)
    {
        if (ChooseSession is null)
        {
            StatusText = "다른 접속으로 열 수 없습니다(접속 정보 없음).";
            return;
        }

        ISession? session;
        try
        {
            session = await ChooseSession(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusText = "모니터 세션을 열지 못했습니다: " + MonitorRows.FirstLine(ex.Message);
            return;
        }

        if (session is not null)
        {
            await AttachAsync(session, chosen: true, ct);
        }
    }

    /// <summary>「다시 접속」: 끊긴 모니터 세션을 닫고 같은 방법으로 다시 연다.</summary>
    public async Task ReconnectAsync(CancellationToken ct = default)
    {
        await StopAsync();
        await StartAsync(ct);
    }

    private async Task AttachAsync(ISession session, bool chosen, CancellationToken ct)
    {
        if (!session.Server.Capabilities.CanQueryPerformanceViews)
        {
            await ConnectionService.CloseAsync(session, timeout: CloseTimeout);
            if (_session is null)
            {
                Access.ShowDenied(session.Profile);
                Access.SetAccount(session.Profile);
                State = LockViewState.Denied;
            }

            StatusText = "권한 없음 · v$lock·v$session 을 읽을 수 없습니다";
            return;
        }

        var previous = Interlocked.Exchange(ref _session, session);
        IsOpen = true;
        ClearError();
        Access.ClearNotice();
        Access.SetAccount(session.Profile);
        if (chosen)
        {
            Remember?.Invoke(session.Profile);
        }

        if (previous is not null)
        {
            PostToUi(ClearData);
            await CloseSessionAsync(previous);
        }

        _ddlLoaded = false;
        _idleLoaded = false;
        _deadlocksLoaded = false;
        _tracker.Reset();
        _history = LockHistoryBuffer.For(session.Profile.Id, TimeSpan.FromMinutes(HistoryMinutes));
        PostToUi(UpdateHistory);
        State = LockViewState.Loading;
        UpdateKeys();
        OnPropertyChanged(nameof(CanKill));
        await RefreshAsync(ct);
        if (StartAutoRefresh && !_autoStarted && _session is not null && State is LockViewState.Data or LockViewState.Empty)
        {
            _autoStarted = true;
            AutoRefresh = true;
        }
    }

    private async Task CloseSessionAsync(ISession session)
    {
        await _gate.WaitAsync();
        try
        {
            await ConnectionService.CloseAsync(session, timeout: CloseTimeout);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>자동 새로 고침을 멈추고 모니터 세션을 닫는다(탭이 닫힐 때).</summary>
    public async Task StopAsync()
    {
        AutoRefresh = false;
        StopLoop();
        var session = Interlocked.Exchange(ref _session, null);
        IsOpen = false;
        State = LockViewState.NoConnection;
        Access.SetAccount(null);
        UpdateKeys();
        ClearThreshold();
        if (session is not null)
        {
            await CloseSessionAsync(session);
        }
    }

    /// <summary>첫 읽기 취소(Esc).</summary>
    public void CancelLoad() => _firstRead?.Cancel();
}
