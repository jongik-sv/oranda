using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Execution;
using Oranda.Oracle.Execution;

namespace Oranda.Oracle.Tests.Execution;

// 대량 읽기의 LOB 미리 받기(PF3)가 명령(OracleCommand.InitialLOBFetchSize)과 FetchSize 계산에 닿는지(DB 없이). 실제 값 복사는 통합 시험 몫.
public sealed class BulkLobPrefetchTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(65536)]
    [InlineData(1048576)]
    public void A_positive_option_goes_to_the_command_initial_lob_fetch_size(int size)
    {
        using var cmd = new OracleCommand();

        var applied = OracleBulkExecutionService.ApplyLobPrefetch(cmd, new ExecutionOptions { LobPrefetchSize = size });

        Assert.True(applied);
        Assert.Equal(size, cmd.InitialLOBFetchSize);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void Without_a_positive_option_the_command_keeps_the_driver_default(int? size)
    {
        using var cmd = new OracleCommand();
        var before = cmd.InitialLOBFetchSize;

        var applied = OracleBulkExecutionService.ApplyLobPrefetch(cmd, new ExecutionOptions { LobPrefetchSize = size });

        Assert.False(applied);
        Assert.Equal(before, cmd.InitialLOBFetchSize); // -1(상한 없는 전체 받기)도 옵션으로는 켜지지 않는다
    }

    [Fact]
    public void The_default_execution_options_do_not_prefetch_lobs()
    {
        Assert.Null(ExecutionOptions.Default.LobPrefetchSize);
    }

    [Theory]
    [InlineData(200L, false)]
    [InlineData(200L, true)]   // 작은 행: 미리 받기를 켜도 옛 행 수 그대로
    [InlineData(500_000L, false)]
    public void Without_a_large_prefetched_row_the_fetch_size_is_row_size_times_fetch_rows(long rowSize, bool prefetch)
    {
        Assert.Equal(rowSize * OracleBulkExecutionService.FetchRows, OracleBulkExecutionService.FetchSizeFor(rowSize, prefetch));
    }

    [Fact]
    public void A_prefetched_lob_row_caps_one_round_trip_at_the_byte_limit()
    {
        const long rowSize = 140_000; // 64K 글자 CLOB 하나쯤
        var fetch = OracleBulkExecutionService.FetchSizeFor(rowSize, lobPrefetch: true);

        Assert.True(fetch <= OracleBulkExecutionService.LobPrefetchFetchBytes);
        Assert.Equal(0, fetch % rowSize);
        Assert.Equal(OracleBulkExecutionService.LobPrefetchFetchBytes / rowSize, fetch / rowSize);
    }

    [Fact]
    public void A_prefetched_row_larger_than_the_limit_still_fetches_one_row()
    {
        var huge = OracleBulkExecutionService.LobPrefetchFetchBytes * 3;

        Assert.Equal(huge, OracleBulkExecutionService.FetchSizeFor(huge, lobPrefetch: true));
    }

    [Fact]
    public void An_unknown_row_size_counts_as_one_byte()
    {
        Assert.Equal(OracleBulkExecutionService.FetchRows, OracleBulkExecutionService.FetchSizeFor(0, lobPrefetch: true));
    }
}
