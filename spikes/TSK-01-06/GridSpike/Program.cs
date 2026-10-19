using System.Diagnostics;
using System.Runtime;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace GridSpike;

/// <summary>
/// 사용법: GridSpike.exe --mode window|offscreen|mem [--render angle|software] [--out 파일] [--seconds 10]
///   window    : 실제 창(1600x900)에서 자동 스크롤, RequestAnimationFrame 간격 = 프레임 시간
///   offscreen : RenderTargetBitmap(CPU Skia) 로 기록+래스터 시간을 vsync 없이 측정
///   mem       : 페이지 캐시 행당 메모리, FormattedText 캐시 셀당 메모리
/// </summary>
public static class Program
{
    public const int Rows = 1_000_000, Cols = 50, MaxPages = 400, W = 1600, H = 900;
    static Dictionary<string, string> _args = new();
    static readonly StringBuilder Log = new();

    public static string Arg(string k, string d) => _args.TryGetValue(k, out var v) ? v : d;

    [STAThread]
    public static int Main(string[] argv)
    {
        for (int i = 0; i + 1 < argv.Length; i += 2) _args[argv[i].TrimStart('-')] = argv[i + 1];
        var mode = Arg("mode", "window");
        var render = Arg("render", "angle");
        VirtualGrid.TextMode = Arg("text", "layout");
        var builder = AppBuilder.Configure<App>().UsePlatformDetect()
            .With(new Win32PlatformOptions
            {
                RenderingMode = render == "software" ? [Win32RenderingMode.Software] : [Win32RenderingMode.AngleEgl],
            });
        Line($"# GridSpike mode={mode} grid={Arg("grid", "custom")} render={render} text={VirtualGrid.TextMode} rows={Rows:N0} cols={Cols} pageSize={SyntheticResultSet.PageSize} maxPages={MaxPages}");
        Line($"# {Environment.OSVersion} cores={Environment.ProcessorCount} .NET {Environment.Version} gc={(GCSettings.IsServerGC ? "server" : "workstation")}");
        try
        {
            if (mode == "window") builder.StartWithClassicDesktopLifetime(argv);
            else
            {
                builder.SetupWithoutStarting();
                if (mode == "mem") MemBench();
                else Offscreen();
            }
        }
        catch (Exception ex) { Line("ERROR " + ex); }
        File.WriteAllText(Arg("out", $"result-{mode}-{Arg("grid", "custom")}-{render}-{VirtualGrid.TextMode}.txt"), Log.ToString());
        return 0;
    }

    public static void Line(string s) { Log.AppendLine(s); Console.WriteLine(s); }

    // ---------- 시나리오 ----------
    public record Phase(string Name, double Seconds, int Frames, Func<int, VirtualGrid, (double x, double y)> Step);

    public static Phase[] Phases(double sec)
    {
        var rnd = new Random(42);
        int fr = (int)(sec * 60);
        return
        [
            new("warmup", 2, 120, (i, g) => (0, i * 66.0)),
            new("V-smooth 3행/프레임", sec, fr, (i, g) => (0, 200_000 * VirtualGrid.RowHeight + i * 66.0)),
            new("V-fast 60행/프레임", sec, fr, (i, g) => (0, 300_000 * VirtualGrid.RowHeight + i * 60 * VirtualGrid.RowHeight)),
            new("H-scroll 40px/프레임", sec, fr, (i, g) =>
            {
                double span = g.MaxOffsetX, p = (i * 40.0) % (2 * span);
                return (p <= span ? p : 2 * span - p, 500_000 * VirtualGrid.RowHeight);
            }),
            new("Jump 무작위 행", sec, fr, (i, g) => (rnd.NextDouble() * g.ExtentWidth * 0.7, rnd.Next(Rows) * VirtualGrid.RowHeight)),
        ];
    }

    public static string Stats(IList<double> xs)
    {
        if (xs.Count == 0) return "n=0";
        var s = xs.OrderBy(v => v).ToArray();
        double P(double q) => s[Math.Min(s.Length - 1, (int)Math.Ceiling(q * s.Length) - 1)];
        return $"n={s.Length} mean={s.Average():F2} p50={P(.5):F2} p95={P(.95):F2} p99={P(.99):F2} max={s[^1]:F2}";
    }

    public static string MemNow()
    {
        var p = Process.GetCurrentProcess();
        p.Refresh();
        return $"managed={GC.GetTotalMemory(false) / 1048576.0:F1}MB workingSet={p.WorkingSet64 / 1048576.0:F1}MB private={p.PrivateMemorySize64 / 1048576.0:F1}MB gen0/1/2={GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}";
    }

    // ---------- offscreen ----------
    static void Offscreen()
    {
        var rs = new SyntheticResultSet(Rows, Cols);
        var grid = new VirtualGrid(rs, MaxPages);
        grid.Measure(new Size(W, H));
        grid.Arrange(new Rect(0, 0, W, H));
        using var rtb = new RenderTargetBitmap(new PixelSize(W, H), new Vector(96, 96));
        Line($"start {MemNow()}");
        foreach (var ph in Phases(double.Parse(Arg("seconds", "10"))))
        {
            var total = new List<double>(ph.Frames);
            grid.RenderMs.Clear();
            long created0 = grid.TextCreated, fetch0 = grid.Cache.Fetches; grid.PageMsSum = grid.TextMsSum = 0;
            for (int i = 0; i < ph.Frames; i++)
            {
                var (x, y) = ph.Step(i, grid);
                grid.ScrollTo(x, y);
                long t = Stopwatch.GetTimestamp();
                rtb.Render(grid);
                total.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
            }
            if (ph.Name == "warmup") continue;
            double mean = total.Average();
            Line($"[{ph.Name}] frame(record+raster) ms: {Stats(total)} => 이론 fps={1000 / mean:F0}");
            Line($"[{ph.Name}] Render(record) ms: {Stats(grid.RenderMs)} | 프레임당 page={grid.PageMsSum / ph.Frames:F2} text={grid.TextMsSum / ph.Frames:F2}");
            Line($"[{ph.Name}] cells/frame={grid.LastVisibleCells} newText/frame={(grid.TextCreated - created0) / (double)ph.Frames:F1} pageFetch={grid.Cache.Fetches - fetch0} cachedPages={grid.Cache.Count}");
        }
        if (_args.TryGetValue("png", out var png)) { grid.ScrollTo(300, 123_456 * VirtualGrid.RowHeight); rtb.Render(grid); rtb.Save(png); }
        Line($"end {MemNow()}");
    }

    // ---------- mem ----------
    static void MemBench()
    {
        static long Settle() { for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); } return GC.GetTotalMemory(true); }
        var rs = new SyntheticResultSet(Rows, Cols);
        var cache = new PageCache(rs, 10_000);
        cache.Get(0); cache.Clear();
        foreach (int pages in new[] { 1, 20, 200, 400 })
        {
            cache.Clear();
            long before = Settle();
            var p0 = Process.GetCurrentProcess(); p0.Refresh(); long priv0 = p0.PrivateMemorySize64;
            for (int i = 0; i < pages; i++) cache.Get(i * SyntheticResultSet.PageSize);
            long after = Settle();
            var p1 = Process.GetCurrentProcess(); p1.Refresh();
            double rows = pages * (double)SyntheticResultSet.PageSize;
            Line($"[page cache] pages={pages} rows={rows:N0} managed+={(after - before) / 1048576.0:F2}MB perRow={(after - before) / rows:F0}B perPage={(after - before) / (double)pages / 1024:F0}KB privateDelta={(p1.PrivateMemorySize64 - priv0) / 1048576.0:F1}MB");
        }
        cache.Clear();
        // 열 종류별 셀 비용(페이지 1개 기준)
        {
            var pg = rs.Fetch(1000);
            long b = Settle();
            var keep = new List<Page>();
            for (int i = 0; i < 40; i++) keep.Add(rs.Fetch(1000 + i));
            long a = Settle();
            Line($"[page] 40 pages direct managed per cell={(a - b) / (40.0 * 500 * Cols):F1}B (50열 혼합: long/double/DateTime 8B, Text 8B 참조+문자열, Code 공유 문자열)");
            GC.KeepAlive(keep); GC.KeepAlive(pg);
        }
        // 셀 텍스트 캐시 항목 1개당 비용(방식별). 20,000셀 = 500행×40열.
        foreach (var tm in new[] { "formatted", "layout", "glyph" })
        {
            VirtualGrid.TextMode = tm;
            var grid = new VirtualGrid(rs, 4);
            var pg = rs.Fetch(1500);
            var strs = new List<string>(20000);
            for (int i = 0; i < 500; i++)
                for (int c = 0; c < 40; c++)
                    strs.Add((c % 5) switch
                    {
                        0 => ((long[])pg.Columns[c])[i].ToString(),
                        3 => ((double[])pg.Columns[c])[i].ToString("N2"),
                        2 => ((DateTime[])pg.Columns[c])[i].ToString("yyyy-MM-dd HH:mm:ss"),
                        _ => ((string[])pg.Columns[c])[i],
                    });
            grid.Text("warm").Dispose();
            var texts = new List<CellText>(20000);
            long b = Settle();
            var p0 = Process.GetCurrentProcess(); p0.Refresh(); long priv0 = p0.PrivateMemorySize64;
            var t = Stopwatch.GetTimestamp();
            foreach (var s in strs) texts.Add(grid.Text(s));
            double ms = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
            long a = Settle();
            var p1 = Process.GetCurrentProcess(); p1.Refresh();
            Line($"[text cache {tm}] n={texts.Count} create={ms / texts.Count * 1000:F1}us/cell managed/cell={(a - b) / (double)texts.Count:F0}B privateDelta={(p1.PrivateMemorySize64 - priv0) / 1048576.0:F1}MB (문자열 자체 제외)");
            foreach (var x in texts) x.Dispose();
            GC.KeepAlive(strs); GC.KeepAlive(grid);
        }
        Line($"end {MemNow()}");
    }
}

public sealed class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        if (Program.Arg("grid", "custom") == "tdg")
            Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://GridSpike/")) { Source = new Uri("avares://Avalonia.Controls.TreeDataGrid/Themes/Fluent.axaml") });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d)
        {
            var rs = new SyntheticResultSet(Program.Rows, Program.Cols);
            if (Program.Arg("grid", "custom") == "tdg")
            {
                var cache = new PageCache(rs, Program.MaxPages);
                var tdg = TdgRunner.Create(rs, cache);
                var tw = new Window { Width = Program.W, Height = Program.H, Title = "GridSpike TDG", Content = tdg, WindowStartupLocation = WindowStartupLocation.CenterScreen };
                d.MainWindow = tw;
                tw.Opened += (_, _) => DispatcherTimer.RunOnce(() => new TdgRunner(tw, tdg, cache, d).Start(), TimeSpan.FromMilliseconds(1500));
                base.OnFrameworkInitializationCompleted();
                return;
            }
            var grid = new VirtualGrid(rs, Program.MaxPages);
            var win = new Window
            {
                Width = Program.W, Height = Program.H, Title = "GridSpike",
                Content = grid, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            };
            d.MainWindow = win;
            win.Opened += (_, _) => DispatcherTimer.RunOnce(() => new WindowRunner(win, grid, d).Start(), TimeSpan.FromMilliseconds(800));
        }
        base.OnFrameworkInitializationCompleted();
    }
}

/// <summary>RequestAnimationFrame 마다 스크롤을 진행하고 콜백 간격을 프레임 시간으로 기록한다.</summary>
sealed class WindowRunner(Window win, VirtualGrid grid, IClassicDesktopStyleApplicationLifetime life)
{
    Program.Phase[] _phases = [];
    int _pi, _frame;
    long _last;
    readonly List<double> _intervals = new(2048);
    long _created0, _fetch0;
    readonly Stopwatch _phaseClock = new();

    public void Start()
    {
        _phases = Program.Phases(double.Parse(Program.Arg("seconds", "10")));
        Program.Line($"window client={win.ClientSize} scaling={win.RenderScaling} start {Program.MemNow()}");
        BeginPhase();
        win.RequestAnimationFrame(Tick);
    }

    void BeginPhase()
    {
        _frame = 0; _last = 0; _intervals.Clear(); grid.RenderMs.Clear();
        _created0 = grid.TextCreated; _fetch0 = grid.Cache.Fetches; grid.PageMsSum = grid.TextMsSum = 0;
        _phaseClock.Restart();
    }

    void Tick(TimeSpan _)
    {
        long now = Stopwatch.GetTimestamp();
        if (_last != 0) _intervals.Add(Stopwatch.GetElapsedTime(_last, now).TotalMilliseconds);
        _last = now;
        var ph = _phases[_pi];
        if (_phaseClock.Elapsed.TotalSeconds >= ph.Seconds)
        {
            if (ph.Name != "warmup")
            {
                int frames = _intervals.Count;
                double fps = frames / _phaseClock.Elapsed.TotalSeconds;
                Program.Line($"[{ph.Name}] frame interval ms: {Program.Stats(_intervals)} => 실측 fps={fps:F1} (>=17.5ms 프레임 {_intervals.Count(v => v >= 17.5) * 100.0 / Math.Max(1, frames):F1}%)");
                Program.Line($"[{ph.Name}] Render(record, UI 스레드) ms: {Program.Stats(grid.RenderMs)} | 프레임당 page={grid.PageMsSum / Math.Max(1, _frame):F2} text={grid.TextMsSum / Math.Max(1, _frame):F2}");
                Program.Line($"[{ph.Name}] cells/frame={grid.LastVisibleCells} newText/frame={(grid.TextCreated - _created0) / (double)Math.Max(1, _frame):F1} pageFetch={grid.Cache.Fetches - _fetch0} cachedPages={grid.Cache.Count} {Program.MemNow()}");
            }
            if (++_pi >= _phases.Length) { life.Shutdown(); return; }
            BeginPhase();
            ph = _phases[_pi];
        }
        var (x, y) = ph.Step(_frame++, grid);
        grid.ScrollTo(x, y);
        win.RequestAnimationFrame(Tick);
    }
}
