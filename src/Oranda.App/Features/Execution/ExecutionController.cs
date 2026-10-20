using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Oranda.App.Features.GridEdit;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.History;
using Oranda.Core.Language;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Text;

namespace Oranda.App.Features.Execution;

/// <summary>실행 범위(commands.md 「SQL」).</summary>
public enum RunMode
{
    /// <summary>선택이 있으면 선택, 없으면 커서 문장.</summary>
    Statement,

    /// <summary>선택 영역(문장이 여럿이면 스크립트로).</summary>
    Selection,

    /// <summary>문서 전체.</summary>
    Script,

    /// <summary>커서 문장부터 끝까지.</summary>
    FromCursor,

    /// <summary>커서 문장 하나를 실행하고 커서를 다음 문장으로 옮긴다.</summary>
    Step,
}

/// <summary>
/// 컨트롤러가 쓰는 바깥 의존. 서비스는 전부 호출 때 해석한다(<see cref="Func{TResult}"/>) — 편집기 생성과 시작 경로가
/// Oracle·Language 어셈블리를 깨우지 않도록(D-006, 04-02 decisions).
/// </summary>
public sealed class ExecutionDeps
{
    public required ExecutionState State { get; init; }

    public required ExecutionLog Log { get; init; }

    public required IExecutionDialogs Dialogs { get; init; }

    public required Func<ISession?> Session { get; init; }

    public required Func<IExecutionService> Execution { get; init; }

    public required Func<ILanguageService> Language { get; init; }

    public Func<SqlEditorHost> Host { get; init; } = () => SqlEditorHost.Current;

    public Func<IRowEditor?> RowEditor { get; init; } = () => null;

    public Func<IMetadataProvider?> Metadata { get; init; } = () => null;

    /// <summary>도구 창 탭을 앞으로 가져온다(패널 ID). 실행하면 「조회 결과」 창을 보인다.</summary>
    public Action<string> ActivatePanel { get; init; } = _ => { };

    /// <summary>도구 창 보이기/숨기기(패널 ID).</summary>
    public Action<string> TogglePanel { get; init; } = _ => { };

    /// <summary>바인드 변수 목록(「바인드 변수」 창과 같은 것). 기본값은 버스 없는 공용 목록.</summary>
    public BindVariableStore Binds { get; init; } = BindVariableStore.For(null);
}

/// <summary>
/// SQL 실행 화면의 중심(TSK-06-02). 편집기에서 실행 범위를 정하고(문장 나누기는 <see cref="ILanguageService"/>),
/// 바인드·치환 변수를 받고, <see cref="IExecutionService"/> 로 실행하고, 결과를 결과 영역(<see cref="ResultTabsViewModel"/> →
/// <c>SqlResultPaneViewModel</c>)·메시지 창·상태에 반영한다. DB 호출은 항상 스레드 풀에서 한다(UI 스레드 금지).
/// </summary>
public sealed class ExecutionController : IDisposable
{
    private readonly ExecutionDeps _d;
    private CancellationTokenSource? _cts;
    private Window? _window;
    private bool _exitApproved;
    private bool _lastCancelled;
    /// <summary>결과·오류를 보이는 아래 도구 창(<see cref="ResultsPanelViewModel"/>).</summary>
    public const string ResultsPanelId = "oranda.panel.results";

    /// <summary>바인드 변수 창(<see cref="BindVariablesViewModel"/>).</summary>
    public const string BindsPanelId = "oranda.panel.binds";

    public ExecutionController(ExecutionDeps deps)
    {
        ArgumentNullException.ThrowIfNull(deps);
        _d = deps;

        // 그리드 편집(TSK-06-04)이 비워 둔 연결 지점을 채운다.
        GridEditCommands.Rerun = RerunForEditAsync;
        GridEditCommands.ConfirmPreview = (text, ct) => _d.Dialogs.PreviewDmlAsync(text, ct);
    }

    public ExecutionState State => _d.State;

    public ExecutionLog Log => _d.Log;

    private SqlEditorHost Host => _d.Host();

    // ---- 실행 ------------------------------------------------------------------------------

    /// <summary>편집기 <paramref name="tab"/>(없으면 활성 탭)에서 <paramref name="mode"/> 범위를 실행한다.</summary>
    public async Task<bool> RunAsync(RunMode mode, bool newResultTab = false, SqlEditorViewModel? tab = null, CancellationToken ct = default)
    {
        tab ??= Host.Active;
        if (tab is null || State.Busy)
        {
            return false;
        }

        var (stmts, script) = SelectStatements(tab, mode);
        if (stmts.Count == 0)
        {
            Log.AddMessage(MessageKind.Warning, "실행할 문장이 없습니다.");
            return false;
        }

        var ok = stmts.Count == 1 && !script
            ? await RunOneAsync(tab, stmts[0], newResultTab, ct).ConfigureAwait(true)
            : await RunScriptAsync(tab, stmts, newResultTab, ct).ConfigureAwait(true);

        if (mode == RunMode.Step && ok)
        {
            MoveToNextStatement(tab, stmts[0]);
        }

        return ok;
    }

    /// <summary>모든 탭을 차례로 스크립트로 실행한다. 취소하면 거기서 멈춘다.</summary>
    public async Task RunAllTabsAsync(CancellationToken ct = default)
    {
        foreach (var tab in Host.Tabs.ToList())
        {
            _lastCancelled = false;
            await RunAsync(RunMode.Script, newResultTab: false, tab, ct).ConfigureAwait(true);
            if (_lastCancelled)
            {
                break;
            }
        }
    }

    /// <summary>실행 취소. 서버에서 중단하고(엔진이 <c>OracleCommand.Cancel</c>) 상태는 「취소 중」으로 바뀐다.</summary>
    public void Cancel()
    {
        if (_cts is { IsCancellationRequested: false } cts)
        {
            State.MarkCancelling();
            Log.AddMessage(MessageKind.Info, "실행 취소를 요청했습니다.");
            cts.Cancel();
        }
    }

    /// <summary>F9 · 「ROWID 추가 후 다시 실행」: 한 문장을 실행하고 결과를 편집 모드로 연다. <paramref name="query"/> 가 null 이면 커서 문장.</summary>
    public async Task RerunForEditAsync(SqlStatement? query, CancellationToken ct)
    {
        var tab = Host.Active;
        if (tab is null || State.Busy)
        {
            return;
        }

        var stmt = query ?? tab.GetStatementAtCaret();
        if (stmt is null)
        {
            Log.AddMessage(MessageKind.Warning, "실행할 문장이 없습니다.");
            return;
        }

        if (!await RunOneAsync(tab, stmt, newResultTab: false, ct).ConfigureAwait(true))
        {
            return;
        }

        if (tab.ResultPane is not ResultTabsViewModel tabs
            || tabs.Selected is not { Pane.Model: { } model } item
            || _d.Session() is not { } session
            || _d.RowEditor() is not { } editor
            || _d.Metadata() is not { } metadata)
        {
            return;
        }

        var es = await GridEditSession.OpenAsync(
            editor, metadata, session, stmt, model.Columns,
            (row, col) => model.TryGetValue(row, col, out var v) ? v : null,
            () => model.LoadedRowCount, null, ct).ConfigureAwait(true);
        item.EditSession = es;
        item.Title += " (편집)";
        GridEditCommands.Active = es;
        GridEditBinding.Attach(model, es); // 바꾼 값·새 행을 그리드에 그리고 셀 편집기가 세션을 찾는다
        item.Pane.SetEditSession(es); // 편집 툴바(고칠 수 있을 때)나 고칠 수 없는 이유(안내 막대)를 보인다
        if (!es.CanEdit)
        {
            Log.AddMessage(MessageKind.Warning, "결과를 편집할 수 없습니다: " + (es.Capability.Message ?? es.Capability.Reason.ToString()));
        }
    }

    // ---- 트랜잭션 ---------------------------------------------------------------------------

    public async Task CommitAsync(CancellationToken ct = default)
    {
        if (_d.Session() is not { } s)
        {
            return;
        }

        try
        {
            await Task.Run(() => s.CommitAsync(ct), CancellationToken.None).ConfigureAwait(true);
            State.ApplyTransaction(s.HasPendingTransaction);
            Log.AddMessage(MessageKind.Info, "커밋했습니다.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.AddMessage(MessageKind.Error, "커밋 실패: " + e.Message);
        }
    }

    public async Task RollbackAsync(CancellationToken ct = default)
    {
        if (_d.Session() is not { } s)
        {
            return;
        }

        try
        {
            await Task.Run(() => s.RollbackAsync(ct), CancellationToken.None).ConfigureAwait(true);
            State.ApplyTransaction(s.HasPendingTransaction);
            Log.AddMessage(MessageKind.Info, "롤백했습니다.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.AddMessage(MessageKind.Error, "롤백 실패: " + e.Message);
        }
    }

    /// <summary>자동 커밋을 켜면 이후 성공한 변경 문장 뒤에 바로 커밋한다(화면 수준 동작).</summary>
    public void ToggleAutoCommit() => State.AutoCommit = !State.AutoCommit;

    /// <summary>
    /// 미커밋 트랜잭션이 있으면 커밋/롤백/취소를 묻는다(SAFE: 종료 시 확인). true = 계속 닫아도 된다.
    /// </summary>
    public async Task<bool> ConfirmCloseAsync(CancellationToken ct = default)
    {
        if (!State.TxnPending)
        {
            return true;
        }

        var choice = await _d.Dialogs.AskUncommittedAsync(ct).ConfigureAwait(true);
        switch (choice)
        {
            case UncommittedChoice.Commit:
                await CommitAsync(ct).ConfigureAwait(true);
                return !State.TxnPending;
            case UncommittedChoice.Rollback:
                await RollbackAsync(ct).ConfigureAwait(true);
                return !State.TxnPending;
            default:
                return false;
        }
    }

    /// <summary>주 창이 닫힐 때 미커밋을 묻도록 건다. 창이 처음 닫힐 때 한 번만 가로챈다.</summary>
    public void AttachWindow(Window? window)
    {
        if (window is null || ReferenceEquals(_window, window))
        {
            return;
        }

        _window = window;
        window.Closing += (_, e) =>
        {
            if (_exitApproved || !State.TxnPending)
            {
                return;
            }

            e.Cancel = true;
            _ = CloseAfterConfirmAsync(window);
        };
    }

    private async Task CloseAfterConfirmAsync(Window window)
    {
        if (await ConfirmCloseAsync().ConfigureAwait(true))
        {
            _exitApproved = true;
            window.Close();
        }
    }

    /// <summary>조회 결과 창 보이기/숨기기(<c>oranda.window.toggleResultPane</c>). 숨겨도 탭의 결과는 남는다.</summary>
    public void ToggleResultPane() => _d.TogglePanel(ResultsPanelId);

    /// <summary>그리드 편집 반영 전 DML 미리보기. 사용자가 확인하면 true.</summary>
    public Task<bool> ConfirmPreviewAsync(string text, CancellationToken ct) => _d.Dialogs.PreviewDmlAsync(text, ct);

    // ---- 바인드 ----------------------------------------------------------------------------

    /// <summary>
    /// 바인드 변수 창(<c>oranda.exec.bindVariables</c>): 창을 보이고, 커서 문장의 바인드 이름을 목록에 채워 첫 값 칸으로 간다.
    /// 실행은 하지 않는다(오렌지 Bind Variable List).
    /// </summary>
    public Task<bool> EditBindsAsync(SqlEditorViewModel? tab = null, CancellationToken ct = default)
    {
        tab ??= Host.Active;
        _d.ActivatePanel(BindsPanelId);
        if (tab?.GetStatementAtCaret() is not { BindNames.Count: > 0 } stmt)
        {
            Log.AddMessage(MessageKind.Info, "현재 문장에 바인드 변수가 없습니다. 「바인드 변수」 창에서 직접 더할 수 있습니다.");
            return Task.FromResult(false);
        }

        _d.Binds.Ensure(stmt.BindNames);
        _d.Binds.RequestFocus(stmt.BindNames[0]);
        return Task.FromResult(true);
    }

    /// <summary>
    /// 실행할 바인드 값을 「바인드 변수」 목록에서 만든다(묻는 창 없음). 처음 보는 이름은 목록에 더하고 실행하지 않는다:
    /// 값 없이 실행하면 INSERT·UPDATE 가 NULL 로 돌 수 있어서다. 형식에 맞지 않는 값도 실행하지 않는다.
    /// </summary>
    private Dictionary<string, BindValue>? ResolveBinds(IEnumerable<string> names, ResultTabsViewModel? results)
    {
        var list = names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0)
        {
            return new Dictionary<string, BindValue>(StringComparer.OrdinalIgnoreCase);
        }

        string? problem = null;
        var added = _d.Binds.Ensure(list);
        if (added.Count > 0)
        {
            problem = $"바인드 변수 {string.Join(", ", added.Select(n => ":" + n))} 의 값을 「바인드 변수」 창에 넣은 뒤 다시 실행하세요. (빈 값은 NULL)";
            _d.Binds.RequestFocus(added[0]);
        }
        else if (!_d.Binds.TryBuild(list, out var values, out var error))
        {
            problem = error ?? "바인드 값을 읽을 수 없습니다.";
        }
        else
        {
            return values;
        }

        _d.ActivatePanel(BindsPanelId);
        Log.AddMessage(added.Count > 0 ? MessageKind.Warning : MessageKind.Error, problem);
        if (results is not null)
        {
            results.Clear();
            results.AddMessage("바인드 변수", problem, isError: added.Count == 0);
        }

        return null;
    }

    /// <summary>
    /// 편집기의 바인드 변수를 목록의 값으로 바꿔 넣는다(오렌지 「Paste Bind Variables Into Editor」, <c>oranda.exec.pasteBindValues</c>).
    /// 렉서의 바인드 토큰만 바꾼다(문자열·주석 안은 그대로). 목록에 없거나 값을 읽을 수 없는 변수는 남긴다. 한 번에 실행 취소된다.
    /// </summary>
    public int PasteBindValues(SqlEditorViewModel? tab = null)
    {
        tab ??= Host.Active;
        if (tab is null || tab.IsReadOnly)
        {
            return 0;
        }

        var text = tab.Text;
        var edits = new List<(int Start, int Length, string Literal)>();
        foreach (var t in Oranda.Language.Lexing.SqlLexer.Tokenize(text))
        {
            if (t.Kind != Oranda.Language.Lexing.TokenKind.Bind || t.Length < 2)
            {
                continue;
            }

            var name = text.Substring(t.Start + 1, t.Length - 1);
            if (_d.Binds.Find(name) is { } row && BindVariableStore.ToLiteral(row) is { } literal)
            {
                edits.Add((t.Start, t.Length, literal));
            }
        }

        if (edits.Count == 0)
        {
            Log.AddMessage(MessageKind.Info, "바꿀 바인드 변수가 없습니다(목록에 값이 있는 변수만 바꿉니다).");
            return 0;
        }

        var doc = tab.Document;
        doc.BeginUpdate();
        try
        {
            for (int i = edits.Count - 1; i >= 0; i--)
            {
                doc.Replace(edits[i].Start, edits[i].Length, edits[i].Literal);
            }
        }
        finally
        {
            doc.EndUpdate();
        }

        Log.AddMessage(MessageKind.Info, $"바인드 변수 {edits.Count.ToString(CultureInfo.InvariantCulture)}곳을 값으로 바꿨습니다.");
        return edits.Count;
    }

    // ---- 문장 고르기 -------------------------------------------------------------------------

    private SplitOptions Options => new() { SplitOnBlankLine = State.BlankLineDelimiter };

    /// <summary>실행 범위의 문장 목록. 두 번째 값이 참이면 스크립트 실행(문장이 하나여도 스크립트로 취급하는 경우).</summary>
    public (IReadOnlyList<SqlStatement> Statements, bool Script) SelectStatements(SqlEditorViewModel tab, RunMode mode)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var lang = _d.Language();
        var text = tab.Text;
        switch (mode)
        {
            case RunMode.Script:
                return (NonEmpty(lang.Split(text, Options)), true);

            case RunMode.Selection:
                return (SplitSelection(lang, tab), false);

            case RunMode.Statement when tab.HasSelection:
                return (SplitSelection(lang, tab), false);

            case RunMode.FromCursor:
                {
                    var all = NonEmpty(lang.Split(text, Options));
                    int caret = tab.CaretOffset;
                    int index = -1;
                    for (int i = 0; i < all.Count; i++)
                    {
                        if (all[i].Range.End >= caret)
                        {
                            index = i;
                            break;
                        }
                    }

                    return (index < 0 ? [] : [.. all.Skip(index)], true);
                }

            default:
                {
                    var one = lang.GetStatementAt(text, tab.CaretOffset, Options);
                    return (one is null || string.IsNullOrWhiteSpace(one.Text) ? [] : [one], false);
                }
        }
    }

    private List<SqlStatement> SplitSelection(ILanguageService lang, SqlEditorViewModel tab)
    {
        int start = tab.SelectionStart;
        var sel = tab.Document.GetText(start, tab.SelectionLength);
        var parts = NonEmpty(lang.Split(sel, Options));
        return [.. parts.Select(p => p with { Range = new TextRange(p.Range.Start + start, p.Range.Length) })];
    }

    private static List<SqlStatement> NonEmpty(IEnumerable<SqlStatement> all) => [.. all.Where(s => !string.IsNullOrWhiteSpace(s.Text))];

    private void MoveToNextStatement(SqlEditorViewModel tab, SqlStatement done)
    {
        // 다음 문장이 있으면 그 시작으로, 없으면 현재 자리.
        var next = NonEmpty(_d.Language().Split(tab.Text, Options)).FirstOrDefault(s => s.Range.Start >= done.Range.End);
        if (next is not null)
        {
            tab.Select(next.Range.Start, 0);
        }
    }

    // ---- 실행 몸통 ---------------------------------------------------------------------------

    private async Task<bool> RunOneAsync(SqlEditorViewModel tab, SqlStatement stmt, bool newResultTab, CancellationToken ct)
    {
        var results = Prepare(tab, out var session);
        if (session is null)
        {
            return false;
        }

        var binds = ResolveBinds(stmt.BindNames, results);
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
            await AfterStatementAsync(session, result, token).ConfigureAwait(true);
        }, ct).ConfigureAwait(true);

        if (result is null)
        {
            return false;
        }

        if (!newResultTab)
        {
            results.Clear();
        }

        Present(tab, results, stmt, result, newResultTab ? results.Tabs.Count + 1 : 1);
        _lastCancelled = result.Status == ExecutionStatus.Cancelled;
        return !_lastCancelled;
    }

    private async Task<bool> RunScriptAsync(SqlEditorViewModel tab, IReadOnlyList<SqlStatement> stmts, bool newResultTab, CancellationToken ct)
    {
        var results = Prepare(tab, out var session);
        if (session is null)
        {
            return false;
        }

        var binds = ResolveBinds(stmts.SelectMany(st => st.BindNames), results);
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

                    await AfterStatementAsync(session, r, token).ConfigureAwait(true);
                    summary.AppendLine(CultureInfo.InvariantCulture, $"{StatusMark(r.Status)} {Brief(r.Statement)} — {Describe(r)}");
                    Present(tab, results, r.Statement, r, results.Tabs.Count + 1, scriptMode: true);
                }
            }
            finally
            {
                await en.DisposeAsync().ConfigureAwait(true);
            }
        }, ct).ConfigureAwait(true);

        var line = $"스크립트: {stmts.Count.ToString(CultureInfo.InvariantCulture)}개 중 {done.ToString(CultureInfo.InvariantCulture)}개 실행, 성공 {ok.ToString(CultureInfo.InvariantCulture)}, 실패 {failed.ToString(CultureInfo.InvariantCulture)}" + (cancelled ? ", 취소됨" : string.Empty);
        Log.AddMessage(failed > 0 ? MessageKind.Warning : MessageKind.Info, line);
        var firstQuery = results.Tabs.FirstOrDefault(t => t.HasPane);
        results.AddMessage("스크립트", line + Environment.NewLine + summary, failed > 0);
        if (firstQuery is not null)
        {
            results.Selected = firstQuery;
        }

        _lastCancelled = cancelled;
        return !cancelled;
    }

    /// <summary>실행하는 범위를 편집기에 잠깐 칠한다. 문서 밖에서 만든 문장(범위 없음)은 건너뛴다.</summary>
    private static void Flash(SqlEditorViewModel tab, int start, int end)
    {
        if (end > start && end <= tab.Document.TextLength)
        {
            tab.FlashRange(start, end - start);
        }
    }

    /// <summary>실행 직전 준비: 세션 확인, 결과 영역을 편집기에 붙임.</summary>
    private ResultTabsViewModel Prepare(SqlEditorViewModel tab, out ISession? session)
    {
        session = _d.Session();
        var results = EnsureResults(tab, session);
        _d.ActivatePanel(ResultsPanelId); // 결과·오류는 아래 「조회 결과」 탭에(포커스는 편집기에 남는다)
        if (session is null)
        {
            const string none = "접속이 없습니다. 접속한 뒤 실행하세요.";
            Log.AddMessage(MessageKind.Warning, none);
            results.Clear();
            results.AddMessage("메시지", none, isError: true);
        }
        else if (State.RunState == SessionRunState.Broken)
        {
            const string broken = "세션이 끊겼습니다. 다시 접속한 뒤 실행하세요.";
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
    private async Task GuardedAsync(ISession session, Func<CancellationToken, Task> body, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        _cts = cts;
        State.BeginRun();
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
            _cts = null;
            State.ApplyTransaction(session.HasPendingTransaction);
            State.EndRun();
        }
    }

    /// <summary>자동 커밋이 켜져 있으면 성공한 변경 뒤에 커밋한다.</summary>
    private async Task AfterStatementAsync(ISession session, ExecutionResult r, CancellationToken ct)
    {
        if (State.AutoCommit && r.Status == ExecutionStatus.Succeeded && r.TransactionPending && !State.ReadOnly)
        {
            await Task.Run(() => session.CommitAsync(ct), CancellationToken.None).ConfigureAwait(true);
        }
    }

    // ---- 결과 반영 ---------------------------------------------------------------------------

    private void Present(SqlEditorViewModel tab, ResultTabsViewModel results, SqlStatement stmt, ExecutionResult r, int ordinal, bool scriptMode = false)
    {
        var title = "결과 " + ordinal.ToString(CultureInfo.InvariantCulture);
        var text = Describe(r);
        switch (r.Status)
        {
            case ExecutionStatus.Succeeded:
                Log.AddMessage(MessageKind.Info, $"{Brief(stmt)} — {text}");

                // SQL*Plus 명령이 낸 글(PROMPT, DEFINE 목록, SPOOL 상태 등)은 메시지 창에 그대로 보인다.
                if (stmt.Kind == StatementKind.SqlPlus)
                {
                    foreach (var line in r.DbmsOutput)
                    {
                        Log.AddMessage(MessageKind.Info, line);
                    }
                }
                if (r.ResultSet is { } rs)
                {
                    results.AddQuery(rs, title, stmt);
                }
                else if (!scriptMode)
                {
                    results.AddMessage("메시지", text, isError: false, stmt);
                }

                foreach (var (name, value) in r.OutValues)
                {
                    if (value is IResultSet cursor)
                    {
                        results.AddQuery(cursor, ":" + name, stmt);
                    }
                    else
                    {
                        Log.AddMessage(MessageKind.Info, $":{name} = {value ?? "NULL"}");
                    }
                }

                break;

            case ExecutionStatus.Failed:
                State.SetError(stmt, r.Error, tab.TabId);
                Log.AddMessage(MessageKind.Error, $"{Brief(stmt)} — {text}");
                if (!scriptMode)
                {
                    results.AddMessage("오류", ErrorText(r), isError: true, stmt);
                }
                else
                {
                    results.AddMessage("오류 " + ordinal.ToString(CultureInfo.InvariantCulture), ErrorText(r), isError: true, stmt);
                }

                break;

            case ExecutionStatus.Denied:
                Log.AddMessage(MessageKind.Warning, $"{Brief(stmt)} — 거부됨: {r.Policy?.Message}");
                results.AddMessage("거부됨", r.Policy?.Message ?? "정책 관문이 거부했습니다.", isError: true, stmt);
                break;

            default:
                Log.AddMessage(MessageKind.Info, $"{Brief(stmt)} — 취소됨");
                if (!scriptMode)
                {
                    results.AddMessage("메시지", "취소되었습니다.", isError: false, stmt);
                }

                break;
        }
    }

    private static string StatusMark(ExecutionStatus s) => s switch
    {
        ExecutionStatus.Succeeded => "[성공]",
        ExecutionStatus.Failed => "[실패]",
        ExecutionStatus.Denied => "[거부]",
        _ => "[취소]",
    };

    private static string Describe(ExecutionResult r)
    {
        var time = r.Elapsed.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + "초";
        return r.Status switch
        {
            ExecutionStatus.Succeeded when r.RowsAffected is { } n => $"{n.ToString("N0", CultureInfo.InvariantCulture)}행 처리됨 ({time})",
            ExecutionStatus.Succeeded when r.ResultSet is not null => $"조회 완료 ({time})",
            ExecutionStatus.Succeeded => $"완료 ({time})",
            ExecutionStatus.Failed => DbErrorText.Format(r.Error),
            ExecutionStatus.Denied => "거부됨",
            _ => "취소됨",
        };
    }

    private static string ErrorText(ExecutionResult r)
    {
        var e = r.Error;
        if (e is null)
        {
            return "실행에 실패했습니다.";
        }

        var sb = new StringBuilder();
        sb.AppendLine(DbErrorText.Format(e));
        if (e.Line is { } l && e.Column is { } c)
        {
            sb.Append(CultureInfo.InvariantCulture, $"위치: 문장 안 줄 {l}, 열 {c}").AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private static string Brief(SqlStatement s)
    {
        var line = s.Text.TrimStart().Split('\n', 2)[0].Trim();
        return line.Length > 60 ? line[..60] + "…" : line;
    }

    // ---- 오류 문장으로 이동 --------------------------------------------------------------------

    /// <summary>마지막 오류가 난 자리로 편집기 커서를 옮긴다(<c>oranda.exec.goToError</c>).</summary>
    public bool GoToError()
    {
        if (State.LastErrorStatement is not { } stmt)
        {
            return false;
        }

        var host = Host;
        var tab = (State.LastErrorTab is { } id ? host.Tabs.FirstOrDefault(t => t.TabId == id) : null) ?? host.Active;
        if (tab is null || stmt.Range.Length == 0)
        {
            return false;
        }

        int offset = Math.Min(stmt.Range.Start + Math.Max(0, State.LastError?.Offset ?? 0), tab.Document.TextLength);
        host.Active = tab;
        tab.Select(offset, 0);
        tab.Surface?.ScrollToLine(tab.PositionOf(offset).Line);
        tab.Surface?.FocusEditor();
        return true;
    }

    public void Dispose() => _cts?.Cancel();
}
