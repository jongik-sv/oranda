using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.Tuning.Stats;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Tuning;
using static Oranda.App.Features.Tuning.Stats.TuningText;

namespace Oranda.App.Features.SchemaBrowser;

/// <summary>열 통계 요약의 한 줄(이름 | 값).</summary>
public sealed record ColumnStatLine(string Label, string Value);

/// <summary>
/// 스키마 브라우저 속성 창의 열 통계 요약. 트리에서 열 노드를 고를 때만 읽는다(트리를 펼칠 때는 아무것도 읽지 않는다).
/// 열 통계(<c>tuning.column_stats</c>)는 테이블당 한 번 읽어 테이블 단위로 두고, 히스토그램(<c>tuning.histogram</c>)은 그 열을 처음 고를 때 열당 한 번 읽는다.
/// 읽기는 세션이 바쁘면 줄을 서지 않고(<see cref="SessionWait.Brief"/>) 실패·권한 없음·취소는 화면을 막지 않고 안내 한 줄로 끝낸다(실패는 두지 않아 다음에 다시 읽는다).
/// 열을 빠르게 바꿔 고르면 앞 요청은 취소한다. 세션이 바뀌면 모아 둔 것을 버린다.
/// </summary>
public sealed partial class SchemaColumnStatsViewModel : ObservableObject, IDisposable
{
    /// <summary>바쁜 세션에서 잠금을 기다리는 한도(넘으면 읽지 않고 안내만).</summary>
    internal static readonly TimeSpan BusyWait = TimeSpan.FromMilliseconds(300);

    private readonly Func<ITuningService?> _tuning;
    private readonly Func<ISession?> _session;
    private readonly Dictionary<(string Owner, string Table), IReadOnlyList<ColumnStatsRow>> _tables = [];
    private readonly Dictionary<(string Owner, string Table, string Column), IReadOnlyList<HistogramBucket>> _histograms = [];
    private ISession? _cachedFor;
    private CancellationTokenSource? _cts;
    private int _generation;

    public SchemaColumnStatsViewModel(Func<ITuningService?> tuning, Func<ISession?> session)
    {
        ArgumentNullException.ThrowIfNull(tuning);
        ArgumentNullException.ThrowIfNull(session);
        _tuning = tuning;
        _session = session;
    }

    /// <summary>고른 열을 읽기 전에 기다리는 시간(빠르게 옮겨 다닐 때 조회가 쌓이지 않게, 시험은 0).</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>열을 골라 요약을 보이는 중인가(속성 탭 대신 이 요약을 보인다).</summary>
    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private string _title = string.Empty;

    /// <summary>안내 한 줄(읽는 중·통계 없음·읽지 못함). 비면 숨긴다.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string _message = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<ColumnStatLine> _lines = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHistogram))]
    private IReadOnlyList<HistogramBarItem> _histogramBars = [];

    /// <summary>히스토그램 머리(종류 · 버킷 수) 또는 히스토그램 안내.</summary>
    [ObservableProperty]
    private string _histogramCaption = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>마지막으로 보인 테이블·열.</summary>
    private (DbObjectRef Table, string Column)? _shown;

    public bool HasMessage => Message.Length > 0;

    public bool HasHistogram => HistogramBars.Count > 0;

    /// <summary>시험용: 지금까지 읽은 열 통계(테이블)·히스토그램(열) 횟수.</summary>
    internal int TableReads { get; private set; }

    internal int HistogramReads { get; private set; }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>요약을 감춘다(열이 아닌 노드를 골랐다). 읽던 것은 취소한다.</summary>
    public void Hide()
    {
        _cts?.Cancel();
        ++_generation;
        IsVisible = false;
        IsBusy = false;
    }

    /// <summary>모아 둔 열 통계·히스토그램을 버린다(세션이 바뀜·새로 고침).</summary>
    public void ClearCache()
    {
        _tables.Clear();
        _histograms.Clear();
        _cachedFor = null;
    }

    /// <summary>한 테이블의 열 통계·히스토그램만 버린다(통계를 새로 모은 뒤, SG-1). 다음에 그 테이블 열을 고르면 다시 읽는다.</summary>
    public void Invalidate(string owner, string table)
    {
        _tables.Remove((owner, table));
        foreach (var key in _histograms.Keys.Where(k => k.Owner == owner && k.Table == table).ToList())
        {
            _histograms.Remove(key);
        }
    }

    /// <summary>
    /// 한 테이블의 캐시를 버리고(<see cref="Invalidate"/>), 지금 그 테이블의 열 요약을 보이고 있으면 다시 읽는다(통계 수집 뒤).
    /// </summary>
    public Task RefreshAsync(string owner, string table)
    {
        Invalidate(owner, table);
        return IsVisible && _shown is { } shown
            && string.Equals(shown.Table.Owner, owner, StringComparison.Ordinal) && string.Equals(shown.Table.Name, table, StringComparison.Ordinal)
            ? ShowAsync(shown.Table, shown.Column)
            : Task.CompletedTask;
    }

    /// <summary><paramref name="table"/> 의 <paramref name="column"/> 통계 요약을 보인다. 앞서 부른 것은 취소한다.</summary>
    public async Task ShowAsync(DbObjectRef table, string column)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentException.ThrowIfNullOrEmpty(column);
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var gen = ++_generation;
        _shown = (table, column);

        IsVisible = true;
        Title = T($"열 통계 · {column}", $"Column statistics · {column}");
        Lines = [];
        HistogramBars = [];
        HistogramCaption = string.Empty;
        Message = string.Empty;

        if (_tuning() is not { } tuning || _session() is not { } session)
        {
            Message = T("접속하면 열 통계를 볼 수 있습니다.", "Connect to see column statistics.");
            return;
        }

        if (!ReferenceEquals(_cachedFor, session))
        {
            ClearCache();
            _cachedFor = session;
        }

        var key = (table.Owner, table.Name);
        try
        {
            if (!_tables.TryGetValue(key, out var rows))
            {
                Message = T("통계를 읽는 중…", "Reading statistics…");
                if (Delay > TimeSpan.Zero)
                {
                    await Task.Delay(Delay, ct).ConfigureAwait(true);
                }

                IsBusy = true;
                TableReads++;
                using (SessionWait.Brief(BusyWait))
                {
                    rows = await tuning.GetColumnStatsAsync(session, table.Owner, table.Name, ct).ConfigureAwait(true);
                }

                ct.ThrowIfCancellationRequested();
                _tables[key] = rows;
            }

            if (gen != _generation)
            {
                return;
            }

            var row = rows.FirstOrDefault(r => string.Equals(r.ColumnName, column, StringComparison.Ordinal));
            if (row is null || (row.LastAnalyzed is null && row.NumDistinct is null && row.NumNulls is null))
            {
                Message = T("통계 없음 — 통계 수집 필요", "No statistics — gather statistics");
                return;
            }

            var item = new ColumnStatsItem(row);
            Message = string.Empty;
            Lines = Describe(item);
            if (!item.HasHistogram)
            {
                HistogramCaption = T("히스토그램 없음", "No histogram");
                return;
            }

            HistogramCaption = Caption(item);
            await ShowHistogramAsync(tuning, session, table, column, gen, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            if (gen == _generation)
            {
                // 바쁜 세션·권한 없음(뷰 없음)·그 밖 실패: 화면을 막지 않고 한 줄만. 실패는 두지 않아 다음에 다시 읽는다.
                Message = T("통계를 읽지 못했습니다", "Could not read statistics");
            }
        }
        finally
        {
            if (gen == _generation)
            {
                IsBusy = false;
            }
        }
    }

    private async Task ShowHistogramAsync(ITuningService tuning, ISession session, DbObjectRef table, string column, int gen, CancellationToken ct)
    {
        var key = (table.Owner, table.Name, column);
        if (!_histograms.TryGetValue(key, out var buckets))
        {
            IsBusy = true;
            HistogramReads++;
            try
            {
                using (SessionWait.Brief(BusyWait))
                {
                    buckets = await tuning.GetHistogramAsync(session, table.Owner, table.Name, column, ct).ConfigureAwait(true);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (gen == _generation)
                {
                    HistogramCaption += T(" · 히스토그램을 읽지 못했습니다", " · could not read the histogram");
                }

                return;
            }

            ct.ThrowIfCancellationRequested();
            _histograms[key] = buckets;
        }

        if (gen == _generation)
        {
            HistogramBars = HistogramBarItem.From(buckets);
        }
    }

    private static string Caption(ColumnStatsItem item) =>
        string.Create(CultureInfo.InvariantCulture, $"{item.Row.Histogram} · {StatsFormat.Count(item.Row.NumBuckets)} {T("버킷", "buckets")}");

    /// <summary>요약 줄: 고유 값·NULL·최솟값·최댓값·히스토그램·버킷·마지막 수집. 값이 없으면 「-」.</summary>
    internal static IReadOnlyList<ColumnStatLine> Describe(ColumnStatsItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var r = item.Row;
        return
        [
            new(T("고유 값", "Distinct"), Dash(StatsFormat.Count(r.NumDistinct))),
            new("NULL", Dash(StatsFormat.Count(r.NumNulls))),
            new(T("최솟값", "Low"), Dash(r.LowValue)),
            new(T("최댓값", "High"), Dash(r.HighValue)),
            new(T("히스토그램", "Histogram"), item.HistogramText),
            new(T("버킷", "Buckets"), Dash(StatsFormat.Count(r.NumBuckets))),
            new(T("마지막 수집", "Last analyzed"), Dash(StatsFormat.Date(r.LastAnalyzed))),
        ];
    }

    private static string Dash(string? text) => string.IsNullOrEmpty(text) ? "-" : text;
}
