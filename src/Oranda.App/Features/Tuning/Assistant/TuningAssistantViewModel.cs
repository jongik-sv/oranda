using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.Localization;
using Oranda.App.Features.MyBatisWorkspace.Save;
using Oranda.App.Shell;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Plans;
using CorePlan = Oranda.Core.Plans.ExecutionPlan;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;
using Oranda.Core.Tuning;
using Oranda.Language.Tuning;

namespace Oranda.App.Features.Tuning.Assistant;

/// <summary>「원문과 차이」 한 줄.</summary>
public sealed record RewriteDiffRow(string Mark, string Text, bool IsAdded, bool IsRemoved);

/// <summary>도우미 입력: 편집기 문장과(있으면) 그 계획.</summary>
/// <param name="Statement">편집기 현재 문장. 없으면 null(「문장 없음」 화면).</param>
/// <param name="Session">활성 탭의 세션. 없으면 null(「접속 없음」).</param>
public sealed record TuningAssistantInput(SqlStatement? Statement, ISession? Session)
{
    /// <summary>편집기 탭 제목(예: query3.sql).</summary>
    public string? SourceTitle { get; init; }

    /// <summary>문장 첫 줄(1 부터). 모르면 null.</summary>
    public int? FirstLine { get; init; }

    public CorePlan? Plan { get; init; }

    /// <summary>계획이 실제 계획(DISPLAY_CURSOR)인가. 거짓이면 예상 계획.</summary>
    public bool PlanIsActual { get; init; }

    public DateTime? PlanTime { get; init; }
}

/// <summary>
/// <c>oranda.panel.tuningAssistant</c> — 튜닝 도우미(TSK-06-05, TN-09·10·11). 편집기 문장과 그 계획으로 인덱스 후보·재작성 후보·힌트 대안을
/// 규칙(<see cref="IndexAdvisor"/>·<see cref="RewriteAdvisor"/>·<see cref="HintAdvisor"/>)으로 만들고, 원문 + 체크한 변형을
/// <see cref="ITuningService.RunBenchmarkAsync"/> 로 N 회씩 재어 표로 보인다. 만든 DDL·SQL 은 실행하지 않는다(새 편집기 탭에 넣기·복사만).
/// 벤치마크는 SELECT 만(<see cref="BenchmarkRules.RejectReason"/>), 운영 접속이면 실행 전에 묻는다.
/// </summary>
[OrandaToolPanel("oranda.panel.tuningAssistant", ToolPanelKind.Document, DockLocation.Document)]
public sealed partial class TuningAssistantViewModel : ToolPanelViewModelBase
{
    private readonly IServiceProvider? _services;
    private ITuningService? _tuning;
    private CancellationTokenSource? _benchmarkCts;
    private TuningAssistantInput? _input;
    private IReadOnlyList<IndexAdvice> _indexAdvice = [];

    [ObservableProperty]
    private string _sourceText = string.Empty;

    [ObservableProperty]
    private bool _hasStatement;

    [ObservableProperty]
    private bool _hasSession;

    [ObservableProperty]
    private bool _hasPlan;

    [ObservableProperty]
    private bool _isAnalyzing;

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    private int _candidateTab;

    [ObservableProperty]
    private IndexCandidateItem? _selectedIndex;

    [ObservableProperty]
    private RewriteCandidateItem? _selectedRewrite;

    [ObservableProperty]
    private HintCandidateItem? _selectedHint;

    [ObservableProperty]
    private bool _showRewriteDiff;

    [ObservableProperty]
    private bool _allowParallelHints;

    [ObservableProperty]
    private int _iterations = BenchmarkRules.DefaultIterations;

    [ObservableProperty]
    private bool _isBenchmarking;

    [ObservableProperty]
    private string _benchmarkProgress = string.Empty;

    [ObservableProperty]
    private string _benchmarkFinishedText = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>시험·디자이너용.</summary>
    public TuningAssistantViewModel()
    {
        StatusText = L("편집기에서 문장에 커서를 두고 「튜닝 도우미」 를 여세요.", "Put the caret in a statement and open the tuning assistant.");
    }

    /// <summary>셸(DI)이 쓰는 생성자. 서비스는 처음 쓸 때 꺼낸다(D-006).</summary>
    public TuningAssistantViewModel(IServiceProvider services)
        : this()
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        Keys = services.GetService(typeof(ContextKeyService)) as ContextKeyService;
    }

    /// <summary>시험용: 튜닝 서비스를 직접 준다.</summary>
    public TuningAssistantViewModel(ITuningService? tuning)
        : this()
    {
        _tuning = tuning;
    }

    public ObservableCollection<IndexCandidateItem> IndexItems { get; } = [];

    public ObservableCollection<RewriteCandidateItem> RewriteItems { get; } = [];

    public ObservableCollection<HintCandidateItem> HintItems { get; } = [];

    public ObservableCollection<BenchmarkRowItem> BenchmarkRows { get; } = [];

    /// <summary>재작성 후보와 원문의 줄 차이(「원문과 차이」).</summary>
    public ObservableCollection<RewriteDiffRow> RewriteDiff { get; } = [];

    /// <summary>메뉴 활성 키 <c>tuningAssistantOpen</c> 을 쓰는 곳.</summary>
    public ContextKeyService? Keys { get; set; }

    /// <summary>실제 계획을 받는 방법(마지막 실행의 커서). 없으면 단추가 꺼진다. null 결과 = 받을 커서 없음.</summary>
    public Func<CancellationToken, Task<CorePlan?>>? FetchActualPlan { get; set; }

    /// <summary>예상 계획(EXPLAIN PLAN)을 받는 방법.</summary>
    public Func<CancellationToken, Task<CorePlan?>>? FetchEstimatedPlan { get; set; }

    /// <summary>운영 접속 벤치마크 확인 창. 없으면 운영 접속에서 벤치마크하지 않는다.</summary>
    public Func<string, CancellationToken, Task<bool>>? Confirm { get; set; }

    /// <summary>글을 새 SQL 편집기 탭에 넣는 방법(실행하지 않는다).</summary>
    public Action<string>? OpenInNewEditor { get; set; }

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

    public TuningAssistantInput? Input => _input;

    public string? StatementText => _input?.Statement?.Text;

    // ---- 화면 상태 ----

    public int IndexCount => IndexItems.Count;

    public int RewriteCount => RewriteItems.Count;

    public int HintCount => HintItems.Count;

    /// <summary>접속·계획이 없을 때 위쪽 띠 글. 없으면 빈 글.</summary>
    public string NoticeText =>
        HasStatement && !HasSession
            ? L("접속이 없습니다. 재작성 후보만 보입니다 — 인덱스 후보·힌트·벤치마크는 접속한 뒤 도우미를 다시 여세요.",
                "No connection. Only rewrite candidates are shown — connect and open the assistant again for index candidates, hints and the benchmark.")
            : string.Empty;

    public bool HasNotice => NoticeText.Length > 0;

    /// <summary>분석·계획 받기 오류 띠.</summary>
    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    partial void OnErrorTextChanged(string? value) => OnPropertyChanged(nameof(HasError));

    public bool ShowNoStatement => !HasStatement && !IsAnalyzing;

    public bool ShowCandidates => HasStatement;

    public bool CanFetchActualPlan => HasStatement && HasSession && FetchActualPlan is not null && !IsAnalyzing;

    public bool CanFetchEstimatedPlan => HasStatement && HasSession && FetchEstimatedPlan is not null && !IsAnalyzing;

    public bool ShowPlanButtons => HasStatement && HasSession && !HasPlan;

    public string IndexEmptyText =>
        !HasPlan ? L("실제 계획이 있어야 후보를 고릅니다", "Candidates need the actual plan")
        : L("규칙에 걸린 곳이 없습니다 — 전체 읽기 단계가 없거나 조건 열이 없습니다", "No rule matched — no full scan step or no condition column");

    public static string RewriteEmptyText => L("규칙에 걸린 곳이 없습니다", "No rule matched");

    public string HintEmptyText =>
        !HasPlan ? L("계획이 있어야 표 이름·별칭으로 힌트를 만듭니다", "Hints are built from the plan's tables and aliases")
        : L("힌트 대안이 없습니다 — 계획에 표 읽기 단계가 없습니다", "No hint alternatives — the plan reads no table");

    public bool IndexEmpty => IndexItems.Count == 0;

    public bool RewriteEmpty => RewriteItems.Count == 0;

    public bool HintEmpty => HintItems.Count == 0;

    /// <summary>바꾼 SQL 없이 경고만인 규칙이 있다(목록 위 안내 한 줄).</summary>
    public bool HasWarningOnlyRewrite => RewriteItems.Any(r => r.IsWarningOnly);

    /// <summary>쓸 수 없는 힌트가 있다(목록 위 안내 한 줄).</summary>
    public bool HasDisabledHint => HintItems.Any(h => h.IsDisabled);

    public static string RewriteCheckNote => L(
        "체크할 수 있는 것은 바꾼 SQL 이 있는 규칙뿐입니다 — 「경고만」 규칙은 오른쪽 설명을 보고 직접 고치세요",
        "Only rules with rewritten SQL can be checked — for \"Warn\" rules, read the note on the right and fix the SQL yourself");

    public static string HintCheckNote => L(
        "체크할 수 있는 것은 적용 가능한 힌트뿐입니다 — 「—」 줄은 「뜻」 칸에 이유가 있습니다",
        "Only applicable hints can be checked — rows marked \"—\" give the reason in the Meaning column");

    // ---- 인덱스 상세 ----

    public string IndexReason => SelectedIndex?.Advice.Reason ?? string.Empty;

    public string IndexDdl => SelectedIndex?.Advice.Candidate.Ddl ?? string.Empty;

    public bool CanInsertIndexDdl => SelectedIndex is { Advice.CanCreate: true } && OpenInNewEditor is not null;

    // ---- 재작성 상세 ----

    public string RewriteTitle => SelectedRewrite?.Title ?? string.Empty;

    public string RewriteDetail => SelectedRewrite?.Advice.Candidate.Description ?? string.Empty;

    public string? RewriteCaution => SelectedRewrite?.Advice.Caution;

    public bool HasRewriteCaution => !string.IsNullOrEmpty(RewriteCaution) && SelectedRewrite?.Advice.HasRewrite == true;

    public string RewriteSql => SelectedRewrite?.Advice.Candidate.Sql ?? string.Empty;

    public bool HasRewriteSql => SelectedRewrite?.Advice.HasRewrite == true;

    public bool ShowRewriteCode => HasRewriteSql && !ShowRewriteDiff;

    public bool ShowRewriteDiffList => HasRewriteSql && ShowRewriteDiff;

    public bool CanInsertRewrite => HasRewriteSql && OpenInNewEditor is not null;

    // ---- 벤치마크 ----

    /// <summary>벤치마크를 못 하는 이유(단추 툴팁). 할 수 있으면 null.</summary>
    public string? BenchmarkBlockedReason
    {
        get
        {
            if (!HasStatement)
            {
                return L("문장이 없습니다", "No statement");
            }

            if (!HasSession)
            {
                return L("접속이 없습니다", "No connection");
            }

            if (ResolveTuning() is null)
            {
                return L("튜닝 조회 서비스를 쓸 수 없습니다", "The tuning service is not available");
            }

            return BenchmarkRules.RejectReason(StatementText, LiteralTranslator.IsEnglish);
        }
    }

    public bool CanRunBenchmark => !IsBenchmarking && !IsAnalyzing && BenchmarkBlockedReason is null;

    public string BenchmarkTip => BenchmarkBlockedReason
        ?? L("원문과 체크한 변형을 SELECT 로만 실행해 잽니다(DB 에 쓰지 않습니다). 운영 접속이면 실행 전에 묻습니다.",
            "Runs the original and checked variants as SELECT only (nothing is written). Asks first on a production connection.");

    public string BenchmarkCaption =>
        F("{0} 회 평균 · SELECT 만 · v$mystat 전후 차이", "average of {0} runs · SELECT only · v$mystat before/after", Iterations);

    public bool BenchmarkEmpty => BenchmarkRows.Count == 0;

    public static string BenchmarkEmptyText => L("벤치마크할 변형을 위 목록에서 체크하고 「벤치마크 실행」 을 누르세요. 원문은 늘 첫 줄입니다.",
        "Check the variants to benchmark above and press Run benchmark. The original is always the first row.");

    // ---- 불러오기·분석 ----

    /// <summary>문장·계획을 받아 후보를 만든다(기존 인덱스는 서비스로 읽는다).</summary>
    public async Task LoadAsync(TuningAssistantInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        CancelBenchmark();
        _input = input;
        HasStatement = input.Statement is { } st && !string.IsNullOrWhiteSpace(st.Text);
        HasSession = input.Session is not null;
        HasPlan = input.Plan is { Steps.Count: > 0 };
        SourceText = BuildSourceText(input);
        BenchmarkRows.Clear();
        BenchmarkFinishedText = string.Empty;
        await AnalyzeAsync(ct).ConfigureAwait(true);
    }

    /// <summary>「다시 분석」: 같은 문장·계획으로 후보를 다시 만든다(기존 인덱스를 다시 읽는다).</summary>
    public Task ReanalyzeAsync(CancellationToken ct = default) => _input is null ? Task.CompletedTask : AnalyzeAsync(ct);

    /// <summary>「실제 계획 받기」.</summary>
    public Task FetchActualPlanAsync(CancellationToken ct = default) => FetchPlanAsync(FetchActualPlan, actual: true, ct);

    /// <summary>「예상 계획으로」.</summary>
    public Task FetchEstimatedPlanAsync(CancellationToken ct = default) => FetchPlanAsync(FetchEstimatedPlan, actual: false, ct);

    private async Task FetchPlanAsync(Func<CancellationToken, Task<CorePlan?>>? fetch, bool actual, CancellationToken ct)
    {
        if (fetch is null || _input is null)
        {
            return;
        }

        IsAnalyzing = true;
        Raise();
        try
        {
            var plan = await fetch(ct).ConfigureAwait(true);
            if (plan is null || plan.Steps.Count == 0)
            {
                StatusText = actual
                    ? L("받을 실제 계획이 없습니다. 이 문장을 한 번 실행한 뒤 다시 누르세요(마지막 실행의 sql_id 로 받습니다).",
                        "No actual plan to fetch. Run this statement once and try again (the last execution's sql_id is used).")
                    : L("예상 계획을 받지 못했습니다.", "Could not get the estimated plan.");
                return;
            }

            await LoadAsync(_input with { Plan = plan, PlanIsActual = actual, PlanTime = DateTime.Now }, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            StatusText = L("계획 받기를 취소했습니다.", "Plan fetch cancelled.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ErrorText = FirstLine(ex.Message);
            StatusText = L("계획을 받지 못했습니다: ", "Could not get the plan: ") + ErrorText;
        }
        finally
        {
            IsAnalyzing = false;
            Raise();
        }
    }

    private async Task AnalyzeAsync(CancellationToken ct)
    {
        var input = _input!;
        IsAnalyzing = true;
        ErrorText = null;
        Raise();
        try
        {
            IndexItems.Clear();
            RewriteItems.Clear();
            HintItems.Clear();
            _indexAdvice = [];
            if (!HasStatement)
            {
                StatusText = L("문장이 없습니다. 편집기에서 커서를 문장 안에 두고 다시 여세요.", "No statement. Put the caret inside a statement and open it again.");
                return;
            }

            var en = LiteralTranslator.IsEnglish;
            var sql = input.Statement!.Text;
            var plan = HasPlan ? input.Plan : null;
            if (plan is not null)
            {
                var existing = await ReadExistingIndexesAsync(plan, input.Session, ct).ConfigureAwait(true);
                _indexAdvice = IndexAdvisor.Suggest(plan, existing is null ? null : (o, t) => existing.GetValueOrDefault((o, t)), input.Session?.Profile.User?.ToUpperInvariant(), en);
                foreach (var a in _indexAdvice)
                {
                    IndexItems.Add(new IndexCandidateItem(a));
                }

                var hints = HintAdvisor.Suggest(plan, sql, _indexAdvice, new HintAdvisorOptions { AllowParallel = AllowParallelHints, English = en });
                foreach (var h in hints)
                {
                    HintItems.Add(new HintCandidateItem(h, check: h.Hint.StartsWith("INDEX(", StringComparison.Ordinal)));
                }
            }

            foreach (var r in RewriteAdvisor.Analyze(sql, plan, en))
            {
                RewriteItems.Add(new RewriteCandidateItem(r));
            }

            SelectedIndex = IndexItems.FirstOrDefault();
            SelectedRewrite = RewriteItems.FirstOrDefault();
            SelectedHint = HintItems.FirstOrDefault();
            if (CandidateTab == 0 && IndexItems.Count == 0 && RewriteItems.Count > 0)
            {
                CandidateTab = 1; // 계획이 없으면 볼 것이 있는 재작성 탭으로
            }

            StatusText = SummaryText();
        }
        catch (OperationCanceledException)
        {
            StatusText = L("분석을 취소했습니다.", "Analysis cancelled.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ErrorText = FirstLine(ex.Message);
            StatusText = L("분석하지 못했습니다: ", "Analysis failed: ") + ErrorText;
        }
        finally
        {
            IsAnalyzing = false;
            Raise();
        }
    }

    // 후보 표마다 기존 인덱스 열 구성을 읽는다. 서비스가 없거나 지원하지 않으면 null(겹침 「확인 못함」).
    private async Task<Dictionary<(string, string), IReadOnlyList<IndexColumnsRow>?>?> ReadExistingIndexesAsync(CorePlan plan, ISession? session, CancellationToken ct)
    {
        if (session is null || ResolveTuning() is not { } tuning)
        {
            return null;
        }

        var map = new Dictionary<(string, string), IReadOnlyList<IndexColumnsRow>?>();
        foreach (var t in IndexAdvisor.TablesToCheck(plan, session.Profile.User?.ToUpperInvariant()))
        {
            try
            {
                map[(t.Owner, t.Table)] = await tuning.GetIndexColumnsAsync(session, t.Owner, t.Table, ct).ConfigureAwait(true);
            }
            catch (NotSupportedException)
            {
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                map[(t.Owner, t.Table)] = null; // 이 표만 「확인 못함」
            }
        }

        return map;
    }

    /// <summary>병렬 힌트를 켜고 끄면 힌트만 다시 만든다(체크는 지킨다).</summary>
    partial void OnAllowParallelHintsChanged(bool value)
    {
        if (_input?.Plan is not { } plan || !HasPlan)
        {
            return;
        }

        var checkedHints = HintItems.Where(h => h.IsChecked).Select(h => h.Advice.Hint).ToHashSet(StringComparer.Ordinal);
        var hints = HintAdvisor.Suggest(plan, StatementText, _indexAdvice, new HintAdvisorOptions { AllowParallel = value, English = LiteralTranslator.IsEnglish });
        HintItems.Clear();
        foreach (var h in hints)
        {
            HintItems.Add(new HintCandidateItem(h, checkedHints.Contains(h.Hint)));
        }

        SelectedHint = HintItems.FirstOrDefault();
        Raise();
    }

    partial void OnSelectedIndexChanged(IndexCandidateItem? value)
    {
        OnPropertyChanged(nameof(IndexReason));
        OnPropertyChanged(nameof(IndexDdl));
        OnPropertyChanged(nameof(CanInsertIndexDdl));
    }

    partial void OnSelectedRewriteChanged(RewriteCandidateItem? value)
    {
        ShowRewriteDiff = false;
        RebuildDiff();
        foreach (var name in new[]
        {
            nameof(RewriteTitle), nameof(RewriteDetail), nameof(RewriteCaution), nameof(HasRewriteCaution), nameof(RewriteSql), nameof(HasRewriteSql),
            nameof(ShowRewriteCode), nameof(ShowRewriteDiffList), nameof(CanInsertRewrite),
        })
        {
            OnPropertyChanged(name);
        }
    }

    partial void OnShowRewriteDiffChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowRewriteCode));
        OnPropertyChanged(nameof(ShowRewriteDiffList));
    }

    partial void OnIterationsChanged(int value)
    {
        var clamped = Math.Clamp(value, BenchmarkRules.MinIterations, BenchmarkRules.MaxIterations);
        if (clamped != value)
        {
            Iterations = clamped;
            return;
        }

        OnPropertyChanged(nameof(BenchmarkCaption));
    }

    partial void OnIsBenchmarkingChanged(bool value) => OnPropertyChanged(nameof(CanRunBenchmark));

    private void RebuildDiff()
    {
        RewriteDiff.Clear();
        if (SelectedRewrite is { Advice.HasRewrite: true } r && StatementText is { } original)
        {
            foreach (var line in MapperLineDiff.Build(original.Replace("\r\n", "\n", StringComparison.Ordinal), r.Advice.Candidate.Sql.Replace("\r\n", "\n", StringComparison.Ordinal), 1, 1))
            {
                RewriteDiff.Add(new RewriteDiffRow(line.Kind switch
                {
                    MapperDiffKind.Added => "+",
                    MapperDiffKind.Removed => "−",
                    MapperDiffKind.Gap => "…",
                    _ => " ",
                }, line.Kind == MapperDiffKind.Gap ? string.Empty : line.Text, line.Kind == MapperDiffKind.Added, line.Kind == MapperDiffKind.Removed));
            }
        }
    }

    // ---- 문안 넣기 ----

    /// <summary>고른 인덱스 후보의 CREATE INDEX 문안을 새 편집기 탭에 넣는다(실행하지 않는다).</summary>
    public bool InsertIndexDdl()
    {
        if (!CanInsertIndexDdl)
        {
            return false;
        }

        OpenInNewEditor!(IndexDdl + "\n");
        StatusText = L("문안을 새 편집기 탭에 넣었습니다. 실행하면 DB 에 씁니다 — 검토한 뒤 직접 실행하세요.",
            "The DDL is in a new editor tab. Running it writes to the database — review it and run it yourself.");
        return true;
    }

    /// <summary>고른 재작성 후보를 새 편집기 탭에 넣는다(실행하지 않는다).</summary>
    public bool InsertRewrite()
    {
        if (!CanInsertRewrite)
        {
            return false;
        }

        var header = L($"-- 튜닝 도우미 재작성 후보: {RewriteTitle} · 원문과 결과가 같은지 확인하세요\n",
            $"-- Tuning assistant rewrite: {RewriteTitle} · check that the result matches the original\n");
        OpenInNewEditor!(header + RewriteSql + "\n");
        StatusText = L("재작성 후보를 새 편집기 탭에 넣었습니다.", "The rewrite is in a new editor tab.");
        return true;
    }

    // ---- 벤치마크 ----

    /// <summary>원문 + 체크한 재작성·힌트 변형(번호 순).</summary>
    public IReadOnlyList<BenchmarkRowItem> BuildCases()
    {
        var list = new List<BenchmarkRowItem>();
        if (StatementText is not { } original)
        {
            return list;
        }

        var baseSql = BenchmarkRules.Trim(original);
        list.Add(new BenchmarkRowItem(1, L("원문", "Original"), baseSql, BenchmarkSource.Original));
        foreach (var r in RewriteItems.Where(r => r.IsChecked && r.CanCheck))
        {
            var label = r.Advice.ShortLabel.Length > 0 ? r.Advice.ShortLabel : r.Title;
            list.Add(new BenchmarkRowItem(list.Count + 1, label, BenchmarkRules.Trim(r.Advice.Candidate.Sql), BenchmarkSource.Rewrite));
        }

        foreach (var h in HintItems.Where(h => h.IsChecked && h.CanCheck))
        {
            if (HintAdvisor.Apply(baseSql, h.Advice.Hint) is { } hinted)
            {
                list.Add(new BenchmarkRowItem(list.Count + 1, h.Advice.Hint, hinted, BenchmarkSource.Hint));
            }
        }

        return list;
    }

    /// <summary>
    /// 벤치마크: 원문 + 체크한 변형을 <see cref="Iterations"/> 번씩(한 번에 1 회씩 불러 최소·최대·진행을 안다). SELECT 가 아니면 거부, 운영 접속이면 먼저 묻는다.
    /// 결과 행 수가 원문과 다르면 그 변형이 끝나는 즉시 경고하고 「가장 나음」 에서 뺀다.
    /// </summary>
    public async Task RunBenchmarkAsync(CancellationToken ct = default)
    {
        if (IsBenchmarking)
        {
            return;
        }

        if (BenchmarkBlockedReason is { } blocked)
        {
            StatusText = L("벤치마크하지 않습니다: ", "Benchmark refused: ") + blocked;
            return;
        }

        var session = _input!.Session!;
        var tuning = ResolveTuning()!;
        var cases = BuildCases();
        if (session.Profile.Environment == ConnectionEnvironment.Production)
        {
            var question = F("운영 접속({0})에서 SELECT {1}개를 {2}회씩 실행합니다. DB 에 쓰지 않지만 서버 자원을 씁니다. 계속할까요?",
                    "This runs {1} SELECT statements {2} times each on the production connection {0}. Nothing is written, but it uses server resources. Continue?",
                session.Profile.Name, cases.Count, Iterations);
            if (Confirm is null || !await Confirm(question, ct).ConfigureAwait(true))
            {
                StatusText = L("운영 접속 벤치마크를 취소했습니다.", "Benchmark on production cancelled.");
                return;
            }
        }

        BenchmarkRows.Clear();
        foreach (var c in cases)
        {
            BenchmarkRows.Add(c);
        }

        var n = Iterations;
        _benchmarkCts?.Dispose();
        _benchmarkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _benchmarkCts.Token;
        IsBenchmarking = true;
        BenchmarkFinishedText = string.Empty;
        Raise();
        var clock = Stopwatch.StartNew();
        var stopped = false;
        try
        {
            for (var i = 0; i < cases.Count && !stopped; i++)
            {
                var row = cases[i];
                if (BenchmarkRules.RejectReason(row.Sql, LiteralTranslator.IsEnglish) is { } reason)
                {
                    row.State = BenchmarkRowState.Rejected;
                    row.VerdictText = reason;
                    continue;
                }

                row.State = BenchmarkRowState.Running;
                var physical = 0L;
                var logical = 0L;
                for (var it = 1; it <= n; it++)
                {
                    BenchmarkProgress = F("변형 {0}/{1} · 반복 {2}/{3}", "variant {0}/{1} · run {2}/{3}", i + 1, cases.Count, it, n);
                    row.VerdictText = F("반복 {0}/{1}", "run {0}/{1}", it, n);
                    try
                    {
                        var r = await tuning.RunBenchmarkAsync(session, new BenchmarkCase(row.Label, row.Sql), 1, token).ConfigureAwait(true);
                        row.ElapsedSamples.Add(r.Elapsed.TotalMilliseconds);
                        logical += r.LogicalReads;
                        physical += r.PhysicalReads;
                        row.Rows = r.Rows;
                        row.PlanHash = r.PlanHashValue ?? row.PlanHash;
                        row.LogicalReads = logical / it;
                        row.PhysicalReads = physical / it;
                        row.ShowMeasures();
                    }
                    catch (OperationCanceledException)
                    {
                        row.State = BenchmarkRowState.Cancelled;
                        row.VerdictText = L("취소됨", "Cancelled");
                        stopped = true;
                        break;
                    }
                    catch (NotSupportedException)
                    {
                        row.State = BenchmarkRowState.Failed;
                        row.VerdictText = L("이 접속에서는 벤치마크를 지원하지 않습니다", "Benchmark is not supported on this connection");
                        stopped = true;
                        break;
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        row.State = BenchmarkRowState.Failed;
                        row.VerdictText = L("실패: ", "Failed: ") + FirstLine(ex.Message);
                        break;
                    }
                }

                if (row.State == BenchmarkRowState.Running)
                {
                    row.Elapsed = TimeSpan.FromMilliseconds(row.ElapsedSamples.Average());
                    row.ShowMeasures();
                    row.State = BenchmarkRowState.Done;
                    row.VerdictText = row.IsBaseline ? L("기준", "Baseline") : string.Empty;
                    // 원문과 행 수가 다르면 끝나는 즉시 경고
                    if (!row.IsBaseline && cases[0].State == BenchmarkRowState.Done && row.Rows != cases[0].Rows)
                    {
                        row.IsRowsMismatch = true;
                        row.VerdictText = RowDeltaText(row.Rows - cases[0].Rows);
                    }

                    UpdateBars(cases);
                }
            }

            foreach (var rest in cases.Where(c => c.State == BenchmarkRowState.Pending))
            {
                rest.State = BenchmarkRowState.Cancelled;
                rest.VerdictText = L("취소됨", "Cancelled");
            }

            ApplyVerdicts(cases);
            clock.Stop();
            BenchmarkFinishedText = F("{0:HH:mm:ss} 끝 · {1:0.0} s", "finished {0:HH:mm:ss} · {1:0.0} s", DateTime.Now, clock.Elapsed.TotalSeconds);
            StatusText = BenchmarkSummary(cases, n, stopped);
        }
        finally
        {
            BenchmarkProgress = string.Empty;
            IsBenchmarking = false;
            Raise();
        }
    }

    /// <summary>실행 중인 벤치마크를 멈춘다(지금 실행은 서비스가 취소한다).</summary>
    public void CancelBenchmark()
    {
        if (_benchmarkCts is { IsCancellationRequested: false } cts)
        {
            cts.Cancel();
        }
    }

    private static void UpdateBars(IReadOnlyList<BenchmarkRowItem> rows)
    {
        var max = rows.Where(r => r.Elapsed is not null).Select(r => r.Elapsed!.Value.TotalMilliseconds).DefaultIfEmpty(0).Max();
        foreach (var r in rows)
        {
            r.BarWidth = max > 0 && r.Elapsed is { } e ? Math.Max(2, BenchmarkRowItem.BarMax * e.TotalMilliseconds / max) : 0;
        }
    }

    private static void ApplyVerdicts(IReadOnlyList<BenchmarkRowItem> rows)
    {
        var verdicts = BenchmarkRules.Judge(rows.Select(r => new BenchmarkMeasure(r.State == BenchmarkRowState.Done ? r.Elapsed : null, r.LogicalReads, r.Rows)).ToList());
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var v = verdicts[i];
            r.Verdict = v.Kind;
            if (r.State != BenchmarkRowState.Done)
            {
                continue; // 실패·거부·취소 글은 그대로
            }

            r.IsRowsMismatch = v.Kind == BenchmarkVerdictKind.RowMismatch;
            r.VerdictText = v.Kind switch
            {
                BenchmarkVerdictKind.Baseline => L("기준", "Baseline"),
                BenchmarkVerdictKind.Best => F("가장 나음 · {0:0.0} 배", "Best · {0:0.0}×", v.Ratio),
                BenchmarkVerdictKind.Faster => F("{0:0.0} 배", "{0:0.0}×", v.Ratio),
                BenchmarkVerdictKind.Slower => F("느림 · {0:0.0} 배", "Slower · {0:0.0}×", v.Ratio),
                BenchmarkVerdictKind.RowMismatch => RowDeltaText(v.RowDelta),
                _ => L("원문을 재지 못해 판정 안 함", "Not judged — the original was not measured"),
            };
        }
    }

    private static string RowDeltaText(long? delta) =>
        F("행 수 다름 ({0})", "Row count differs ({0})",
            delta is { } d ? (d > 0 ? "+" : "−") + Math.Abs(d).ToString("N0", CultureInfo.InvariantCulture) : "?");

    private string BenchmarkSummary(IReadOnlyList<BenchmarkRowItem> rows, int n, bool stopped)
    {
        var total = IndexItems.Count + RewriteItems.Count + HintItems.Count;
        var text = F("후보 {0} · 벤치마크 {1} 변형 × {2} 회", "{0} candidates · benchmark {1} variants × {2} runs", total, rows.Count, n);
        var best = rows.FirstOrDefault(r => r.IsBest);
        text += best is not null
            ? F(" · 가장 나음 = 변형 {0}", " · best = variant {0}", best.Number)
            : L(" · 원문보다 나은 변형이 없습니다", " · no variant beats the original");
        var mismatch = rows.Where(r => r.IsRowsMismatch).Select(r => r.Number.ToString(CultureInfo.InvariantCulture)).ToList();
        if (mismatch.Count > 0)
        {
            text += F(" (행 수가 다른 변형 {0} 은 판정에서 뺌)", " (variants {0} differ in row count and are excluded)", string.Join(", ", mismatch));
        }

        if (stopped)
        {
            text += L(" · 중간에 멈춤", " · stopped early");
        }

        return text;
    }

    private string SummaryText()
    {
        var parts = F("인덱스 후보 {0} · 재작성 후보 {1} · 힌트 대안 {2}", "{0} index candidates · {1} rewrites · {2} hints",
            IndexItems.Count, RewriteItems.Count, HintItems.Count);
        if (BenchmarkRules.RejectReason(StatementText, LiteralTranslator.IsEnglish) is { } reason)
        {
            parts += " · " + reason;
        }

        return parts;
    }

    private static string BuildSourceText(TuningAssistantInput input)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(input.SourceTitle))
        {
            parts.Add(input.SourceTitle!);
        }

        if (input.FirstLine is { } line && input.Statement is { } st)
        {
            var count = st.Text.Split('\n').Length;
            parts.Add(count > 1
                ? F("줄 {0}–{1}", "lines {0}–{1}", line, line + count - 1)
                : F("줄 {0}", "line {0}", line));
        }

        if (input.Plan is { Steps.Count: > 0 })
        {
            var kind = input.PlanIsActual ? L("실제 계획", "actual plan") : L("예상 계획", "estimated plan");
            parts.Add(input.PlanTime is { } at ? kind + " " + at.ToString("HH:mm", CultureInfo.InvariantCulture) : kind);
        }
        else if (input.Statement is not null)
        {
            parts.Add(L("계획 없음", "no plan"));
        }

        return string.Join(" · ", parts);
    }

    private ITuningService? ResolveTuning() => _tuning ??= _services?.GetService(typeof(ITuningService)) as ITuningService;

    /// <summary>계산 속성 알림과 메뉴 키.</summary>
    private void Raise()
    {
        foreach (var name in new[]
        {
            nameof(IndexCount), nameof(RewriteCount), nameof(HintCount), nameof(NoticeText), nameof(HasNotice), nameof(ShowNoStatement), nameof(ShowCandidates),
            nameof(CanFetchActualPlan), nameof(CanFetchEstimatedPlan), nameof(ShowPlanButtons), nameof(IndexEmptyText), nameof(HintEmptyText),
            nameof(IndexEmpty), nameof(RewriteEmpty), nameof(HintEmpty), nameof(HasWarningOnlyRewrite), nameof(HasDisabledHint), nameof(BenchmarkBlockedReason), nameof(CanRunBenchmark), nameof(BenchmarkTip),
            nameof(BenchmarkEmpty), nameof(CanInsertIndexDdl), nameof(CanInsertRewrite),
        })
        {
            OnPropertyChanged(name);
        }

        Keys?.Set("tuningAssistantOpen", true);
    }

    /// <summary>문서가 닫혔다: 벤치마크를 멈추고 메뉴 키를 끈다.</summary>
    public void Closed()
    {
        CancelBenchmark();
        Keys?.Set("tuningAssistantOpen", false);
    }

    private static string L(string ko, string en) => LiteralTranslator.IsEnglish ? en : ko;

    // 언어에 맞는 서식 글. 서식은 언어마다 달라 CompositeFormat 으로 미리 만들어 둘 수 없다.
#pragma warning disable CA1863
    private static string F(string ko, string en, params object?[] args) => string.Format(CultureInfo.InvariantCulture, L(ko, en), args);
#pragma warning restore CA1863

    private static string FirstLine(string message)
    {
        var i = message.IndexOf('\n', StringComparison.Ordinal);
        return (i < 0 ? message : message[..i]).Trim();
    }
}
