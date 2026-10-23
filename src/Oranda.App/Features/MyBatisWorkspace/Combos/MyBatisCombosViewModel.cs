using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Shell;
using Oranda.Core.MyBatis;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;

namespace Oranda.App.Features.MyBatisWorkspace.Combos;

/// <summary>
/// 「조합 검사」 도구 창(MB-07, TSK-03-04, 시안 Combos 아트보드). 머리 줄(문장 · 조건 n개 → 조합 m개, 통과·오류 수, 조합 방식, 검사 방식, 다시 검사·취소),
/// 왼쪽 결과 표(#·조건별 켬/끔·결과·펼친 SQL), 오른쪽 고른 줄의 펼친 SQL(오류 자리 강조)·오류 설명·「시나리오로 저장」·「편집기에서 보기」.
/// <para>
/// 검사는 <see cref="ComboCheckRunner"/> 로 스레드 풀에서 돌고 줄이 하나씩 붙는다. 새 검사를 시작하면 돌던 검사는 취소된다.
/// 계산기·언어 서비스(Oranda.Language)는 처음 검사할 때 <see cref="ComboServices"/> 가 꺼낸다(시작 시험 제약, design.md 9장).
/// </para>
/// </summary>
[OrandaToolPanel("oranda.panel.mybatisCombos", ToolPanelKind.Tool, DockLocation.Bottom, Order = 7)]
public sealed partial class MyBatisCombosViewModel : ToolPanelViewModelBase
{
    /// <summary>「조합 방식」 선택지. 0 = 전부(조건 8개 이하), 1 = 줄이기.</summary>
    public static IReadOnlyList<string> StrategyChoices => [ComboText.StrategyAll, ComboText.StrategyReduce];

    /// <summary>「검사」 선택지. 0 = 구문 분석, 1 = 구문 분석 + 실행 계획.</summary>
    public static IReadOnlyList<string> ModeChoices => [ComboText.ModeSyntax, ComboText.ModePlan];

    private readonly IServiceProvider? _services;
    private CancellationTokenSource? _run;
    private IMyBatisWorkspaceSession? _session;
    private string? _statementId;
    private ComboCheckReport? _report;
    private IReadOnlyList<ComboCondition> _conditions = [];

    [ObservableProperty]
    private string _title = ComboText.Hint;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PassText))]
    private int _passCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ErrorText))]
    private int _errorCount;

    [ObservableProperty]
    private bool _hasReport;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private int _progressDone;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private int _progressTotal;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(RecheckCommand))]
    private bool _isRunning;

    [ObservableProperty]
    private int _strategyIndex;

    [ObservableProperty]
    private int _modeIndex;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveScenarioCommand))]
    [NotifyCanExecuteChangedFor(nameof(ShowInEditorCommand))]
    private ComboRowItem? _selectedRow;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _footnote = ComboText.Footnote;

    /// <summary>시험·디자이너용: 서비스 없이 만든다.</summary>
    public MyBatisCombosViewModel()
    {
        Current = this;
    }

    /// <summary>셸(DI)이 쓰는 생성자.</summary>
    public MyBatisCombosViewModel(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        Current = this;
    }

    /// <summary>가장 최근에 만든 조합 검사 창(명령 처리기가 검사를 맡기는 곳).</summary>
    public static MyBatisCombosViewModel? Current { get; private set; }

    /// <summary>머리 줄 「통과 n」 칩.</summary>
    public string PassText => ComboText.Passed(PassCount);

    /// <summary>머리 줄 「오류 n」 칩.</summary>
    public string ErrorText => ComboText.Errors(ErrorCount);

    /// <summary>검사 중 머리 줄 진행 글 「검사 중 n/m」(진행 막대 옆, 검사가 끝나면 숨는다).</summary>
    public string ProgressText => ComboText.Progress(ProgressDone, ProgressTotal);

    /// <summary>조건 열 머리(test 식).</summary>
    public ObservableCollection<string> ConditionLabels { get; } = [];

    public ObservableCollection<ComboRowItem> Rows { get; } = [];

    /// <summary>UI 스레드로 넘기는 방법. 시험은 바로 실행하도록 바꾼다.</summary>
    public Action<Action> PostToUi { get; set; } = a =>
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            a();
        }
        else
        {
            Dispatcher.UIThread.Post(a);
        }
    };

    /// <summary>
    /// 검사 결과 줄을 UI 스레드로 모아 넘기는 간격. 줄마다 넘기면(줄당 약 2 ms) 그리기가 몰려 창이 깜박였다(2026-10-24 사용자 신고).
    /// 0 이면 줄마다 넘긴다(시험용).
    /// </summary>
    public TimeSpan ProgressInterval { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>실행 계획에 쓸 접속을 고르는 방법(기본 = 활성 탭의 세션). 시험이 바꾼다.</summary>
    public Func<ISession?> SessionSource { get; set; } = () => null;

    /// <summary>마지막 검사 결과(없으면 null).</summary>
    public ComboCheckReport? Report => _report;

    /// <summary>
    /// <paramref name="session"/> 의 문장 <paramref name="statementId"/> 를 검사한다. 돌던 검사는 취소한다.
    /// 문장이 없거나 계산기를 찾을 수 없으면 상태 글만 바꾼다.
    /// </summary>
    public async Task CheckAsync(IMyBatisWorkspaceSession session, string statementId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(statementId);
        Cancel();
        _session = session;
        _statementId = statementId;
        RecheckCommand.NotifyCanExecuteChanged();

        var runner = ComboServices.Runner(_services);
        var element = session.ElementOf(statementId);
        if (runner is null || element is null)
        {
            StatusText = runner is null ? ComboText.NoEvaluator : ComboText.StatementMissing(statementId);
            return;
        }

        using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _run = run;
        UiProgress? progress = null;
        RefreshFixes(element.Text);
        Rows.Clear();
        ConditionLabels.Clear();
        SelectedRow = null;
        PassCount = 0;
        ErrorCount = 0;
        ProgressDone = 0;
        ProgressTotal = 0;
        HasReport = false;
        IsRunning = true;
        StatusText = ComboText.Checking;
        try
        {
            var scenarios = await ScenariosAsync(session, statementId, run.Token).ConfigureAwait(true);
            var mode = ModeIndex == 1 ? ComboCheckMode.SyntaxAndPlan : ComboCheckMode.Syntax;
            var request = new ComboCheckRequest(statementId, element, session.Fragments, scenarios, mode,
                mode == ComboCheckMode.SyntaxAndPlan ? SessionSource() : null, StrategyIndex == 1 ? 0 : 8);
            (_conditions, int total) = runner.Plan(request);
            ProgressTotal = total;
            foreach (var c in _conditions)
            {
                ConditionLabels.Add(c.Label);
            }

            Title = Heading(statementId, _conditions.Count, null);
            progress = new UiProgress(this, run, ProgressInterval);
            var report = await runner.RunAsync(request, progress, run.Token).ConfigureAwait(true);
            progress.Flush(); // 끝·취소 때 아직 모아 둔 줄을 붙인다
            if (ReferenceEquals(_run, run))
            {
                ApplyReport(session, element.Text, report); // 새 검사가 이 검사를 밀어냈으면 결과를 버린다
            }
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested)
        {
            // 시나리오 목록을 읽는 중에 취소됐다. 새 검사가 이어 받는다.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ReferenceEquals(_run, run))
            {
                StatusText = ComboText.CheckFailed(ex.Message);
            }
        }
        finally
        {
            progress?.Flush();
            if (ReferenceEquals(_run, run))
            {
                _run = null;
                IsRunning = false;
            }
        }
    }

    /// <summary>돌던 검사를 취소한다(이미 붙은 줄은 남는다).</summary>
    [RelayCommand(CanExecute = nameof(IsRunning))]
    public void Cancel()
    {
        try
        {
            _run?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 방금 끝난 검사.
        }
    }

    /// <summary>마지막 문장을 지금 고른 방식으로 다시 검사한다.</summary>
    [RelayCommand(CanExecute = nameof(CanRecheck))]
    public Task RecheckAsync() => _session is { } s && _statementId is { } id ? CheckAsync(s, id) : Task.CompletedTask;

    private bool CanRecheck() => !IsRunning && _session is not null && _statementId is not null;

    /// <summary>고른 조합을 그 문장의 새 시나리오로 저장한다(이름 「조합 n」).</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedCombination))]
    public async Task SaveScenarioAsync()
    {
        if (SelectedRow is not { Result.IsScenario: false } row || _session is not { } session || _statementId is not { } id
            || session.ElementOf(id) is not { } element || ComboServices.Runner(_services) is not { } runner || ComboServices.Scenarios(_services) is not { } store)
        {
            StatusText = ComboText.CannotSaveScenario;
            return;
        }

        var values = runner.ScenarioValues(element, session.Fragments, _conditions, row.Result.States, out bool matches);
        MyBatisScenario saved;
        try
        {
            saved = await store.SaveAsync(new MyBatisScenario
            {
                MapperPath = session.MapperPath,
                Namespace = session.MapperNamespace,
                StatementId = id,
                Name = row.Result.Title,
                Values = values,
            }, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusText = ComboText.SaveFailed(ex.Message);
            return;
        }

        StatusText = matches
            ? ComboText.ScenarioSaved(saved.Name)
            : ComboText.ScenarioSavedAdjust(saved.Name);
    }

    private bool HasSelectedCombination() => SelectedRow is { Result.IsScenario: false };

    /// <summary>고른 줄의 원인 줄로 문장 탭을 연다.</summary>
    [RelayCommand(CanExecute = nameof(CanShowInEditor))]
    public void ShowInEditor()
    {
        if (_session is not { } session || _statementId is not { } id)
        {
            return;
        }

        var tab = session.OpenStatement(id);
        tab?.GoToLine(SelectedRow?.Result.SourceLine ?? 1);
    }

    private bool CanShowInEditor() => SelectedRow is not null && _session is not null;

    private void Add(ComboCheckResult result)
    {
        var row = new ComboRowItem(result, _conditions);
        Rows.Add(row);
        ProgressDone++;
        if (row.IsError)
        {
            ErrorCount++;
            SelectedRow ??= row;
        }
        else
        {
            PassCount++;
        }
    }

    private void ApplyReport(IMyBatisWorkspaceSession session, string checkedText, ComboCheckReport report)
    {
        _report = report;
        HasReport = true;

        // 진행 중 붙인 줄과 결과가 어긋나면(UI 로 넘기던 중 취소 등) 결과로 다시 채운다.
        if (Rows.Count != report.Results.Count)
        {
            Rows.Clear();
            PassCount = 0;
            ErrorCount = 0;
            ProgressDone = 0;
            foreach (var result in report.Results)
            {
                Add(result);
            }
        }

        Title = Heading(report.StatementId, report.Conditions.Count, report.CombinationCount);
        SelectedRow ??= Rows.FirstOrDefault();
        var tail = report.Notice is { } notice ? " " + notice : string.Empty;
        StatusText = report.Cancelled
            ? ComboText.Cancelled(report.CombinationCount, report.Results.Count)
            : ComboText.Finished(report.PassCount, report.ErrorCount, tail);
        if (!report.Cancelled)
        {
            ComboCheckResults.Set(session, report.StatementId, checkedText, report.ErrorCount, report.CombinationCount,
                [.. report.Results.Where(r => r.Outcome == ComboOutcome.Error && r.SourceLine is not null).Select(r => r.SourceLine!.Value).Distinct().Order()]);
        }
    }

    private async Task<IReadOnlyList<ComboScenarioInput>> ScenariosAsync(IMyBatisWorkspaceSession session, string statementId, CancellationToken ct)
    {
        if (ComboServices.Scenarios(_services) is not { } store)
        {
            return [];
        }

        var list = await store.ListAsync(session.MapperPath, session.MapperNamespace, statementId, ct).ConfigureAwait(true);
        return [.. list.Select(s => new ComboScenarioInput(s.Name, ComboCheckRunner.ToValues(s)))];
    }

    private static string Heading(string statementId, int conditions, int? combinations) =>
        combinations is { } n
            ? ComboText.Heading(statementId, conditions, n)
            : ComboText.HeadingPending(statementId, conditions);

    // 실행기의 결과를 모아 간격마다 한 번 UI 스레드에서 붙인다(순서 유지). 이 검사가 이미 바뀌었으면(새 검사) 버린다.
    // 붙이기는 잠금 안에서 해 늦게 온 예약 붙이기와 끝의 Flush 가 겹쳐도 줄 순서가 섞이지 않는다.
    private sealed class UiProgress(MyBatisCombosViewModel owner, CancellationTokenSource run, TimeSpan interval) : IProgress<ComboCheckResult>
    {
        private readonly Lock _gate = new();
        private List<ComboCheckResult> _pending = [];
        private bool _scheduled;

        public void Report(ComboCheckResult value)
        {
            lock (_gate)
            {
                _pending.Add(value);
                if (_scheduled)
                {
                    return;
                }

                _scheduled = true;
            }

            if (interval <= TimeSpan.Zero)
            {
                owner.PostToUi(Flush);
            }
            else
            {
                _ = Task.Delay(interval).ContinueWith(_ => owner.PostToUi(Flush), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            }
        }

        /// <summary>모아 둔 줄을 붙인다.</summary>
        public void Flush()
        {
            lock (_gate)
            {
                var batch = _pending;
                _pending = [];
                _scheduled = false;
                if (!ReferenceEquals(owner._run, run))
                {
                    return;
                }

                foreach (var result in batch)
                {
                    owner.Add(result);
                }
            }
        }
    }
}
