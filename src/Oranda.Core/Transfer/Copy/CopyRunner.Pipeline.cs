using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Oranda.Core.Execution;

namespace Oranda.Core.Transfer.Copy;

/// <summary>
/// 테이블 하나의 적재 흐름(PF1): 원본 읽기 → 대상 쓰기, 멈춤 감시(결정 70)·테이블당 대기 한도(결정 73).
/// <para>
/// 기본은 읽기와 쓰기를 겹친다(결정 69, <see cref="PumpOverlappedAsync"/>): 읽기 작업이 원본 행을 배치로 묶어 크기가 정해진 채널에 넣고,
/// 쓰기 작업(부른 흐름)이 채널에서 꺼내 배치마다 보낸다. 세션은 그대로 원본 하나·대상 하나다(세션 명령 관문 D-041: 세션마다 명령 하나 —
/// 원본 세션은 읽기 작업만, 대상 세션은 쓰기 흐름만 쓴다). <see cref="CopyOptions.SerialReadWrite"/> 면 옛 동작 그대로 번갈아 한다(<see cref="PumpSeriallyAsync"/>).
/// </para>
/// <para>
/// 두 흐름 모두 표 세이브포인트·배치 세이브포인트·실패 행 상한·커밋 방식은 그대로다(배치는 쓰기 흐름 하나가 차례로 보낸다). 쓰기가 멈추면(상한 초과·거부)
/// 읽기를 취소하고, 읽기가 실패하면 쓰기는 지금 배치만 끝내고 남은 버퍼를 버린 뒤 부른 쪽이 표를 되돌린다. 끝날 때는 언제나 두 작업을 모두 기다려 예외를 관찰한다.
/// </para>
/// <para>
/// 뒤에 올 테이블 동시 복사(PF2: 레인 = 세션 한 쌍)는 레인마다 이 흐름(<see cref="PumpTableAsync"/>)을 그대로 쓴다. 세션은 지금 <see cref="RunState"/> 의 한 쌍이다.
/// </para>
/// </summary>
public partial class CopyRunner
{
    /// <summary>
    /// 채널에 기다리게 둘 배치 수(LOB 없는 테이블). 메모리에는 많아야 이 수 + 2 배치(읽는 중 하나·쓰는 중 하나)가 있다 — 옛 동작은 1 배치.
    /// 2 면 원본·대상 속도가 배치 하나 정도 흔들려도 서로 기다리지 않는다.
    /// </summary>
    internal const int BufferedBatches = 2;

    /// <summary>
    /// LOB(CLOB·NCLOB·BLOB) 열이 있는 테이블의 채널 배치 수. 대량 읽기는 LOB 를 통째로 메모리에 올리므로(<see cref="IBulkExecutionService.StreamRowsAsync"/>)
    /// 배치 하나가 클 수 있다: 겹치기에 꼭 필요한 하나만 둔다(많아야 3 배치 = 옛 동작의 3배, LOB 없는 테이블은 4배). 더 줄이려면 배치 크기를 줄인다.
    /// </summary>
    internal const int BufferedLobBatches = 1;

    /// <summary>
    /// 테이블 원본 읽기의 LOB 미리 받기 크기(<see cref="ExecutionOptions.LobPrefetchSize"/>, CLOB·NCLOB 글자 · BLOB 바이트, PF3).
    /// 이보다 짧은 LOB 은 행과 함께 와서 행마다 서버를 다시 부르지 않고, 긴 값만 옛 방식처럼 나머지를 따로 읽는다(긴 값은 옮기는 시간이 왕복보다 훨씬 길다).
    /// 메모리: 배치에 쌓이는 양은 그대로다(옛 동작도 LOB 을 끝까지 읽어 배치에 담았다, 배치 수 = <see cref="BufferedLobBatches"/>).
    /// 더해지는 것은 원본 드라이버의 행 버퍼뿐이고 드라이버가 왕복 한 번 양에 상한을 둔다(Oracle 32MB). 64K 는 업무 테이블 LOB 대부분을 덮는 크기다.
    /// <see cref="CopyOptions.NoLobPrefetch"/> 면 넘기지 않는다.
    /// </summary>
    internal const int LobPrefetchSize = 64 * 1024;

    /// <summary>멈춤 감시 시각. 시험이 줄인다(실행기 인스턴스마다라 시험끼리 섞이지 않는다).</summary>
    internal CopyWatchTiming WatchTiming { get; set; } = CopyWatchTiming.Default;

    /// <summary>적재 흐름이 끝난 모양.</summary>
    private enum PumpEnd
    {
        /// <summary>끝까지 읽고 썼다(쓰기가 상한 초과 등으로 표를 실패로 두었을 수 있다 — 부른 쪽이 <see cref="TableState.Outcome"/> 를 본다).</summary>
        Done,

        /// <summary>원본 읽기가 서버 오류로 실패했다. 표를 되돌리고 실패로 두었다.</summary>
        ReadFailed,

        /// <summary>한 명령이 대기 한도를 넘어 취소했다(그 쪽 = <see cref="PumpResult.Side"/>). 표는 아직 되돌리지 않았다.</summary>
        WaitLimit,

        /// <summary>중지·대기 한도로 취소했는데 명령이 정해진 시간 안에 돌아오지 않았다(그 쪽 = <see cref="PumpResult.Side"/>).</summary>
        Stuck,
    }

    /// <param name="SourceHeld">
    /// <see cref="PumpEnd.Stuck"/> 이고 대상 명령이 돌아오지 않을 때, 원본 읽기도 <see cref="CopyWatchTiming.CancelGrace"/> 안에 끝나지 않았다
    /// (「읽기·쓰기 번갈아」면 멈춘 쓰기가 원본 커서를 쥔 채다): 원본 세션도 더 쓰지 않는다(리뷰 PF1-1).
    /// </param>
    private readonly record struct PumpResult(PumpEnd End, SideWatch? Side, bool SourceHeld = false);

    /// <summary>멈춤 감시가 보고하는 자리: 테이블·예상 건수·단계(적재 또는 대상 정리).</summary>
    private sealed record WatchScope(TableState Table, long? Estimated, string Phase);

    /// <summary>테이블 하나 적재에 필요한 것(문장·열 배치·예상 건수·일시 정지 신호).</summary>
    private sealed record TableLoad(CopyTablePlan Plan, TableState Table, RowShape Shape, SqlStatement Dml, SqlWithBinds Select,
                                    long? Estimated, CopyRunControl Control);

    /// <summary>읽기 작업이 남긴 실패(쓰기 흐름이 배치마다 본다).</summary>
    private sealed class ReadOutcome
    {
        private Exception? _error;

        public Exception? Error => Volatile.Read(ref _error);

        public void Fail(Exception error) => Volatile.Write(ref _error, error);
    }

    /// <summary>
    /// 테이블 하나를 읽고 쓴다(표 세이브포인트·읽기 시점은 부른 쪽이 이미 세웠다). 멈춤 감시를 함께 돌리고, 끝나면 감시를 멈추고 기다린다.
    /// 중지면 <see cref="OperationCanceledException"/> 이 그대로 올라간다(멈춘 명령이 돌아오지 않을 때만 <see cref="PumpEnd.Stuck"/>).
    /// </summary>
    private async Task<PumpResult> PumpTableAsync(RunState run, TableLoad load, CancellationToken ct)
    {
        var watch = new TableWatch(WaitLimit(run), ct);
        Task? abandoned = null;
        using var stopMonitor = new CancellationTokenSource();
        var monitor = MonitorAsync(run, new WatchScope(load.Table, load.Estimated, PhaseLoad), watch, stopMonitor.Token);
        try
        {
            var pump = run.Job.Options.SerialReadWrite ? PumpSeriallyAsync(run, load, watch, ct) : PumpOverlappedAsync(run, load, watch, ct);
            if (await Task.WhenAny(pump, watch.Stuck) != pump)
            {
                // 돌아오지 않는 명령을 더 기다리지 않는다. 남은 작업은 끝날 때 예외만 관찰한다(세션은 부른 쪽이 버린다).
                // 반대쪽 취소는 스레드 풀에서 보낸다(SideWatch.Cancel): 감시(취소 토큰)는 남은 작업이 끝난 뒤에 버린다 — 여기서 버리면
                // 취소가 닿기 전에 토큰이 사라져 꽉 찬 채널을 기다리는 읽기 작업이 원본 세션(명령 관문)을 쥔 채 남는다(리뷰 PF1-1).
                abandoned = pump;
                watch.CancelAll();
                ObserveLater(pump);
                var stuck = watch.StuckSide!;
                var sourceHeld = stuck.Side == CopyWaitSide.Target && !await CompletesWithin(watch.SourceReleased, WatchTiming.CancelGrace);
                return new PumpResult(PumpEnd.Stuck, stuck, sourceHeld);
            }

            return new PumpResult(await pump ? PumpEnd.Done : PumpEnd.ReadFailed, null);
        }
        catch (Exception) when (!ct.IsCancellationRequested && watch.LimitSide is { } side)
        {
            // 대기 한도로 취소한 명령이 어떤 모양으로 올라오든(취소 예외·취소 결과를 받은 Accept 의 취소 예외) 대기 한도 초과다.
            return new PumpResult(PumpEnd.WaitLimit, side);
        }
        finally
        {
            stopMonitor.Cancel();
            await monitor; // 감시는 예외를 올리지 않는다
            if (watch.Source.ShownMinute != 0 || watch.Target.ShownMinute != 0)
            {
                // 멈춤 표시가 남은 채 끝났다(대기 한도·중지): 표시를 지운다.
                run.ReportWaits(load.Table.Name, load.Table.SourceRows, load.Estimated, load.Table.Loaded, []);
            }

            EndProbe(run, watch.Target);
            DisposeWhenDone(watch, abandoned);
        }
    }

    /// <summary>작업 옵션의 테이블당 대기 한도(명령 하나). null = 없음.</summary>
    private TimeSpan? WaitLimit(RunState run) =>
        run.Job.Options.WaitLimitMinutes > 0 ? WatchTiming.LimitUnit * run.Job.Options.WaitLimitMinutes : null;

    /// <summary>감시를 버린다. 버린(돌아오지 않는) 작업이 있으면 그 작업이 끝난 뒤에 버린다(끝나지 않으면 토큰 몇 개가 남는다 — 세션도 버렸다).</summary>
    private static void DisposeWhenDone(TableWatch watch, Task? abandoned)
    {
        if (abandoned is null)
        {
            watch.Dispose();
            return;
        }

        _ = abandoned.ContinueWith(_ => watch.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary><paramref name="task"/> 가 <paramref name="within"/> 안에 끝나는가. 먼저 끝나면 기다리던 Delay 타이머를 취소한다(CA2027).</summary>
    private static async Task<bool> CompletesWithin(Task task, TimeSpan within)
    {
        using var delayCancel = new CancellationTokenSource();
        var completed = await Task.WhenAny(task, Task.Delay(within, delayCancel.Token)) == task;
        await delayCancel.CancelAsync();
        return completed;
    }

    /// <summary>
    /// 대상 명령 하나(대상 정리 DELETE·「테이블마다 커밋」의 COMMIT)를 적재와 같은 멈춤 감시 아래 보낸다(결정 70·73, 리뷰 PF1-4):
    /// 10초 넘게 응답이 없으면 멈춤 표시, 대기 한도를 넘으면 취소(취소가 예외로 올라오면 <see cref="PumpEnd.WaitLimit"/>, 「취소됨」 결과로 오면
    /// <see cref="PumpEnd.Done"/> 과 그 결과 — 부른 쪽이 <see cref="SideWatch.LimitHit"/> 로 본다), 취소한 명령이 <see cref="CopyWatchTiming.CancelGrace"/> 안에
    /// 돌아오지 않으면 <see cref="PumpEnd.Stuck"/>(부른 쪽이 세션을 버린다). 중지면 <see cref="OperationCanceledException"/> 이 그대로 올라간다.
    /// </summary>
    /// <param name="send">명령을 보낸다. 받은 토큰(실행 중지 + 대기 한도)으로 보낸다.</param>
    private async Task<(PumpEnd End, T? Result, SideWatch Side)> WatchTargetCommandAsync<T>(RunState run, TableState table, long? estimated, CopyWaitStep step,
                                                                                           Func<CancellationToken, Task<T>> send, CancellationToken ct)
    {
        var watch = new TableWatch(WaitLimit(run), ct, step);
        var side = watch.Target;
        Task? abandoned = null;
        using var stopMonitor = new CancellationTokenSource();
        var monitor = MonitorAsync(run, new WatchScope(table, estimated, side.Phase), watch, stopMonitor.Token);
        try
        {
            side.Busy();
            var command = send(side.Token);
            if (await Task.WhenAny(command, watch.Stuck) != command)
            {
                abandoned = command;
                ObserveLater(command);
                return (PumpEnd.Stuck, default, side);
            }

            // 대기 한도 취소가 예외 없이 「취소됨」 결과로 돌아오면(트랜잭션 제어) 부른 쪽이 결과와 Side.LimitHit 로 본다:
            // 한도를 넘는 순간 명령이 성공했을 수도 있어 결과를 보지 않고는 실패로 둘 수 없다.
            return (PumpEnd.Done, await command, side);
        }
        catch (Exception) when (!ct.IsCancellationRequested && side.LimitHit)
        {
            return (PumpEnd.WaitLimit, default, side);
        }
        finally
        {
            side.Idle();
            stopMonitor.Cancel();
            await monitor; // 감시는 예외를 올리지 않는다
            if (side.ShownMinute != 0)
            {
                run.ReportWaits(table.Name, table.SourceRows, estimated, table.Loaded, [], side.Phase);
            }

            EndProbe(run, side);
            DisposeWhenDone(watch, abandoned);
        }
    }

    /// <summary>
    /// 옛 동작(「읽기·쓰기 번갈아」): 원본 행을 읽다 배치가 차면 쓰기를 기다린 뒤 다시 읽는다. 끝까지 읽었거나 쓰기가 멈췄으면 true,
    /// 원본 읽기가 서버 오류로 실패해 표를 되돌렸으면 false.
    /// </summary>
    private async Task<bool> PumpSeriallyAsync(RunState run, TableLoad load, TableWatch watch, CancellationToken ct)
    {
        var (table, shape, dml, select) = (load.Table, load.Shape, load.Dml, load.Select);
        var batch = new List<object?[]>(run.Job.Options.BatchSize);
        var stop = false;
        try
        {
            watch.Source.Busy();
            await using var rows = _bulk.StreamRowsAsync(run.Source, CopyCommonStatements.Query(select), select.Binds, SourceReadOptions(run), watch.Source.Token)
                .GetAsyncEnumerator(CancellationToken.None); // 토큰은 호출 인자로 넘겼다
            while (await NextRowAsync(rows, table))
            {
                watch.Source.Touch();
                AddMasked(batch, shape, rows.Current, table);
                table.SourceRows++;
                if (batch.Count < run.Job.Options.BatchSize)
                {
                    continue;
                }

                // 쓰는 동안 원본은 다음 행을 기다리지 않는다(커서만 열려 있다): 원본 멈춤으로 보지 않는다.
                watch.Source.Idle();
                stop = !await FlushAsync(run, table, shape, dml, batch, load.Control, watch.Target, watch.Target.Token);
                batch.Clear();
                run.Progress(table.Name, table.SourceRows, load.Estimated, PhaseLoad, table.Loaded);
                if (stop)
                {
                    break;
                }

                watch.Source.Busy();
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && watch.LimitSide is null && CopyErrors.Server(_errors, ex) is { } error)
        {
            await ReadFailedAsync(run, table, ex, error, ct);
            return false;
        }
        finally
        {
            watch.Source.Idle();
            watch.ReleaseSource(); // 원본 커서를 닫았다(위 try 블록을 나가며 rows 를 버렸다)
        }

        if (!stop && batch.Count > 0)
        {
            await FlushAsync(run, table, shape, dml, batch, load.Control, watch.Target, watch.Target.Token);
        }

        return true;
    }

    /// <summary>
    /// 읽기·쓰기 겹치기(결정 69): 읽기 작업(스레드 풀)이 배치를 채널에 넣고 이 흐름이 꺼내 보낸다. 반환은 <see cref="PumpSeriallyAsync"/> 와 같다.
    /// 두 작업은 어느 길로 끝나든 둘 다 끝난 뒤 돌아온다(읽기 작업은 예외를 밖으로 내지 않고 <see cref="ReadOutcome"/> 에 남긴다).
    /// </summary>
    private async Task<bool> PumpOverlappedAsync(RunState run, TableLoad load, TableWatch watch, CancellationToken ct)
    {
        var table = load.Table;
        var channel = Channel.CreateBounded<List<object?[]>>(new BoundedChannelOptions(HasLob(load) ? BufferedLobBatches : BufferedBatches)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var read = new ReadOutcome();

        // 원본 드라이버의 비동기 호출이 실제로는 스레드를 붙잡을 수 있어(동기 구현) 읽기는 스레드 풀에서 돌린다: 쓰기 흐름과 실제로 겹치게.
        var reading = Task.Run(async () =>
        {
            try
            {
                await ReadBatchesAsync(run, load, watch.Source, channel.Writer);
            }
            catch (Exception ex)
            {
                read.Fail(ex);
            }
            finally
            {
                watch.Source.Idle();
                channel.Writer.TryComplete();
                watch.ReleaseSource(); // 원본 커서를 닫았다(ReadBatchesAsync 가 rows 를 버렸다)
            }
        }, CancellationToken.None);

        bool stop;
        try
        {
            stop = await WriteBatchesAsync(run, load, watch.Target, channel.Reader, read);
        }
        catch
        {
            // 쓰기 쪽 예외(중지·대기 한도·예상 못 한 오류): 읽기를 멈추고 끝날 때까지 기다린 뒤 그대로 올린다.
            watch.Source.StopReading();
            await reading;
            throw;
        }

        if (stop)
        {
            // 쓰기가 표를 멈췄다(실패 행 상한·거부): 읽기도 멈춘다. 그 뒤의 읽기 오류(취소 포함)는 이 표의 결과와 상관없다.
            watch.Source.StopReading();
            await reading;
            return true;
        }

        await reading;
        if (read.Error is not { } error)
        {
            return true;
        }

        ct.ThrowIfCancellationRequested();
        if (watch.LimitSide is null && CopyErrors.Server(_errors, error) is { } dbError)
        {
            await ReadFailedAsync(run, table, error, dbError, ct);
            return false;
        }

        // 정책 거부·규칙 오류·대기 한도 취소: 옛 동작과 같은 곳(LoadTableAsync·PumpTableAsync)에서 받는다.
        ExceptionDispatchInfo.Capture(error).Throw();
        return false;
    }

    /// <summary>읽기 작업: 원본 행을 배치로 묶어 채널에 넣는다. 채널이 차서 기다리는 동안은 원본 멈춤으로 보지 않는다.</summary>
    private async Task ReadBatchesAsync(RunState run, TableLoad load, SideWatch reader, ChannelWriter<List<object?[]>> batches)
    {
        var size = run.Job.Options.BatchSize;
        var table = load.Table;
        var select = load.Select;
        var batch = new List<object?[]>(size);
        reader.Busy();
        await using var rows = _bulk.StreamRowsAsync(run.Source, CopyCommonStatements.Query(select), select.Binds, SourceReadOptions(run), reader.Token)
            .GetAsyncEnumerator(CancellationToken.None); // 토큰은 호출 인자로 넘겼다
        while (await NextRowAsync(rows, table))
        {
            reader.Touch();
            AddMasked(batch, load.Shape, rows.Current, table);
            table.SourceRows++; // 읽기 작업만 바꾼다(쓰기 흐름·감시는 진행 보고에 읽기만 한다)
            if (batch.Count < size)
            {
                continue;
            }

            reader.Idle();
            await batches.WriteAsync(batch, reader.Token);
            reader.Busy();
            batch = new List<object?[]>(size);
        }

        reader.Idle();
        if (batch.Count > 0)
        {
            await batches.WriteAsync(batch, reader.Token);
        }
    }

    /// <summary>쓰기 흐름: 채널의 배치를 차례로 보낸다. 표를 멈췄으면(상한 초과·거부) true. 읽기가 실패했으면 남은 배치를 버리고 false.</summary>
    private async Task<bool> WriteBatchesAsync(RunState run, TableLoad load, SideWatch writer, ChannelReader<List<object?[]>> batches, ReadOutcome read)
    {
        var table = load.Table;
        while (await batches.WaitToReadAsync(writer.Token))
        {
            while (batches.TryRead(out var batch))
            {
                if (read.Error is not null)
                {
                    return false; // 읽기 실패: 버퍼를 버린다(표는 부른 쪽이 되돌린다)
                }

                var stop = !await FlushAsync(run, table, load.Shape, load.Dml, batch, load.Control, writer, writer.Token);
                run.Progress(table.Name, table.SourceRows, load.Estimated, PhaseLoad, table.Loaded);
                if (stop)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 원본 테이블 행을 읽는 대량 읽기 옵션: <see cref="RunState.BulkOptions"/> 에 LOB 미리 받기(<see cref="LobPrefetchSize"/>)를 더한다(PF3).
    /// <see cref="CopyOptions.NoLobPrefetch"/> 면 그대로. 대상 COUNT·카탈로그 조회 같은 다른 읽기는 이것을 쓰지 않는다.
    /// </summary>
    private static ExecutionOptions SourceReadOptions(RunState run) =>
        run.Job.Options.NoLobPrefetch ? run.BulkOptions : run.BulkOptions with { LobPrefetchSize = LobPrefetchSize };

    /// <summary>
    /// 원본 행 하나를 받는다. 받기까지 걸린 시간(커서 열기 포함 — 원본 DB 를 기다린 시간)을 표의 읽기 시간에 더한다(결정 74). 겹치기에서는 읽기 작업이
    /// 채널을 기다린 시간이 들어가지 않는다(그때는 이것을 부르지 않는다).
    /// </summary>
    private static async ValueTask<bool> NextRowAsync(IAsyncEnumerator<object?[]> rows, TableState table)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            return await rows.MoveNextAsync();
        }
        finally
        {
            table.Times.AddRead(started);
        }
    }

    /// <summary>마스킹을 건 행을 배치에 넣는다. 그 시간은 표의 변환 시간이다(결정 74).</summary>
    private static void AddMasked(List<object?[]> batch, RowShape shape, object?[] row, TableState table)
    {
        var started = Stopwatch.GetTimestamp();
        batch.Add(shape.Apply(row));
        table.Times.AddConvert(started);
    }

    /// <summary>원본 읽기가 서버 오류로 실패: 스냅샷이 너무 오래됐으면 실행 전체를 멈추고, 이 표의 적재분을 되돌린 뒤 실패로 둔다.</summary>
    private async Task ReadFailedAsync(RunState run, TableState table, Exception ex, DbError error, CancellationToken ct)
    {
        if (ReadPointFailure(run, table, ex) is { } failure)
        {
            // 같은 시점 읽기 + 동시 복사(PF2b): 그 시점으로 못 읽었다. 지금 데이터로 바꿔 읽지 않고 이 테이블을 실패로(시점 데이터를 잃었으면 전체 중단).
            await RollbackTableAsync(run, table, ct);
            table.Fail(failure);
            return;
        }

        if (error.Kind == DbErrorKind.SnapshotTooOld)
        {
            run.Notices.Add($"{table.Name}: 원본 읽기가 오래 걸려 이전 시점 데이터를 읽지 못했습니다{ErrorNameNote(_sourceDialect.SnapshotTooOldErrorName)}. 기간을 줄이거나 나눠 실행하세요.");
            run.Abort(null);
        }

        await RollbackTableAsync(run, table, ct);
        table.Fail("원본을 읽지 못했습니다: " + SafeError(ex.Message));
    }

    /// <summary>복사 열에 LOB(CLOB·NCLOB·BLOB)가 있는가(채널 배치 수를 줄인다). 형식을 모르면 LOB 가 아닌 것으로 본다.</summary>
    private bool HasLob(TableLoad load)
    {
        var copied = load.Shape.CopyColumns.ToHashSet(StringComparer.Ordinal);
        foreach (var column in load.Plan.Source.Columns.Where(c => copied.Contains(c.Name)))
        {
            try
            {
                if (_sourceCodec.KindOf(column.DataType) is BulkKind.Clob or BulkKind.Blob)
                {
                    return true;
                }
            }
            catch (NotSupportedException)
            {
                // 대량 읽기가 모르는 형식: 열 배치(RowShape)가 따로 막는다.
            }
        }

        return false;
    }

    private static void ObserveLater(Task task) =>
        _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    // ---- 대기 한도 초과·돌아오지 않는 명령 ----

    /// <summary>
    /// 한 명령이 대기 한도를 넘어 취소했다(결정 73): 「배치마다 커밋」이면 쓰다 만 배치를 배치 세이브포인트로, 아니면 표 세이브포인트로 되돌리고
    /// 표를 실패(사유 = 대기 한도 초과, 단계·배치 번호)로 둔다. 부른 쪽은 다음 테이블로 간다.
    /// </summary>
    private async Task StopAtWaitLimitAsync(RunState run, TableState table, SideWatch side, CancellationToken ct)
    {
        var token = ct.IsCancellationRequested ? CancellationToken.None : ct;
        if (side.Side == CopyWaitSide.Target && run.Job.Options.Commit == CommitMode.PerBatch)
        {
            // 「배치마다 커밋」은 커밋이 표 세이브포인트를 지운다: 쓰다 취소한 배치(한 행씩 넣은 행 포함)는 배치 세이브포인트로만 되돌릴 수 있다.
            var back = await ControlLoggedAsync(run, run.Target, _dialect.RollbackToSavepoint(CopyCommonStatements.BatchSavepoint), token, "대기 한도로 멈춘 배치 되돌리기");
            if (back.Status == ExecutionStatus.Succeeded)
            {
                // 되돌린 배치에서 넣은 실패 행도 뺀다(상한 초과와 같다, AddFailedRowAsync): 남기면 마무리 커밋 때 확정돼 결과·기록에 들어간다(리뷰 PF1-2).
                // 앞 배치의 실패 행은 그 배치 커밋 때 이미 확정됐다. 이 배치의 커밋이 끝난 뒤였다면 목록이 비어 있어 뺄 것이 없다.
                var from = Math.Min(table.PendingAtBatchStart, table.PendingFailed.Count);
                table.PendingFailed.RemoveRange(from, table.PendingFailed.Count - from);
            }
            else
            {
                run.Notices.Add($"{table.Name}: 대기 한도로 멈춘 배치를 되돌리지 못했습니다: " + Describe(back));
            }
        }

        await RollbackTableAsync(run, table, token);
        table.Fail(WaitLimitText(run, side));
    }

    /// <summary>
    /// 중지·대기 한도로 취소한 명령이 <see cref="CopyWatchTiming.CancelGrace"/> 안에 돌아오지 않았다: 그 세션을 더 쓰지 않는다.
    /// Core 실행기는 세션을 새로 열 수 없다(접속 비밀번호는 앱이 가진다). 그래서 실행 전체를 멈추고 안내한다(「같은 시점 기준 읽기」면 어차피 새 세션으로 이어 읽을 수 없다).
    /// 대상 세션이 살아 있으면 이 표의 적재분은 되돌린다. 중지 중이면 중지 예외를 올린다.
    /// </summary>
    /// <param name="sourceHeld">대상이 멈췄는데 원본 읽기도 끝나지 않았다(<see cref="PumpResult.SourceHeld"/>): 원본 세션도 버린다.</param>
    private async Task AbandonStuckAsync(RunState run, TableState table, SideWatch side, CancellationToken ct, bool sourceHeld = false)
    {
        var grace = ((int)WatchTiming.CancelGrace.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        var sideText = side.Side == CopyWaitSide.Source ? "원본" : "대상";
        if (side.Side == CopyWaitSide.Source)
        {
            run.SourceAbandoned = true;
        }
        else
        {
            run.TargetAbandoned = true;
        }

        if (sourceHeld)
        {
            // 멈춘 쓰기가 원본 커서를 쥐고 있다(「읽기·쓰기 번갈아」): 원본 세션에 보내는 마무리(읽기 전용 트랜잭션 닫기)도 차례를 못 받는다.
            run.SourceAbandoned = true;
        }

        var text = $"{table.Name}: {side.Describe()} 명령을 취소했지만 {grace}초 안에 돌아오지 않아 {sideText} 세션을 더 쓰지 않고 실행을 멈춥니다."
                   + (sourceHeld ? " 원본 읽기도 끝나지 않아 원본 세션도 더 쓰지 않습니다." : string.Empty)
                   + " 복사 창을 닫아 세션을 정리한 뒤 다시 실행하세요.";
        run.Abort(text);
        await RecordEventAsync(run, "stuck", table.Name, text);
        if (!run.TargetAbandoned)
        {
            await RollbackTableAsync(run, table, ct);
        }

        ct.ThrowIfCancellationRequested();
        table.Fail(side.LimitHit ? WaitLimitText(run, side) + " 취소한 명령이 돌아오지 않았습니다." : $"{side.Describe()} 명령이 돌아오지 않았습니다.");
    }

    /// <summary>
    /// 「테이블마다 커밋」의 COMMIT 이 대기 한도를 넘어 취소했다(리뷰 PF1-4): 테이블 세이브포인트로 되돌려 본다. 되돌리면 커밋은 되지 않았다(테이블 실패, 적재분 버림).
    /// 되돌리지 못하면 커밋이 이미 됐을 수 있다(커밋이 세이브포인트를 지운다): 실패로 두되 그렇게 적고, 앞 DELETE 도 확정됐을 수 있는 것으로 본다.
    /// </summary>
    private async Task StopCommitAtWaitLimitAsync(RunState run, TableState table, SideWatch side, CancellationToken ct)
    {
        await RollbackTableAsync(run, table, ct.IsCancellationRequested ? CancellationToken.None : ct);
        if (table.WasRolledBack)
        {
            table.Fail(WaitLimitText(run, side));
            return;
        }

        run.DeletesPersisted = true;
        table.Fail(WaitLimitText(run, side, "명령을 취소했지만 이 테이블을 되돌리지 못했습니다. 커밋이 이미 됐을 수 있으니 대상 건수를 확인하세요."));
    }

    private static string WaitLimitText(RunState run, SideWatch side, string? outcome = null) =>
        $"대기 한도 초과({run.Job.Options.WaitLimitMinutes.ToString(CultureInfo.InvariantCulture)}분) · {side.Phase} · {side.Describe()}: "
        + (outcome ?? (side.Step == CopyWaitStep.Delete ? "명령을 취소했습니다. 이 테이블은 지우지 않았으니 적재하지 않습니다." : "명령을 취소하고 이 테이블을 되돌렸습니다."));

    // ---- 멈춤 감시 ----

    /// <summary>
    /// 멈춤 감시(결정 70·73): <see cref="CopyWatchTiming.Tick"/> 마다 두 쪽을 본다. 응답 없이 <see cref="CopyWatchTiming.StallAfter"/> 를 넘으면
    /// 테이블 진행 줄(단계 칸)과 작업 로그·실행 기록에 「배치 n 쓰는 중 · m분째 대기」/「원본 읽는 중 · m분째」를 남기고 분이 바뀔 때마다 고친다.
    /// 대기 한도를 넘으면 그 쪽 명령을 취소한다. 취소(중지 포함)한 명령이 <see cref="CopyWatchTiming.CancelGrace"/> 안에 돌아오지 않으면 적재 흐름에 알린다.
    /// 감시가 실패해도 적재는 멈추지 않는다(예외를 올리지 않는다).
    /// </summary>
    private async Task MonitorAsync(RunState run, WatchScope scope, TableWatch watch, CancellationToken stop)
    {
        var timing = WatchTiming;
        try
        {
            using var timer = new PeriodicTimer(timing.Tick);
            while (await timer.WaitForNextTickAsync(stop))
            {
                await CheckWaitsAsync(run, scope, watch, timing);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // 적재 흐름이 끝났다.
        }
        catch (Exception ex)
        {
            run.Log("멈춤 감시를 계속하지 못했습니다: " + SafeError(ex.Message), CopyLogKind.Warning, scope.Table.Name, scope.Phase);
        }
    }

    private static async Task CheckWaitsAsync(RunState run, WatchScope scope, TableWatch watch, CopyWatchTiming timing)
    {
        var table = scope.Table;
        var phase = scope.Phase;
        var changed = false;
        foreach (var side in watch.Sides)
        {
            if (side.Waiting is not { } waited)
            {
                if (side.ShownMinute != 0)
                {
                    side.ShownMinute = 0;
                    changed = true;
                }

                EndProbe(run, side); // 대기가 끝났다: 대기 원인을 지우고 감시 세션을 닫게 한다(WH1)
                continue;
            }

            if (watch.Run.IsCancellationRequested || side.LimitHit || side.StopRequested)
            {
                // 취소를 보냈다(중지·대기 한도·쓰기가 멈춰 읽기를 멈춤): 돌아오기를 기다린다. 너무 오래면 적재 흐름이 그 세션을 버린다.
                side.CancelledAt ??= Stopwatch.GetTimestamp();
                if (Stopwatch.GetElapsedTime(side.CancelledAt.Value) >= timing.CancelGrace)
                {
                    watch.SignalStuck(side);
                }

                continue;
            }

            // 대기 원인(WH1): 대상 명령이 30초 넘게 기다리면 감시 세션으로 읽는다. 읽기는 감시 흐름과 따로 돌려(감시 조회도 막힐 수 있다)
            // 대기 한도 판정을 늦추지 않는다. 결과는 다음 감시 차례에 대기 줄에 붙는다.
            if (side.Side == CopyWaitSide.Target && run.WaitProbe is { IsOff: false } probe && waited > probe.StartAfter
                && side.Probing is not { IsCompleted: false })
            {
                side.Probed = true;
                side.Probing = ProbeWaitAsync(run, probe, side, side.WaitStamp, waited, table.Name, phase);
            }

            if (waited >= timing.StallAfter)
            {
                var wait = side.Wait(waited);
                if (wait.Minute != side.ShownMinute || !string.Equals(wait.Cause, side.ShownCause, StringComparison.Ordinal))
                {
                    side.ShownMinute = wait.Minute;
                    side.ShownCause = wait.Cause;
                    changed = true;
                    var text = CopyLogText.Wait(wait);
                    run.Log(text, CopyLogKind.Warning, table.Name, phase);
                    await RecordEventAsync(run, "wait", table.Name, text);
                }
            }

            if (watch.Limit is { } limit && waited >= limit)
            {
                side.LimitHit = true;
                side.CancelledAt = Stopwatch.GetTimestamp();
                var text = $"대기 한도({run.Job.Options.WaitLimitMinutes.ToString(CultureInfo.InvariantCulture)}분)를 넘어 {side.Describe()} 명령을 취소합니다.";
                run.Log(text, CopyLogKind.Error, table.Name, phase);
                await RecordEventAsync(run, "waitLimit", table.Name, text);
                side.Cancel();
            }
        }

        if (changed)
        {
            var waits = new List<CopyWait>();
            foreach (var side in watch.Sides)
            {
                if (side.ShownMinute != 0 && side.Waiting is { } waited)
                {
                    waits.Add(side.Wait(waited));
                }
            }

            run.ReportWaits(table.Name, table.SourceRows, scope.Estimated, table.Loaded, waits, phase);
        }
    }

    /// <summary>
    /// 대기 원인을 한 번 읽어 그 쪽 감시에 둔다(WH1). 읽는 사이 그 대기가 끝났으면(다음 배치·한 행 진행 포함, <paramref name="stamp"/> 가 다르면) 버린다.
    /// 볼 권한이 없어 그만둔 안내는 실행 끝에 한 번 남긴다(<see cref="NoteWaitProbe"/>: 안내 목록은 실행 흐름에서만 바꾼다).
    /// 감시는 복사를 막지 않는다: 예외는 작업 로그 경고로만 남긴다.
    /// </summary>
    private static async Task ProbeWaitAsync(RunState run, CopyWaitProbe probe, SideWatch side, long stamp, TimeSpan waited, string table, string phase)
    {
        try
        {
            var cause = await Task.Run(() => probe.DescribeAsync(run.Target, waited, CancellationToken.None));
            if (side.WaitStamp == stamp)
            {
                side.Cause = cause;
            }
        }
#pragma warning disable CA1031 // 대기 원인 감시는 복사를 막지 않는다.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            run.Log("대기 원인을 읽지 못했습니다: " + SafeError(ex.Message), CopyLogKind.Warning, table, phase);
        }
    }

    /// <summary>그 쪽 대기가 끝났다: 대기 원인을 지우고, 감시 세션을 썼으면 기다리는 세션이 더 없을 때 닫게 한다(기다리지 않는다).</summary>
    private static void EndProbe(RunState run, SideWatch side)
    {
        side.Cause = null;
        side.ShownCause = null;
        if (!side.Probed || run.WaitProbe is not { } probe)
        {
            return;
        }

        side.Probed = false;

        // 읽던 원인이 있으면 그 조회가 끝난 뒤 닫는다(먼저 닫으면 늦게 온 조회가 감시 세션을 다시 연다).
        var probing = side.Probing ?? Task.CompletedTask;
        side.Probing = null;
        _ = probing.ContinueWith(_ => probe.EndWaitAsync(run.Target), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
    }

    /// <summary>실행 끝: 볼 권한이 없어 대기 원인 감시를 그만두었으면 안내를 한 번 남긴다(WH1).</summary>
    private static void NoteWaitProbe(RunState run)
    {
        if (run.WaitProbe?.TakeNotice() is { } notice)
        {
            run.Notices.Add(notice);
        }
    }

    /// <summary>실행 기록(copy-runs)에 사건 한 줄. 기록 실패는 적재를 끊지 않는다(작업 로그에 경고만).</summary>
    private static async Task RecordEventAsync(RunState run, string kind, string? table, string text)
    {
        if (run.Record is not { } record)
        {
            return;
        }

        try
        {
            using var step = new CancellationTokenSource(CleanupTimeout);
            await record.RecordEventAsync(kind, table, text, step.Token);
        }
        catch (Exception ex)
        {
            // 기록 호출만 감싼다(권한·디스크 등 어떤 예외든): 적재·마무리를 끊지 않는다.
            run.Log("실행 기록에 남기지 못했습니다: " + SafeError(ex.Message), CopyLogKind.Warning, table);
        }
    }

    /// <summary>테이블 하나의 두 쪽 감시와, 돌아오지 않는 명령 신호.</summary>
    private sealed class TableWatch : IDisposable
    {
        private readonly TaskCompletionSource _stuck = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _sourceReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private SideWatch? _stuckSide;

        /// <param name="step">대상 쪽 명령의 종류(적재 밖의 대상 명령 하나를 감시할 때 정리·커밋). 원본 쪽은 언제나 적재 읽기다.</param>
        public TableWatch(TimeSpan? limit, CancellationToken run, CopyWaitStep step = CopyWaitStep.Load)
        {
            Run = run;
            Limit = limit;
            Source = new SideWatch(CopyWaitSide.Source, CopyWaitStep.Load, run);
            Target = new SideWatch(CopyWaitSide.Target, step, run);
            Sides = [Source, Target];
        }

        /// <summary>원본 읽기가 끝나 원본 커서를 닫았으면 끝나는 작업(원본 세션의 명령 관문이 풀렸다).</summary>
        public Task SourceReleased => _sourceReleased.Task;

        public void ReleaseSource() => _sourceReleased.TrySetResult();

        /// <summary>실행의 중지 토큰.</summary>
        public CancellationToken Run { get; }

        /// <summary>명령 하나의 대기 한도. null = 없음.</summary>
        public TimeSpan? Limit { get; }

        public SideWatch Source { get; }

        public SideWatch Target { get; }

        public IReadOnlyList<SideWatch> Sides { get; }

        /// <summary>취소한 명령이 돌아오지 않으면 끝나는 작업.</summary>
        public Task Stuck => _stuck.Task;

        public SideWatch? StuckSide => Volatile.Read(ref _stuckSide);

        /// <summary>대기 한도로 취소한 쪽(대상 먼저). 없으면 null.</summary>
        public SideWatch? LimitSide => Target.LimitHit ? Target : Source.LimitHit ? Source : null;

        public void SignalStuck(SideWatch side)
        {
            Interlocked.CompareExchange(ref _stuckSide, side, null);
            _stuck.TrySetResult();
        }

        public void CancelAll()
        {
            Source.Cancel();
            Target.Cancel();
        }

        public void Dispose()
        {
            Source.Dispose();
            Target.Dispose();
        }
    }

    /// <summary>
    /// 한 쪽(원본 읽기·대상 쓰기)의 감시: 지금 응답을 기다리는 명령이 언제부터인지, 그 쪽 명령의 취소 토큰(실행 중지와 이어짐 + 대기 한도·읽기 멈춤).
    /// 시각은 그 쪽 흐름이 쓰고 감시가 읽는다(Volatile). 감시만 쓰는 칸(<see cref="ShownMinute"/>·<see cref="CancelledAt"/>)은 감시 흐름 하나가 쓴다.
    /// </summary>
    private sealed class SideWatch : IDisposable
    {
        private readonly CancellationTokenSource _cts;
        private long _since; // Stopwatch 시각. 0 = 쉬는 중(명령을 기다리지 않음)
        private int _batch;
        private volatile bool _limitHit;
        private volatile bool _stopRequested;
        private string? _cause;

        public SideWatch(CopyWaitSide side, CopyWaitStep step, CancellationToken run)
        {
            Side = side;
            Step = step;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(run);
            Token = _cts.Token;
        }

        public CopyWaitSide Side { get; }

        /// <summary>기다리는 명령의 종류(적재·대상 정리·테이블 커밋).</summary>
        public CopyWaitStep Step { get; }

        /// <summary>작업 로그·진행 줄의 단계: 대상 정리면 「대상 정리」, 아니면 「적재」(테이블 커밋도 적재의 끝이다).</summary>
        public string Phase => Step == CopyWaitStep.Delete ? PhaseDelete : PhaseLoad;

        /// <summary>이 쪽 명령에 넘기는 토큰.</summary>
        public CancellationToken Token { get; }

        /// <summary>쓰는 배치 번호(대상만).</summary>
        public int Batch => Volatile.Read(ref _batch);

        /// <summary>대기 한도로 이 쪽 명령을 취소했다.</summary>
        public bool LimitHit
        {
            get => _limitHit;
            set => _limitHit = value;
        }

        /// <summary>쓰기가 멈춰 읽기를 멈추라고 취소했다.</summary>
        public bool StopRequested => _stopRequested;

        /// <summary>감시만 쓴다: 지금 보이는 멈춤 표시의 분(0 = 표시 없음).</summary>
        public int ShownMinute { get; set; }

        /// <summary>감시만 쓴다: 취소를 처음 본 시각(Stopwatch).</summary>
        public long? CancelledAt { get; set; }

        /// <summary>대기 원인(WH1, 대상만). 감시가 읽어 둔다. 대기가 끝나면 지운다.</summary>
        public string? Cause
        {
            get => Volatile.Read(ref _cause);
            set => Volatile.Write(ref _cause, value);
        }

        /// <summary>감시만 쓴다: 지금 보이는 대기 원인.</summary>
        public string? ShownCause { get; set; }

        /// <summary>감시만 쓴다: 이번 대기에 감시 세션을 썼다(대기가 끝나면 닫게 한다).</summary>
        public bool Probed { get; set; }

        /// <summary>감시만 쓴다: 읽고 있는 대기 원인(한 번에 하나).</summary>
        public Task? Probing { get; set; }

        /// <summary>지금 대기의 시작 시각(Stopwatch, 0 = 쉬는 중). 대기 원인을 읽는 사이 같은 대기인지 가린다(WH1).</summary>
        public long WaitStamp => Volatile.Read(ref _since);

        /// <summary>지금 명령을 기다린 시간. 쉬는 중이면 null.</summary>
        public TimeSpan? Waiting
        {
            get
            {
                var since = Volatile.Read(ref _since);
                return since == 0 ? null : Stopwatch.GetElapsedTime(since);
            }
        }

        /// <summary>배치 <paramref name="batch"/> 를 보내기 시작한다.</summary>
        public void Begin(int batch)
        {
            Volatile.Write(ref _batch, batch);
            Busy();
        }

        /// <summary>명령을 기다리기 시작한다.</summary>
        public void Busy() => Volatile.Write(ref _since, Stopwatch.GetTimestamp());

        /// <summary>응답이 왔다(행 하나·명령 하나): 기다린 시간을 새로 잰다.</summary>
        public void Touch() => Busy();

        /// <summary>명령을 기다리지 않는다(버퍼가 차 쓰기를 기다림·일시 정지·끝남).</summary>
        public void Idle() => Volatile.Write(ref _since, 0);

        /// <summary>쓰기가 멈췄으니 읽기를 멈춘다(이 쪽 명령 취소).</summary>
        public void StopReading()
        {
            _stopRequested = true;
            Cancel();
        }

        /// <summary>
        /// 이 쪽 명령을 취소한다. 드라이버의 취소(브레이크)가 붙잡혀도 감시·적재 흐름이 서지 않게 스레드 풀에서 보낸다.
        /// </summary>
        public void Cancel() => _ = Task.Run(() =>
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 표가 이미 끝났다.
            }
            catch (AggregateException)
            {
                // 취소 처리기(드라이버 취소)의 실패: 명령이 돌아오지 않으면 감시가 세션을 버린다.
            }
        });

        public CopyWait Wait(TimeSpan waited) =>
            new(Side, Side == CopyWaitSide.Target && Step == CopyWaitStep.Load ? Batch : null, waited) { Step = Step, Cause = Cause };

        /// <summary>단계 글: 「원본 읽는 중」·「배치 n 쓰는 중」·「대상 정리(DELETE) 중」·「테이블 커밋 중」.</summary>
        public string Describe() => Step switch
        {
            CopyWaitStep.Delete => "대상 정리(DELETE) 중",
            CopyWaitStep.Commit => "테이블 커밋 중",
            _ => Side == CopyWaitSide.Source ? "원본 읽는 중" : $"배치 {Batch.ToString(CultureInfo.InvariantCulture)} 쓰는 중",
        };

        public void Dispose() => _cts.Dispose();
    }
}

/// <summary>
/// 멈춤 감시 시각(결정 70·73). <paramref name="StallAfter"/> = 멈춤으로 보일 때까지(10초), <paramref name="Tick"/> = 감시 간격,
/// <paramref name="CancelGrace"/> = 취소한 명령이 돌아오기를 기다리는 시간(넘으면 그 세션을 버린다), <paramref name="LimitUnit"/> = 대기 한도의 단위(1분).
/// </summary>
internal sealed record CopyWatchTiming(TimeSpan StallAfter, TimeSpan Tick, TimeSpan CancelGrace, TimeSpan LimitUnit)
{
    /// <summary>대기 원인을 읽기 시작하는 대기(WH1, 기본 <see cref="CopyWaitProbe.Threshold"/> 30초). 시험이 줄인다.</summary>
    public TimeSpan ProbeAfter { get; init; } = CopyWaitProbe.Threshold;

    public static readonly CopyWatchTiming Default = new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1));
}
