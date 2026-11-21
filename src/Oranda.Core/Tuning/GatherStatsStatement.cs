using System.Globalization;
using System.Text;
using Oranda.Core.Connection;

namespace Oranda.Core.Tuning;

/// <summary>열 히스토그램 고르기(<c>method_opt</c>).</summary>
public enum GatherStatsHistogram
{
    /// <summary><c>FOR ALL COLUMNS SIZE AUTO</c> — DB 가 쓰임새를 보고 정한다(기본).</summary>
    Auto,

    /// <summary><c>FOR ALL COLUMNS SIZE 1</c> — 히스토그램을 만들지 않는다.</summary>
    None,

    /// <summary><c>FOR ALL COLUMNS SIZE SKEWONLY</c> — 값이 치우친 열에만.</summary>
    SkewOnly,
}

/// <summary>
/// 통계 수집 옵션(SG-1). <see cref="SamplePercent"/> null = <c>AUTO_SAMPLE_SIZE</c>, <see cref="Degree"/> null = 테이블 설정(인자 빼기).
/// </summary>
public sealed record GatherStatsOptions
{
    public static GatherStatsOptions Default { get; } = new();

    /// <summary>표본 비율(0 초과 100 이하). null 이면 자동.</summary>
    public decimal? SamplePercent { get; init; }

    public GatherStatsHistogram Histogram { get; init; } = GatherStatsHistogram.Auto;

    /// <summary>인덱스 통계도 함께(<c>cascade</c>).</summary>
    public bool Cascade { get; init; } = true;

    /// <summary>병렬 정도(1~1024). null 이면 인자를 넣지 않는다.</summary>
    public int? Degree { get; init; }
}

/// <summary>공급자가 지원하는 옵션. 지원하지 않는 것은 화면에서 숨긴다.</summary>
public sealed record GatherStatsSupport(bool SamplePercent, bool SkewOnly, bool Cascade, bool Degree);

/// <summary>
/// <c>DBMS_STATS.GATHER_TABLE_STATS</c> 실행 문장(익명 블록, 끝에 <c>/</c> 없음)을 공급자별로 만든다(SG-1).
/// 이름은 사전 표기(<see cref="TuningScripts.DictionaryName"/>)를 문자열 리터럴로 넣는다.
/// </summary>
public abstract class GatherStatsDialect
{
    private static readonly GatherStatsDialect OracleDialect = new OracleGatherStats();
    private static readonly GatherStatsDialect TiberoDialect = new TiberoGatherStats();

    public abstract GatherStatsSupport Support { get; }

    /// <summary>DBMS 에 맞는 생성기. 모르는 DBMS 는 Oracle 형태.</summary>
    public static GatherStatsDialect For(Dbms dbms) => dbms == Dbms.Tibero ? TiberoDialect : OracleDialect;

    /// <summary>실행할 PL/SQL 블록. 이름·옵션이 틀리면 <see cref="ArgumentException"/>, 지원하지 않는 옵션이면 <see cref="NotSupportedException"/>.</summary>
    public string Build(string owner, string table, GatherStatsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var o = TuningScripts.DictionaryName(owner, nameof(owner));
        var t = TuningScripts.DictionaryName(table, nameof(table));
        if (options.SamplePercent is { } p && (p <= 0 || p > 100))
        {
            throw new ArgumentOutOfRangeException(nameof(options), p, "표본 비율은 0 초과 100 이하입니다.");
        }

        if (options.Degree is { } d && (d < 1 || d > 1024))
        {
            throw new ArgumentOutOfRangeException(nameof(options), d, "병렬 정도는 1~1024 입니다.");
        }

        if (options.Histogram == GatherStatsHistogram.SkewOnly && !Support.SkewOnly)
        {
            throw new NotSupportedException("이 DBMS 는 SIZE SKEWONLY 를 지원하지 않습니다.");
        }

        var args = new List<(string Name, string Value)>
        {
            ("ownname", TuningScripts.Literal(o)),
            ("tabname", TuningScripts.Literal(t)),
        };
        if (options.SamplePercent is { } percent)
        {
            args.Add(("estimate_percent", percent.ToString(CultureInfo.InvariantCulture)));
        }
        else if (AutoSample is { } auto)
        {
            args.Add(("estimate_percent", auto));
        }

        args.Add(("method_opt", TuningScripts.Literal(MethodOpt(options.Histogram))));
        args.Add(("cascade", options.Cascade ? "TRUE" : "FALSE"));
        if (options.Degree is { } degree)
        {
            args.Add(("degree", degree.ToString(CultureInfo.InvariantCulture)));
        }

        args.AddRange(Extra);

        var sb = new StringBuilder();
        sb.Append("BEGIN\n  DBMS_STATS.GATHER_TABLE_STATS(\n");
        for (var i = 0; i < args.Count; i++)
        {
            sb.Append("    ").Append(args[i].Name.PadRight(16)).Append(" => ").Append(args[i].Value).Append(i == args.Count - 1 ? ");\n" : ",\n");
        }

        sb.Append("END;");
        return sb.ToString();
    }

    /// <summary>자동 표본 값(null 이면 인자를 빼 서버 기본을 쓴다).</summary>
    protected abstract string? AutoSample { get; }

    /// <summary>끝에 붙는 공급자 전용 인자.</summary>
    protected virtual IEnumerable<(string Name, string Value)> Extra => [];

    private static string MethodOpt(GatherStatsHistogram histogram) => histogram switch
    {
        GatherStatsHistogram.None => "FOR ALL COLUMNS SIZE 1",
        GatherStatsHistogram.SkewOnly => "FOR ALL COLUMNS SIZE SKEWONLY",
        _ => "FOR ALL COLUMNS SIZE AUTO",
    };

    private sealed class OracleGatherStats : GatherStatsDialect
    {
        public override GatherStatsSupport Support { get; } = new(SamplePercent: true, SkewOnly: true, Cascade: true, Degree: true);

        protected override string? AutoSample => "DBMS_STATS.AUTO_SAMPLE_SIZE";

        protected override IEnumerable<(string Name, string Value)> Extra => [("no_invalidate", "DBMS_STATS.AUTO_INVALIDATE")];
    }

    /// <summary>
    /// Tibero(6 실측, 조정자 win144): AUTO_SAMPLE_SIZE·SIZE SKEWONLY·degree·cascade=>TRUE/FALSE 는 된다.
    /// <c>DBMS_STATS.AUTO_INVALIDATE</c>·<c>AUTO_CASCADE</c> 상수는 PSM 컴파일 오류(ODP-15146)라 쓰지 않는다(no_invalidate 인자를 빼 서버 기본).
    /// 실서버 확인 = <c>TiberoGatherStatsIt</c>.
    /// </summary>
    private sealed class TiberoGatherStats : GatherStatsDialect
    {
        public override GatherStatsSupport Support { get; } = new(SamplePercent: true, SkewOnly: true, Cascade: true, Degree: true);

        protected override string? AutoSample => "DBMS_STATS.AUTO_SAMPLE_SIZE";
    }
}
