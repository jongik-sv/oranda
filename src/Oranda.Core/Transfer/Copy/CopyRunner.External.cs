using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.Versioning;
using Oranda.Core.Execution;
using Oranda.Core.Text;
using Oranda.Core.Transfer.Copy.ExternalLoad;

namespace Oranda.Core.Transfer.Copy;

/// <summary>
/// 외부 적재 도구로 테이블 하나 넣기(조정 결정 88·89, XL1·XL2). 사전 점검이 외부 도구로 정한 테이블(<see cref="CopyTablePlan.Load"/>)만 온다.
/// <para>
/// 흐름: 대상 트랜잭션 확정(정리 DELETE 가 도구의 잠금을 막지 않게) → 원본 읽기(마스킹·이식 값) → 임시 폴더에 데이터 파일 → 제어 파일 → 도구 실행
/// (이번에 만든 테이블 = direct path, 이미 있는 테이블 = 일반 경로) → 로그·bad 읽기 → 넣은 수·실패 행. 임시 폴더는 끝나면(실패·취소도) 지운다.
/// 비밀번호는 표준 입력(또는 현재 사용자만 읽는 임시 parfile)으로만 넘기고 로그·명령줄·예외 글에 넣지 않는다.
/// </para>
/// <para>
/// 실패(도구 실행 실패·실패 행 상한·인덱스를 못 쓰게 됨·넣은 수를 모름)면 그 테이블을 정리한 뒤 기본 방식으로 다시 넣는다(<see cref="ExternalEnd.Fallback"/>).
/// 이미 있는 테이블의 추가만 하기는 도구가 넣은 행을 가려 지울 수 없어 다시 하지 않고 실패로 둔다. 취소하면 도구 프로세스 트리를 끝낸다(실행기 몫).
/// 도구가 도는 동안은 일시 정지·대기 한도가 듣지 않는다(도구 한 번이 한 명령).
/// </para>
/// </summary>
public partial class CopyRunner
{
    internal const string PhaseExternal = "외부 도구";

    private enum ExternalEnd
    {
        /// <summary>도구가 넣고 확정했다(실패 행은 결과에 넣었다).</summary>
        Loaded,

        /// <summary>도구로 못 넣어 테이블을 정리했다: 부른 쪽이 기본 방식으로 다시 넣는다.</summary>
        Fallback,

        /// <summary>테이블이 실패했다(실패로 두었다).</summary>
        Failed,
    }

    /// <summary>외부 도구로 넣을 테이블인가(사전 점검이 정했고, 이 실행에 준비물과 쓸 수 있는 도구가 있다).</summary>
    private static bool UsesExternalTool(RunState run, CopyTablePlan plan) =>
        plan.Load == LoadMethod.ExternalTool && run.External is not null && run.Report.ExternalTool is { Usable: true } && !run.IsRetry;

    private async Task<ExternalEnd> LoadExternalAsync(RunState run, CopyTablePlan plan, TableState table, RowShape shape, long? estimated, CancellationToken ct)
    {
        var external = run.External!;
        var tool = run.Report.ExternalTool!;
        var name = CopyLoad.ToolName(tool.Kind);
        var fresh = CopyLoad.Fresh(plan);
        var direct = CopyLoad.Direct(plan);

        // 1. 정리(DELETE)·앞 테이블 적재분을 확정한다: 커밋 안 된 DELETE 의 잠금을 도구 세션이 끝없이 기다리지 않게.
        var commit = await ControlLoggedAsync(run, run.Target, CopyCommonStatements.Commit(), ct, $"{name} 적재 전 정리 확정");
        if (!RunState.Accept(commit, table, "외부 도구 적재 전에 정리를 확정하지 못했습니다", ct))
        {
            return ExternalEnd.Failed;
        }

        run.DeletesPersisted = true;
        var login = await external.TargetLogin(ct);
        if (login is null)
        {
            return await FallBackAsync(run, plan, table, "대상 접속 정보(비밀번호)를 다시 받지 못했습니다", ct);
        }

        if (!run.Job.ConsistentRead && !await BeginTableReadAsync(run, table, ct))
        {
            return ExternalEnd.Failed;
        }

        var columns = CopyLoad.Columns(plan, shape.CopyColumns, _sourceDbms, _targetDbms);
        if (Interlocked.Exchange(ref run.ExternalStaleCleaned, 1) == 0)
        {
            // 앱이 죽어 남은 예전 실행의 임시 폴더(하루 넘은 것)를 실행당 한 번 지운다.
            LoaderWorkspace.DeleteStale(external.WorkRoot, TimeSpan.FromDays(1));
        }

        var select = run.Builder.SourceSelect(table.Name, shape.CopyColumns);
        run.LogStatement(run.Source, select.Sql, select.Binds);
        var streaming = Streams(external, tool);
        var chunkRows = !streaming && external.ChunkRows > 0 ? external.ChunkRows : long.MaxValue;
        var feed = streaming ? "파이프로 흘려 넣음(도구 한 번)"
            : chunkRows < long.MaxValue ? $"조각 {chunkRows.ToString("N0", CultureInfo.InvariantCulture)}행씩(쓰기와 적재를 겹침)" : "데이터 파일 하나";
        run.Log($"{name} 로 넣습니다({(direct ? "direct path" : "일반 경로")}{(direct && !fresh ? ", 이미 있는 테이블" : string.Empty)}) · {feed}");

        // 이미 있는 테이블의 direct path: 도구가 FK·CHECK 를 끄고 넣은 뒤 다시 켠다(REENABLE). 다시 켜지 못한 제약을 찾으려고 전에 꺼진 것을 적어 둔다.
        var disabledBefore = direct && !fresh ? await DisabledConstraintsAsync(run, plan, ct) : null;

        // 2. 원본 → 조각 데이터 파일 → 도구(결정 97). 조각 k 를 도구가 넣는 동안 조각 k+1 을 쓴다. 도구 실행은 한 번에 하나(같은 테이블 direct path 는 겹칠 수 없다).
        var started = Stopwatch.GetTimestamp();
        var maxFailed = run.Job.Options.MaxFailedRows;
        var lobFix = new LobFix();
        var pipe = new ChunkPipe(run, plan, table, shape, columns, tool, login, direct, streaming, maxFailed, estimated, ct);
        string? problem = null;
        try
        {
            var size = Math.Max(1, run.Job.Options.BatchSize);
            ExternalChunk? current = null;
            try
            {
                await foreach (var row in _bulk.StreamRowsAsync(run.Source, CopyCommonStatements.Query(select), select.Binds, SourceReadOptions(run), ct))
                {
                    var masked = shape.Apply(row);
                    table.SourceRows++;
                    if (current is null)
                    {
                        var (opened, openProblem) = await pipe.NewChunkAsync();
                        if (opened is null)
                        {
                            problem = openProblem ?? $"{name} 실행을 준비하지 못했습니다";
                            break; // 파이프: 도구가 파이프를 열기 전에 끝났다
                        }

                        current = opened;
                    }

                    if (shape.TryToPortable(masked, out var portable, out var error)
                        && current.Writer.TryWrite(portable, KeyTexts(shape, masked), out error))
                    {
                        lobFix.Note(columns, portable, shape, masked);
                    }
                    else
                    {
                        table.PendingFailed.Add(new FailedRow(table.Name, shape.KeyOf(masked), error!));
                        if (table.PendingFailed.Count > maxFailed)
                        {
                            await pipe.DrainAsync();
                            table.Fail($"외부 도구용 데이터 파일을 쓰다 실패 행이 상한({maxFailed.ToString("N0", CultureInfo.InvariantCulture)})을 넘었습니다.");
                            table.PendingFailed.Clear();
                            return ExternalEnd.Failed;
                        }
                    }

                    if (table.SourceRows % size == 0)
                    {
                        run.Progress(table.Name, table.SourceRows, estimated, PhaseLoad);
                    }

                    if (current.Writer.Records >= chunkRows)
                    {
                        problem = await pipe.HandAsync(current);
                        current = null;
                        if (problem is not null)
                        {
                            break; // 원본 읽기를 멈춘다(열거를 끝내면 질의도 닫힌다)
                        }
                    }
                }
            }
            catch (IOException ex) when (!ct.IsCancellationRequested && streaming && current is not null)
            {
                // 파이프에 쓰다 끊겼다: 도구가 먼저 끝났다(치명 오류·실패 행 상한 등). 도구의 결과로 판정한다.
                problem = await pipe.BrokenAsync(current, ex);
                current = null;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && (CopyErrors.IsServer(_errors, ex) || ex is IOException or UnauthorizedAccessException))
            {
                await pipe.DrainAsync();
                table.Fail((ex is IOException or UnauthorizedAccessException ? "외부 도구용 데이터 파일을 쓰지 못했습니다: " : "원본을 읽지 못했습니다: ") + SafeError(ex.Message));
                return ExternalEnd.Failed;
            }

            if (problem is null && current is not null)
            {
                problem = await pipe.HandAsync(current);
            }

            problem ??= await pipe.FinishAsync();
        }
        finally
        {
            await pipe.DrainAsync(); // 취소·예외로 나가도 도는 도구를 기다리고 조각 폴더를 지운다
            table.Times.AddReadTicks(pipe.WriteTicks);
            table.Times.AddWriteTicks(pipe.ToolTicks);
            table.Times.AddLoad(started); // 적재 구간 = 조각 쓰기 + 도구(겹침). 결과의 Times 로 나간다(#182)
            table.Pumped = true;
        }

        run.Log($"데이터 파일 {pipe.Records.ToString("N0", CultureInfo.InvariantCulture)}행 · 조각 {pipe.Chunks.ToString(CultureInfo.InvariantCulture)}개{(streaming ? "(파이프)" : string.Empty)}"
                + $" · 쓰기 {CopyLogText.Elapsed(Stopwatch.GetElapsedTime(0, pipe.WriteTicks))} · 도구 {CopyLogText.Elapsed(Stopwatch.GetElapsedTime(0, pipe.ToolTicks))}"
                + $" · 전체 {CopyLogText.Elapsed(Stopwatch.GetElapsedTime(started))}");

        // 3. 결과 판정
        if (problem is not null)
        {
            if (pipe.Loaded > 0 && plan.Rule.Action == TargetAction.AppendOnly && !fresh)
            {
                table.Fail($"{name} 적재가 실패했습니다({problem}). 추가만 하기라 도구가 넣은 {pipe.Loaded.ToString("N0", CultureInfo.InvariantCulture)}행이 남아 있어 기본 방식으로 다시 넣지 않았습니다.");
                table.Loaded = pipe.Loaded;
                table.PendingFailed.Clear();
                return ExternalEnd.Failed;
            }

            return await FallBackAsync(run, plan, table, $"{name} 적재가 실패했습니다({problem})", ct);
        }

        table.Loaded = pipe.Loaded;
        await FixLobNullsAsync(run, plan, table, shape, lobFix, ct);
        if (direct && !fresh)
        {
            await CheckReenabledAsync(run, plan, table, disabledBefore, ct);
        }

        // 도구가 커밋했다: 실패 행을 결과에 넣는다(기본 방식의 테이블마다 커밋과 같다). 변환 실패·도구 거절 행 수를 세어 결과를 「일부 실패」로.
        table.FailedRows += table.PendingFailed.Count;
        foreach (var failed in table.PendingFailed.Take(20))
        {
            run.Log($"실패 행: {failed.Error}", CopyLogKind.Error);
        }

        await ConfirmFailedRowsAsync(run, table);
        table.Note($"외부 도구({name})로 넣었습니다.");
        table.LoadCompleted = table.Outcome != CopyOutcome.Failed;
        return ExternalEnd.Loaded;
    }

    /// <summary>파이프로 흘려 넣는가(결정 98): 설정이 파이프이고 SQL*Loader·Windows 일 때만.</summary>
    private static bool Streams(CopyExternalLoad external, ExternalToolInfo tool) =>
        external.Feed == LoaderFeed.Pipe && tool.Kind == ExternalLoaderKind.SqlLoader && OperatingSystem.IsWindows();

    /// <summary>
    /// 조각 하나: 작업 폴더와 그 데이터 파일 쓰기(레코드 번호는 조각 안에서 1부터 — 거절 행 키는 그 조각의 키 파일에서 찾는다).
    /// <see cref="Pipe"/> 가 있으면 데이터는 파일이 아니라 그 파이프로 간다(결정 98).
    /// </summary>
    private sealed class ExternalChunk(int index, LoaderWorkspace workspace, LoaderDataWriter writer, NamedPipeServerStream? pipe = null) : IDisposable
    {
        public int Index { get; } = index;

        public LoaderWorkspace Workspace { get; } = workspace;

        public LoaderDataWriter Writer { get; } = writer;

        public NamedPipeServerStream? Pipe { get; } = pipe;

        public void Dispose()
        {
            if (Pipe is null)
            {
                Writer.Dispose();
            }
            else
            {
                // 파이프: 도구가 먼저 끝났거나 연결 전이면 비우기가 실패한다(남은 데이터는 버린다).
                try
                {
                    Writer.Dispose();
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
                {
                }

                Pipe.Dispose();
            }

            Workspace.Dispose();
        }
    }

    /// <summary>조각 하나의 도구 실행 결과.</summary>
    private sealed record ChunkRun(ExternalProcessResult Result, LoaderLogResult Log, string? LogText);

    /// <summary>
    /// 조각 파일 쓰기와 도구 실행 겹치기(결정 97). <see cref="HandAsync"/> = 다 쓴 조각을 넘긴다: 앞 조각의 도구가 끝나기를 기다려 판정한 뒤 이 조각의 도구를 띄우고 바로 돌아온다
    /// (부른 쪽은 다음 조각을 쓴다). <see cref="FinishAsync"/> = 마지막 도구를 기다려 판정. 실패 이유(문자열)를 돌려주면 부른 쪽이 멈춘다. <see cref="DrainAsync"/> = 정리(예외·취소 포함).
    /// <para>
    /// 파이프(<paramref name="streaming"/>, 결정 98): 조각은 하나뿐이다. <see cref="NewChunkAsync"/> 가 파이프를 만들고 도구를 띄운 뒤 도구가 파이프를 열 때까지 기다린다.
    /// 부른 쪽이 행을 쓰는 동안 도구가 읽고, <see cref="HandAsync"/> 가 도구가 다 읽기를 기다려 파이프를 닫고(도구는 파일 끝으로 본다) 도구 결과를 판정한다.
    /// 중간에 멈추면(<see cref="DrainAsync"/>) 도구를 먼저 끝낸다 — 파이프를 닫으면 도구가 받은 데까지 넣고 확정하기 때문이다.
    /// </para>
    /// </summary>
    private sealed class ChunkPipe(RunState run, CopyTablePlan plan, TableState table, RowShape shape, IReadOnlyList<LoaderColumn> columns,
                                   ExternalToolInfo tool, ExternalLoadLogin login, bool direct, bool streaming, int maxFailed, long? estimated, CancellationToken ct)
    {
        /// <summary>파이프 쓰기 버퍼(바이트).</summary>
        private const int PipeBufferBytes = 1 << 20;

        private readonly List<ExternalChunk> _open = [];
        private ExternalChunk? _pendingChunk;
        private Task<ChunkRun>? _pending;
        private CancellationTokenSource? _toolCts;
        private long _chunkStarted;

        public long Loaded { get; private set; }

        public long Records { get; private set; }

        public int Chunks { get; private set; }

        /// <summary>조각을 쓰는 데(원본 읽기 포함) 쓴 시간: 조각의 첫 행부터 다 쓸 때까지. 도구를 기다린 시간은 들지 않는다(파이프는 도구가 읽기를 기다린 시간이 든다).</summary>
        public long WriteTicks { get; private set; }

        /// <summary>도구가 돈 시간(조각 합).</summary>
        public long ToolTicks { get; private set; }

        private string Name => CopyLoad.ToolName(tool.Kind);

        /// <summary>새 조각. 파이프면 도구를 띄우고 파이프를 열 때까지 기다린다 — 그 전에 도구가 끝나면 (null, 실패 이유).</summary>
        public async Task<(ExternalChunk? Chunk, string? Problem)> NewChunkAsync()
        {
            Chunks++;
            var workspace = LoaderWorkspace.Create(run.External!.WorkRoot, run.RunId, table.Name);
            var format = ExternalLoadControl.FileFormat(tool.Kind);
            if (!streaming)
            {
                var chunk = new ExternalChunk(Chunks, workspace, new LoaderDataWriter(workspace.Directory, columns, format));
                _open.Add(chunk);
                _chunkStarted = Stopwatch.GetTimestamp();
                return (chunk, null);
            }

            ExternalChunk piped;
            NamedPipeServerStream? server = null;
            try
            {
                // 현재 사용자만 열 수 있는 파이프(다른 사용자가 먼저 붙어 데이터를 읽지 못하게). 이름은 실행마다 새로.
                var pipeName = $"oranda-load-{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}-{Guid.NewGuid():N}";
                server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                                                   PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 0, PipeBufferBytes);
                piped = new ExternalChunk(Chunks, workspace, new LoaderDataWriter(workspace.Directory, columns, format, server, PipePath(pipeName)), server);
            }
            catch
            {
                server?.Dispose();
                workspace.Dispose();
                throw;
            }

            _open.Add(piped);
            _chunkStarted = Stopwatch.GetTimestamp();
            if (Start(piped) is { } problem)
            {
                return (null, problem);
            }

            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var connect = piped.Pipe!.WaitForConnectionAsync(stop.Token);
            if (await Task.WhenAny(connect, _pending!) != connect)
            {
                await stop.CancelAsync();
                try
                {
                    await connect;
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
                {
                }

                ct.ThrowIfCancellationRequested();
                return (null, await JudgePendingAsync() ?? $"{Name} 이(가) 데이터 파이프를 열지 않고 끝났습니다");
            }

            await connect;
            return (piped, null);
        }

        public async Task<string?> HandAsync(ExternalChunk chunk)
        {
            if (streaming)
            {
                // 도구가 다 읽기를 기다려 파이프를 닫는다(도구는 파일 끝으로 본다). 그 뒤 도구가 끝나기를 기다려 판정.
                string? broken = null;
                try
                {
                    chunk.Writer.Flush();
                    if (OperatingSystem.IsWindows())
                    {
                        WaitForDrain(chunk.Pipe!);
                    }

                    chunk.Writer.Complete();
                }
                catch (IOException ex)
                {
                    broken = SafeError(ex.Message);
                }

                WriteTicks += Stopwatch.GetTimestamp() - _chunkStarted;
                Records += chunk.Writer.Records;
                return await JudgePendingAsync() ?? (broken is null ? null : $"{Name} 이(가) 데이터를 다 읽기 전에 파이프를 닫았습니다: {broken}");
            }

            chunk.Writer.Complete();
            WriteTicks += Stopwatch.GetTimestamp() - _chunkStarted;
            Records += chunk.Writer.Records;
            if (await JudgePendingAsync() is { } problem)
            {
                return problem;
            }

            return Start(chunk);
        }

        /// <summary>파이프에 쓰다 끊겼다(도구가 먼저 닫았다): 쓰기를 접고 도구 결과로 판정한다.</summary>
        public async Task<string> BrokenAsync(ExternalChunk chunk, IOException ex)
        {
            try
            {
                chunk.Writer.Complete();
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException)
            {
            }

            WriteTicks += Stopwatch.GetTimestamp() - _chunkStarted;
            Records += chunk.Writer.Records;
            return await JudgePendingAsync() ?? $"{Name} 이(가) 데이터를 다 읽기 전에 파이프를 닫았습니다: {SafeError(ex.Message)}";
        }

        public Task<string?> FinishAsync() => JudgePendingAsync();

        /// <summary>도는 도구를 기다리고(결과는 버린다) 남은 조각 폴더를 지운다. 파이프면 도구를 먼저 끝낸다(받은 데까지 확정하지 않게). 여러 번 불러도 된다.</summary>
        public async Task DrainAsync()
        {
            if (_pending is { } pending)
            {
                _pending = null;
                if (streaming && _toolCts is { } toolCts)
                {
                    await toolCts.CancelAsync(); // 실행기가 프로세스 트리를 끝낸다
                }

                try
                {
                    await pending;
                }
                catch (Exception ex) when (ex is OperationCanceledException or ExternalProcessException or IOException or UnauthorizedAccessException)
                {
                    // 이미 실패·취소로 나가는 길이다.
                }
            }

            foreach (var chunk in _open)
            {
                chunk.Dispose();
                if (chunk.Workspace.CleanupFailure is { } failure)
                {
                    run.Notices.Add($"{table.Name}: 외부 도구 임시 폴더를 지우지 못했습니다({failure}).");
                }
            }

            _open.Clear();
            _toolCts?.Dispose();
            _toolCts = null;
        }

        /// <summary>이름 있는 파이프의 Win32 경로.</summary>
        internal static string PipePath(string pipeName) => @"\\.\pipe\" + pipeName;

        [SupportedOSPlatform("windows")]
        private static void WaitForDrain(NamedPipeServerStream pipe) => pipe.WaitForPipeDrain();

        /// <summary>이 조각의 도구를 띄운다(기다리지 않음). 준비하지 못하면 실패 이유.</summary>
        private string? Start(ExternalChunk chunk)
        {
            // 남은 실패 행 한도 안에서 이 조각을 넘긴다.
            var errors = Math.Max(0, maxFailed - table.PendingFailed.Count);
            PreparedLoad prepared;
            try
            {
                prepared = ExternalLoadControl.Prepare(tool.Kind, chunk.Workspace, tool.Path!, TargetOwnerOf(plan), plan.Rule.Table, columns, chunk.Writer.DataFile,
                                                       errors, direct, login, run.External!.PasswordVia);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                return $"{Name} 실행을 준비하지 못했습니다: {SafeError(ex.Message)}";
            }

            if (chunk.Index == 1)
            {
                run.Log($"{Name} 실행: {prepared.Description}", CopyLogKind.Statement);
            }

            if (!streaming)
            {
                run.Progress(table.Name, table.SourceRows, estimated, PhaseExternal);
            }

            _toolCts?.Dispose();
            _toolCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _pendingChunk = chunk;
            _pending = RunToolAsync(chunk, prepared, _toolCts.Token);
            return null;
        }

        private async Task<ChunkRun> RunToolAsync(ExternalChunk chunk, PreparedLoad prepared, CancellationToken token)
        {
            var toolStarted = Stopwatch.GetTimestamp();
            ExternalProcessResult result;
            try
            {
                prepared.Begin(); // 비밀 parfile(조정 결정 89 차선)이면 여기서 쓴다
                result = await run.External!.Runner.RunAsync(prepared.Start, token);
            }
            finally
            {
                if (!prepared.End())
                {
                    run.Notices.Add($"{table.Name}: {Name} 의 임시 parfile 을 바로 지우지 못했습니다(작업 폴더 정리가 다시 지웁니다).");
                }

                ToolTicks += Stopwatch.GetTimestamp() - toolStarted;
            }

            // 로그 파일을 만들기 전에 멈췄으면(접속 실패 등) 표준 출력을 같은 규칙으로 읽는다.
            var logText = chunk.Workspace.ReadText(prepared.LogFile);
            return new ChunkRun(result, ExternalLoadControl.ParseLog(tool.Kind, logText ?? result.StandardOutput), logText);
        }

        /// <summary>앞 조각의 도구를 기다려 판정한다: 실패 이유 또는 null(넣은 수·거절 행을 반영하고 그 조각 폴더를 지운다).</summary>
        private async Task<string?> JudgePendingAsync()
        {
            if (_pending is not { } pending || _pendingChunk is not { } chunk)
            {
                return null;
            }

            ChunkRun done;
            try
            {
                done = await pending;
            }
            catch (Exception ex) when (ex is ExternalProcessException or IOException or UnauthorizedAccessException)
            {
                _pending = null;
                return $"{Name} 을(를) 실행하지 못했습니다: {SafeError(ex.Message)}";
            }
            _pending = null;
            _pendingChunk = null;
            var (result, log, logText) = done;
            run.Log($"{Name} 끝(조각 {chunk.Index.ToString(CultureInfo.InvariantCulture)}) · 종료 코드 {result.ExitCode.ToString(CultureInfo.InvariantCulture)}"
                    + $" · 넣음 {(log.Loaded is { } n ? n.ToString("N0", CultureInfo.InvariantCulture) : "모름")} · 거부 {log.Rejected.ToString("N0", CultureInfo.InvariantCulture)}");
            if (ToolSummary(logText) is { Length: > 0 } summary)
            {
                run.Log($"{Name} 로그 요약: {summary}"); // Path used·Bind array·Elapsed time 등(속도 확인용)
            }

            foreach (var fatal in log.Fatal.Take(5))
            {
                run.Log($"{Name}: {SafeError(fatal)}", CopyLogKind.Warning);
            }

            Loaded += log.Loaded ?? 0;
            var problem = ExternalProblem(tool.Kind, result, log, table.PendingFailed.Count, maxFailed);
            if (problem is not null)
            {
                // 원인을 볼 수 있게 도구의 표준 출력·오류와 로그 파일을 작업 로그에 남긴다(비밀번호는 가린다, 길면 자른다).
                run.Log($"{Name} 출력(실패 원인 확인용):\n" + ToolOutput(result, logText, login.Password), CopyLogKind.Warning);
                return problem;
            }

            var keys = chunk.Writer.ReadKeys(log.Rejects.Select(r => r.Record).ToHashSet());
            foreach (var reject in log.Rejects)
            {
                var key = keys.TryGetValue(reject.Record, out var texts) ? KeyOf(shape, texts) : new Dictionary<string, string?>(StringComparer.Ordinal);
                table.PendingFailed.Add(new FailedRow(table.Name, key, SafeError(reject.Error)));
            }

            chunk.Dispose();
            _open.Remove(chunk);
            return null;
        }
    }

    /// <summary>도구 로그에서 속도를 보는 줄(경로·바인드 배열·읽기 버퍼·걸린 시간)만 한 줄로.</summary>
    internal static string ToolSummary(string? logText)
    {
        if (string.IsNullOrEmpty(logText))
        {
            return string.Empty;
        }

        string[] wanted = ["Path used", "Bind array", "Space allocated for bind array", "Read   buffer bytes", "Elapsed time", "CPU time"];
        var picked = logText.Split('\n')
            .Select(l => l.Trim())
            .Where(l => wanted.Any(w => l.StartsWith(w, StringComparison.OrdinalIgnoreCase)))
            .Select(l => string.Join(' ', l.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
        return string.Join(" · ", picked);
    }

    /// <summary>작업 로그에 남길 도구 출력: 표준 출력·표준 오류·로그 파일(각 앞 4,000자), 비밀번호 자리는 가린다.</summary>
    internal static string ToolOutput(ExternalProcessResult result, string? logText, string? password)
    {
        static string Part(string title, string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return $"[{title}] (없음)";
            }

            var t = text.Trim();
            return $"[{title}]\n" + (t.Length > 4000 ? t[..4000] + " …(잘림)" : t);
        }

        var all = string.Join("\n", Part("표준 출력", result.StandardOutput), Part("표준 오류", result.StandardError), Part("로그 파일", logText));
        return string.IsNullOrEmpty(password) ? all : all.Replace(password, SqlLoaderCommand.PasswordMask, StringComparison.Ordinal);
    }

    /// <summary>도구 결과에서 실패로 볼 이유(없으면 null): 실행 실패 종료 코드·넣은 수를 모름·실패 행 상한·인덱스를 못 쓰게 됨.</summary>
    private static string? ExternalProblem(ExternalLoaderKind kind, ExternalProcessResult result, LoaderLogResult log, int convertFailed, int maxFailed)
    {
        if (result.TimedOut)
        {
            return "시간 제한";
        }

        if (!ExternalLoadControl.ExitOk(kind, result.ExitCode))
        {
            return $"종료 코드 {result.ExitCode.ToString(CultureInfo.InvariantCulture)}" + (log.Fatal.Count > 0 ? " — " + SafeError(string.Join(" ", log.Fatal.Take(4))) : string.Empty);
        }

        if (log.Loaded is null)
        {
            return "로그에서 넣은 행 수를 읽지 못함";
        }

        if (log.ErrorLimitHit || convertFailed + log.Rejected > maxFailed)
        {
            return $"실패 행이 상한({maxFailed.ToString("N0", CultureInfo.InvariantCulture)})을 넘음";
        }

        return log.UnusableIndexes.Count > 0 ? "인덱스를 못 쓰게 됨: " + string.Join(", ", log.UnusableIndexes) : null;
    }

    /// <summary>
    /// 도구로 못 넣었다: 그 테이블을 다시 정리(기본 방식이 지우는 범위, 이번에 만든 테이블은 전부)한 뒤 기본 방식으로 넣게 돌려준다.
    /// 정리할 수 없는 테이블(이미 있는 테이블의 추가만 하기)·정리 실패면 실패로 둔다.
    /// </summary>
    private async Task<ExternalEnd> FallBackAsync(RunState run, CopyTablePlan plan, TableState table, string reason, CancellationToken ct)
    {
        table.PendingFailed.Clear();
        table.SourceRows = 0;
        table.Loaded = 0;
        if (CopyLoad.Direct(plan) && !CopyLoad.Fresh(plan) && await TruncateAfterDirectAsync(run, plan, ct))
        {
            run.Notices.Add($"{table.Name}: {reason}. 테이블을 비운 뒤(TRUNCATE — direct path 가 못 쓰게 둔 인덱스도 되살림) 기본 방식으로 다시 넣습니다.");
            table.Note(reason + " — 기본 방식으로 다시 넣었습니다.");
            return ExternalEnd.Fallback;
        }

        var delete = run.Builder.TargetDelete(table.Name);
        var options = run.HistoryOptions;
        if (delete is null && CopyLoad.Fresh(plan))
        {
            // 이번 실행이 만든 테이블은 사전 점검에 정리 문장이 없다: 도구가 넣다 만 행을 비우는 DELETE 는 테이블을 만든 승인의 일부로 본다
            // (WHERE 없는 DELETE 확인 게이트에 막히지 않게, #177).
            delete = new SqlWithBinds($"DELETE FROM {CopyCommonStatements.Qualified(TargetOwnerOf(plan), plan.Rule.Table)}", new Dictionary<string, BindValue>(StringComparer.Ordinal));
            var confirmed = new HashSet<string>(run.HistoryOptions.PreConfirmed ?? new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal)
            {
                CopyCommonStatements.Delete(delete).Text,
            };
            options = run.HistoryOptions with { PreConfirmed = confirmed };
        }

        if (delete is null)
        {
            if (plan.Rule.Action == TargetAction.AppendOnly)
            {
                // 도구가 아무것도 넣지 않았으면(넣기 전에 실패) 기본 방식으로 넣어도 겹치지 않는다.
                run.Notices.Add($"{table.Name}: {reason}. 기본 방식으로 넣습니다.");
                table.Note(reason + " — 기본 방식으로 넣었습니다.");
                return ExternalEnd.Fallback;
            }

            table.Fail(reason + ".");
            return ExternalEnd.Failed;
        }

        var r = await ExecuteLoggedAsync(run, run.Target, CopyCommonStatements.Delete(delete), delete.Binds, ct, options);
        if (r.Status != ExecutionStatus.Succeeded)
        {
            table.Fail($"{reason}. 기본 방식으로 다시 넣으려고 정리(DELETE)했지만 실패했습니다: {Describe(r)}");
            return ExternalEnd.Failed;
        }

        run.Notices.Add($"{table.Name}: {reason}. 테이블을 정리한 뒤 기본 방식으로 다시 넣습니다.");
        table.Note(reason + " — 기본 방식으로 다시 넣었습니다.");
        return ExternalEnd.Fallback;
    }

    /// <summary>LOB 값 하나(빈 값 또는 NULL): 열 이름·BLOB 인가·그 행의 키.</summary>
    private sealed record LobMark(string Column, bool Binary, Dictionary<string, string?> Key);

    /// <summary>
    /// 외부 도구로 넣은 LOB 의 NULL·빈 값 바로잡기 재료(결정 96). SQL*Loader 는 LOBFILE 이름 칸이 비어도(NULL) 빈 LOB 을 넣는다(win144 #183:
    /// 원본 NULL 이 대상 길이 0). 그래서 적재 뒤 원본 NULL 이 있던 열의 빈 LOB 을 NULL 로 돌리고, 원본이 정말 빈 값이던 행만 키로 빈 LOB 으로 되돌린다.
    /// </summary>
    private sealed class LobFix
    {
        /// <summary>키로 하나씩 고치는 값은 이만큼까지(넘으면 알림만).</summary>
        public const int MaxKeyed = 10_000;

        /// <summary>원본에 NULL 이 있던 LOB 열(이름 → BLOB 인가).</summary>
        public Dictionary<string, bool> NullColumns { get; } = new(StringComparer.Ordinal);

        /// <summary>원본 NULL 값(키). 테이블 전체·범위를 넣지 않았을 때(추가만 하기) 키로 고친다.</summary>
        public List<LobMark> Nulls { get; } = [];

        /// <summary>원본 빈 값(키).</summary>
        public List<LobMark> Empties { get; } = [];

        public bool NullsOverflow { get; private set; }

        public bool EmptiesOverflow { get; private set; }

        public void Note(IReadOnlyList<LoaderColumn> columns, object?[] portable, RowShape shape, object?[] masked)
        {
            Dictionary<string, string?>? key = null;
            for (var i = 0; i < columns.Count && i < portable.Length; i++)
            {
                var type = columns[i].TargetType.Trim().ToUpperInvariant();
                var binary = type.StartsWith("BLOB", StringComparison.Ordinal);
                if (!binary && !type.StartsWith("CLOB", StringComparison.Ordinal) && !type.StartsWith("NCLOB", StringComparison.Ordinal))
                {
                    continue;
                }

                if (portable[i] is null or DBNull)
                {
                    NullColumns[columns[i].Name] = binary;
                    if (Nulls.Count < MaxKeyed)
                    {
                        Nulls.Add(new LobMark(columns[i].Name, binary, key ??= shape.KeyOf(masked)));
                    }
                    else
                    {
                        NullsOverflow = true;
                    }
                }
                else if (portable[i] is byte[] { Length: 0 } or string { Length: 0 })
                {
                    if (Empties.Count < MaxKeyed)
                    {
                        Empties.Add(new LobMark(columns[i].Name, binary, key ??= shape.KeyOf(masked)));
                    }
                    else
                    {
                        EmptiesOverflow = true;
                    }
                }
            }
        }
    }

    /// <summary>
    /// LOB 의 NULL·빈 값 바로잡기(<see cref="LobFix"/>). 이번 적재가 채운 행만 고친다:
    /// 테이블 전체를 넣었으면(새 테이블·「모두 지우고 넣기」) 열마다 한 문장(길이 0 인 값을 NULL 로), 범위 지우고 넣기면 그 범위 조건으로,
    /// 그 밖(추가만 하기)은 키로 하나씩. 그 뒤 원본이 빈 값이던 행을 키로 <c>EMPTY_BLOB()</c>·<c>EMPTY_CLOB()</c> 로. 조건이 「지금 값이 틀린 행」이라 도구가 맞게 넣었으면 바뀌는 것이 없다.
    /// 키가 없거나 너무 많거나 실패하면 알림만(적재 결과는 그대로).
    /// </summary>
    private async Task FixLobNullsAsync(RunState run, CopyTablePlan plan, TableState table, RowShape shape, LobFix fix, CancellationToken ct)
    {
        if (fix.NullColumns.Count == 0 && fix.Empties.Count == 0)
        {
            return;
        }

        var target = CopyCommonStatements.Qualified(TargetOwnerOf(plan), plan.Rule.Table);
        var keyWhere = string.Join(" AND ", shape.KeyNames.Select((k, i) => $"{SqlIdentifier.QuoteAlways(k)} = :K{(i + 1).ToString(CultureInfo.InvariantCulture)}"));
        var hasKey = shape.KeyNames.Count > 0;
        var delete = run.Builder.TargetDelete(table.Name);
        var whole = CopyLoad.Fresh(plan) || (delete is not null && !delete.Sql.Contains(" WHERE ", StringComparison.Ordinal));
        var range = !whole && delete is not null ? delete.Sql[(delete.Sql.IndexOf(" WHERE ", StringComparison.Ordinal) + 7)..] : null;
        var changed = 0L;

        async Task<bool> Exec(string sql, IReadOnlyDictionary<string, BindValue> binds)
        {
            var stmt = new SqlStatement(sql, default, StatementKind.Dml) { Verb = "UPDATE" };
            var confirmed = new HashSet<string>(run.HistoryOptions.PreConfirmed ?? new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal) { sql };
            var r = await _execution.ExecuteAsync(run.Target, stmt, binds, run.HistoryOptions with { SkipHistory = true, PreConfirmed = confirmed }, ct);
            if (r.Status != ExecutionStatus.Succeeded)
            {
                run.Log($"LOB NULL·빈 값 바로잡기 실패: {Describe(r)}", CopyLogKind.Warning);
                run.Notices.Add($"{table.Name}: 외부 도구로 넣은 LOB 의 NULL·빈 값을 바로잡지 못했습니다(NULL 이 빈 LOB 으로 남았을 수 있음): {Describe(r)}");
                await ControlLoggedAsync(run, run.Target, CopyCommonStatements.Rollback(), ct, "LOB 바로잡기 취소");
                return false;
            }

            changed += r.RowsAffected ?? 0;
            return true;
        }

        // 1. 원본 NULL → 대상에 빈 LOB 으로 들어간 것을 NULL 로.
        foreach (var name in fix.NullColumns.Keys)
        {
            var column = SqlIdentifier.QuoteAlways(name);
            var wrong = $"{column} IS NOT NULL AND DBMS_LOB.GETLENGTH({column}) = 0";
            if (whole)
            {
                if (!await Exec($"UPDATE {target} SET {column} = NULL WHERE {wrong}", new Dictionary<string, BindValue>(StringComparer.Ordinal)))
                {
                    return;
                }
            }
            else if (range is not null)
            {
                if (!await Exec($"UPDATE {target} SET {column} = NULL WHERE ({range}) AND {wrong}", delete!.Binds))
                {
                    return;
                }
            }
            else if (!hasKey || fix.NullsOverflow)
            {
                run.Notices.Add($"{table.Name}: 열 {name} 의 NULL 이 외부 도구로 빈 LOB 으로 들어갔을 수 있습니다({(hasKey ? "값이 너무 많아" : "키가 없어")} 바로잡지 않았습니다).");
            }
            else
            {
                foreach (var mark in fix.Nulls.Where(m => string.Equals(m.Column, name, StringComparison.Ordinal)))
                {
                    if (!await Exec($"UPDATE {target} SET {column} = NULL WHERE {wrong} AND {keyWhere}", shape.KeyBinds(new FailedRow(table.Name, mark.Key, string.Empty))))
                    {
                        return;
                    }
                }
            }
        }

        // 2. 원본 빈 값 → 1 이 NULL 로 돌렸거나 도구가 NULL 로 넣은 것을 빈 LOB 으로.
        if (fix.Empties.Count > 0)
        {
            if (!hasKey || fix.EmptiesOverflow)
            {
                run.Notices.Add($"{table.Name}: 빈 LOB(EMPTY_BLOB·EMPTY_CLOB) 값이 NULL 로 남았을 수 있습니다({(hasKey ? "값이 너무 많아" : "키가 없어")} 되돌리지 않았습니다).");
            }
            else
            {
                foreach (var mark in fix.Empties)
                {
                    var column = SqlIdentifier.QuoteAlways(mark.Column);
                    var sql = $"UPDATE {target} SET {column} = {(mark.Binary ? "EMPTY_BLOB()" : "EMPTY_CLOB()")} WHERE {column} IS NULL AND {keyWhere}";
                    if (!await Exec(sql, shape.KeyBinds(new FailedRow(table.Name, mark.Key, string.Empty))))
                    {
                        return;
                    }
                }
            }
        }

        var commit = await ControlLoggedAsync(run, run.Target, CopyCommonStatements.Commit(), ct, "LOB NULL·빈 값 바로잡기 확정");
        if (commit.Status != ExecutionStatus.Succeeded)
        {
            run.Notices.Add($"{table.Name}: 외부 도구로 넣은 LOB 의 NULL·빈 값 바로잡기를 확정하지 못했습니다.");
            return;
        }

        run.Log($"LOB NULL·빈 값 바로잡기: NULL 열 {fix.NullColumns.Count.ToString("N0", CultureInfo.InvariantCulture)}개 · 빈 값 {fix.Empties.Count.ToString("N0", CultureInfo.InvariantCulture)}개 · 바꾼 행 {changed.ToString("N0", CultureInfo.InvariantCulture)}");
    }

    /// <summary>대상 테이블의 꺼진 제약 이름(이미 있는 테이블의 direct path 전·후 비교용). 읽지 못하면 null(비교하지 않는다).</summary>
    private async Task<HashSet<string>?> DisabledConstraintsAsync(RunState run, CopyTablePlan plan, CancellationToken ct)
    {
        try
        {
            var rows = await RowsAsync(run, run.Target,
                CopyCommonStatements.Query("SELECT CONSTRAINT_NAME FROM ALL_CONSTRAINTS WHERE OWNER = :OWN AND TABLE_NAME = :TBL AND STATUS = 'DISABLED'"),
                new Dictionary<string, BindValue>(StringComparer.Ordinal)
                {
                    ["OWN"] = new(BindDataType.Varchar2, TargetOwnerOf(plan)),
                    ["TBL"] = new(BindDataType.Varchar2, plan.Rule.Table),
                }, ct);
            return rows.Select(r => Convert.ToString(r[0], CultureInfo.InvariantCulture) ?? string.Empty).ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex) when (CopyErrors.IsServer(_errors, ex))
        {
            run.Log($"꺼진 제약을 읽지 못했습니다(direct path 뒤 다시 켜졌는지 확인하지 않음): {SafeError(ex.Message)}", CopyLogKind.Warning);
            return null;
        }
    }

    /// <summary>
    /// 이미 있는 테이블의 direct path 뒤: 도구가 끈 FK·CHECK 가 다시 켜졌는지 본다(결정 96). 다시 켜지 못한 제약(검증 실패 등)이 있으면
    /// 결과를 「일부 실패」로 두고 알린다 — 사용자가 고친 뒤 <c>ALTER TABLE … ENABLE CONSTRAINT</c> 로 켠다.
    /// </summary>
    private async Task CheckReenabledAsync(RunState run, CopyTablePlan plan, TableState table, HashSet<string>? before, CancellationToken ct)
    {
        if (before is null || await DisabledConstraintsAsync(run, plan, ct) is not { } after)
        {
            return;
        }

        var left = after.Except(before, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (left.Count == 0)
        {
            return;
        }

        var names = string.Join(", ", left);
        table.Degrade($"direct path 적재 뒤 다시 켜지 못한 제약이 있습니다: {names}. 데이터를 확인한 뒤 ALTER TABLE … ENABLE CONSTRAINT 로 켜세요.");
        run.Notices.Add($"{table.Name}: direct path 적재 뒤 제약 {names} 이(가) 꺼진 채 남았습니다(다시 켤 때 검증 실패).");
    }

    /// <summary>
    /// 이미 있는 테이블에 direct path 로 넣다 실패했을 때의 정리: <c>TRUNCATE</c> 는 도구가 못 쓰게 둔 인덱스도 다시 쓸 수 있게 만든다
    /// (DELETE 는 못 쓰는 고유 인덱스에 막힌다). 이 실행의 정리가 이미 테이블 전체를 비웠으므로 지우는 범위는 같다. 실패하면 false(부른 쪽이 DELETE 로 다시 한다).
    /// </summary>
    private async Task<bool> TruncateAfterDirectAsync(RunState run, CopyTablePlan plan, CancellationToken ct)
    {
        var stmt = CopyCommonStatements.RecreateDdl($"TRUNCATE TABLE {CopyCommonStatements.Qualified(TargetOwnerOf(plan), plan.Rule.Table)}");
        var confirmed = new HashSet<string>(run.HistoryOptions.PreConfirmed ?? new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal) { stmt.Text };
        var r = await ExecuteLoggedAsync(run, run.Target, stmt, new Dictionary<string, BindValue>(StringComparer.Ordinal), ct,
                                         run.HistoryOptions with { PreConfirmed = confirmed });
        return r.Status == ExecutionStatus.Succeeded;
    }

    private static string TargetOwnerOf(CopyTablePlan plan) => plan.Target.Table.Owner;

    private static IReadOnlyList<string?> KeyTexts(RowShape shape, object?[] row)
    {
        var key = shape.KeyOf(row);
        return [.. shape.KeyNames.Select(k => key.TryGetValue(k, out var v) ? v : null)];
    }

    private static Dictionary<string, string?> KeyOf(RowShape shape, IReadOnlyList<string?> texts)
    {
        var key = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = 0; i < shape.KeyNames.Count && i < texts.Count; i++)
        {
            key[shape.KeyNames[i]] = texts[i];
        }

        return key;
    }
}
