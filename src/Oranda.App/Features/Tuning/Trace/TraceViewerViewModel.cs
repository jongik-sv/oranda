using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Shell;
using Oranda.Core.Monitoring;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;
using Oranda.Core.Tuning;

namespace Oranda.App.Features.Tuning.Trace;

/// <summary>트레이스 보기 화면 상태(TSK-06-04).</summary>
public enum TraceViewState
{
    /// <summary>접속 없음.</summary>
    NoSession,

    Loading,

    /// <summary>파일이 비었다(트레이스를 켠 적이 없음).</summary>
    NoTrace,

    /// <summary>11g 등 본문을 SQL 로 읽을 수 없다 — 경로와 이유만.</summary>
    Unreadable,

    /// <summary>v$diag_* 권한 없음.</summary>
    NoPrivilege,

    Error,

    Ready,
}

/// <summary>요약 정렬 기준(TKPROF 의 sort= 와 같은 뜻).</summary>
public enum TraceSortKey
{
    Elapsed,
    Cpu,
    Disk,
    Query,
    Current,
    Rows,
    Executions,
}

/// <summary>
/// <c>oranda.panel.traceViewer</c> — 세션 트레이스 파일 보기(TSK-06-04, TN-07·08). 왼쪽 = 이 화면에서 읽은 파일, 오른쪽 = TKPROF 식 「요약」 / 「본문」.
/// 파일 경로·본문은 <see cref="ITuningService"/>(v$diag_info, 12.2+ v$diag_trace_file_contents, 모두 SELECT)로 읽는다.
/// 요약은 Core 의 TKPROF 식 파서(<see cref="Parser"/>)가 만든다. 큰 파일은 앞 <see cref="MaxReadBytes"/> 만 보인다(「모두 읽기」로 전부).
/// </summary>
[OrandaToolPanel(TuningCommands.TraceViewerPanelId, ToolPanelKind.Document, DockLocation.Document)]
public sealed partial class TraceViewerViewModel : ToolPanelViewModelBase
{
    /// <summary>처음 보이는 크기(5 MB).</summary>
    public const long MaxReadBytes = 5L * 1024 * 1024;

    /// <summary>권한이 없을 때 복사해 줄 GRANT 문(DBA 에게 부탁할 글, 실행하지 않는다).</summary>
    public const string GrantSql = "GRANT SELECT ON v_$diag_info TO <user>;\nGRANT SELECT ON v_$diag_trace_file_contents TO <user>;";

    public static readonly IReadOnlyList<string> SortNames = ["elapsed", "cpu", "disk", "query", "current", "rows", "실행"];

    public static readonly IReadOnlyList<string> CallColumns = ["call", "count", "cpu(s)", "elapsed(s)", "disk", "query", "current", "rows"];

    private readonly IServiceProvider? _services;
    private ITuningService? _tuning;
    private int _generation;
    private TraceSummary _summary = new([], []);
    private List<TraceLineItem> _allLines = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNoSession), nameof(IsLoading), nameof(IsNoTrace), nameof(IsUnreadable), nameof(IsNoPrivilege), nameof(IsError), nameof(IsReady),
        nameof(ShowSummary), nameof(ShowBody))]
    private TraceViewState _state = TraceViewState.NoSession;

    /// <summary>상태 화면 안내 글.</summary>
    [ObservableProperty]
    private string _message = "접속한 뒤 「튜닝 › 트레이스 보기」를 여세요.";

    /// <summary>11g·권한 없음 때 보이는 서버 경로(없으면 빈 글).</summary>
    [ObservableProperty]
    private string _serverPath = string.Empty;

    [ObservableProperty]
    private string _targetText = "내 세션";

    /// <summary>0 = 요약, 1 = 본문.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSummary), nameof(ShowBody), nameof(IsSummaryView))]
    private int _viewIndex;

    [ObservableProperty]
    private int _sortIndex;

    [ObservableProperty]
    private TraceFileEntry? _selectedFile;

    [ObservableProperty]
    private TraceStatementItem? _selectedStatement;

    [ObservableProperty]
    private IReadOnlyList<IReadOnlyList<object?>> _fileRows = [];

    [ObservableProperty]
    private IReadOnlyList<IReadOnlyList<object?>> _callRows = [];

    [ObservableProperty]
    private string _callCaption = string.Empty;

    [ObservableProperty]
    private string _findText = string.Empty;

    [ObservableProperty]
    private bool _showWaitLines = true;

    [ObservableProperty]
    private bool _showStatLines;

    [ObservableProperty]
    private string _bodyInfoText = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _sourceText = "v$diag_trace_file_contents";

    /// <summary>앞 5 MB 만 보이는 중.</summary>
    [ObservableProperty]
    private bool _isTruncated;

    /// <summary>10053 실행 중(툴바 단추 꺼짐 + 진행 막대).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunOptimizerTrace))]
    private bool _isTracing;

    /// <summary>본문에서 보이게 할 줄 번호(10053 표시 줄). 화면이 받아 옮긴다.</summary>
    [ObservableProperty]
    private TraceLineItem? _scrollTarget;

    public TraceViewerViewModel()
    {
    }

    public TraceViewerViewModel(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    /// <summary>시험용: 튜닝 서비스를 직접 넣는다.</summary>
    internal TraceViewerViewModel(ITuningService tuning) => _tuning = tuning;

    /// <summary>
    /// TKPROF 식 요약 파서. 기본 = Core 의 <see cref="TraceParser.Parse"/>(TSK-06-02). 시험은 바꾼다.
    /// </summary>
    public Func<IReadOnlyList<string>, TraceSummary> Parser { get; set; } = TraceParser.Parse;

    /// <summary>10053 실행(명령 처리기가 <see cref="OptimizerTraceRunner"/> 로 채운다). 결과 = 세션·파일·표시.</summary>
    public Func<CancellationToken, Task<OptimizerTraceOutcome>>? OptimizerTrace { get; set; }

    public ISession? Session { get; private set; }

    /// <summary>보는 세션(<c>default</c> = 내 세션).</summary>
    public SessionKey Target { get; private set; }

    public ObservableCollection<TraceFileEntry> Files { get; } = [];

    public ObservableCollection<TraceStatementItem> Statements { get; } = [];

    public ObservableCollection<TraceWaitItem> Waits { get; } = [];

    /// <summary>찾기·WAIT·STAT 거름을 지난 본문 줄.</summary>
    public ObservableCollection<TraceLineItem> VisibleLines { get; } = [];

    public static IReadOnlyList<string> FileColumns { get; } = ["파일", "크기", "읽음"];

    public string WaitTotalText { get; private set; } = string.Empty;

    public bool IsNoSession => State == TraceViewState.NoSession;

    public bool IsLoading => State == TraceViewState.Loading;

    public bool IsNoTrace => State == TraceViewState.NoTrace;

    public bool IsUnreadable => State == TraceViewState.Unreadable;

    public bool IsNoPrivilege => State == TraceViewState.NoPrivilege;

    public bool IsError => State == TraceViewState.Error;

    public bool IsReady => State == TraceViewState.Ready;

    public bool IsSummaryView => ViewIndex == 0;

    public bool ShowSummary => IsReady && ViewIndex == 0;

    public bool ShowBody => IsReady && ViewIndex == 1;

    public bool HasStatements => Statements.Count > 0;

    public bool CanRunOptimizerTrace => !IsTracing && OptimizerTrace is not null;

    /// <summary>요약 정렬 기준.</summary>
    public TraceSortKey SortKey => (TraceSortKey)Math.Clamp(SortIndex, 0, 6);

    /// <summary>세션의 트레이스 파일을 읽는다(<paramref name="target"/> 가 default 면 내 세션).</summary>
    public async Task LoadAsync(ISession session, SessionKey target = default, string? targetText = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        Session = session;
        Target = target;
        TargetText = targetText ?? (target == default ? "내 세션" : $"세션 {target.InstanceId}·{target.Sid},{target.Serial}");
        var gen = Interlocked.Increment(ref _generation);
        State = TraceViewState.Loading;
        Message = "트레이스 파일을 찾는 중…";
        try
        {
            var tuning = ResolveTuning();
            var info = await Task.Run(() => tuning.GetTraceFileAsync(session, target, ct), CancellationToken.None).ConfigureAwait(true);
            if (gen != _generation)
            {
                return;
            }

            await OpenAsync(session, info, null, gen, ct).ConfigureAwait(true);
        }
        catch (Exception ex) when (gen == _generation)
        {
            Fail(ex);
        }
    }

    /// <summary>
    /// 옵티마이저 트레이스(10053)를 켜고 지금 문장 사본을 하드 파스한 뒤 끄고, 그 파일을 골라 본문으로 연다.
    /// </summary>
    public async Task RunOptimizerTraceAsync(CancellationToken ct = default)
    {
        if (OptimizerTrace is not { } run || IsTracing)
        {
            StatusText = "편집기에서 문장에 커서를 둔 뒤 「옵티마이저 트레이스(10053)」를 누르세요.";
            return;
        }

        IsTracing = true;
        StatusText = "10053 이벤트를 켜고 사본을 하드 파스하는 중…";
        var gen = Interlocked.Increment(ref _generation);
        try
        {
            var outcome = await run(ct).ConfigureAwait(true);
            Session = outcome.Session;
            Target = default;
            TargetText = "내 세션";
            await OpenAsync(outcome.Session, outcome.File, outcome.Marker, gen, ct).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText = "10053 실패: " + FirstLine(ex);
            if (State is TraceViewState.Loading)
            {
                Fail(ex);
            }
        }
        finally
        {
            IsTracing = false;
        }
    }

    /// <summary>잘린 파일을 모두 읽은 것으로 바꾼다(이미 받은 줄 전부를 쓴다).</summary>
    public void ReadAll()
    {
        if (SelectedFile is not { IsTruncated: true } file)
        {
            return;
        }

        var full = file with { Lines = file.AllLines };
        var at = Files.IndexOf(file);
        if (at >= 0)
        {
            Files[at] = full;
        }

        SelectedFile = full;
        RefreshFileRows();
    }

    /// <summary>요약(TKPROF 식 글) 또는 보이는 본문 줄을 글로(복사·저장).</summary>
    public string ExportText()
    {
        if (ViewIndex == 1)
        {
            return string.Join(Environment.NewLine, VisibleLines.Select(l => l.Text));
        }

        var sb = new StringBuilder();
        sb.AppendLine(SelectedFile?.Info.Path ?? string.Empty);
        sb.AppendLine(string.Join('\t', "#", "sql_id", "sql", "exec", "cpu(s)", "elapsed(s)", "disk", "query", "current", "rows"));
        foreach (var s in Statements)
        {
            sb.AppendLine(string.Join('\t', s.Number, s.SqlIdText, s.ShortText, s.ExecutionsText, s.CpuText, s.ElapsedText, s.DiskText, s.QueryText, s.CurrentText, s.RowsText));
        }

        sb.AppendLine();
        sb.AppendLine(string.Join('\t', "event", "count", "avg(ms)", "total(s)"));
        foreach (var w in Waits)
        {
            sb.AppendLine(string.Join('\t', w.Event + (w.IsIdle ? " (idle)" : string.Empty), w.CountText, w.AverageText, w.TotalText));
        }

        return sb.ToString();
    }

    /// <summary>읽기 가능 여부·줄 수에 따라 상태를 정하고 목록에 넣는다(시험도 쓴다).</summary>
    internal async Task OpenAsync(ISession session, TraceFileInfo info, string? marker, int gen, CancellationToken ct)
    {
        ServerPath = info.Path ?? string.Empty;
        if (!info.Readable)
        {
            State = TraceViewState.Unreadable;
            Message = info.UnavailableReason ?? "이 DB 에서는 트레이스 파일을 SQL 로 읽을 수 없습니다.";
            SourceText = "v$diag_info 로 경로만 읽음";
            StatusText = ServerPath;
            return;
        }

        Message = "트레이스 본문을 읽는 중…";
        var tuning = ResolveTuning();
        var lines = await Task.Run(() => tuning.ReadTraceLinesAsync(session, info, ct), CancellationToken.None).ConfigureAwait(true);
        if (gen != _generation)
        {
            return;
        }

        var entry = new TraceFileEntry(info, Head(lines, MaxReadBytes), lines, DateTime.Now) { Is10053 = marker is not null, Marker = marker };
        var existing = Files.FirstOrDefault(f => string.Equals(f.Info.Path, info.Path, StringComparison.Ordinal));
        if (existing is not null)
        {
            Files[Files.IndexOf(existing)] = entry;
        }
        else
        {
            Files.Insert(0, entry);
        }

        SourceText = "v$diag_trace_file_contents · 12.2+";
        RefreshFileRows();
        SelectedFile = entry;
        if (lines.Count == 0)
        {
            State = TraceViewState.NoTrace;
            Message = "이 세션에 트레이스 파일이 없습니다 — 세션 모니터 「트레이스 켜기」 또는 「옵티마이저 트레이스(10053)」로 남기세요.";
            return;
        }

        State = TraceViewState.Ready;
        if (marker is not null)
        {
            ViewIndex = 1; // 10053 파일은 요약할 문장이 없어 본문을 먼저 보인다
            ScrollTarget = VisibleLines.FirstOrDefault(l => l.Text.Contains(marker, StringComparison.Ordinal))
                ?? VisibleLines.FirstOrDefault(l => l.Text.Contains("10053", StringComparison.Ordinal));
        }
    }

    /// <summary>앞에서부터 <paramref name="maxBytes"/> 를 넘지 않는 줄까지(적어도 한 줄).</summary>
    internal static IReadOnlyList<string> Head(IReadOnlyList<string> lines, long maxBytes)
    {
        long total = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            total += lines[i].Length + 1;
            if (total > maxBytes && i > 0)
            {
                return [.. lines.Take(i)];
            }
        }

        return lines;
    }

    partial void OnSelectedFileChanged(TraceFileEntry? value)
    {
        IsTruncated = value?.IsTruncated == true;
        _summary = value is { Lines.Count: > 0 } ? SafeParse(value.Lines) : new TraceSummary([], []);
        _allLines = value is null ? [] : [.. value.Lines.Select((l, i) => new TraceLineItem(i + 1, l.TrimEnd('\r', '\n')))];
        RebuildSummary();
        RebuildBody();
        if (value is not null && State is TraceViewState.NoTrace or TraceViewState.Ready)
        {
            State = value.Lines.Count == 0 ? TraceViewState.NoTrace : TraceViewState.Ready;
        }
    }

    partial void OnSortIndexChanged(int value) => RebuildSummary();

    partial void OnSelectedStatementChanged(TraceStatementItem? value) => RebuildCalls();

    partial void OnFindTextChanged(string value) => RebuildBody();

    partial void OnShowWaitLinesChanged(bool value) => RebuildBody();

    partial void OnShowStatLinesChanged(bool value) => RebuildBody();

    private void RebuildSummary()
    {
        var keep = SelectedStatement?.Statement;
        Statements.Clear();
        var total = _summary.Statements.Sum(s => s.ElapsedMicros);
        var top = _summary.Statements.Count == 0 ? null : _summary.Statements.MaxBy(s => s.ElapsedMicros);
        var n = 0;
        foreach (var s in Sort(_summary.Statements, SortKey))
        {
            Statements.Add(new TraceStatementItem(++n, s, total, ReferenceEquals(s, top) && s.ElapsedMicros > 0));
        }

        Waits.Clear();
        var w = 0;
        foreach (var wait in _summary.Waits.OrderByDescending(x => x.ElapsedMicros))
        {
            Waits.Add(new TraceWaitItem(++w, wait));
        }

        var busyWait = Waits.Where(x => !x.IsIdle).Sum(x => x.Wait.ElapsedMicros);
        WaitTotalText = TraceStatementItem.Seconds(busyWait);
        OnPropertyChanged(nameof(WaitTotalText));
        OnPropertyChanged(nameof(HasStatements));
        SelectedStatement = Statements.FirstOrDefault(s => ReferenceEquals(s.Statement, keep)) ?? Statements.FirstOrDefault();
        RebuildCalls();
        if (SelectedFile is { } file)
        {
            StatusText = $"{file.FileName} · 문장 {Statements.Count} · 경과 합계 {TraceStatementItem.Seconds(total)} s · 대기 합계 {WaitTotalText} s (idle 제외)"
                + (file.IsTruncated ? $" · 앞 {TraceFileEntry.Size(file.ShownBytes)} 만" : string.Empty);
        }
    }

    internal static IEnumerable<TraceStatement> Sort(IEnumerable<TraceStatement> items, TraceSortKey key) => key switch
    {
        TraceSortKey.Cpu => items.OrderByDescending(s => s.CpuMicros),
        TraceSortKey.Disk => items.OrderByDescending(s => s.Disk),
        TraceSortKey.Query => items.OrderByDescending(s => s.Query),
        TraceSortKey.Current => items.OrderByDescending(s => s.Current),
        TraceSortKey.Rows => items.OrderByDescending(s => s.Rows),
        TraceSortKey.Executions => items.OrderByDescending(s => s.ExecuteCount),
        _ => items.OrderByDescending(s => s.ElapsedMicros),
    };

    /// <summary>
    /// 고른 문장의 호출 표. 계약(TraceStatement)은 호출별 횟수와 문장 전체 합계만 주므로 cpu·elapsed·블록·행은 합계 줄에만 쓴다.
    /// </summary>
    private void RebuildCalls()
    {
        if (SelectedStatement?.Statement is not { } s)
        {
            CallRows = [];
            CallCaption = string.Empty;
            return;
        }

        string N(long v) => TraceStatementItem.N(v);
        CallRows =
        [
            ["Parse", N(s.ParseCount), string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty],
            ["Execute", N(s.ExecuteCount), string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty],
            ["Fetch", N(s.FetchCount), string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty],
            ["합계", N(s.ParseCount + s.ExecuteCount + s.FetchCount), TraceStatementItem.Seconds(s.CpuMicros), TraceStatementItem.Seconds(s.ElapsedMicros),
                N(s.Disk), N(s.Query), N(s.Current), N(s.Rows)],
        ];
        CallCaption = s.SqlId ?? $"문장 {SelectedStatement.Number}";
    }

    private void RebuildBody()
    {
        var find = FindText?.Trim() ?? string.Empty;
        VisibleLines.Clear();
        foreach (var line in _allLines)
        {
            if (!ShowWaitLines && line.Kind is TraceLineKind.Wait or TraceLineKind.IdleWait)
            {
                continue;
            }

            if (!ShowStatLines && line.Kind == TraceLineKind.Stat)
            {
                continue;
            }

            if (find.Length > 0 && !line.Text.Contains(find, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            VisibleLines.Add(line);
        }

        var file = SelectedFile;
        BodyInfoText = file is null
            ? string.Empty
            : $"{TraceFileEntry.Size(file.SizeBytes)} · {TraceStatementItem.N(file.AllLines.Count)} 줄"
                + (file.IsTruncated ? $" 중 앞 {TraceStatementItem.N(file.Lines.Count)} 줄" : string.Empty)
                + (VisibleLines.Count != _allLines.Count ? $" · 보이는 줄 {TraceStatementItem.N(VisibleLines.Count)}" : string.Empty);
    }

    private void RefreshFileRows() =>
        FileRows = [.. Files.Select(f => (IReadOnlyList<object?>)[f.FileName, TraceFileEntry.Size(f.SizeBytes), f.ReadAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture)])];

    private void Fail(Exception ex)
    {
        if (ex is UnauthorizedAccessException || IsPrivilegeError(ex))
        {
            State = TraceViewState.NoPrivilege;
            Message = "트레이스를 읽을 권한이 없습니다(v$diag_info·v$diag_trace_file_contents SELECT 필요). DBA 에게 아래 권한을 부탁하세요.";
            return;
        }

        if (ex is NotSupportedException)
        {
            State = TraceViewState.Error;
            Message = "이 접속에서는 트레이스 조회를 지원하지 않습니다.";
            return;
        }

        State = TraceViewState.Error;
        Message = ex is OperationCanceledException ? "트레이스 읽기를 취소했습니다." : "트레이스를 읽지 못했습니다: " + FirstLine(ex);
    }

    private static bool IsPrivilegeError(Exception ex) =>
        ex.Message.Contains("ORA-00942", StringComparison.Ordinal) || ex.Message.Contains("ORA-01031", StringComparison.Ordinal);

    private static string FirstLine(Exception ex) => (ex.Message ?? string.Empty).Split('\n', 2)[0].Trim();

    private TraceSummary SafeParse(IReadOnlyList<string> lines)
    {
        try
        {
            return Parser(lines);
        }
        catch (Exception)
        {
            return new TraceSummary([], []);
        }
    }

    private ITuningService ResolveTuning() =>
        _tuning ??= _services?.GetService(typeof(ITuningService)) as ITuningService
            ?? throw new NotSupportedException("튜닝 서비스가 없습니다.");
}
