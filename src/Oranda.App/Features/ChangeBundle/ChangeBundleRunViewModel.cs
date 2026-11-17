using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.Execution;
using Oranda.App.Features.TableEdit;
using Oranda.Core.ChangeBundle;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Language;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Sessions;

namespace Oranda.App.Features.ChangeBundle;

/// <summary>
/// 묶음 「실행…」 창(설계 9.2, TE-15·TE-22): 지금 열린 접속을 고르고(기본 = 묶음 DBMS 와 같은 테스트 접속), 소유자 바꾸기를 적은 뒤
/// 체크한 문장을 화면 순서대로 <see cref="IExecutionService.ExecuteScriptAsync"/>(<see cref="ScriptErrorMode.Stop"/>, 출처 <see cref="HistorySource.ChangeBundle"/>)로 보낸다.
/// 문장마다 결과를 실행 기록으로 남기고(<see cref="IChangeBundleService.RecordRunAsync"/>), 자동 넣기는 출처로 빠진다(9.3).
/// 보내기 전에(T10, 설계 9.2·10장·TE-20·TE-21·TE-34): 해석할 수 있는 문장만 실행 전 검사(<see cref="ChangeBundleCheckPlanner"/> + <see cref="ITableEditChecks"/>) →
/// ✖ 면 보내지 않음(의뢰 스크립트 저장은 된다, TE-19) → 정책 판단을 미리 보고 확인이 필요하면(개발 접속 제외) 확인 창 한 번
/// (<see cref="IExecutionInteraction.ConfirmBatchAsync"/>, 검사 줄을 같은 창에) → 받은 문장 전부 <see cref="ExecutionOptions.PreConfirmed"/> 로 문장별 실행.
/// 확인 창을 거치지 않은 테스트·운영 접속의 ⚠ 는 창에 보이고 「그래도 실행」을 한 번 더 눌러야 보낸다. 개발 접속의 ⚠ 는 보이기만 한다(TE-20).
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

    /// <summary>실행 전 검사 읽기(셸 = <c>DbmsServices.For&lt;ITableEditChecks&gt;</c>). 없으면 검사할 문장은 ⚠ 「확인 못 함」.</summary>
    internal Func<ISession, ITableEditChecks?> ChecksFor { get; set; } = _ => null;

    /// <summary>DROP 이 휴지통을 거치지 않는 DBMS 인가(⚠ 문구, 셸 = <c>ITableEditDialect.PurgesOnDrop</c>).</summary>
    internal Func<ISession, bool> PurgesOnDropFor { get; set; } = _ => false;

    /// <summary>정책 판단을 미리 본다(셸 = <see cref="IExecutionPolicy"/>). 없으면 실행 엔진이 문장마다 확인한다.</summary>
    internal IExecutionPolicy? Policy { get; set; }

    /// <summary>확인 한 번 창(셸 = <see cref="IExecutionInteraction"/>). 정책이 확인을 원하는데 없으면 보내지 않는다.</summary>
    internal IExecutionInteraction? Interaction { get; set; }

    /// <summary>세션에 미커밋 변경이 있을 때(DDL 이 커밋한다) 계속할지. 없으면 묻지 않는다.</summary>
    internal Func<CancellationToken, Task<bool>>? ConfirmPendingCommit { get; set; }

    /// <summary>검사 질의 하나의 시간 제한(설계 10장 기본 10초).</summary>
    internal TimeSpan CheckTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>실행 전 검사 줄(✖ → ⚠ → ✔).</summary>
    public ObservableCollection<string> FindingLines { get; } = [];

    /// <summary>마지막 검사 결과(시험·보고).</summary>
    public IReadOnlyList<CheckFinding> Findings { get; private set; } = [];

    public bool HasFindings => FindingLines.Count > 0;

    /// <summary>「실행」 단추 글: ⚠ 를 본 뒤에는 「그래도 실행」.</summary>
    public string RunText => _acknowledged is not null ? "그래도 실행" : "실행";

    // ⚠ 를 보고 「그래도 실행」을 누를 차례인 실행(세션 + 문장). 접속·소유자 바꾸기를 고치면 다시 검사한다.
    private string? _acknowledged;

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
        SetAcknowledged(null);
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
        var statements = prepared.Select(p => p.Statement).ToList();
        // 검사·확인부터 실행 끝까지 세션을 「실행 중」으로 잡고(편집기 실행이 섞이지 않게), 「중지」·실행 취소 명령이 어느 단계든 끊는다.
        IsRunning = true;
        var done = 0;
        string? stopped = null;
        var ran = false;
        using var cts = _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        state.BeginRun(session.Id, null);
        using var cancelHook = state.RegisterCancel(session.Id, cts.Cancel); // 실행 취소 명령(oranda.exec.cancel)도 끊는다
        try
        {
            HashSet<string>? confirmed;
            try
            {
                confirmed = await GuardAsync(session, statements, origin, cts.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                StatusText = "취소했다.";
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                StatusText = "실행 전 확인 중 오류: " + ex.Message;
                return;
            }

            if (confirmed is null)
            {
                return;
            }

            ran = true;
            StatusText = $"실행 중… 0/{prepared.Count}";
            var options = new ExecutionOptions { Source = HistorySource.ChangeBundle, PreConfirmed = confirmed.Count == 0 ? null : confirmed };
            await foreach (var result in exec.ExecuteScriptAsync(session, statements, ScriptErrorMode.Stop, options, cts.Token).ConfigureAwait(true))
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
            Finished = ran;
        }

        if (!ran)
        {
            return;
        }

        StatusText = stopped is null
            ? $"{SucceededCount}/{prepared.Count} 문장을 {connection.Session.Profile.Name} 에 실행했다."
            : $"{SucceededCount}/{prepared.Count} 문장 성공. " + stopped;
        Completed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 검사 → 정책 미리 보기 → 확인 한 번 → ⚠ 확인 → 미커밋 확인. 보내도 되면 확인받은 문장 글(없으면 빈 집합), 아니면 null(상태 줄에 이유).
    /// </summary>
    private async Task<HashSet<string>?> GuardAsync(ISession session, List<SqlStatement> statements, ChangeBundleOrigin origin, CancellationToken ct)
    {
        var environment = session.Profile.Environment;
        StatusText = "실행 전 검사 중…";
        var purge = PurgesOnDropFor(session);
        var findings = await CheckAsync(session, statements, origin.CurrentSchema, purge, ct).ConfigureAwait(true);
        Findings = findings;
        FindingLines.Clear();
        foreach (var f in findings)
        {
            FindingLines.Add(CheckFindingText.Format(f, purge));
        }

        OnPropertyChanged(nameof(HasFindings));
        if (findings.Any(f => f.Severity == CheckSeverity.Block))
        {
            SetAcknowledged(null);
            StatusText = "실행 전 검사에서 ✖ 가 있어 실행하지 않았다(의뢰용 스크립트 저장은 된다).";
            return null;
        }

        var confirmed = new HashSet<string>(StringComparer.Ordinal);
        if (Policy is { } policy)
        {
            var decisions = new List<PolicyDecision>();
            foreach (var stmt in statements)
            {
                decisions.Add(await policy.EvaluateAsync(new PolicyContext(session.Profile, session.Server, HistorySource.ChangeBundle), stmt, ct)
                    .ConfigureAwait(true));
            }

            if (decisions.FirstOrDefault(d => d.Kind == PolicyDecisionKind.Deny) is { } deny)
            {
                StatusText = "정책이 실행을 막았다: " + (deny.Message ?? deny.Rule.ToString());
                return null;
            }

            var asks = decisions.Where(d => d.Kind == PolicyDecisionKind.Confirm).ToList();
            if (asks.Count > 0 && environment != ConnectionEnvironment.Development)
            {
                // 운영 확인 한 번(TE-21·TE-34): 문장마다 묻지 않고 한 창에 문장 묶음·검사 줄을 싣는다.
                var shown = asks.FirstOrDefault(d => d.RetypeText is not null) ?? asks[0];
                if (Interaction is null || !await Interaction.ConfirmBatchAsync(statements, shown, findings, session.Profile, ct).ConfigureAwait(true))
                {
                    SetAcknowledged(null);
                    StatusText = "실행하지 않았다.";
                    return null;
                }

                confirmed.UnionWith(statements.Select(s => s.Text));
            }
        }

        // 확인 창을 거치지 않은 테스트·운영 접속의 ⚠: 창에 보이고 한 번 더 눌러야 보낸다. 개발 접속은 보이기만(TE-20).
        var signature = session.Id.ToString("N") + "|" + string.Join("\u0001", statements.Select(s => s.Text));
        if (confirmed.Count == 0 && environment != ConnectionEnvironment.Development && findings.Any(f => f.Severity == CheckSeverity.Warning)
            && !string.Equals(_acknowledged, signature, StringComparison.Ordinal))
        {
            SetAcknowledged(signature);
            StatusText = "⚠ 를 확인하고 「그래도 실행」을 누르면 보낸다.";
            return null;
        }

        SetAcknowledged(null);
        if (session.HasPendingTransaction && ConfirmPendingCommit is { } pending && !await pending(ct).ConfigureAwait(true))
        {
            StatusText = "실행하지 않았다.";
            return null;
        }

        return confirmed;
    }

    private void SetAcknowledged(string? signature)
    {
        _acknowledged = signature;
        OnPropertyChanged(nameof(RunText));
    }

    /// <summary>해석할 수 있는 문장만 검사(<see cref="ChangeBundleCheckPlanner"/>). 읽기 실패·검사 읽기 없음 = ⚠ 「확인 못 함」. 사용자 취소는 던진다.</summary>
    private async Task<IReadOnlyList<CheckFinding>> CheckAsync(ISession session, List<SqlStatement> statements, string currentSchema, bool purge,
        CancellationToken ct)
    {
        var options = new TableEditCheckOptions { Environment = session.Profile.Environment, Server = session.Server, Timeout = CheckTimeout };
        var findings = new List<CheckFinding>();
        try
        {
            var plans = ChangeBundleCheckPlanner.Plan([.. statements.Select(s => s.Text)], currentSchema, options, purge);
            var checks = plans.Any(p => p.Plan.Checks.Count > 0) ? ChecksFor(session) : null;
            foreach (var plan in plans)
            {
                if (checks is null)
                {
                    findings.AddRange(plan.Plan.Findings);
                    if (plan.Plan.Checks.Count > 0)
                    {
                        findings.Add(new CheckFinding(CheckSeverity.Warning, CheckCode.NotChecked, plan.Plan.Checks[0].RowKey)
                        {
                            Subject = plan.Plan.Checks[0].Subject, Detail = "검사 읽기 구현이 없다",
                        });
                    }

                    continue;
                }

                findings.AddRange(await TableEditCheckRunner.RunAsync(plan.Plan, plan.Table, checks, session, options, ct).ConfigureAwait(true));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            findings.Add(new CheckFinding(CheckSeverity.Warning, CheckCode.NotChecked, TableDraft.TableRowKey) { Detail = ex.Message });
        }

        return [.. findings.OrderBy(f => f.Severity switch { CheckSeverity.Block => 0, CheckSeverity.Warning => 1, _ => 2 })];
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
