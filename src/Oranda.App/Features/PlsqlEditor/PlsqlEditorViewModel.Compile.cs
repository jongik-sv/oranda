using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Oranda.App.Features.Execution;
using Oranda.App.Features.Localization;
using Oranda.App.Shell.Settings;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Settings;

namespace Oranda.App.Features.PlsqlEditor;

// PlsqlEditorViewModel 의 일부: 컴파일(CREATE OR REPLACE) 한 번의 흐름.
// 공유 DB 에서 다른 세션이 실행 중인 객체를 컴파일하면 서버가 library cache 잠금을 기다린다(대개 몇 분 뒤 ORA-04021).
// 그래서 컴파일마다 취소 원천 하나(호출자 토큰 + 「컴파일 취소」 단추·실행 취소·탭 닫기)를 모든 단계에 쓰고, CREATE 에는 대기 상한을 둔다.
// 컴파일하는 동안 그 세션은 「실행 중」이다(ExecutionState): 세션 바쁨 막대·실행 취소가 컴파일에도 동작하고, 같은 접속의 다른 실행은 이유를 알고 기다린다.
public sealed partial class PlsqlEditorViewModel
{
    /// <summary>사전 확인(서버 소스 변경·다른 세션 사용·현재 스키마) 한 단계의 상한.</summary>
    internal static readonly TimeSpan DefaultPrecheckLimit = TimeSpan.FromSeconds(5);

    /// <summary>컴파일 뒤 오류 목록·마지막 DDL 시각 읽기의 상한.</summary>
    private static readonly TimeSpan AfterCompileReadLimit = TimeSpan.FromSeconds(10);

    private ExecutionState? _state;

    /// <summary>지금 컴파일의 취소 원천(컴파일 중이 아니면 null).</summary>
    private CancellationTokenSource? _compileCts;

    /// <summary>지금 컴파일이 「실행 중」으로 잡은 세션(실행 상태에 잡지 않았으면 null).</summary>
    private Guid? _compileRunSession;

    /// <summary>세션 실행 상태(세션 바쁨 막대·실행 취소). 시험은 직접 넣는다. 이벤트 버스가 없으면 null(상태를 건드리지 않는다).</summary>
    internal ExecutionState? State
    {
        get => _state ?? (ResolveEvents() is { } bus ? ExecutionState.For(bus) : null);
        set => _state = value;
    }

    /// <summary>시험용: 대기 상한(설정 대신). <see cref="Timeout.InfiniteTimeSpan"/> = 상한 없음.</summary>
    internal TimeSpan? CompileWaitOverride { get; set; }

    /// <summary>시험용: 사전 확인 한 단계의 상한.</summary>
    internal TimeSpan PrecheckLimit { get; set; } = DefaultPrecheckLimit;

    /// <summary>
    /// 취소(단추·탭 닫기·대기 상한·실행 취소)를 보낸 뒤 드라이버가 돌아오기를 기다리는 시간(PHC-7). 넘으면 그 세션을 버린다(<see cref="IAbandonableSession"/>).
    /// 서버 OOB 가 막혔거나 긴급 데이터가 사라지는 경로에서는 break 가 닿지 않아 드라이버가 영영 돌아오지 않는다(실측, phc-it).
    /// </summary>
    internal TimeSpan CancelGrace { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>CREATE 를 보낸 뒤 이만큼 지나도 끝나지 않으면 막는 세션을 찾는다(PHC-8).</summary>
    internal TimeSpan BlockerDelay { get; set; } = TimeSpan.FromSeconds(3);

    private ICompileBlockerProbe? _blockerProbe;

    /// <summary>막는 세션 찾기(PHC-8). 시험은 직접 넣는다. 앱 조립이 없으면 null(찾지 않는다).</summary>
    internal ICompileBlockerProbe? BlockerProbe
    {
        get => _blockerProbe ??= _services is null ? null : new SessionCompileBlockerProbe(_services);
        set => _blockerProbe = value;
    }

    /// <summary>세션마다 한 번 읽어 둔 SID(<c>plsql.session_sid</c>). 컴파일 세션은 CREATE 가 막힌 동안 다른 문장을 보낼 수 없어서 미리 읽는다.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ISession, object> SessionSids = [];

    /// <summary>
    /// 지금 컴파일을 취소한다(「컴파일 취소」 단추·실행 취소 <c>oranda.exec.cancel</c>·탭이나 작업 공간 문서 닫기). 컴파일 중이 아니면 아무것도 안 한다.
    /// 서버에는 드라이버 중단(break, ORA-01013)이 간다. 결과는 <see cref="PlsqlCompileStatus.Cancelled"/>.
    /// </summary>
    public void CancelCompile()
    {
        if (_compileCts is not { IsCancellationRequested: false } cts)
        {
            return;
        }

        if (_compileRunSession is { } id)
        {
            State?.MarkCancelling(id);
        }

        StatusText = UiText.Get("Plsql.compile.cancelling", "컴파일을 취소하는 중…");
        try
        {
            // 취소 콜백(드라이버 break 는 서버 왕복)을 스레드 풀에서 돌린다: UI 가 멈추지 않고, 컴파일 뒤처리가 이 호출 안에서 돌지 않는다.
            _ = cts.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            // 막 끝난 컴파일: 취소할 것이 없다.
        }
    }

    /// <summary>CREATE 대기 상한(설정 <c>execution.plsqlCompileWaitSeconds</c>, 0 = 상한 없음).</summary>
    private TimeSpan CompileWait()
    {
        if (CompileWaitOverride is { } fixedWait)
        {
            return fixedWait;
        }

        var seconds = (_services?.GetService(typeof(SettingsService)) as SettingsService)?.Current.Execution.PlsqlCompileWaitSeconds
            ?? ExecutionSettings.DefaultPlsqlCompileWaitSeconds;
        return seconds <= 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(Math.Min(seconds, ExecutionSettings.MaxPlsqlCompileWaitSeconds)); // 손으로 고친 큰 값도 받는다
    }

    /// <summary>
    /// 드라이버를 부르는 일을 UI 스레드 밖에서 돌린다(드라이버 실행은 동기로 막힐 수 있다: 그동안 「컴파일 취소」 단추가 눌려야 한다).
    /// UI 스레드가 아니면(시험) 그대로 부른다.
    /// </summary>
    private static Task<T> OffUi<T>(Func<Task<T>> call) => Dispatcher.UIThread.CheckAccess() ? Task.Run(call, CancellationToken.None) : call();

    private static Task OffUi(Func<Task> call) => Dispatcher.UIThread.CheckAccess() ? Task.Run(call, CancellationToken.None) : call();

    private async Task<PlsqlCompileOutcome> CompilePartAsync(PlsqlSourcePart part, bool force, CancellationToken ct)
    {
        if (IsCompiling)
        {
            return Report(PlsqlCompileStatus.Blocked, UiText.Get("Plsql.compile.already", "이미 컴파일하는 중이다 — 끝나거나 취소한 뒤 다시 컴파일한다"));
        }

        Session ??= ResolveSession();
        if (_session is { State: SessionState.Broken or SessionState.Closed } dead)
        {
            // 끊긴(버린) 세션: 그 자리에 다시 연 세션이 있을 때만 그 세션으로 이어 간다(같은 DB 라 서버 기준은 그대로 쓴다).
            // 같은 프로필의 다른 살아 있는 세션(예: 추가 세션이 끊겼을 때의 첫 세션)으로 옮기지 않는다: 그 세션의 미커밋을 CREATE 가 커밋해 버린다.
            if (ReplacementFor(_services?.GetService(typeof(ConnectionManager.IConnectionService)) as ConnectionManager.IConnectionService, dead) is { } fresh)
            {
                Session = fresh;
            }
            else
            {
                return Report(PlsqlCompileStatus.NoSession, UiText.Get("Plsql.compile.sessionBroken", "세션이 끊겼다 — 다시 접속한 뒤 컴파일한다"));
            }
        }

        if (_session is not { } session)
        {
            return Report(PlsqlCompileStatus.NoSession, _restoredProfileId is null ? "접속이 없다" : "이 탭을 연 접속이 아직 열리지 않았다 — 그 접속을 열면 컴파일한다");
        }

        var (text, hasMore) = PlsqlSourceText.SplitCompileUnit(part.Editor.Text);
        if (hasMore)
        {
            return Report(PlsqlCompileStatus.NothingToCompile, "단위가 여러 개인 파일은 컴파일하지 않는다 — SQL 탭에서 스크립트로 실행한다");
        }

        var header = PlsqlSourceText.ParseHeader(text);
        if (string.IsNullOrWhiteSpace(text) || header is null)
        {
            return Report(PlsqlCompileStatus.NothingToCompile, "PACKAGE·PROCEDURE·FUNCTION·TRIGGER·TYPE 소스가 아니다");
        }

        // 명세 쪽에는 명세, 본문 쪽에는 본문만(한 탭 두 쪽이 서로 덮어쓰지 않게).
        if (_secondary is not null && PlsqlSourceText.SiblingType(header.Value.ObjectType) is not null
            && PlsqlSourceText.IsBodyType(header.Value.ObjectType) != (part.Side == PlsqlSide.Body))
        {
            return Report(PlsqlCompileStatus.NothingToCompile, part.Side == PlsqlSide.Body
                ? "본문 쪽에는 본문(BODY) 소스를 둔다 — 명세는 「명세」 쪽에서 컴파일한다"
                : "명세 쪽에는 명세 소스를 둔다 — 본문은 「본문」 쪽에서 컴파일한다");
        }

        var meta = ResolveMeta();
        var exec = ResolveExec();
        if (exec is null)
        {
            return Report(PlsqlCompileStatus.NoSession, "실행 서비스가 없다");
        }

        var state = State is { } s && s.IsTracked(session.Id) ? s : null;
        if (state?.IsBusy(session.Id) == true)
        {
            // 같은 접속에서 다른 실행이 돈다: 컴파일이 그 뒤에 줄 서서 잠금 대기처럼 보이지 않게, 그 실행의 취소 방법을 덮지 않게 보내지 않는다.
            return Report(PlsqlCompileStatus.Blocked, UiText.Get("Plsql.compile.sessionBusy", "이 접속에서 다른 문장이 실행 중이다 — 끝나거나 취소한 뒤 컴파일한다"));
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = cts.Token;
        _compileCts = cts;
        _compileRunSession = state is null ? null : session.Id;
        IsBusy = true;
        IsCompiling = true;
        state?.BeginRun(session.Id, null);
        var cancelHook = state?.RegisterCancel(session.Id, CancelCompile); // 실행 취소(Ctrl+Break·세션 바쁨 막대)도 이 컴파일을 끊는다
        TimeSpan? waitLimit = null;
        CancellationTokenSource? wait = null;
        BlockerWatch? blocker = null;
        var notes = new List<string>();
        var usersUnknown = false;
        try
        {
            var (owner, privilegeNote) = await ResolveOwnerAsync(exec, session, header.Value.Owner, header.Value.ObjectType, token).ConfigureAwait(true);
            if (privilegeNote is not null)
            {
                notes.Add(privilegeNote);
            }

            var effective = new DbObjectRef(owner, header.Value.Name, header.Value.ObjectType);
            if (part.Target is null || part.Target.Owner != effective.Owner || part.Target.Name != effective.Name || part.Target.ObjectType != effective.ObjectType)
            {
                // 이름·유형이 바뀌었거나 새 문서: 새 객체로 본다(충돌 기준 없음).
                part.Target = effective;
                part.Baseline = null;
                part.ServerSource = null;
                if (part.Editor.FilePath is null)
                {
                    Title = $"{effective.Owner}.{effective.Name}"; // 파일 탭은 파일 이름을 그대로 둔다
                }

                RaiseSides();
            }

            if (!force && meta is not null && part.Baseline is not null)
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
                limit.CancelAfter(PrecheckLimit);
                bool changed;
                try
                {
                    changed = await CheckServerChangedAsync(part, limit.Token).ConfigureAwait(true);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    WarningText = UiText.Format("Plsql.compile.changeCheckTimeout",
                        "서버 소스가 바뀌었는지 {0}초 안에 확인하지 못했습니다. 「무시하고 덮어쓰기」로 확인 없이 컴파일할 수 있습니다.", Seconds(PrecheckLimit));
                    return Report(PlsqlCompileStatus.Failed, WarningText);
                }

                if (changed)
                {
                    return Report(PlsqlCompileStatus.Conflict, "다른 사람이 서버 소스를 바꿨다 — 차이를 확인하고 덮어쓸지 정한다");
                }
            }

            if (!force)
            {
                var (others, unknownReason) = await CountOtherUsersAsync(exec, session, effective, token).ConfigureAwait(true);
                if (others > 0)
                {
                    WarningText = $"다른 세션 {others}개가 이 객체를 쓰고 있다. 지금 컴파일하면 기다리거나 그 세션이 영향을 받는다.";
                    return Report(PlsqlCompileStatus.InUse, WarningText);
                }

                if (unknownReason is not null)
                {
                    usersUnknown = true;
                    // 모름은 막지 않는다(권한 없는 개발 계정이 컴파일을 못 하게 되면 안 된다). 기다릴 수 있음을 미리 알린다.
                    notes.Add(UiText.Format("Plsql.compile.usersUnknown",
                        "다른 세션의 사용 여부를 확인할 수 없습니다({0}). 다른 세션이 이 객체를 실행 중이면 컴파일이 잠금을 기다립니다.", unknownReason));
                }
            }

            WarningText = string.Join(Environment.NewLine, notes);
            DiffText = string.Empty;
            await ArchiveServerSourceAsync(meta, effective, token).ConfigureAwait(true);
            var probe = BlockerProbe;
            var sid = probe is null ? null : await ReadSessionSidAsync(exec, session, token).ConfigureAwait(true);
            var stmt = new SqlStatement(text, default, StatementKind.Ddl) { Verb = "CREATE" };

            // CREATE 는 다른 세션이 이 객체를 실행 중이면 잠금을 기다린다: 상한을 넘으면 끊고 「시간 초과」로 알린다(취소와 구분).
            // 상한은 드라이버로 보내는 순간(ExecutionOptions.Sending)부터 센다: 실행 확인 창을 읽는 시간은 세지 않는다.
            waitLimit = CompileWait();
            var timer = wait = new CancellationTokenSource();
            var limitToArm = waitLimit.Value;
            var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var both = CancellationTokenSource.CreateLinkedTokenSource(token, timer.Token);
            var sendToken = both.Token;
            var options = new ExecutionOptions
            {
                Source = HistorySource.Editor,
                Sending = () =>
                {
                    if (limitToArm != Timeout.InfiniteTimeSpan)
                    {
                        timer.CancelAfter(limitToArm);
                    }

                    sent.TrySetResult();
                },
            };
            var send = OffUi(() => exec.ExecuteAsync(session, stmt, new Dictionary<string, BindValue>(), options, sendToken));
            blocker = new BlockerWatch();
#pragma warning disable CA2000 // 아래 finally 가 스레드 풀에서 취소한 뒤 Dispose 한다.
            var watchStop = new CancellationTokenSource();
#pragma warning restore CA2000
            var watch = probe is not null && sid is { } mine ? WatchBlockerAsync(probe, session, mine, sent.Task, notes, blocker, watchStop.Token) : Task.CompletedTask;
            ExecutionResult? result;
            try
            {
                result = await AwaitOrAbandonAsync(send, session, sent.Task, token, timer.Token).ConfigureAwait(true);
            }
            finally
            {
                // 찾기는 기다리지 않는다(다른 연결이 늦어도 컴파일 결과를 붙잡지 않게): 끝났다고 적어 두고(UI 스레드, 이 뒤로 띠를 바꾸지 않는다)
                // 취소는 스레드 풀에서 보낸다(찾는 질의의 드라이버 취소가 같은 나쁜 경로에서 막혀도 UI 가 기다리지 않게).
                blocker.Finished = true;
                _ = Task.Run(() =>
                {
                    watchStop.Cancel();
                    watchStop.Dispose();
                }, CancellationToken.None);
                _ = watch;
            }

            if (result is null)
            {
                return AbandonedReport(session, wait, waitLimit, blocker.LastText, token);
            }

            if (result.ResultSet is not null)
            {
                await result.ResultSet.DisposeAsync().ConfigureAwait(true);
            }

            // 드라이버가 ORA-24344(컴파일 오류와 함께 만들어짐)를 실패로 돌려주면: 서버는 받았다 — 오류 목록을 읽는다.
            var serverReportedErrors = result.Status == ExecutionStatus.Failed && result.Error?.Kind == DbErrorKind.CompiledWithErrors;
            switch (result.Status)
            {
                case ExecutionStatus.Denied:
                    return Report(PlsqlCompileStatus.Blocked, "정책이 컴파일을 막았다");
                case ExecutionStatus.Cancelled:
                    return StoppedReport(wait, waitLimit, token, blocker?.LastText);
                case ExecutionStatus.Failed when !serverReportedErrors:
                    return Report(PlsqlCompileStatus.Failed, FailureMessage(result.Error, privilegeNote));
            }

            // 새 문서를 패키지·타입으로 처음 만들었으면 다른 쪽을 붙인다(처음 볼 때 서버에서 읽는다).
            BecomePair(part);
            IReadOnlyList<CompileError> errors = [];
            string? readFailure = null;
            if (meta is not null)
            {
                (errors, readFailure) = await ReadErrorsAfterCompileAsync(meta, session, part, token).ConfigureAwait(true);
                part.Errors = errors;
                part.Baseline = await ReadLastDdlAfterCompileAsync(meta, session, effective, token).ConfigureAwait(true);
                PublishErrors(publish: true);
            }

            part.ServerSource = PlsqlSourceText.ToEditorText(text);
            part.Missing = false;
            part.Loaded = true;
            SetServerChanged(part, false);
            if (part.Editor.FilePath is null)
            {
                part.Editor.MarkSaved(); // 파일 탭은 컴파일해도 파일에는 쓰지 않았으므로 고침 표시를 지우지 않는다
            }

            RefreshMembers();
            WarningText = string.Empty;
            var errorCount = errors.Count(x => !x.IsWarning);
            var status = errorCount > 0 || serverReportedErrors ? PlsqlCompileStatus.CompiledWithErrors : PlsqlCompileStatus.Compiled;
            var message = readFailure is not null
                ? UiText.Format("Plsql.compile.errorsUnread", "컴파일됐지만 오류 목록을 읽지 못했습니다: {0}", readFailure)
                : errorCount > 0 ? $"컴파일됐지만 오류 {errorCount}건"
                : serverReportedErrors ? UiText.Get("Plsql.compile.errorsEmpty", "컴파일됐지만 서버가 오류를 알렸습니다(오류 목록이 비어 있습니다)")
                : errors.Count > 0 ? $"컴파일됨 (경고 {errors.Count}건)" : "컴파일됨";
            if (usersUnknown && readFailure is null)
            {
                message += " · " + UiText.Get("Plsql.compile.usersUnknownShort", "다른 세션의 사용 여부는 확인하지 못했습니다");
            }

            return Report(status, message, errors);
        }
        catch (OperationCanceledException)
        {
            return StoppedReport(wait, waitLimit, token, blocker?.LastText);
        }
        catch (Exception ex)
        {
            return Report(PlsqlCompileStatus.Failed, ex.Message);
        }
        finally
        {
            wait?.Dispose();
            cancelHook?.Dispose();
            _compileCts = null;
            _compileRunSession = null;
            state?.EndRun(session.Id);
            IsCompiling = false;
            IsBusy = false;
        }
    }

    /// <summary>끊긴 세션 <paramref name="dead"/> 의 자리에 다시 연 작업 세션(열려 있고 쓸 수 있을 때). 같은 프로필의 다른 세션은 고르지 않는다. 없으면 null.</summary>
    internal static ISession? ReplacementFor(ConnectionManager.IConnectionService? connections, ISession dead) =>
        connections?.ReopenedAs(dead) is { Purpose: SessionPurpose.Work, State: SessionState.Idle or SessionState.Busy } fresh && connections.IsOpen(fresh) ? fresh : null;

    /// <summary>CREATE 가 멈췄다: 대기 상한이면 「시간 초과」(안내 글은 경고 띠에), 아니면 취소.</summary>
    private PlsqlCompileOutcome StoppedReport(CancellationTokenSource? wait, TimeSpan? waitLimit, CancellationToken token, string? blockerText = null)
    {
        if (wait is { IsCancellationRequested: true } && !token.IsCancellationRequested && waitLimit is { } limit)
        {
            var timeout = UiText.Format("Plsql.compile.timeout",
                "{0}초 안에 컴파일이 끝나지 않아 멈췄습니다. 다른 세션이 이 객체를 실행 중이면 컴파일이 잠금을 기다립니다 — 그 실행이 끝난 뒤 다시 컴파일하세요. 대기 상한은 설정 ▸ 실행에서 바꿉니다.",
                Seconds(limit));
            Report(PlsqlCompileStatus.Timeout, timeout);
            WarningText = blockerText is null ? timeout : timeout + Environment.NewLine + blockerText; // 막는 세션(SID)은 DBA 에게 알릴 때 쓰므로 남긴다
            return new PlsqlCompileOutcome(PlsqlCompileStatus.Timeout, [], timeout);
        }

        WarningText = string.Empty;
        return Report(PlsqlCompileStatus.Cancelled, "컴파일이 취소됐다");
    }

    /// <summary>
    /// CREATE 결과를 기다린다. 버릴 수 있는 세션(<see cref="IAbandonableSession"/>)이면 드라이버로 보낸 뒤(<paramref name="sent"/>) 취소가 났는데
    /// <see cref="CancelGrace"/> 안에 돌아오지 않을 때 기다리기를 그만두고 null(부르는 쪽이 세션을 버린다). 보내기 전의 취소(확인 창·명령 잠금 대기)는 엔진이 곧 돌려주므로 기다린다.
    /// </summary>
    private async Task<ExecutionResult?> AwaitOrAbandonAsync(Task<ExecutionResult> send, ISession session, Task sent, CancellationToken token, CancellationToken timer)
    {
        if (session is not IAbandonableSession)
        {
            return await send.ConfigureAwait(true);
        }

        using var done = new CancellationTokenSource();
        var stuck = StuckAfterCancelAsync(sent, CancelGrace, token, timer, done.Token);
        var first = await Task.WhenAny(send, stuck).ConfigureAwait(true);
        if (!ReferenceEquals(first, send) && stuck.IsCompletedSuccessfully && !send.IsCompleted) // 유예가 끝나는 순간 돌아왔으면 버리지 않는다
        {
            // 버린 CREATE 가 나중에 돌아오면(연결을 닫아 깨어남) 결과 집합을 닫고 예외는 관찰만 한다.
            _ = send.ContinueWith(static t =>
            {
                if (t.IsCompletedSuccessfully)
                {
                    _ = t.Result.ResultSet?.DisposeAsync().AsTask();
                }
                else
                {
                    _ = t.Exception;
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            return null;
        }

        await done.CancelAsync().ConfigureAwait(true);
        return await send.ConfigureAwait(true);
    }

    /// <summary>
    /// 드라이버로 보낸 뒤 취소가 났고 <paramref name="grace"/> 가 지났다(true). <paramref name="done"/> 이면 취소 예외로 끝난다.
    /// 취소는 보내는 토큰(연결 토큰)이 아니라 그 원천 둘(<paramref name="token"/>·<paramref name="timer"/>)에서 본다: 토큰 콜백은 나중에 단 것부터 돌므로
    /// 보내는 토큰에 달면 드라이버 취소(<c>OracleCommand.Cancel</c>, 나쁜 경로에서 막힐 수 있다)가 먼저 돌아 유예 시계가 늦게 시작한다.
    /// </summary>
    private static async Task<bool> StuckAfterCancelAsync(Task sent, TimeSpan grace, CancellationToken token, CancellationToken timer, CancellationToken done)
    {
        var cancelSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (token.Register(() => cancelSeen.TrySetResult()))
        using (timer.Register(() => cancelSeen.TrySetResult()))
        using (done.Register(() => cancelSeen.TrySetCanceled(done)))
        {
            await cancelSeen.Task.ConfigureAwait(false);
        }

        await sent.WaitAsync(done).ConfigureAwait(false);
        await Task.Delay(grace, done).ConfigureAwait(false);
        return true;
    }

    /// <summary>취소가 돌아오지 않아 세션을 버렸다(PHC-7): 끊김으로 알리고 연결 닫기를 시작한 뒤, 시간 초과·취소 글에 그 사실을 덧붙인다.</summary>
    private PlsqlCompileOutcome AbandonedReport(ISession session, CancellationTokenSource? wait, TimeSpan? waitLimit, string? blockerText, CancellationToken token)
    {
        var reason = UiText.Get("Plsql.compile.abandoned",
            "서버가 취소에 답하지 않아 연결을 끊었습니다. 서버 쪽 컴파일은 막는 실행이 끝날 때까지 남고 그 뒤 반영될 수 있습니다. 다시 열어 서버 소스를 확인하세요.");
        (session as IAbandonableSession)?.Abandon(reason);
        var stopped = StoppedReport(wait, waitLimit, token);
        var message = stopped.Message + Environment.NewLine + reason;
        Report(stopped.Status, message);
        WarningText = blockerText is null ? message : message + Environment.NewLine + blockerText;
        return new PlsqlCompileOutcome(stopped.Status, [], message);
    }

    /// <summary>
    /// CREATE 를 보내고 <see cref="BlockerDelay"/> 가 지나도 끝나지 않으면 다른 연결에서 막는 세션을 찾아 경고 띠에 보인다(PHC-8). 예외를 내지 않는다.
    /// 컴파일이 끝나면(<paramref name="stop"/>) 띠를 바꾸지 않는다.
    /// </summary>
    private async Task WatchBlockerAsync(ICompileBlockerProbe probe, ISession session, int sid, Task sent, IReadOnlyList<string> notes, BlockerWatch watch, CancellationToken stop)
    {
        try
        {
            await sent.WaitAsync(stop).ConfigureAwait(true);
            await Task.Delay(BlockerDelay, stop).ConfigureAwait(true);
            var found = await OffUi(() => probe.FindAsync(session, sid, stop)).ConfigureAwait(true);
            if (!watch.Finished)
            {
                watch.LastText = BlockerText(found);
                WarningText = string.Join(Environment.NewLine, [.. notes, watch.LastText]);
            }
        }
        catch (OperationCanceledException)
        {
            // 컴파일이 먼저 끝났다.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 찾기는 덧붙이는 안내다: 실패해도 컴파일 흐름은 그대로.
        }
    }

    /// <summary>막는 세션 안내 글.</summary>
    internal static string BlockerText(CompileBlockerResult found)
    {
        if (found.Blocker is { Sid: { } blockerSid } b)
        {
            var who = string.Join("·", new[] { b.User, b.Program, b.Machine }.Where(x => !string.IsNullOrWhiteSpace(x)));
            var running = b.RunningObject is { } obj ? UiText.Format("Plsql.compile.blockerRunning", " — {0} 실행 중", obj) : string.Empty;
            return UiText.Format("Plsql.compile.blocker", "SID {0}({1})이 이 객체를 실행 중이라 컴파일이 잠금을 기다립니다{2}. 그 실행이 끝나거나 DBA 가 그 세션을 처리하면 컴파일이 이어집니다.",
                blockerSid.ToString(CultureInfo.InvariantCulture), who.Length > 0 ? who : "-", running);
        }

        return UiText.Format("Plsql.compile.blockerUnknown",
            "컴파일이 끝나지 않고 있습니다. 다른 세션이 이 객체를 실행 중이면 컴파일이 잠금을 기다립니다(막는 세션을 찾지 못함: {0}). DBA 에게 막는 세션 처리를 부탁할 수 있습니다.",
            found.Unknown ?? UiText.Get("Plsql.compile.blockerServerUnknown", "서버가 막는 세션을 알려 주지 않음"));
    }

    /// <summary>이 세션의 SID(<c>plsql.session_sid</c>, 세션마다 한 번 읽어 둔다). 문장이 없거나 못 읽으면 null(막는 세션을 찾지 않는다).</summary>
    private async Task<int?> ReadSessionSidAsync(IExecutionService exec, ISession session, CancellationToken ct)
    {
        if (SessionSids.TryGetValue(session, out var known))
        {
            return (int)known;
        }

        var sql = CatalogRouting.Text(_services, session, "plsql.session_sid");
        if (sql is null)
        {
            return null;
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(PrecheckLimit);
        try
        {
            var (row, _) = await QueryFirstRowAsync(exec, session, sql, limit.Token).ConfigureAwait(true);
            if (row is [{ } value, ..])
            {
                var sid = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                SessionSids.AddOrUpdate(session, sid);
                return sid;
            }

            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static string Seconds(TimeSpan span) => ((int)Math.Round(span.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

    /// <summary>서버 거부 글. 권한 부족(ORA-01031)이고 다른 스키마에 컴파일하려 했으면 필요한 권한을 덧붙인다.</summary>
    private static string FailureMessage(DbError? error, string? privilegeNote)
    {
        if (error is null)
        {
            return "컴파일 실패";
        }

        var text = $"{error.Code} {error.Message}";
        return error.Kind == DbErrorKind.InsufficientPrivileges && privilegeNote is not null ? text + " — " + privilegeNote : text;
    }

    /// <summary>다른 스키마에 이 유형을 만들 때 필요한 시스템 권한.</summary>
    private static string CreateAnyPrivilege(string objectType) => objectType switch
    {
        "TRIGGER" => "CREATE ANY TRIGGER",
        "TYPE" or "TYPE BODY" => "CREATE ANY TYPE",
        _ => "CREATE ANY PROCEDURE",
    };

    /// <summary>
    /// 컴파일할 객체의 소유자. 머리글에 소유자가 없으면 서버가 쓰는 현재 스키마(<c>SYS_CONTEXT('USERENV','CURRENT_SCHEMA')</c>: 로그온 트리거·
    /// <c>ALTER SESSION SET CURRENT_SCHEMA</c> 반영)다. 읽지 못하면 접속 계정. 소유자가 로그인 계정과 다르면 필요한 권한 안내(PrivilegeNote)도 준다.
    /// </summary>
    private async Task<(string Owner, string? PrivilegeNote)> ResolveOwnerAsync(IExecutionService exec, ISession session, string? written, string objectType, CancellationToken ct)
    {
        var login = session.Profile.User.ToUpperInvariant();
        if (written is not null && string.Equals(written, login, StringComparison.Ordinal))
        {
            return (written, null);
        }

        var ids = await ReadSessionSchemaAsync(exec, session, ct).ConfigureAwait(true);
        var owner = written ?? ids?.CurrentSchema ?? login;
        var sessionUser = ids?.SessionUser ?? login;
        return string.Equals(owner, sessionUser, StringComparison.Ordinal)
            ? (owner, null)
            : (owner, UiText.Format("Plsql.compile.otherSchema",
                "이 객체의 소유자({0})가 로그인 계정({1})과 다릅니다. 다른 스키마에 컴파일하려면 {2} 권한이 필요합니다.",
                owner, sessionUser, CreateAnyPrivilege(objectType)));
    }

    /// <summary>세션의 현재 스키마·세션 사용자(<c>rollback.session_schema</c>). 문장이 없거나 못 읽으면(상한 포함) null.</summary>
    private async Task<(string CurrentSchema, string SessionUser)?> ReadSessionSchemaAsync(IExecutionService exec, ISession session, CancellationToken ct)
    {
        var sql = CatalogRouting.Text(_services, session, "rollback.session_schema");
        if (sql is null)
        {
            return null;
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(PrecheckLimit);
        try
        {
            var (row, _) = await QueryFirstRowAsync(exec, session, sql, limit.Token).ConfigureAwait(true);
            return row is [string schema, string user, ..] && schema.Length > 0 && user.Length > 0 ? (schema, user) : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// 이 객체를 지금 실행하고 있는 다른 세션 수(<c>meta.object_users</c>, Oracle 은 v$session 기준·RAC 의 모든 인스턴스). 모르면 Count = null 이고 그 이유
    /// (이 서버에 확인 문장 없음·권한 없음·시간 초과·오류). 상한은 <see cref="PrecheckLimit"/>.
    /// </summary>
    private async Task<(int? Count, string? UnknownReason)> CountOtherUsersAsync(IExecutionService exec, ISession session, DbObjectRef target, CancellationToken ct)
    {
        static string Lit(string s) => s.Replace("'", "''", StringComparison.Ordinal);

        var sql = CatalogRouting.Text(_services, session, "meta.object_users", ("owner", Lit(target.Owner)), ("name", Lit(target.Name)));
        if (sql is null)
        {
            return (null, UiText.Get("Plsql.compile.usersNoQuery", "이 서버에는 확인할 문장이 없음"));
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(PrecheckLimit);
        try
        {
            var (row, error) = await QueryFirstRowAsync(exec, session, sql, limit.Token).ConfigureAwait(true);
            if (error.IsNoAccess()
                && CatalogRouting.Text(_services, session, "meta.object_users_local", ("owner", Lit(target.Owner)), ("name", Lit(target.Name))) is { } local)
            {
                // GV_$SESSION 권한은 없고 V_$SESSION 만 받은 계정: 접속한 인스턴스만이라도 센다.
                (row, error) = await QueryFirstRowAsync(exec, session, local, limit.Token).ConfigureAwait(true);
            }

            if (error is not null)
            {
                return (null, error.IsNoAccess()
                    ? UiText.Format("Plsql.compile.usersNoAccess", "다른 세션 정보를 읽을 권한이 없음: {0}", error.Code)
                    : $"{error.Code} {error.Message}");
            }

            return row is [{ } first, ..] ? (Convert.ToInt32(first, CultureInfo.InvariantCulture), null) : (null, UiText.Get("Plsql.compile.usersNoRow", "결과가 없음"));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, UiText.Format("Plsql.compile.usersTimeout", "{0}초 안에 답이 없음", Seconds(PrecheckLimit)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>질의의 첫 행(열 순서, 행이 없으면 null). 서버가 거부하면 Error 에 그 오류, 취소되면 <see cref="OperationCanceledException"/>.</summary>
    private static async Task<(object?[]? Row, DbError? Error)> QueryFirstRowAsync(IExecutionService exec, ISession session, string sql, CancellationToken ct)
    {
        var stmt = new SqlStatement(sql, default, StatementKind.Query) { Verb = "SELECT" };
        var r = await OffUi(() => exec.ExecuteAsync(session, stmt, new Dictionary<string, BindValue>(),
            new ExecutionOptions { Source = HistorySource.Editor, SkipHistory = true }, ct)).ConfigureAwait(true);
        if (r.Status == ExecutionStatus.Cancelled)
        {
            throw new OperationCanceledException(ct);
        }

        if (r.ResultSet is not { } results)
        {
            return (null, r.Error ?? new DbError("ORANDA-QUERY", r.Status.ToString()));
        }

        await using (results.ConfigureAwait(true))
        {
            var page = await OffUi(() => results.GetPageAsync(0, ct)).ConfigureAwait(true);
            return page.RowCount == 0 || page.Columns.Count == 0 ? (null, null) : ([.. page.Columns.Select(c => c.Count > 0 ? c[0] : null)], null);
        }
    }

    /// <summary>컴파일 뒤 오류 목록. 못 읽으면(취소·상한 포함) 빈 목록과 그 이유 — 오류 있는 컴파일이 「컴파일됨」으로 보이지 않게 부르는 쪽이 알린다.</summary>
    private async Task<(IReadOnlyList<CompileError> Errors, string? Failure)> ReadErrorsAfterCompileAsync(IMetadataProvider meta, ISession session, PlsqlSourcePart part, CancellationToken ct)
    {
        if (part.Target is not { } target)
        {
            return ([], null);
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(AfterCompileReadLimit);
        try
        {
            var errors = await OffUi(() => meta.GetCompileErrorsAsync(session, target, limit.Token)).ConfigureAwait(true);
            return (_secondary is null ? errors : [.. errors.Select(e => e with { ObjectType = target.ObjectType })], null);
        }
        catch (OperationCanceledException)
        {
            return ([], ct.IsCancellationRequested
                ? UiText.Get("Plsql.compile.readCancelled", "취소함")
                : UiText.Format("Plsql.compile.readTimeout", "{0}초 안에 답이 없음", Seconds(AfterCompileReadLimit)));
        }
        catch (Exception ex)
        {
            return ([], ex.Message);
        }
    }

    /// <summary>컴파일 뒤 마지막 DDL 시각(다음 충돌 확인 기준). 못 읽으면 null(다음 컴파일은 충돌 확인을 건너뛴다).</summary>
    private static async Task<DateTime?> ReadLastDdlAfterCompileAsync(IMetadataProvider meta, ISession session, DbObjectRef target, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(AfterCompileReadLimit);
        try
        {
            return await ReadLastDdlAsync(meta, session, target, limit.Token).ConfigureAwait(true);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>막는 세션 찾기 한 번의 상태(UI 스레드에서만 읽고 쓴다): 컴파일이 끝났는지, 마지막으로 띠에 보인 막는 세션 글.</summary>
    private sealed class BlockerWatch
    {
        public bool Finished { get; set; }

        public string? LastText { get; set; }
    }
}
