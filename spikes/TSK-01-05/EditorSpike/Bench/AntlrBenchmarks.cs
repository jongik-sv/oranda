#if HAS_PLSQL_ANTLR
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Jobs;
using EditorSpike.Antlr;

namespace EditorSpike.Bench;

/// <summary>e3-ANTLR 워밍업 뒤(정상 상태) 파싱 시간. 콜드 첫 파싱은 AntlrProbe(자식 프로세스) 로 따로 잰다.</summary>
[MemoryDiagnoser]
[Config(typeof(AntlrConfig))]
public class AntlrBenchmarks
{
    string _pkg = "", _proc = "";

    [GlobalSetup]
    public void Setup()
    {
        _pkg = PlSqlGen.Package();
        _proc = AntlrProbe.OneProc(_pkg);
        AntlrProbe.Parse(_pkg); // DFA 캐시 워밍업
    }

    [Benchmark(Description = "ANTLR 웜: 프로시저 1개(~50줄)")]
    public double Proc() => AntlrProbe.Parse(_proc).ms;

    [Benchmark(Description = "ANTLR 웜: 1만 줄 패키지")]
    public double Package() => AntlrProbe.Parse(_pkg).ms;
}
public sealed class AntlrConfig : BenchmarkDotNet.Configs.ManualConfig
{
    public AntlrConfig()
    {
        AddJob(BenchmarkDotNet.Jobs.Job.Default.WithStrategy(RunStrategy.Monitoring).WithLaunchCount(1).WithWarmupCount(3).WithIterationCount(10)
            .WithToolchain(BenchmarkDotNet.Toolchains.InProcess.Emit.InProcessEmitToolchain.Instance));
        AddColumn(BenchmarkDotNet.Columns.StatisticColumn.P95);
    }
}
#endif
