using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.SessionMonitor;
using Oranda.App.Shell;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;
using Oranda.Core.Tuning;

namespace Oranda.App.Features.SqlMonitor;

/// <summary>
/// TSK-06-06 튜닝 탭(대기 분해·AWR 계획 이력·계획 관리)과 도구 모음 단추(자식 커서·스크립트 만들기). TN-12·13·14·16.
/// <para>모든 조회는 SELECT 다. 팩이 필요한 조회(AWR·ASH)는 모니터 세션 프로필의 보유 선언(서버 <c>control_management_pack_access</c> 가 더 낮으면 그것)이
/// 모자라면 서버로 보내지 않고 이유를 보인다. 스크립트는 새 편집기 탭에 글만 넣는다(실행하지 않는다).</para>
/// </summary>
public sealed partial class SqlMonitorViewModel
{
    /// <summary>아래 탭 번호.</summary>
    public const int DetailTab = 0;

    public const int PlanTab = 1;

    public const int WaitsTab = 2;

    public const int AwrTab = 3;

    public const int ControlsTab = 4;

    /// <summary>자식 커서 명령(TSK-06-03 레인 tn-stats 가 등록). 인자 = sql_id 문자열.</summary>
    public const string ChildCursorsCommand = "oranda.tuning.childCursors";

    /// <summary>Lock 모니터 명령(TSK-05-03). 등록돼 있을 때만 바로 가기를 보인다.</summary>
    public const string LockMonitorCommand = "oranda.monitor.locks";

    /// <summary>접속 관리자 명령(팩 선언을 켜는 곳).</summary>
    public const string ConnectionManagerCommand = "oranda.conn.manage";

    private ITuningService? _tuning;
    private SqlWaitsViewModel? _waits;
    private AwrHistoryViewModel? _awr;
    private PlanControlsViewModel? _controls;

    [ObservableProperty]
    private int _detailTabIndex;

    /// <summary>튜닝 조회 서비스. 시험이 넣고, 셸은 처음 쓸 때 DI 에서 꺼낸다.</summary>
    public ITuningService? TuningService
    {
        get => _tuning ??= _services?.GetService(typeof(ITuningService)) as ITuningService;
        set => _tuning = value;
    }

    /// <summary>대기 분해 탭.</summary>
    public SqlWaitsViewModel Waits => _waits ??= new SqlWaitsViewModel(this);

    /// <summary>AWR 계획 이력 탭.</summary>
    public AwrHistoryViewModel Awr => _awr ??= new AwrHistoryViewModel(this);

    /// <summary>계획 관리 탭.</summary>
    public PlanControlsViewModel Controls => _controls ??= new PlanControlsViewModel(this);

    /// <summary>명령이 등록돼 있는지. 셸은 명령 레지스트리로 채운다. 없으면 모든 바로 가기를 숨긴다.</summary>
    public Func<string, bool>? CommandAvailable { get; set; }

    /// <summary>명령 실행(명령 ID, 인자). 셸은 명령 레지스트리로, 없으면 <see cref="ShellCommandRunner"/>(인자 없이).</summary>
    public Func<string, object?, Task>? RunCommand { get; set; }

    /// <summary>새 편집기 탭에 글을 넣는 방법(제목, 본문). 기본 = SQL 편집기 작업 공간의 새 문서.</summary>
    public Action<string, string>? OpenEditor { get; set; }

    /// <summary>모니터 세션이 Diagnostics Pack 조회를 보낼 수 있는가.</summary>
    public bool HasDiagnostics => _session is { } s && HeldPack(s) >= LicensePack.Diagnostics;

    /// <summary>「자식 커서」 단추를 보일지(명령이 등록돼 있을 때만).</summary>
    public bool CanOpenChildCursors => IsCommandAvailable(ChildCursorsCommand);

    /// <summary>Lock 모니터 바로 가기를 보일지.</summary>
    public bool CanOpenLockMonitor => IsCommandAvailable(LockMonitorCommand);

    /// <summary>스크립트 단추·자식 커서 단추를 누를 수 있는가(SQL 을 골랐을 때).</summary>
    public bool HasSelectedSql => SelectedSql is not null;

    /// <summary>지금 고른 SQL 의 sql_id(없으면 null).</summary>
    public string? SelectedSqlId => SelectedSql?.SqlId;

    /// <summary>프로필 보유 선언과 서버 <c>control_management_pack_access</c> 중 낮은 쪽.</summary>
    public static LicensePack HeldPack(ISession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var held = session.Profile.LicensePack;
        if (ExecutionPolicy.ParseServerPackAccess(session.Server.ManagementPackAccess) is { } server && server < held)
        {
            held = server;
        }

        return held;
    }

    /// <summary>Diagnostics Pack 이 꺼진 이유와 켜는 방법. 켜져 있으면 null.</summary>
    public (string Reason, string HowTo)? DiagnosticsOff()
    {
        if (_session is not { } s)
        {
            return ("모니터 세션이 열려 있지 않습니다.", "SQL 모니터를 열면 접속 프로필의 팩 선언을 확인합니다.");
        }

        if (HeldPack(s) >= LicensePack.Diagnostics)
        {
            return null;
        }

        if (s.Profile.LicensePack >= LicensePack.Diagnostics)
        {
            return ($"서버 설정 control_management_pack_access = {s.Server.ManagementPackAccess} 이라 팩 조회를 보내지 않았습니다.",
                "DBA 가 서버 값을 DIAGNOSTIC 이상으로 바꿔야 쓸 수 있습니다.");
        }

        return ($"접속 '{s.Profile.Name}' 에 Diagnostics Pack 보유가 선언되어 있지 않아 dba_hist_*·ASH 를 읽지 않았습니다.",
            "보유한 경우에만: 접속 관리자 › 접속 편집 › 고급 설정 › 「관리 팩」 에서 Diagnostics Pack 을 고르세요.");
    }

    /// <summary>
    /// 「자식 커서」: 고른 sql_id(RAC 면 그 인스턴스)로 자식 커서 문서를 연다(TSK-06-03 <c>TuningNavigation</c>). 명령이 없으면 아무것도 안 한다.
    /// 시험은 <see cref="RunCommand"/> 로 받는다(인자 = sql_id).
    /// </summary>
    public Task OpenChildCursorsAsync()
    {
        if (SelectedSql is not { } item || !CanOpenChildCursors)
        {
            return Task.CompletedTask;
        }

        return RunCommand is null
            ? Oranda.App.Features.Tuning.Stats.TuningNavigation.OpenChildCursorsAsync(item.SqlId, item.Row.Cursor.InstanceId)
            : Run(ChildCursorsCommand, item.SqlId);
    }

    /// <summary>Lock 모니터 열기.</summary>
    public Task OpenLockMonitorAsync() => CanOpenLockMonitor ? Run(LockMonitorCommand, null) : Task.CompletedTask;

    /// <summary>접속 관리자 열기(팩 선언을 켜는 곳).</summary>
    public Task OpenConnectionManagerAsync() => Run(ConnectionManagerCommand, null);

    /// <summary>
    /// 「Tuning Advisor 스크립트」: DBMS_SQLTUNE 문안을 새 편집기 탭에 넣는다(실행하지 않는다). 팩이 없어도 만들 수 있다 —
    /// 머리 주석에 「DB 에 씁니다·Tuning Pack 필요」를 적는다.
    /// </summary>
    public bool CreateTuningAdvisorScript()
    {
        if (SelectedSqlId is not { } id)
        {
            StatusText = "SQL 목록에서 행을 고르세요.";
            return false;
        }

        var script = TuningScripts.TuningAdvisor(id);
        OpenScript($"tuning_advisor_{id}.sql", Header(id, null) + script.Body);
        StatusText = $"Tuning Advisor 스크립트를 새 편집기 탭에 넣었습니다(실행하지 않음). Tuning Pack 이 필요합니다.";
        return true;
    }

    /// <summary>
    /// 「baseline 고정 스크립트」: DBMS_SPM.LOAD_PLANS_FROM_CURSOR_CACHE 문안을 새 편집기 탭에 넣는다(실행하지 않는다).
    /// plan_hash 는 고른 자식 커서의 것(gv$sql, 팩 없음)을 쓰고, 읽지 못하면 그 sql_id 의 모든 계획으로 둔다.
    /// </summary>
    public async Task<bool> CreateBaselineScriptAsync(CancellationToken ct = default)
    {
        if (SelectedSql is not { } item)
        {
            StatusText = "SQL 목록에서 행을 고르세요.";
            return false;
        }

        var id = item.SqlId;
        Oranda.Core.Tuning.ChildCursorRow? child = null;
        await RunTuningAsync(async (tuning, session) =>
        {
            var children = await tuning.GetChildCursorsAsync(session, id, ct);
            child = children.FirstOrDefault(c => c.ChildNumber == item.Row.Cursor.ChildNumber)
                ?? children.Where(c => c.Executions > 0).OrderBy(c => c.ElapsedPerExecMicros).FirstOrDefault()
                ?? (children.Count > 0 ? children[0] : null);
        }, _ => { }, ct);

        var script = TuningScripts.Baseline(id, child?.PlanHashValue);
        OpenScript($"baseline_{id}.sql", Header(id, child) + script.Body);
        StatusText = child is null
            ? "baseline 고정 스크립트를 새 편집기 탭에 넣었습니다(실행하지 않음). plan_hash 를 정하지 못해 이 sql_id 의 모든 계획을 대상으로 했습니다."
            : $"baseline 고정 스크립트를 새 편집기 탭에 넣었습니다(실행하지 않음). plan_hash {child.PlanHashValue.ToString(CultureInfo.InvariantCulture)}.";
        return true;
    }

    /// <summary>
    /// 튜닝 조회 한 번. 모니터 세션의 같은 관문을 지난다(Top SQL 검색과 겹치지 않게). 취소는 그대로 던지고, 그 밖의 실패는 <paramref name="fail"/> 로 넘긴다.
    /// 세션·서비스가 없으면 false.
    /// </summary>
    internal async Task<bool> RunTuningAsync(Func<ITuningService, ISession, Task> work, Action<Exception> fail, CancellationToken ct)
    {
        if (_session is not { } session || TuningService is not { } tuning)
        {
            fail(new InvalidOperationException(_session is null ? "모니터 세션이 열려 있지 않습니다." : "튜닝 서비스가 없습니다."));
            return false;
        }

        try
        {
            await _gate.WaitAsync(ct);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        try
        {
            await work(tuning, session);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            fail(ex);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>실패를 화면 문구로(첫 줄). 준비 안 된 기능은 따로 적는다.</summary>
    internal static string Describe(Exception ex) => ex switch
    {
        NotSupportedException or NotImplementedException => "이 서버 연결에서는 아직 이 조회를 쓸 수 없습니다.",
        _ => FirstLine(ex.Message),
    };

    /// <summary>모니터 세션이 바뀌었다(열림·닫힘·다른 접속).</summary>
    private void OnTuningSessionChanged()
    {
        OnPropertyChanged(nameof(HasDiagnostics));
        _waits?.Reset();
        _awr?.Reset();
        _controls?.Reset();
    }

    /// <summary>고른 SQL 이 바뀌었다.</summary>
    private void OnTuningTargetChanged()
    {
        OnPropertyChanged(nameof(HasSelectedSql));
        OnPropertyChanged(nameof(SelectedSqlId));
        OnPropertyChanged(nameof(CanOpenChildCursors));
        _waits?.OnTargetChanged();
        _awr?.OnTargetChanged(DetailTabIndex == AwrTab);
        _controls?.OnTargetChanged(DetailTabIndex == ControlsTab);
    }

    partial void OnDetailTabIndexChanged(int value)
    {
        // 탭을 처음 열 때 읽는다(보이지 않는 탭은 서버에 묻지 않는다).
        if (value == AwrTab)
        {
            _ = Awr.EnsureLoadedAsync();
        }
        else if (value == ControlsTab)
        {
            _ = Controls.EnsureLoadedAsync();
        }
    }

    private bool IsCommandAvailable(string id) =>
        CommandAvailable is { } available
            ? available(id)
            : _services?.GetService(typeof(ICommandRegistry)) is ICommandRegistry registry && registry.Find(id) is not null;

    private Task Run(string id, object? parameter)
    {
        if (RunCommand is { } run)
        {
            return run(id, parameter);
        }

        if (_services?.GetService(typeof(ICommandRegistry)) is ICommandRegistry registry && _services.GetService(typeof(ContextKeyService)) is ContextKeyService keys)
        {
            return registry.ExecuteAsync(id, keys, parameter);
        }

        return ShellCommandRunner.RunAsync(id);
    }

    private void OpenScript(string title, string body)
    {
        if (OpenEditor is { } open)
        {
            open(title, body);
        }
        else
        {
            Oranda.App.Features.SqlEditor.SqlEditorHost.Current.NewDocument(body).Title = title;
        }
    }

    /// <summary>머리 주석의 대상 줄(문안 머리 주석 위에 붙는다).</summary>
    private string Header(string sqlId, Oranda.Core.Tuning.ChildCursorRow? child)
    {
        var sb = new StringBuilder();
        sb.Append("-- 대상: sql_id ").Append(sqlId);
        if (child is not null)
        {
            sb.Append(" · plan_hash ").Append(child.PlanHashValue.ToString(CultureInfo.InvariantCulture))
              .Append(" (child ").Append(child.ChildNumber.ToString(CultureInfo.InvariantCulture));
            if (child.ElapsedPerExecMicros is { } us)
            {
                sb.Append(", ").Append(AwrRowItem.Ms(us / 1000)).Append("/실행");
            }

            sb.Append(')');
        }

        if (_session is { } s)
        {
            sb.Append(" · 접속 ").Append(s.Profile.Name);
        }

        sb.AppendLine();
        return sb.ToString();
    }
}
