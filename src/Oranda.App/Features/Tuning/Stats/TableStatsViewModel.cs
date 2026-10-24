using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.SessionMonitor;
using Oranda.App.Shell;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;
using Oranda.Core.Tuning;
using static Oranda.App.Features.Tuning.Stats.TuningText;

namespace Oranda.App.Features.Tuning.Stats;

/// <summary>
/// <c>oranda.panel.tuningStats</c> — 표 하나의 통계(TN-03): 표 요약(stale 강조)·열 통계·고른 열의 히스토그램·인덱스 통계.
/// 「통계 수집 스크립트 만들기」(TN-16)는 DBMS_STATS 문안을 새 편집기 탭에 넣기만 한다(DB 로 보내지 않는다).
/// 조회는 <see cref="ITuningService"/>(SELECT 만) 하나로, 한 세션에서 한 번에 하나씩 한다.
/// </summary>
[OrandaToolPanel(TuningCommands.StatsPanelId, ToolPanelKind.Document, DockLocation.Document, AllowMultiple = true, Icon = "table")]
public sealed partial class TableStatsViewModel : ToolPanelViewModelBase, IDisposable
{
    /// <summary>오른쪽 상태 줄의 출처 뷰.</summary>
    public const string SourceViews = "all_tab_statistics · all_tab_col_statistics · all_tab_histograms · all_ind_statistics";

    private readonly IServiceProvider? _services;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ITuningService? _tuning;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _histogramCts;
    private bool _disposed;

    [ObservableProperty]
    private string _targetText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(CanGatherScript))]
    private TableStatsTarget? _target;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHint), nameof(ShowContent), nameof(HintIcon))]
    private TuningPanelState _state = TuningPanelState.NoSession;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGatherScript), nameof(CanRefresh))]
    private bool _isBusy;

    [ObservableProperty]
    private string _hintTitle = string.Empty;

    [ObservableProperty]
    private string _hintText = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStats), nameof(HasNoStats), nameof(IsStale), nameof(NumRowsText), nameof(BlocksText), nameof(AvgRowLenText),
        nameof(LastAnalyzedText), nameof(AgeText), nameof(ScriptEmphasis), nameof(SummaryText))]
    private TableStatsRow? _stats;

    [ObservableProperty]
    private IReadOnlyList<ColumnStatsItem> _columns = [];

    [ObservableProperty]
    private IReadOnlyList<IndexStatsItem> _indexes = [];

    [ObservableProperty]
    private ColumnStatsItem? _selectedColumn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHistogram))]
    private IReadOnlyList<HistogramBarItem> _histogramBars = [];

    [ObservableProperty]
    private string _histogramTitle = string.Empty;

    [ObservableProperty]
    private string _histogramCaption = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHistogramHint))]
    private string _histogramHint = string.Empty;

    /// <summary>인덱스 stale 을 판정할 수 없을 때(비 DBA) 구역 머리의 작은 안내. 아니면 빈 글.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIndexStaleNote))]
    private string _indexStaleNote = string.Empty;

    /// <summary>히스토그램 막대 옆 수: 거짓 = 행 수, 참 = 비율.</summary>
    [ObservableProperty]
    private bool _showShare;

    /// <summary>시험·디자이너용.</summary>
    public TableStatsViewModel()
    {
        SetHint(TuningPanelState.NoSession);
    }

    /// <summary>셸(DI)이 쓰는 생성자. 서비스는 처음 쓸 때 꺼낸다.</summary>
    public TableStatsViewModel(IServiceProvider services)
        : this()
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <summary>시험용: 서비스를 직접 준다.</summary>
    public TableStatsViewModel(ITuningService tuning)
        : this()
    {
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));
    }

    public ISession? Session { get; private set; }

    /// <summary>편집기 커서 아래 표(「편집기 표」). 셸이 넣는다. 없으면 단추가 안내만 한다.</summary>
    public Func<TableStatsTarget?>? EditorTable { get; set; }

    /// <summary>새 편집기 탭에 글을 넣는 방법(제목, 본문). 없으면 <c>SqlEditorHost</c> 의 새 탭.</summary>
    public Action<string, string>? OpenEditor { get; set; }

    /// <summary>지금 시각(나이·스크립트 머리). 시험이 바꾼다.</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.Now;

    /// <summary>문서 탭 제목 「통계 — MES.LOT_HIST」.</summary>
    public string Title => Target is null ? T("통계 보기", "Statistics") : T("통계 — ", "Stats — ") + Target.Display;

    public bool ShowHint => State is not TuningPanelState.Ready;

    public bool ShowContent => State is TuningPanelState.Ready;

    public string HintIcon => State switch
    {
        TuningPanelState.NoSession => "connect",
        TuningPanelState.Denied => "lock",
        TuningPanelState.Error => "warn",
        TuningPanelState.Loading => "refresh",
        _ => "table",
    };

    public bool HasStats => Stats is { LastAnalyzed: not null };

    /// <summary>통계를 한 번도 모으지 않은 표(또는 사전에 통계 줄이 없음).</summary>
    public bool HasNoStats => !HasStats;

    public bool IsStale => Stats is { Stale: true };

    /// <summary>스크립트 단추 강조(통계 없음·stale).</summary>
    public bool ScriptEmphasis => HasNoStats || IsStale;

    public bool CanGatherScript => Target is not null && !IsBusy;

    public bool CanRefresh => !IsBusy;

    public bool HasHistogram => HistogramBars.Count > 0;

    public bool HasHistogramHint => HistogramHint.Length > 0;

    public bool HasIndexStaleNote => IndexStaleNote.Length > 0;

    public string NumRowsText => Dash(StatsFormat.Count(Stats?.NumRows));

    public string BlocksText => Dash(StatsFormat.Count(Stats?.Blocks));

    public string AvgRowLenText => Dash(StatsFormat.Bytes(Stats?.AvgRowLen));

    public string LastAnalyzedText => Stats?.LastAnalyzed is { } at ? StatsFormat.Date(at) : T("수집 안 함", "never");

    /// <summary>「(52 일 전)」. 모르면 빈 글.</summary>
    public string AgeText => AgeDays is { } d ? T($"({d} 일 전)", d == 1 ? "(1 day ago)" : $"({d} days ago)") : string.Empty;

    /// <summary>마지막 수집 뒤 지난 날 수.</summary>
    public int? AgeDays => Stats?.LastAnalyzed is { } at ? Math.Max(0, (int)(Clock() - at).TotalDays) : null;

    /// <summary>stale 일 때 히스토그램 밑 경고. 아니면 빈 글.</summary>
    public string HistogramWarning => IsStale && HasHistogram && AgeDays is > 0
        ? T($"통계 뒤 {AgeDays} 일 동안 들어온 값은 히스토그램에 없습니다. 최근 값을 거르는 조건의 예상 행이 실제보다 작게 나올 수 있습니다.",
            $"Values added in the {AgeDays} days since the last gather are not in the histogram. Estimated rows for filters on recent values may come out too low.")
        : string.Empty;

    public bool HasHistogramWarning => HistogramWarning.Length > 0;

    /// <summary>상태 줄 왼쪽 「MES.LOT_HIST · 열 9 · 인덱스 3 · stale 예」.</summary>
    public string SummaryText => Target is null
        ? string.Empty
        : string.Create(CultureInfo.InvariantCulture, $"{Target.Display} · {T("열", "columns")} {Columns.Count} · {T("인덱스", "indexes")} {Indexes.Count} · stale {(HasNoStats ? T("통계 없음", "no stats") : IsStale ? T("예", "yes") : T("아니요", "no"))}");

    /// <summary>표의 기본 소유자(따옴표 없는 이름일 때). 세션 계정.</summary>
    public string? DefaultOwner => Session?.Server.SessionUser ?? Session?.Profile.User;

    /// <summary>세션과 대상을 정한다(읽지는 않는다). 대상이 없으면 「표를 고르세요」 안내.</summary>
    public void Attach(ISession? session, TableStatsTarget? target)
    {
        Session = session;
        Target = target;
        TargetText = target?.Display ?? string.Empty;
        Stats = null;
        Columns = [];
        Indexes = [];
        ClearHistogram();
        SetHint(session is null ? TuningPanelState.NoSession : target is null ? TuningPanelState.NoTarget : TuningPanelState.Loading);
    }

    /// <summary>위 칸(<see cref="TargetText"/>)의 이름으로 읽는다. 형식이 틀리면 상태 줄에 알린다.</summary>
    public Task ApplyTargetTextAsync(CancellationToken ct = default)
    {
        if (TableStatsTarget.Parse(TargetText, DefaultOwner) is not { } target)
        {
            StatusText = T("표 이름을 owner.table 또는 table 로 넣으세요.", "Type the table as owner.table or table.");
            return Task.CompletedTask;
        }

        Target = target;
        TargetText = target.Display;
        return LoadAsync(ct);
    }

    /// <summary>편집기 커서 아래 표로 바꿔 읽는다.</summary>
    public Task UseEditorTableAsync(CancellationToken ct = default)
    {
        if (EditorTable?.Invoke() is not { } target)
        {
            StatusText = T("편집기 커서 아래에 표 이름이 없습니다.", "There is no table name under the editor cursor.");
            return Task.CompletedTask;
        }

        Target = target;
        TargetText = target.Display;
        return LoadAsync(ct);
    }

    /// <summary>표 통계·열 통계·인덱스 통계를 읽는다(앞 읽기는 취소). 끝나면 히스토그램이 있는 첫 열을 고른다.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (Session is not { } session || session.State is SessionState.Closed or SessionState.Broken)
        {
            SetHint(TuningPanelState.NoSession);
            return;
        }

        if (Target is not { } target)
        {
            SetHint(TuningPanelState.NoTarget);
            return;
        }

        if (Service() is not { } tuning)
        {
            SetHint(TuningPanelState.Error, T("튜닝 조회가 준비되지 않았습니다", "Tuning queries are not available"),
                T("이 빌드에 통계 조회 서비스(ITuningService)가 없습니다.", "This build has no statistics service (ITuningService)."));
            return;
        }

        CancelLoads();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loadCts = cts;
        var hadContent = State is TuningPanelState.Ready && Stats is not null;
        IsBusy = true;
        if (!hadContent)
        {
            SetHint(TuningPanelState.Loading);
        }

        StatusText = T($"{target.Display} 통계를 읽는 중…", $"Reading statistics for {target.Display}…");
        try
        {
            await _gate.WaitAsync(cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            FinishCancelled(cts, hadContent);
            return;
        }

        try
        {
            var stats = await tuning.GetTableStatsAsync(session, target.Owner, target.Table, cts.Token).ConfigureAwait(true);
            var columns = await tuning.GetColumnStatsAsync(session, target.Owner, target.Table, cts.Token).ConfigureAwait(true);
            var indexes = await tuning.GetIndexStatsAsync(session, target.Owner, target.Table, cts.Token).ConfigureAwait(true);
            var indexColumns = await TryIndexColumnsAsync(tuning, session, target, cts.Token).ConfigureAwait(true);
            cts.Token.ThrowIfCancellationRequested();

            Stats = stats;
            Columns = [.. columns.Select(c => new ColumnStatsItem(c))];
            if (stats is null && columns.Count == 0 && indexes.Count == 0)
            {
                // all_tab_statistics 에 줄이 없다 = 이 계정으로 안 보이거나 없는 표(통계를 안 모은 표도 줄은 있다).
                Stats = null;
                Columns = [];
                Indexes = [];
                ClearHistogram();
                SetHint(TuningPanelState.Denied, T("이 계정으로 볼 수 없는 표", "This account cannot see the table"),
                    T($"{target.Display} 이(가) 없거나 {session.Profile.User} 계정에 보이지 않습니다. 이름을 확인하거나 소유자 접속으로 여세요.",
                      $"{target.Display} does not exist or is not visible to {session.Profile.User}. Check the name or connect as the owner."));
                StatusText = HintTitle;
                return;
            }

            var staleKnown = session.Server.Capabilities.IsDba;
            Indexes = [.. indexes.Select(i => new IndexStatsItem(i, indexColumns.TryGetValue(i.IndexName, out var cols) ? cols : null, stats?.Blocks, stats?.NumRows, staleKnown))];
            IndexStaleNote = staleKnown || indexes.Count == 0 ? string.Empty : T("stale 판정 불가(권한) — DBA 가 아니면 남의 인덱스 stale 을 볼 수 없습니다", "stale cannot be judged (privilege) — only DBA accounts see stale flags of other schemas' indexes");
            State = TuningPanelState.Ready;
            OnPropertyChanged(nameof(SummaryText));
            StatusText = SummaryText;
        }
        catch (OperationCanceledException)
        {
            FinishCancelled(cts, hadContent);
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowFailure(ex, target);
            return;
        }
        finally
        {
            _gate.Release();
            if (ReferenceEquals(_loadCts, cts))
            {
                IsBusy = false;
            }
        }

        // 히스토그램이 있는 첫 열(없으면 첫 열)을 고른다 — 고르면 그 열의 히스토그램을 읽는다.
        var pick = Columns.FirstOrDefault(c => c.HasHistogram) ?? (Columns.Count > 0 ? Columns[0] : null);
        if (ReferenceEquals(SelectedColumn, pick))
        {
            await LoadHistogramAsync(pick, cts.Token).ConfigureAwait(true);
        }
        else
        {
            SelectedColumn = pick;
            await (_pendingHistogram ?? Task.CompletedTask).ConfigureAwait(true);
        }
    }

    private Task? _pendingHistogram;

    partial void OnSelectedColumnChanged(ColumnStatsItem? value) => _pendingHistogram = LoadHistogramAsync(value, _loadCts?.Token ?? default);

    partial void OnShowShareChanged(bool value) => HistogramBars = [.. HistogramBars.Select(b => b.WithShare(value))];

    /// <summary>고른 열의 히스토그램을 읽는다. 히스토그램이 없는 열이면 안내만.</summary>
    public async Task LoadHistogramAsync(ColumnStatsItem? column, CancellationToken ct = default)
    {
        _histogramCts?.Cancel();
        if (column is null || Target is not { } target || Session is not { } session)
        {
            ClearHistogram();
            return;
        }

        HistogramTitle = T("히스토그램 · ", "Histogram · ") + column.Name;
        HistogramCaption = column.HasHistogram
            ? string.Create(CultureInfo.InvariantCulture, $"{column.Row.Histogram} · {StatsFormat.Count(column.Row.NumBuckets)} {T("버킷", "buckets")}")
            : string.Empty;
        if (!column.HasHistogram)
        {
            HistogramBars = [];
            HistogramHint = T("히스토그램이 없습니다 (SIZE 1)", "No histogram (SIZE 1)");
            OnPropertyChanged(nameof(HistogramWarning));
            OnPropertyChanged(nameof(HasHistogramWarning));
            return;
        }

        if (Service() is not { } tuning)
        {
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _histogramCts = cts;
        HistogramHint = T("히스토그램을 읽는 중…", "Reading the histogram…");
        try
        {
            await _gate.WaitAsync(cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            var buckets = await tuning.GetHistogramAsync(session, target.Owner, target.Table, column.Name, cts.Token).ConfigureAwait(true);
            cts.Token.ThrowIfCancellationRequested();
            HistogramBars = [.. HistogramBarItem.From(buckets).Select(b => b.WithShare(ShowShare))];
            HistogramHint = HistogramBars.Count == 0 ? T("버킷이 없습니다", "No buckets") : string.Empty;
        }
        catch (OperationCanceledException)
        {
            // 다른 열을 고르면 앞 읽기는 버린다.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            HistogramBars = [];
            HistogramHint = T("히스토그램을 읽지 못했습니다: ", "Could not read the histogram: ") + FirstLine(ex.Message);
        }
        finally
        {
            _gate.Release();
        }

        OnPropertyChanged(nameof(HistogramWarning));
        OnPropertyChanged(nameof(HasHistogramWarning));
    }

    /// <summary>읽는 중인 조회를 멈춘다.</summary>
    public void Cancel() => CancelLoads();

    /// <summary>DBMS_STATS 문안(실행 안 함). 대상이 없으면 null.</summary>
    public TuningScript? BuildGatherScript()
    {
        try
        {
            return Target is { } target ? GatherStatsScript.Build(target, Stats, Clock()) : null;
        }
        catch (ArgumentException)
        {
            return null; // Oracle 식별자가 될 수 없는 이름(큰따옴표·줄바꿈)
        }
    }

    /// <summary>「통계 수집 스크립트 만들기」: 문안을 새 편집기 탭에 넣는다. DB 로 보내지 않는다.</summary>
    public bool OpenGatherScript()
    {
        if (Target is not { } target || BuildGatherScript() is not { } script)
        {
            StatusText = T("통계를 볼 표를 먼저 고르세요.", "Pick a table first.");
            return false;
        }

        var title = GatherStatsScript.Title(target);
        if (OpenEditor is { } open)
        {
            open(title, script.Body);
        }
        else
        {
            Oranda.App.Features.SqlEditor.SqlEditorHost.Current.NewDocument(script.Body).Title = title;
        }

        StatusText = T("통계 수집 스크립트를 새 편집기 탭에 넣었습니다. 검토한 뒤 직접 실행하세요(DB 에 씁니다).",
            "Put the statistics gathering script in a new editor tab. Review it and run it yourself (it writes to the database).");
        return true;
    }

    /// <summary>「복사」: 요약·열·인덱스 통계를 탭으로 나눈 글로.</summary>
    public string BuildCopyText()
    {
        var sb = new StringBuilder();
        if (Target is null)
        {
            return string.Empty;
        }

        sb.Append(Target.Display).Append('\t').Append(T("행 수", "Rows")).Append(' ').Append(NumRowsText)
            .Append('\t').Append(T("블록", "Blocks")).Append(' ').Append(BlocksText)
            .Append('\t').Append(T("마지막 수집", "Last analyzed")).Append(' ').Append(LastAnalyzedText)
            .Append('\t').Append("stale ").Append(IsStale ? "YES" : "NO").Append('\n');
        sb.Append('\n').AppendJoin('\t', ColumnHeaders).Append('\n');
        foreach (var c in Columns)
        {
            sb.AppendJoin('\t', c.Cells).Append('\n');
        }

        sb.Append('\n').AppendJoin('\t', IndexHeaders).Append('\n');
        foreach (var i in Indexes)
        {
            sb.AppendJoin('\t', i.Cells).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>열 통계 격자 머리글.</summary>
    public static IReadOnlyList<string> ColumnHeaders =>
    [
        T("열", "Column"), T("고유 값", "Distinct"), "NULL", T("밀도", "Density"), T("히스토그램", "Histogram"), T("버킷", "Buckets"), T("마지막 수집", "Last analyzed"),
    ];

    /// <summary>인덱스 통계 격자 머리글.</summary>
    public static IReadOnlyList<string> IndexHeaders =>
    [
        T("인덱스", "Index"), T("열", "Columns"), T("고유", "Unique"), "BLEVEL", T("leaf 블록", "Leaf blocks"), T("고유 키", "Distinct keys"),
        "Clustering factor", T("CF 판정", "CF rating"), T("마지막 수집", "Last analyzed"), "stale",
    ];

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelLoads();
        _histogramCts?.Cancel();
    }

    private static string Dash(string text) => text.Length == 0 ? "–" : text;

    private ITuningService? Service() => _tuning ??= _services?.GetService(typeof(ITuningService)) as ITuningService;

    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> TryIndexColumnsAsync(
        ITuningService tuning, ISession session, TableStatsTarget target, CancellationToken ct)
    {
        try
        {
            var rows = await tuning.GetIndexColumnsAsync(session, target.Owner, target.Table, ct).ConfigureAwait(true);
            return rows.GroupBy(r => r.IndexName, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Columns, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal); // 열 구성은 덤 — 못 읽어도 통계는 보인다.
        }
    }

    private void CancelLoads()
    {
        _loadCts?.Cancel();
        _histogramCts?.Cancel();
    }

    private void FinishCancelled(CancellationTokenSource cts, bool hadContent)
    {
        if (!ReferenceEquals(_loadCts, cts))
        {
            return; // 새 읽기가 이어받았다.
        }

        IsBusy = false;
        if (!hadContent)
        {
            SetHint(TuningPanelState.NoTarget, T("읽기를 취소했습니다", "Reading was cancelled"), T("F5 로 다시 읽습니다.", "Press F5 to read again."));
        }

        StatusText = T("취소했습니다.", "Cancelled.");
    }

    private void ShowFailure(Exception ex, TableStatsTarget target)
    {
        Stats = null;
        Columns = [];
        Indexes = [];
        ClearHistogram();
        if (ex is NotSupportedException or NotImplementedException)
        {
            SetHint(TuningPanelState.Error, T("튜닝 조회가 준비되지 않았습니다", "Tuning queries are not available"),
                T("이 접속의 통계 조회가 아직 구현되지 않았습니다.", "Statistics queries are not implemented for this connection yet."));
        }
        else if (MonitorAccessState.IsPrivilegeError(ex))
        {
            var user = Session?.Profile.User ?? string.Empty;
            SetHint(TuningPanelState.Denied, T("이 계정으로 볼 수 없는 표", "This account cannot see the table"),
                T($"{user} 계정으로는 {target.Display} 의 통계를 읽을 수 없습니다(ORA-00942·ORA-01031). 소유자 접속으로 열거나 그 표의 SELECT 권한을 받으세요.",
                  $"The {user} account cannot read statistics of {target.Display} (ORA-00942/ORA-01031). Connect as the owner or get SELECT on the table."));
        }
        else
        {
            SetHint(TuningPanelState.Error, T("통계를 읽지 못했습니다", "Could not read the statistics"), FirstLine(ex.Message));
        }

        StatusText = FirstLine(ex.Message);
    }

    private void ClearHistogram()
    {
        HistogramBars = [];
        HistogramTitle = T("히스토그램", "Histogram");
        HistogramCaption = string.Empty;
        HistogramHint = T("열을 고르면 히스토그램을 봅니다", "Pick a column to see its histogram");
        OnPropertyChanged(nameof(HistogramWarning));
        OnPropertyChanged(nameof(HasHistogramWarning));
    }

    private void SetHint(TuningPanelState state, string? title = null, string? text = null)
    {
        State = state;
        (HintTitle, HintText) = state switch
        {
            TuningPanelState.NoSession => (T("접속 없음", "No connection"), T("DB 에 접속한 뒤 다시 여세요.", "Connect to a database and open this again.")),
            TuningPanelState.NoTarget => (T("통계를 볼 표를 고르세요", "Pick a table to see its statistics"),
                T("편집기에서 표 이름에 커서를 두고 「편집기 표」를 누르거나, 위 칸에 owner.table 을 넣고 Enter.",
                  "Put the editor cursor on a table name and press Editor table, or type owner.table above and press Enter.")),
            TuningPanelState.Loading => (T("통계를 읽는 중…", "Reading statistics…"), Target?.Display ?? string.Empty),
            _ => (string.Empty, string.Empty),
        };
        if (title is not null)
        {
            HintTitle = title;
        }

        if (text is not null)
        {
            HintText = text;
        }
    }
}
