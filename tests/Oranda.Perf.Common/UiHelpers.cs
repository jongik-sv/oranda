using System.Globalization;
using System.Text;

namespace Oranda.Perf.Common;

/// <summary>Oranda.Perf.Ui 명령줄.</summary>
/// <param name="Scenario">format, typing, diagnostics, completion, firstrow, cancel, memory, grid.</param>
/// <param name="Out">결과 폴더.</param>
/// <param name="Samples">표본 수(시나리오마다 뜻이 조금 다르다).</param>
/// <param name="Warmup">앞에서 버리는 표본 수.</param>
/// <param name="Lines">편집기 문서 줄 수 / 포맷 입력 줄 수.</param>
/// <param name="Rows">결과 행 수(grid, memory, firstrow).</param>
/// <param name="Seconds">시나리오 한 구간의 길이(초).</param>
/// <param name="Label">결과 파일 이름에 붙일 꼬리표(여러 번 돌릴 때).</param>
public sealed record UiOptions(string Scenario, string Out, int Samples, int Warmup, int Lines, int Rows, int Seconds, string Label)
{
    public static readonly string[] Scenarios = ["format", "typing", "diagnostics", "completion", "firstrow", "cancel", "memory", "grid"];

    public static UiOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0 || args[0].StartsWith('-'))
        {
            throw new ArgumentException("첫 인수는 시나리오 이름이어야 한다: " + string.Join(", ", Scenarios));
        }

        var scenario = args[0];
        if (!Scenarios.Contains(scenario, StringComparer.Ordinal))
        {
            throw new ArgumentException($"모르는 시나리오: {scenario}. 쓸 수 있는 것: {string.Join(", ", Scenarios)}");
        }

        string outDir = "perf-results", label = string.Empty;
        int samples = 0, warmup = -1, lines = 0, rows = 0, seconds = 0;
        for (var i = 1; i < args.Length; i++)
        {
            string Next()
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException($"{args[i]} 뒤에 값이 필요하다.");
                }

                return args[++i];
            }

            int NextInt(int min)
            {
                var name = args[i];
                var text = Next();
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) || v < min)
                {
                    throw new ArgumentException($"{name} 값은 {min} 이상의 정수여야 한다: {text}");
                }

                return v;
            }

            switch (args[i])
            {
                case "--out": outDir = Next(); break;
                case "--label": label = Next(); break;
                case "--samples": samples = NextInt(1); break;
                case "--warmup": warmup = NextInt(0); break;
                case "--lines": lines = NextInt(1); break;
                case "--rows": rows = NextInt(1); break;
                case "--seconds": seconds = NextInt(1); break;
                default: throw new ArgumentException($"모르는 옵션: {args[i]}");
            }
        }

        return new UiOptions(scenario, outDir, samples, warmup, lines, rows, seconds, label);
    }

    /// <summary>화면이 실제로 그려져야 값이 맞는 시나리오(잠금·가림 점검 대상). 포맷·문법 검사·취소는 렌더와 무관하다.</summary>
    public bool NeedsDisplay => Scenario is not ("format" or "diagnostics" or "cancel");

    public int SamplesOr(int fallback) => Samples > 0 ? Samples : fallback;

    public int WarmupOr(int fallback) => Warmup >= 0 ? Warmup : fallback;

    public int LinesOr(int fallback) => Lines > 0 ? Lines : fallback;

    public int RowsOr(int fallback) => Rows > 0 ? Rows : fallback;

    public int SecondsOr(int fallback) => Seconds > 0 ? Seconds : fallback;

    public string BaseName => "perf-" + Scenario + (Label.Length > 0 ? "-" + Label : string.Empty);
}

/// <summary>프레임 시각(ms) 목록에서 fps·간격 통계를 낸다.</summary>
public sealed record FrameSummary(int Frames, double Fps, double MeanMs, double P95Ms, double MaxMs, int SlowFrames)
{
    /// <summary>한 프레임이 이 값보다 길면 느린 프레임(60 Hz 의 두 틱 = 33.4 ms)으로 센다.</summary>
    public const double SlowFrameMs = 33.4;

    public static FrameSummary From(IReadOnlyList<double> frameTimesMs)
    {
        ArgumentNullException.ThrowIfNull(frameTimesMs);
        if (frameTimesMs.Count < 2)
        {
            throw new ArgumentException("프레임 시각이 2개 이상 필요하다.", nameof(frameTimesMs));
        }

        var intervals = new List<double>(frameTimesMs.Count - 1);
        for (var i = 1; i < frameTimesMs.Count; i++)
        {
            var d = frameTimesMs[i] - frameTimesMs[i - 1];
            if (d < 0)
            {
                throw new ArgumentException("프레임 시각이 거꾸로 간다.", nameof(frameTimesMs));
            }

            intervals.Add(d);
        }

        var total = frameTimesMs[^1] - frameTimesMs[0];
        var fps = total > 0 ? intervals.Count * 1000.0 / total : 0;
        return new FrameSummary(
            frameTimesMs.Count, fps, intervals.Average(), Stats.Percentile(intervals, 95), intervals.Max(), intervals.Count(d => d > SlowFrameMs));
    }
}

public enum ScrollKind
{
    /// <summary>프레임마다 3행.</summary>
    VerticalSmooth,

    /// <summary>프레임마다 60행(스크롤바를 빠르게 끄는 경우).</summary>
    VerticalFast,

    /// <summary>프레임마다 40 px 가로 왕복(세로는 중간).</summary>
    Horizontal,

    /// <summary>프레임마다 무작위 위치.</summary>
    Jump,

    /// <summary>움직이지 않는다(대조용).</summary>
    Idle,
}

/// <summary>그리드 스크롤 시나리오의 프레임별 위치(결정적이다: 같은 씨앗이면 같은 위치).</summary>
public static class ScrollPlan
{
    public static (double X, double Y) Offset(ScrollKind kind, int frame, double rowHeight, double maxX, double maxY, Random rng)
    {
        ArgumentNullException.ThrowIfNull(rng);
        ArgumentOutOfRangeException.ThrowIfNegative(frame);
        if (rowHeight <= 0 || maxX < 0 || maxY < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rowHeight), "행 높이는 0 보다 커야 하고 범위는 0 이상이어야 한다.");
        }

        switch (kind)
        {
            case ScrollKind.VerticalSmooth:
                return (0, Wrap(frame * 3 * rowHeight, maxY));
            case ScrollKind.VerticalFast:
                return (0, Wrap(frame * 60 * rowHeight, maxY));
            case ScrollKind.Idle:
                return (0, 0);
            case ScrollKind.Horizontal:
                return (Triangle(frame * 40.0, maxX), maxY / 2);
            default:
                return (maxX * rng.NextDouble(), maxY * rng.NextDouble());
        }
    }

    private static double Wrap(double v, double max) => max <= 0 ? 0 : v % max;

    /// <summary>0 → max → 0 을 오가는 삼각파.</summary>
    private static double Triangle(double v, double max)
    {
        if (max <= 0)
        {
            return 0;
        }

        var p = v % (2 * max);
        return p <= max ? p : (2 * max) - p;
    }
}

/// <summary>편집기·포맷 시험에 쓰는 합성 SQL.</summary>
public static class SampleSql
{
    private static readonly string[] FormatSamples =
    [
        "select a, b,c from emp e where e.id = 1 and e.name like 'x%' order by a",
        "select count(*), nvl(x,0) from t1 join t2 on t1.id=t2.id where t1.x in (select y from t3 where z = -1) group by a having count(*)>1",
        "-- header comment\nselect a /* inline */ , b -- trailing\nfrom t;\n\n\nupdate t set a = 1, b = :b where c = &sub;\ndelete from t where x = 1;",
        "select case when a = 1 then 'x' when a = 2 then 'y' else 'z' end as c, decode(b, 1, 2) from t\nunion all\nselect 'q', 1 from dual",
        "insert into t (a, b) values (1, 'x');\nmerge into t using s on (t.id = s.id) when matched then update set t.a = s.a when not matched then insert (a) values (s.a);",
        "create table x (a number(10,2) primary key, b varchar2(20 char) not null, c date default sysdate);",
        "SELECT q'[it''s]', 'multi\nline string', \"Quoted Col\" FROM dual;",
        "with a as (select 1 x from dual), b as (select x from a) select * from b where x > 0",
        "select e.ename, d.dname from emp e, dept d where e.deptno = d.deptno(+) and e.sal between 1000 and 2000",
        "select level, sys_connect_by_path(ename, '/') from emp start with mgr is null connect by prior empno = mgr order siblings by ename",
        "select deptno, listagg(ename, ',') within group (order by ename) names from emp group by deptno",
    ];

    /// <summary>줄 수가 <paramref name="lines"/> 이상이 될 때까지 여러 문장을 이어 붙인 포맷 입력(문장마다 「;」 뒤 줄바꿈). 줄 수도 돌려준다.</summary>
    public static (string Sql, int Lines) FormatInput(int lines)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(lines, 1);
        var sb = new StringBuilder();
        var count = 0;
        while (count < lines)
        {
            foreach (var s in FormatSamples)
            {
                sb.Append(s).Append(";\n");
                count += s.Count(c => c == '\n') + 1;
            }
        }

        return (sb.ToString(), count);
    }

    /// <summary>편집기 입력 시험용 문서: 주석·블록 주석·PL/SQL·SELECT 가 섞인 정확히 <paramref name="lines"/> 줄(줄마다 \n).</summary>
    public static string EditorDocument(int lines)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(lines, 1);
        var sb = new StringBuilder(lines * 40);
        for (var i = 0; i < lines; i++)
        {
            sb.Append((i % 20) switch
            {
                0 => "-- section " + i,
                5 => "/* block comment " + i,
                6 => "   still comment */",
                10 => "BEGIN",
                11 => "  IF x" + i + " > 1 THEN y := fn(" + i + ", 'text " + i + "'); END IF;",
                12 => "END;",
                _ => "SELECT col" + i + ", :bind" + i + ", 'literal' FROM tab_" + i + " WHERE id = " + i + ";",
            }).Append('\n');
        }

        return sb.ToString();
    }
}
