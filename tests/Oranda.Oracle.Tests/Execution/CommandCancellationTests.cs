using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Oranda.Oracle.Execution;

namespace Oranda.Oracle.Tests.Execution;

// 소유: TSK-06-11. 취소 토큰 → 실행 중인 서버 호출에 Cancel(드라이버 Break). DB 없이 가짜 명령으로 본다.
public class CommandCancellationTests
{
    [Fact]
    public async Task Token_cancel_sends_cancel_to_running_call_at_once()
    {
        using var cmd = new BlockingCommand();
        using var cts = new CancellationTokenSource();
        using (cmd.CancelOnToken(cts.Token))
        {
            // 토큰을 보지 않는 서버 호출(서버가 잠든 동안의 드라이버 호출과 같다): Cancel 이 와야만 끝난다.
            var call = cmd.ExecuteNonQueryAsync(CancellationToken.None);
            await cmd.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, cmd.CancelCalls);

            cts.Cancel();

            // 콜백이 취소하는 스레드에서 바로 돈다: Cancel() 이 돌아온 때 이미 Break 를 보냈다.
            Assert.Equal(1, cmd.CancelCalls);
            Assert.True(cmd.CancelledWhileRunning);
            Assert.Equal(-1, await call.WaitAsync(TimeSpan.FromSeconds(1)));
        }
    }

    [Fact]
    public async Task Cancel_after_call_ended_does_not_reach_command()
    {
        using var cmd = new BlockingCommand();
        using var cts = new CancellationTokenSource();
        using (cmd.CancelOnToken(cts.Token))
        {
            var call = cmd.ExecuteNonQueryAsync(CancellationToken.None);
            await cmd.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cmd.Finish();
            Assert.Equal(1, await call.WaitAsync(TimeSpan.FromSeconds(1)));
        }

        // 호출이 끝나 등록을 풀었으면 늦은 취소가 같은 연결의 다음 문장으로 새지 않는다.
        cts.Cancel();

        Assert.Equal(0, cmd.CancelCalls);
    }

    [Fact]
    public void Already_cancelled_token_cancels_at_registration()
    {
        using var cmd = new BlockingCommand();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        using (cmd.CancelOnToken(cts.Token))
        {
            Assert.Equal(1, cmd.CancelCalls);
        }
    }

    /// <summary>서버 호출을 흉내 내는 명령. <see cref="Cancel"/> 이나 <see cref="Finish"/> 가 와야 호출이 끝난다.</summary>
    private sealed class BlockingCommand : DbCommand
    {
        private readonly TaskCompletionSource<int> _server = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _cancelCalls;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CancelCalls => Volatile.Read(ref _cancelCalls);

        public bool CancelledWhileRunning { get; private set; }

        [AllowNull]
        public override string CommandText { get; set; } = string.Empty;

        public override int CommandTimeout { get; set; }

        public override CommandType CommandType { get; set; }

        public override bool DesignTimeVisible { get; set; }

        public override UpdateRowSource UpdatedRowSource { get; set; }

        protected override DbConnection? DbConnection { get; set; }

        protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();

        protected override DbTransaction? DbTransaction { get; set; }

        public void Finish() => _server.TrySetResult(1);

        public override void Cancel()
        {
            Interlocked.Increment(ref _cancelCalls);
            CancelledWhileRunning = Started.Task.IsCompleted && !_server.Task.IsCompleted;
            _server.TrySetResult(-1); // ORA-01013 으로 끝난 호출
        }

        public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            return await _server.Task;
        }

        public override int ExecuteNonQuery() => throw new NotSupportedException();

        public override object ExecuteScalar() => throw new NotSupportedException();

        public override void Prepare()
        {
        }

        protected override DbParameter CreateDbParameter() => throw new NotSupportedException();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();
    }
}
