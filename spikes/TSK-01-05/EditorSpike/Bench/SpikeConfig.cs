using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

namespace EditorSpike.Bench;

public sealed class SpikeConfig : ManualConfig
{
    public SpikeConfig()
    {
        // 측정 중 빌드를 돌리지 않도록 InProcess 툴체인(별도 프로젝트 생성·빌드 없음).
        AddJob(Job.Default.WithLaunchCount(1).WithWarmupCount(5).WithIterationCount(20).WithToolchain(InProcessEmitToolchain.Instance));
        AddColumn(StatisticColumn.P95, StatisticColumn.Max);
        SummaryStyle = SummaryStyle.Default.WithTimeUnit(Perfolizer.Horology.TimeUnit.Microsecond);
    }
}
