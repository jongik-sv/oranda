using System.Globalization;
using System.Text;
using Oranda.App.Features.Binds;
using Oranda.App.Features.GridEdit;
using Oranda.App.Features.PlsqlEditor;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.History;
using Oranda.Core.Language;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Text;

namespace Oranda.App.Features.Execution;

// ExecutionController 의 일부: 실행 몸통(ExecutionController.cs 에서 나눔, 동작 불변).
public sealed partial class ExecutionController
{
    // ---- 실행 몸통 ---------------------------------------------------------------------------

    private async Task<bool> RunOneAsync(SqlEditorViewModel tab, SqlStatement stmt, bool newResultTab, CancellationToken ct)
    {
        var results = Prepare(tab, out var session);
        if (session is null)
        {
            return false;
        }

        var binds = ResolveBinds(tab, stmt.BindNames, results);
        if (binds is null)
        {
            return false;
        }

        Flash(tab, stmt.Range.Start, stmt.Range.End);
        ExecutionResult? result = null;
        await GuardedAsync(session, async token =>
        {
            var exec = _d.Execution();
            result = await Task.Run(
                () => exec.ExecuteAsync(session, stmt, binds, new ExecutionOptions { Source = HistorySource.Editor }, token),
                CancellationToken.None).ConfigureAwait(true);
            await AfterStatementAsync(tab, session, stmt, result, token).ConfigureAwait(true);
        }, ct, results).ConfigureAwait(true);

        if (result is null)
        {
            return false;
        }

        if (!newResultTab)
        {
            results.Clear();
        }

        Present(tab, results, stmt, result, results.NextOrdinal()); // 고정한 탭(GRID-06)이 남아 있으면 그 번호를 피한다
        var cancelled = result.Status == ExecutionStatus.Cancelled;
        MarkCancelled(session, cancelled);
        return !cancelled;
    }

    /// <summary>
    /// 이미 만들어 둔 문장과 바인드 값을 그대로 실행한다(MyBatis 「시나리오 값으로 실행」). 탭 글을 나누거나 「바인드 변수」 목록을 거치지 않고,
    /// 접속 확인·전용 세션·정책 관문·취소·결과 표시는 <see cref="RunAsync"/> 의 한 문장 실행과 같은 경로를 쓴다.
    /// </summary>
    public async Task<bool> RunPreparedAsync(SqlEditorViewModel tab, SqlStatement stmt, IReadOnlyDictionary<string, BindValue> binds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(stmt);
        ArgumentNullException.ThrowIfNull(binds);
        if (!await EnsureConnectionAsync(tab, ct).ConfigureAwait(true) || IsBusy(SessionFor(tab)))
        {
            return false;
        }

        var results = Prepare(tab, out var session);
        if (session is null)
        {
            return false;
        }

        ExecutionResult? result = null;
        await GuardedAsync(session, async token =>
        {
            var exec = _d.Execution();
            result = await Task.Run(
                () => exec.ExecuteAsync(session, stmt, binds, new ExecutionOptions { Source = HistorySource.Editor }, token),
                CancellationToken.None).ConfigureAwait(true);
            await AfterStatementAsync(tab, session, stmt, result, token).ConfigureAwait(true);
        }, ct, results).ConfigureAwait(true);

        if (result is null)
        {
            return false;
        }

        results.Clear();
        Present(tab, results, stmt, result, results.NextOrdinal());
        var cancelled = result.Status == ExecutionStatus.Cancelled;
        MarkCancelled(session, cancelled);
        return !cancelled;
    }

    private async Task<bool> RunScriptAsync(SqlEditorViewModel tab, IReadOnlyList<SqlStatement> stmts, bool newResultTab, CancellationToken ct)
    {
        var results = Prepare(tab, out var session);
        if (session is null)
        {
            return false;
        }

        var binds = ResolveBinds(tab, stmts.SelectMany(st => st.BindNames), results);
        if (binds is null)
        {
            return false;
        }

        if (!newResultTab)
        {
            results.Clear();
        }

        Flash(tab, stmts[0].Range.Start, stmts[^1].Range.End);
        int ok = 0, failed = 0, done = 0;
        bool cancelled = false;
        var summary = new StringBuilder();
        await GuardedAsync(session, async token =>
        {
            var exec = _d.Execution();
            var options = new ExecutionOptions { Source = HistorySource.Script, Binds = binds };
            var en = exec.ExecuteScriptAsync(session, stmts, State.ErrorMode, options, token).GetAsyncEnumerator(token);
            try
            {
                while (await Task.Run(() => en.MoveNextAsync().AsTask(), CancellationToken.None).ConfigureAwait(true))
                {
                    var r = en.Current;
                    done++;
                    if (r.Status == ExecutionStatus.Succeeded)
                    {
                        ok++;
                    }
                    else if (r.Status == ExecutionStatus.Cancelled)
                    {
                        cancelled = true;
                    }
                    else
                    {
                        failed++;
                    }

                    await AfterStatementAsync(tab, session, r.Statement, r, token).ConfigureAwait(true);
                    summary.AppendLine(CultureInfo.InvariantCulture, $"{StatusMark(r.Status)} {Brief(r.Statement)} — {Describe(r)}");
                    Present(tab, results, r.Statement, r, results.NextOrdinal(), scriptMode: true);
                }
            }
            finally
            {
                await en.DisposeAsync().ConfigureAwait(true);
            }
        }, ct, results).ConfigureAwait(true);

        var line = $"스크립트: {stmts.Count.ToString(CultureInfo.InvariantCulture)}개 중 {done.ToString(CultureInfo.InvariantCulture)}개 실행, 성공 {ok.ToString(CultureInfo.InvariantCulture)}, 실패 {failed.ToString(CultureInfo.InvariantCulture)}" + (cancelled ? ", 취소됨" : string.Empty);
        Log.AddMessage(failed > 0 ? MessageKind.Warning : MessageKind.Info, line);
        var firstQuery = results.Tabs.FirstOrDefault(t => t.HasPane && !t.IsPinned);
        results.AddMessage("스크립트", line + Environment.NewLine + summary, failed > 0);
        if (firstQuery is not null)
        {
            results.Selected = firstQuery;
        }

        MarkCancelled(session, cancelled);
        return !cancelled;
    }

    private void MarkCancelled(ISession session, bool cancelled)
    {
        if (cancelled)
        {
            _lastCancelled.Add(session.Id);
        }
        else
        {
            _lastCancelled.Remove(session.Id);
        }
    }

    /// <summary>실행하는 범위를 편집기에 잠깐 칠한다. 문서 밖에서 만든 문장(범위 없음)은 건너뛴다.</summary>
    private static void Flash(SqlEditorViewModel tab, int start, int end)
    {
        if (end > start && end <= tab.Document.TextLength)
        {
            tab.FlashRange(start, end - start);
        }
    }

    /// <summary>실행 직전 준비: 세션 확인(탭의 전용 세션 → 공유 작업 세션), 결과 영역을 편집기에 붙임.</summary>
    private ResultTabsViewModel Prepare(SqlEditorViewModel tab, out ISession? session)
    {
        session = SessionFor(tab);
        var results = EnsureResults(tab, session);
        _d.ActivatePanel(ResultsPanelId); // 결과·오류는 아래 「조회 결과」 탭에(포커스는 편집기에 남는다)
        if (session is null)
        {
            const string none = "접속이 없습니다. 접속한 뒤 실행하세요.";
            Log.AddMessage(MessageKind.Warning, none);
            results.Clear();
            results.AddMessage("메시지", none, isError: true);
        }
        else if (State.IsBroken(session.Id))
        {
            var broken = State.IsDedicated(session.Id)
                ? "전용 세션이 끊겼습니다. 이 탭을 닫고 새 전용 세션 탭을 여세요."
                : "세션이 끊겼습니다. 다시 접속한 뒤 실행하세요.";
            Log.AddMessage(MessageKind.Warning, broken);
            results.Clear();
            results.AddMessage("메시지", broken, isError: true);
            session = null;
        }

        return results;
    }

    private static ResultTabsViewModel EnsureResults(SqlEditorViewModel tab, ISession? session)
    {
        if (tab.ResultPane is not ResultTabsViewModel results)
        {
            results = new ResultTabsViewModel(session?.Profile);
            tab.ResultPane = results;
        }
        else
        {
            results.SetProfile(session?.Profile);
        }

        return results;
    }

    /// <summary>실행 상태 표시·취소 토큰을 묶어 본문을 돌린다. 끝나면 상태를 되돌린다.</summary>
    private async Task GuardedAsync(ISession session, Func<CancellationToken, Task> body, CancellationToken outer, ResultTabsViewModel? results = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        _runs[session.Id] = cts;
        s_runningProfile.Value = session.Profile;
        State.BeginRun(session.Id);
        if (results is not null)
        {
            results.IsRunning = true; // 결과 영역 위쪽의 얇은 진행 막대
        }

        try
        {
            await body(cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Log.AddMessage(MessageKind.Info, "취소되었습니다.");
        }
        catch (Exception e)
        {
            // 엔진 밖에서 터진 예외(끊김 등)는 메시지로 남기고 상태를 풀어 준다.
            Log.AddMessage(MessageKind.Error, "실행 중 오류: " + e.Message);
        }
        finally
        {
            _runs.Remove(session.Id);
            if (results is not null)
            {
                results.IsRunning = false; // 끝·오류·취소 모두에서 끈다
            }

            State.ApplyTransaction(session.Id, session.HasPendingTransaction);
            State.EndRun(session.Id);
        }
    }

    /// <summary>자동 커밋이 켜져 있으면 성공한 변경 뒤에 커밋한다.</summary>
    private async Task AfterStatementAsync(SqlEditorViewModel tab, ISession session, SqlStatement stmt, ExecutionResult r, CancellationToken ct)
    {
        if (State.AutoCommit && r.Status == ExecutionStatus.Succeeded && r.TransactionPending && !session.Profile.ReadOnly)
        {
            await Task.Run(() => session.CommitAsync(ct), CancellationToken.None).ConfigureAwait(true);
        }

        // 만든 PL/SQL 객체가 오류와 함께 컴파일됐으면 컴파일 오류 창을 채운다(TSK-13-28).
        if (_d.Metadata() is { } meta && await SqlCompileErrors.CaptureAsync(meta, session, tab, stmt, r, ct).ConfigureAwait(true))
        {
            _d.ActivatePanel("oranda.panel.compileErrors");
        }
    }
}
