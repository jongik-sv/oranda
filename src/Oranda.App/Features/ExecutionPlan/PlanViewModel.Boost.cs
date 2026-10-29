using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;
using CorePlan = Oranda.Core.Plans.ExecutionPlan;

namespace Oranda.App.Features.ExecutionPlan;

/// <summary>보고서 탭 상태(TSK-06-04).</summary>
public enum PlanReportState
{
    /// <summary>실제 계획(커서)이 없다.</summary>
    NoCursor,

    /// <summary>Tuning Pack 보유 선언이 없어 보내지 않았다.</summary>
    NoLicense,

    Loading,

    /// <summary>이 sql_id 의 모니터 기록이 없다.</summary>
    Empty,

    Error,

    Ready,
}

/// <summary>
/// 계획 창 보강(TSK-06-04, TN-02·06·07): A-Rows 없음 띠와 두 해결 경로, 그래프 탭, SQL 모니터 보고서 탭, 통계·자식 커서 단추.
/// </summary>
public sealed partial class PlanViewModel
{
    /// <summary>그래프 탭 위치.</summary>
    public const int GraphTabIndex = 3;

    /// <summary>보고서 탭 위치.</summary>
    public const int ReportTabIndex = 4;

    public const string StatsCommandId = "oranda.tuning.stats";
    public const string ChildCursorsCommandId = "oranda.tuning.childCursors";
    public const string AssistantCommandId = "oranda.tuning.assistant";
    public const string ConnectionManagerCommandId = "oranda.conn.manage";

    /// <summary>예상 계획이 PLAN_TABLE 에 쓴다는 안내(툴팁·상태 줄).</summary>
    public const string ExplainNotice = "EXPLAIN PLAN 은 PLAN_TABLE 에 행을 씁니다. 이미 실행한 문장이면 실제 계획(Ctrl+Shift+E)을 권합니다.";

    public const string NoMonitorRecordText = "이 sql_id 의 모니터 기록이 없습니다 — 5 초 넘게 돌았거나 MONITOR 힌트가 있어야 남습니다.";

    private HashSet<int> _collapsed = [];
    private bool _autoRerunTried;
    private CursorRef? _reportCursor;
    private SqlStatement? _lastStatement;

    /// <summary>실제 계획인데 A-Rows 가 모두 비었다(통계 수집 꺼짐) — 띠를 보인다.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBandVisible))]
    private bool _showStatisticsBand;

    /// <summary>힌트 사본·STATISTICS_LEVEL 다시 실행 중(띠 단추를 끈다).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRerun))]
    private bool _isRerunning;

    /// <summary>지금 계획이 예상 계획(EXPLAIN PLAN)이다.</summary>
    [ObservableProperty]
    private bool _isEstimated;

    [ObservableProperty]
    private PlanGraphLayout _graph = PlanGraphLayout.Empty;

    [ObservableProperty]
    private PlanGraphColorBasis _graphColorBasis = PlanGraphColorBasis.Time;

    /// <summary>그래프에서 고른 단계(트리 선택과 같이 움직인다).</summary>
    [ObservableProperty]
    private int? _graphSelectedId;

    /// <summary>접어 둔 단계 수(상태 줄 안내).</summary>
    [ObservableProperty]
    private int _graphCollapsedCount;

    /// <summary>설정 「실제 계획 때 늘 힌트 넣기」(<c>execution.gatherPlanStatisticsHint</c>).</summary>
    [ObservableProperty]
    private bool _alwaysHint;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReportNoCursor), nameof(IsReportNoLicense), nameof(IsReportLoading), nameof(IsReportEmpty), nameof(IsReportError),
        nameof(IsReportReady), nameof(CanUseReport))]
    private PlanReportState _reportState;

    [ObservableProperty]
    private string _reportText = string.Empty;

    /// <summary>보고서 탭 안내·오류 글.</summary>
    [ObservableProperty]
    private string _reportMessage = "실제 계획(Ctrl+Shift+E)을 연 뒤 보고서를 볼 수 있습니다.";

    [ObservableProperty]
    private string _reportStatusText = "DBMS_SQLTUNE.REPORT_SQL_MONITOR(type => 'TEXT') · HTML 은 외부 브라우저";

    /// <summary>
    /// 다시 실행하는 방법(명령 처리기가 <see cref="PlanStatsRerunner"/> 로 채운다, 시험은 가짜). null 이면 띠 단추가 이유를 보인다.
    /// </summary>
    public Func<PlanRerunMode, SqlStatement, CancellationToken, Task<PlanRerunOutcome>>? Rerunner { get; set; }

    /// <summary>다시 실행할 문장(마지막으로 실행한 문장, 명령 처리기가 넣는다). 없으면 편집기 지금 문장을 <see cref="CurrentStatement"/> 로 얻는다.</summary>
    public SqlStatement? LastStatement
    {
        get => _lastStatement;
        set
        {
            _lastStatement = value;
            _autoRerunTried = false; // 새로 실행한 문장이면 「늘 힌트 넣기」를 다시 한 번 할 수 있다
        }
    }

    /// <summary>편집기 지금 문장(명령 처리기가 넣는다).</summary>
    public Func<SqlStatement?>? CurrentStatement { get; set; }

    /// <summary>명령이 등록돼 있는지(단추 보이기). null 이면 레지스트리를 본다.</summary>
    public Func<string, bool>? HasCommand { get; set; }

    /// <summary>명령 실행(기본 = <see cref="Shell.ShellCommandRunner"/>).</summary>
    public Func<string, Task> RunCommand { get; set; } = Shell.ShellCommandRunner.RunAsync;

    /// <summary>표 통계 열기(기본 = <see cref="Tuning.Stats.TuningNavigation.OpenStatsAsync"/>, TSK-06-03). 시험은 바꾼다.</summary>
    public Func<string?, string, Task> OpenStats { get; set; } = Tuning.Stats.TuningNavigation.OpenStatsAsync;

    /// <summary>자식 커서 열기(기본 = <see cref="Tuning.Stats.TuningNavigation.OpenChildCursorsAsync"/>, TSK-06-03). 시험은 바꾼다.</summary>
    public Func<string, int?, Task> OpenChildCursors { get; set; } = Tuning.Stats.TuningNavigation.OpenChildCursorsAsync;

    /// <summary>설정 저장(「늘 힌트 넣기」 바꿈). null 이면 저장하지 않는다.</summary>
    public Action<bool>? SaveAlwaysHint { get; set; }

    /// <summary>파일 열기(HTML 보고서 → 기본 브라우저). 시험은 바꾼다.</summary>
    public Func<string, bool> OpenFile { get; set; } = DataTransfer.FileLauncher.Open;

    /// <summary>마지막 실제 계획을 읽은 세션.</summary>
    public ISession? LastSession { get; private set; }

    public bool CanRerun => !IsRerunning;

    public bool HasStatsCommand => Has(StatsCommandId);

    public bool HasChildCursorsCommand => Has(ChildCursorsCommandId);

    public bool HasAssistantCommand => Has(AssistantCommandId);

    public bool HasConnectionManagerCommand => Has(ConnectionManagerCommandId);

    /// <summary>「통계 보기」 「자식 커서」 「튜닝 보고서…」 중 하나라도 있으면 구분선과 단추 줄을 보인다.</summary>
    public bool HasTuningCommands => HasStatsCommand || HasChildCursorsCommand || HasTuningReportCommand;

    /// <summary>그래프 색 기준 「시간」을 쓸 수 있는가(실제 시간이 있는 계획).</summary>
    public bool HasTimeBasis => Metrics?.Actual == PlanActualMetric.Time;

    public bool HasGraph => Graph.Nodes.Count > 0;

    /// <summary>범례의 색 진하기 뜻.</summary>
    public string GraphColorLegend => GraphColorBasis == PlanGraphColorBasis.Time && HasTimeBasis ? "시간 비중" : "비용 비중";

    /// <summary>접힌 단계 안내.</summary>
    public string GraphCollapsedText => $"접힌 단계 +{GraphCollapsedCount} · 노드를 누르면 펼침";

    /// <summary>색 기준 묶음의 고른 칸(0 시간, 1 비용).</summary>
    public int GraphColorIndex
    {
        get => GraphColorBasis == PlanGraphColorBasis.Cost ? 1 : 0;
        set => GraphColorBasis = value == 1 ? PlanGraphColorBasis.Cost : PlanGraphColorBasis.Time;
    }

    public bool IsReportNoCursor => ReportState == PlanReportState.NoCursor;

    public bool IsReportNoLicense => ReportState == PlanReportState.NoLicense;

    public bool IsReportLoading => ReportState == PlanReportState.Loading;

    public bool IsReportEmpty => ReportState == PlanReportState.Empty;

    public bool IsReportError => ReportState == PlanReportState.Error;

    public bool IsReportReady => ReportState == PlanReportState.Ready;

    /// <summary>새로 고침·브라우저 단추를 켤 수 있는가(커서와 팩이 있음).</summary>
    public bool CanUseReport => ReportState is PlanReportState.Ready or PlanReportState.Empty or PlanReportState.Error;

    public bool IsTreeToolbar => SelectedTabIndex is 0 or 1 or 2;

    /// <summary>트리 탭이다(접기·펼치기 단추를 보인다).</summary>
    public bool IsTreeTab => SelectedTabIndex == 0;

    /// <summary>띠는 트리·텍스트·비교·그래프 탭 위에 보인다(보고서 탭은 자기 상태 화면이 있다).</summary>
    public bool IsBandVisible => ShowStatisticsBand && SelectedTabIndex != ReportTabIndex;

    /// <summary>「통계 보기」「자식 커서」 단추 줄(보고서 탭에서는 숨김).</summary>
    public bool ShowTuningButtons => HasTuningCommands && SelectedTabIndex != ReportTabIndex;

    public bool IsGraphToolbar => SelectedTabIndex == GraphTabIndex;

    public bool IsReportToolbar => SelectedTabIndex == ReportTabIndex;

    /// <summary>띠 조건: 실제 계획(커서가 있음) + 단계가 있고 A-Rows 가 모두 비었다.</summary>
    public static bool NeedsStatisticsBand(CorePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.Cursor is not null && plan.Steps.Count > 0 && plan.Steps.All(s => s.ActualRows is null);
    }

    /// <summary>세션이 Tuning Pack 을 쓸 수 있는가(보유 선언과 서버 control_management_pack_access 중 낮은 쪽).</summary>
    public static bool HasTuningPack(ISession s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var held = s.Profile.LicensePack;
        if (ExecutionPolicy.ParseServerPackAccess(s.Server.ManagementPackAccess) is { } server && server < held)
        {
            held = server;
        }

        return held >= LicensePack.DiagnosticsAndTuning;
    }

    /// <summary>명령 목록이 바뀌었을 때(단추 보이기 다시 계산).</summary>
    public void RefreshCommandButtons()
    {
        OnPropertyChanged(nameof(HasStatsCommand));
        OnPropertyChanged(nameof(HasChildCursorsCommand));
        OnPropertyChanged(nameof(HasAssistantCommand));
        OnPropertyChanged(nameof(HasConnectionManagerCommand));
        OnPropertyChanged(nameof(HasTuningReportCommand));
        OnPropertyChanged(nameof(HasTuningCommands));
        OnPropertyChanged(nameof(ShowTuningButtons));
    }

    /// <summary>
    /// 「통계 보기」: 고른 단계가 표 접근이면 그 표, 아니면 계획에서 처음 나오는 표(가장 비싼 표 접근을 먼저)를 넘긴다. 표가 없으면 대상 없이 명령을 부른다.
    /// </summary>
    public Task ShowTableStatsAsync()
    {
        if (StatsTarget() is { } step)
        {
            return OpenStats(string.IsNullOrEmpty(step.ObjectOwner) ? null : step.ObjectOwner, step.ObjectName!);
        }

        return RunCommand(StatsCommandId);
    }

    /// <summary>「자식 커서」: 지금 실제 계획의 sql_id 를 넘긴다. 커서가 없으면 대상 없이 명령을 부른다.</summary>
    public Task ShowChildCursorsAsync() =>
        CurrentPlan?.Cursor is { } cursor ? OpenChildCursors(cursor.SqlId, cursor.InstanceId) : RunCommand(ChildCursorsCommandId);

    /// <summary>통계 볼 표 단계(TABLE ACCESS·MAT_VIEW ACCESS 이고 객체 이름이 있는 것).</summary>
    internal PlanStep? StatsTarget()
    {
        static bool IsTable(PlanStep? s) =>
            s is { ObjectName.Length: > 0 } && (s.Operation.StartsWith("TABLE ACCESS", StringComparison.OrdinalIgnoreCase) || s.Operation.StartsWith("MAT_VIEW", StringComparison.OrdinalIgnoreCase));

        if (IsTable(SelectedRow?.Step))
        {
            return SelectedRow!.Step;
        }

        var hot = Metrics is { HottestIndex: >= 0 } m && CurrentPlan is { } p ? p.Steps[m.HottestIndex] : null;
        return IsTable(hot) ? hot : CurrentPlan?.Steps.FirstOrDefault(IsTable);
    }

    /// <summary>A-Rows 를 받으며 다시 실행한다(띠 단추·메뉴 「실제 행 수 받으며 다시 실행」). 끝나면 새 커서의 실제 계획을 보인다.</summary>
    public async Task RerunAsync(PlanRerunMode mode, CancellationToken ct = default)
    {
        if (IsRerunning)
        {
            return;
        }

        var stmt = LastStatement ?? CurrentStatement?.Invoke();
        if (stmt is null)
        {
            StatusText = "다시 실행할 문장이 없습니다. 편집기에서 문장을 실행한 뒤 다시 누르세요.";
            return;
        }

        if (Rerunner is not { } rerun)
        {
            StatusText = "다시 실행할 수 없습니다(접속·실행 엔진이 없습니다).";
            return;
        }

        _autoRerunTried = true;
        IsRerunning = true;
        StatusText = mode == PlanRerunMode.Hint ? "사본 실행 중… (/*+ GATHER_PLAN_STATISTICS */, 모든 행을 끝까지 읽습니다)" : "STATISTICS_LEVEL = ALL 로 다시 실행 중… (모든 행을 끝까지 읽습니다)";
        try
        {
            var outcome = await rerun(mode, stmt, ct).ConfigureAwait(true);
            await ShowCursorAsync(outcome.Session, outcome.Cursor, ct).ConfigureAwait(true);
            // 계획 반영(Apply)은 UI 스레드로 넘겨지므로 같은 길로 뒤에 붙인다.
            PostToUi(() =>
            {
                if (CurrentPlan is { } plan && plan.Cursor?.SqlId == outcome.Cursor.SqlId)
                {
                    StatusText = (NeedsStatisticsBand(plan) ? "다시 실행했지만 A-Rows 가 아직 없습니다. " : "실제 행 수를 받았습니다 · ")
                        + StatusText + $" · {PlanRowItem.Fmt(outcome.Rows)}행 읽음"
                        + (mode == PlanRerunMode.StatisticsLevel ? " · 이 세션의 STATISTICS_LEVEL 은 ALL 로 남습니다" : string.Empty);
                }
            });
        }
        catch (OperationCanceledException)
        {
            StatusText = "다시 실행 취소됨";
        }
        catch (Exception ex)
        {
            StatusText = "다시 실행 실패: " + (ex.Message ?? string.Empty).Split('\n', 2)[0].Trim();
        }
        finally
        {
            IsRerunning = false;
        }
    }

    /// <summary>그래프 노드를 눌렀다: 그 단계를 고르고(트리와 같이), 접힌 노드면 펼친다.</summary>
    public void SelectGraphStep(int stepId)
    {
        if (_collapsed.Remove(stepId))
        {
            RebuildGraph();
        }

        RevealRow(stepId);
        SelectedRow = Rows.FirstOrDefault(r => r.Id == stepId) ?? SelectedRow;
        GraphSelectedId = stepId;
    }

    /// <summary>모든 접힌 하위 트리를 편다.</summary>
    public void ExpandAllGraph()
    {
        if (_collapsed.Count == 0)
        {
            return;
        }

        _collapsed.Clear();
        RebuildGraph();
    }

    /// <summary>보고서를 (다시) 읽는다. 팩 선언이 없으면 보내지 않는다.</summary>
    public async Task LoadReportAsync(CancellationToken ct = default)
    {
        if (CurrentPlan?.Cursor is not { } cursor || LastSession is not { } session)
        {
            SetReport(PlanReportState.NoCursor, "실제 계획(Ctrl+Shift+E)을 연 뒤 보고서를 볼 수 있습니다. 예상 계획에는 모니터 기록이 없습니다.");
            return;
        }

        _reportCursor = cursor;
        if (!HasTuningPack(session))
        {
            SetReport(PlanReportState.NoLicense, "이 접속 프로필에 Tuning Pack 보유가 선언되어 있지 않아 DBMS_SQLTUNE.REPORT_SQL_MONITOR 를 보내지 않았습니다.\n"
                + "조직이 이 팩을 갖고 있다면 접속 관리자 › 이 접속 › 라이선스 에서 「Diagnostics + Tuning」 을 고르세요.");
            return;
        }

        SetReport(PlanReportState.Loading, "SQL 모니터 보고서를 읽는 중…");
        try
        {
            var plans = ResolvePlans();
            var text = await Task.Run(() => plans.GetSqlMonitorReportAsync(session, cursor, html: false, ct), CancellationToken.None).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(text))
            {
                SetReport(PlanReportState.Empty, NoMonitorRecordText);
                return;
            }

            ReportText = text;
            SetReport(PlanReportState.Ready, string.Empty);
            ReportStatusText = $"DBMS_SQLTUNE.REPORT_SQL_MONITOR(type => 'TEXT') · sql_id {cursor.SqlId} · HTML 은 외부 브라우저";
        }
        catch (UnauthorizedAccessException ex)
        {
            SetReport(PlanReportState.NoLicense, ex.Message);
        }
        catch (OperationCanceledException)
        {
            SetReport(PlanReportState.Error, "보고서 읽기를 취소했습니다.");
        }
        catch (Exception ex)
        {
            SetReport(PlanReportState.Error, "보고서를 읽지 못했습니다: " + (ex.Message ?? string.Empty).Split('\n', 2)[0].Trim());
        }
    }

    /// <summary>HTML 보고서를 임시 파일로 만들어 기본 브라우저로 연다. 연 파일 경로(실패면 null).</summary>
    public async Task<string?> OpenReportInBrowserAsync(CancellationToken ct = default)
    {
        if (CurrentPlan?.Cursor is not { } cursor || LastSession is not { } session || !HasTuningPack(session))
        {
            ReportStatusText = "HTML 보고서는 실제 계획과 Tuning Pack 보유 선언이 있어야 합니다.";
            return null;
        }

        try
        {
            var plans = ResolvePlans();
            var html = await Task.Run(() => plans.GetSqlMonitorReportAsync(session, cursor, html: true, ct), CancellationToken.None).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(html))
            {
                ReportStatusText = NoMonitorRecordText;
                return null;
            }

            var path = Path.Combine(Path.GetTempPath(), $"oranda-sqlmonitor-{cursor.SqlId}.html");
            await File.WriteAllTextAsync(path, html, ct).ConfigureAwait(true);
            ReportStatusText = OpenFile(path) ? "HTML 보고서를 브라우저로 열었습니다: " + path : "브라우저를 열지 못했습니다: " + path;
            return path;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ReportStatusText = "HTML 보고서를 열지 못했습니다: " + (ex.Message ?? string.Empty).Split('\n', 2)[0].Trim();
            return null;
        }
    }

    partial void OnSelectedRowChanged(PlanRowItem? value) => GraphSelectedId = value?.Id;

    partial void OnGraphColorBasisChanged(PlanGraphColorBasis value)
    {
        OnPropertyChanged(nameof(GraphColorIndex));
        RebuildGraph();
    }

    partial void OnAlwaysHintChanged(bool value) => SaveAlwaysHint?.Invoke(value);

    partial void OnSelectedTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsTreeToolbar));
        OnPropertyChanged(nameof(IsTreeTab));
        OnPropertyChanged(nameof(IsGraphToolbar));
        OnPropertyChanged(nameof(IsReportToolbar));
        OnPropertyChanged(nameof(IsBandVisible));
        OnPropertyChanged(nameof(ShowTuningButtons));
        if (value == ReportTabIndex && (ReportState is PlanReportState.NoCursor || !Equals(_reportCursor, CurrentPlan?.Cursor)))
        {
            _ = LoadReportAsync();
        }
    }

    /// <summary><see cref="Apply"/> 끝에서 부른다: 띠·그래프·보고서 상태를 새 계획에 맞춘다.</summary>
    private void AfterApply(CorePlan plan)
    {
        IsEstimated = plan.Cursor is null;
        ShowStatisticsBand = NeedsStatisticsBand(plan);
        _collapsed = PlanGraphLayout.InitialCollapsed(plan.Steps);
        if (!HasTimeBasis && GraphColorBasis == PlanGraphColorBasis.Time)
        {
            GraphColorBasis = PlanGraphColorBasis.Cost; // RebuildGraph 를 부른다
        }
        else
        {
            RebuildGraph();
        }

        OnPropertyChanged(nameof(HasTimeBasis));
        GraphSelectedId = SelectedRow?.Id;
        _reportCursor = null;
        ReportText = string.Empty;
        SetReport(PlanReportState.NoCursor, plan.Cursor is null
            ? "예상 계획에는 모니터 기록이 없습니다. 실제 계획(Ctrl+Shift+E)을 여세요."
            : "보고서 탭을 열면 읽습니다.");
        if (SelectedTabIndex == ReportTabIndex && plan.Cursor is not null)
        {
            _ = LoadReportAsync();
        }

        if (IsEstimated)
        {
            StatusText += " · " + ExplainNotice;
        }
        else if (ShowStatisticsBand)
        {
            StatusText += " · A-Rows 없음(statistics_level = TYPICAL)";
            if (AlwaysHint && !_autoRerunTried && !IsRerunning)
            {
                _ = RerunAsync(PlanRerunMode.Hint);
            }
        }
    }

    private void RebuildGraph()
    {
        if (CurrentPlan is not { } plan || Metrics is not { } metrics)
        {
            Graph = PlanGraphLayout.Empty;
            GraphCollapsedCount = 0;
        }
        else
        {
            Graph = PlanGraphLayout.Compute(plan.Steps, metrics, GraphColorBasis, _collapsed);
            GraphCollapsedCount = Graph.Nodes.Sum(n => n.HiddenCount);
        }

        OnPropertyChanged(nameof(HasGraph));
        OnPropertyChanged(nameof(GraphColorLegend));
        OnPropertyChanged(nameof(GraphCollapsedText));
    }

    private void SetReport(PlanReportState state, string message)
    {
        ReportState = state;
        ReportMessage = message;
    }

    private bool Has(string id)
    {
        if (HasCommand is { } has)
        {
            return has(id);
        }

        return _services?.GetService(typeof(Core.Shell.ICommandRegistry)) is Core.Shell.ICommandRegistry registry && registry.Find(id) is not null;
    }

    private IExecutionPlanService ResolvePlans() =>
        _plans ??= _services?.GetService(typeof(IExecutionPlanService)) as IExecutionPlanService
            ?? throw new InvalidOperationException("실행 계획 서비스가 없습니다.");
}
