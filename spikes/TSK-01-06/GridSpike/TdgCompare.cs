using System.Collections;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Threading;

namespace GridSpike;

/// <summary>TreeDataGrid 행 모델: 인덱스로 요청될 때 만들고, 값은 같은 페이지 캐시에서 읽는다.</summary>
public sealed class RowProxy(int row, PageCache cache, SyntheticResultSet rs)
{
    public string Get(int c)
    {
        var p = cache.Get(row);
        int i = row - p.FirstRow;
        return rs.Kinds[c] switch
        {
            ColKind.Int64 => ((long[])p.Columns[c])[i].ToString(CultureInfo.InvariantCulture),
            ColKind.Decimal => ((double[])p.Columns[c])[i].ToString("N2", CultureInfo.InvariantCulture),
            ColKind.Date => ((DateTime[])p.Columns[c])[i].ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            _ => ((string[])p.Columns[c])[i],
        };
    }
}

/// <summary>100만 행 가상 목록(행 객체를 미리 만들지 않는다).</summary>
public sealed class VirtualRows(SyntheticResultSet rs, PageCache cache) : IReadOnlyList<RowProxy>, IList
{
    public RowProxy this[int index] => new(index, cache, rs);
    object? IList.this[int index] { get => this[index]; set => throw new NotSupportedException(); }
    public int Count => rs.RowCount;
    public IEnumerator<RowProxy> GetEnumerator() { for (int i = 0; i < Count; i++) yield return this[i]; }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public bool IsFixedSize => true;
    public bool IsReadOnly => true;
    public bool IsSynchronized => false;
    public object SyncRoot => this;
    public int Add(object? value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public bool Contains(object? value) => false;
    public int IndexOf(object? value) => -1;
    public void Insert(int index, object? value) => throw new NotSupportedException();
    public void Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
    public void CopyTo(Array array, int index) => throw new NotSupportedException();
}

/// <summary>TreeDataGrid.Avalonia(MIT 포크) 로 같은 시나리오를 돌린다.</summary>
public sealed class TdgRunner(Window win, TreeDataGrid tdg, PageCache cache, IClassicDesktopStyleApplicationLifetime life)
{
    public static TreeDataGrid Create(SyntheticResultSet rs, PageCache cache)
    {
        var src = new FlatTreeDataGridSource<RowProxy>(new VirtualRows(rs, cache));
        for (int c = 0; c < rs.Kinds.Length; c++)
        {
            int cc = c;
            double w = rs.Kinds[c] switch { ColKind.Text => 240, ColKind.Date => 150, ColKind.Code => 70, _ => 100 };
            src.Columns.Add(new TextColumn<RowProxy, string>(rs.Names[c], r => r.Get(cc), new GridLength(w)));
        }
        return new TreeDataGrid { Source = src, CanUserResizeColumns = false, CanUserSortColumns = false };
    }

    int _pi, _frame;
    long _last;
    readonly List<double> _intervals = new(2048);
    readonly Stopwatch _clock = new();
    readonly Random _rnd = new(42);
    (string name, double sec)[] _phases = [];
    ScrollViewer? _sv;

    public void Start()
    {
        double sec = double.Parse(Program.Arg("seconds", "10"));
        _phases = [("warmup", 2), ("V-smooth 3행/프레임", sec), ("V-fast 60행/프레임", sec), ("H-scroll 40px/프레임", sec), ("Jump 무작위 행", sec)];
        _sv = tdg.Scroll as ScrollViewer;
        Program.Line($"tdg scroll={_sv?.GetType().Name ?? tdg.Scroll?.GetType().Name} extent={tdg.Scroll?.Extent} viewport={tdg.Scroll?.Viewport} start {Program.MemNow()}");
        _clock.Restart();
        win.RequestAnimationFrame(Tick);
    }

    void Tick(TimeSpan _)
    {
        long now = Stopwatch.GetTimestamp();
        if (_last != 0) _intervals.Add(Stopwatch.GetElapsedTime(_last, now).TotalMilliseconds);
        _last = now;
        var (name, sec) = _phases[_pi];
        if (_clock.Elapsed.TotalSeconds >= sec)
        {
            if (name != "warmup")
                Program.Line($"[TDG {name}] frame interval ms: {Program.Stats(_intervals)} => 실측 fps={_intervals.Count / _clock.Elapsed.TotalSeconds:F1} (>=17.5ms 프레임 {_intervals.Count(v => v >= 17.5) * 100.0 / Math.Max(1, _intervals.Count):F1}%) cachedPages={cache.Count} {Program.MemNow()}");
            if (++_pi >= _phases.Length) { life.Shutdown(); return; }
            _intervals.Clear(); _frame = 0; _last = 0; _clock.Restart();
            name = _phases[_pi].name;
        }
        var s = tdg.Scroll!;
        double maxY = Math.Max(0, s.Extent.Height - s.Viewport.Height), maxX = Math.Max(0, s.Extent.Width - s.Viewport.Width);
        int i = _frame++;
        Vector o = name switch
        {
            "V-smooth 3행/프레임" => new(0, 200_000 * 22 + i * 66.0),
            "V-fast 60행/프레임" => new(0, 300_000 * 22 + i * 60 * 22.0),
            "H-scroll 40px/프레임" => new((i * 40.0) % (2 * maxX) is var p && p <= maxX ? p : 2 * maxX - (i * 40.0) % (2 * maxX), 500_000 * 22),
            "Jump 무작위 행" => new(_rnd.NextDouble() * maxX, _rnd.Next(Program.Rows) * 22.0),
            _ => new(0, i * 66.0),
        };
        s.Offset = new Vector(Math.Clamp(o.X, 0, maxX), Math.Clamp(o.Y, 0, maxY));
        win.RequestAnimationFrame(Tick);
    }
}
