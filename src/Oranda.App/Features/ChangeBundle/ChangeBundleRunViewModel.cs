using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.Execution;
using Oranda.Core.ChangeBundle;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Language;
using Oranda.Core.Sessions;

namespace Oranda.App.Features.ChangeBundle;

/// <summary>
/// 묶음 「실행…」 창(설계 9.2, TE-15·TE-22): 지금 열린 접속을 고르고(기본 = 묶음 DBMS 와 같은 테스트 접속), 소유자 바꾸기를 적은 뒤
/// 체크한 문장을 화면 순서대로 <see cref="IExecutionService.ExecuteScriptAsync"/>(<see cref="ScriptErrorMode.Stop"/>, 출처 <see cref="HistorySource.ChangeBundle"/>)로 보낸다.
/// 문장마다 결과를 실행 기록으로 남기고(<see cref="IChangeBundleService.RecordRunAsync"/>), 자동 넣기는 출처로 빠진다(9.3).
/// 정책 관문(SAFE-03)은 실행 엔진이 그대로 지난다 — 운영 확인 한 번(TE-21)은 T10.
/// </summary>
public sealed partial class ChangeBundleRunViewModel : ObservableObject
{
    private readonly ChangeBundleInfo _bundle;
    private readonly IReadOnlyList<ChangeBundleItem> _items;
    private readonly IChangeBundleService? _service;
    private readonly IOwnerRewriter? _rewriter;
    private readonly ILanguageService? _language;
    private readonly IExecutionService? _execution;
    private readonly IEventBus? _bus;
    private CancellationTokenSource? _cts;

    internal ChangeBundleRunViewModel(ChangeBundleInfo bundle, IReadOnlyList<ChangeBundleItem> checkedItems, IReadOnlyList<ISession> sessions,
        IChangeBundleService? service, IOwnerRewriter? rewriter, ILanguageService? language, IExecutionService? execution, IEventBus? bus)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(checkedItems);
        ArgumentNullException.ThrowIfNull(sessions);
        _bundle = bundle;
        _items = checkedItems;
        _service = service;
        _rewriter = rewriter;
        _language = language;
        _execution = execution;
        _bus = bus;
        foreach (var s in sessions)
        {
            var same = bundle.Dbms is not { } dbms || s.Server.Dbms == dbms;
            Connections.Add(new ChangeBundleConnection(s, same, same ? null : $"묶음 DBMS({bundle.Dbms})와 다른 접속이다."));
        }

        foreach (var row in OwnerMapRow.For(bundle, checkedItems))
        {
            OwnerMap.Add(row);
        }

        _selectedConnection = DefaultConnection();
    }

    public ObservableCollection<ChangeBundleConnection> Connections { get; } = [];

    public ObservableCollection<OwnerMapRow> OwnerMap { get; } = [];

    /// <summary>
    /// 고른 접속에서 실제로 보낼 세션. 셸은 <c>SessionRouter.ForTool</c> — 활성 SQL 작업 공간이 같은 접속을 쓰면 그 세션(DDL 의 암묵 커밋이
    /// 다른 작업 공간의 미커밋을 확정하지 않게, 열 순서 바꾸기와 같다). 시험은 그대로.
    /// </summary>
    internal Func<ISession, ISession> Route { get; set; } = s => s;

    /// <summary>소유자를 붙이지 못한 문장 안내(소유자 바꾸기가 닿지 않는다). 없으면 빈 글.</summary>
    public string OwnerWarningText => ChangeBundleScript.UnqualifiedCount(_items) is var n and > 0
        ? $"⚠ 소유자를 붙이지 못한 문장 {n}개는 소유자 바꾸기가 적용되지 않고 실행 접속의 현재 스키마에서 실행된다."
        : string.Empty;

    public bool HasOwnerWarning => OwnerWarningText.Length > 0;

    public string BundleName => _bundle.Name;

    /// <summary>머리 줄: 「묶음 이름 · 체크한 문장 N개」.</summary>
    public string HeaderText => $"{_bundle.Name} · 체크한 문장 {_items.Count}개";

    public bool HasOwnerMap => OwnerMap.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun), nameof(IsProduction))]
    private ChangeBundleConnection? _selectedConnection;

    partial void OnSelectedConnectionChanged(ChangeBundleConnection? value)
    {
        if (value is { IsEnabled: false, Tooltip: { } why } && !IsRunning)
        {
            StatusText = why;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun), nameof(CloseText))]
    private bool _isRunning;

    /// <summary>창 아래 단추: 실행 중이면 「중지」, 아니면 「닫기」.</summary>
    public string CloseText => IsRunning ? "중지" : "닫기";

    /// <summary>실행을 멈춘다(지금 문장은 서버에 취소를 보낸다). 실행 중이 아니면 아무것도 하지 않는다.</summary>
    public void Cancel() => _cts?.Cancel();

    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>실행이 끝났다(한 번 실행하면 다시 실행하지 않고 닫는다).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    private bool _finished;

    public bool CanRun => !IsRunning && !Finished && SelectedConnection is { IsEnabled: true } && _items.Count > 0 && _execution is not null;

    /// <summary>운영 접속을 골랐다(창에 빨간 안내).</summary>
    public bool IsProduction => SelectedConnection?.Session.Profile.Environment == ConnectionEnvironment.Production;

    /// <summary>문장마다 성공한 수(시험·보고).</summary>
    public int SucceededCount { get; private set; }

    /// <summary>실행이 끝나 묶음 창이 다시 읽어야 한다.</summary>
    public event EventHandler? Completed;

    /// <summary>
    /// 고른 접속에 체크한 문장을 보낸다. 세션이 실행 중이면 섞어 보내지 않는다. 문장마다 결과를 실행 기록으로 남기고 멈춘 곳을 상태 줄에 알린다.
    /// </summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        if (!CanRun || SelectedConnection is not { } connection || _execution is not { } exec)
        {
            return;
        }

        var redacted = ChangeBundleScript.RedactedCount(_items);
        if (redacted > 0)
        {
            StatusText = $"비밀 값을 가린 문장이 {redacted}개 있다(***). 그 문장의 체크를 풀고 실행하거나, 편집기에서 열어 비밀 값을 적고 직접 실행한다.";
            return;
        }

        var session = Route(connection.Session);

        var state = ExecutionState.For(_bus);
        if (state.IsBusy(session.Id))
        {
            StatusText = "그 세션은 지금 실행 중이다. 실행이 끝난 뒤 다시 실행한다.";
            return;
        }

        var prepared = ChangeBundleScript.Prepare(_items, OwnerMapRow.ToMap(OwnerMap), _rewriter, _language);
        var origin = _service?.OriginOf(session.Id) ?? ChangeBundleOrigin.From(session);
        IsRunning = true;
        StatusText = $"실행 중… 0/{prepared.Count}";
        var done = 0;
        string? stopped = null;
        using var cts = _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        state.BeginRun(session.Id, null);
        using var cancelHook = state.RegisterCancel(session.Id, cts.Cancel); // 실행 취소 명령(oranda.exec.cancel)도 끊는다
        try
        {
            var statements = prepared.Select(p => p.Statement).ToList();
            await foreach (var result in exec.ExecuteScriptAsync(session, statements, ScriptErrorMode.Stop,
                               new ExecutionOptions { Source = HistorySource.ChangeBundle }, cts.Token).ConfigureAwait(true))
            {
                if (result.ResultSet is { } rs)
                {
                    await rs.DisposeAsync().ConfigureAwait(true);
                }

                if (done >= prepared.Count)
                {
                    break;
                }

                var item = prepared[done].Item;
                var ok = result.Status == ExecutionStatus.Succeeded;
                if (_service is not null && result.Status != ExecutionStatus.Cancelled)
                {
                    await _service.RecordRunAsync(item.Id, origin, ok, CancellationToken.None).ConfigureAwait(true);
                }

                done++;
                if (ok)
                {
                    SucceededCount++;
                    StatusText = $"실행 중… {done}/{prepared.Count}";
                }
                else
                {
                    stopped = $"{done}번째 문장에서 멈췄다: " + Describe(result);
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            stopped = "취소했다.";
        }
        catch (Exception ex)
        {
            // 세션 끊김·공급자 없음 등: 창이 「실행 중」에 머물지 않게 이유를 보인다.
            stopped = $"{done + 1}번째 문장을 보내지 못했다: {ex.Message}";
        }
        finally
        {
            _cts = null;
            state.ApplyTransaction(session.Id, session.HasPendingTransaction);
            state.EndRun(session.Id);
            IsRunning = false;
            Finished = true;
        }

        StatusText = stopped is null
            ? $"{SucceededCount}/{prepared.Count} 문장을 {connection.Session.Profile.Name} 에 실행했다."
            : $"{SucceededCount}/{prepared.Count} 문장 성공. " + stopped;
        Completed?.Invoke(this, EventArgs.Empty);
    }

    private static string Describe(ExecutionResult result) => result.Status switch
    {
        ExecutionStatus.Denied => "정책이 막았다" + (result.Policy?.Message is { Length: > 0 } reason ? $"({reason})" : string.Empty),
        ExecutionStatus.Cancelled => "취소했다",
        _ => result.Error is { } e ? $"{e.Code} {e.Message}".Trim() : "실패",
    };

    /// <summary>기본 접속: 묶음 DBMS 와 같은 테스트 접속, 없으면 같은 DBMS 의 첫 접속.</summary>
    private ChangeBundleConnection? DefaultConnection() =>
        Connections.FirstOrDefault(c => c.IsEnabled && c.Session.Profile.Environment == ConnectionEnvironment.Test)
        ?? Connections.FirstOrDefault(c => c.IsEnabled);
}
