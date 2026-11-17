using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Sessions;

namespace Oranda.App.Features.TableEdit;

/// <summary>실행 결과 종류.</summary>
public enum TableEditRunOutcome
{
    /// <summary>보낼 문장이 없다(바뀐 것 없음·문장을 못 만듦).</summary>
    Nothing,

    /// <summary>검사에 ✖ 가 있어 보내지 않았다(설계 10장).</summary>
    Blocked,

    /// <summary>사용자가 확인을 거절했다(운영 확인·⚠ 확인·미커밋 경고).</summary>
    Declined,

    /// <summary>정책이 거부했다(읽기 전용 접속 등). 아무것도 보내지 않았다.</summary>
    Denied,

    /// <summary>모든 문장이 성공했다.</summary>
    Succeeded,

    /// <summary>앞 문장 일부가 성공한 뒤 실패했다(TE-5: 남은 편집 다시 계산).</summary>
    PartiallyFailed,

    /// <summary>첫 문장부터 실패했다.</summary>
    Failed,

    /// <summary>실행 중 취소했다.</summary>
    Cancelled,
}

/// <summary>실행 결과.</summary>
/// <param name="Findings">실행 전 검사 판정(✖ → ⚠ → ✔)</param>
/// <param name="SucceededCount">성공한 문장 수(앞에서부터)</param>
/// <param name="Failure">실패·거부·취소된 문장의 결과(없으면 null)</param>
public sealed record TableEditRunResult(TableEditRunOutcome Outcome, IReadOnlyList<CheckFinding> Findings, int SucceededCount, ExecutionResult? Failure)
{
    /// <summary>실행한 문장(미리보기에 보인 같은 객체). 중간 실패 뒤 다시 계산(<see cref="TableDraftRebase.Rebase"/>, TE-5)의 입력.</summary>
    public TableEditScript Script { get; init; } = TableEditScript.Empty;

    /// <summary>사용자에게 보일 한 줄(실패 메시지 포함).</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>문장이 하나라도 반영돼 서버 구조가 바뀌었다(다시 읽어야 함).</summary>
    public bool Changed => SucceededCount > 0;
}

/// <summary>
/// 테이블 편집 실행(테이블 편집기 설계 10·11장, T7): 실행 전 검사(<see cref="ITableEditEngine.Plan"/> + <see cref="ITableEditChecks"/>) →
/// ✖ 면 막음 → 정책 판단을 미리 보고 운영 확인은 한 번(<see cref="IExecutionInteraction.ConfirmBatchAsync"/>, TE-34, 검사 줄을 같은 창에) →
/// 개발 접속은 ⚠ 만이면 창 없이(TE-20), 테스트 접속 ⚠ 는 검사 확인 창 → <see cref="IExecutionService.ExecuteScriptAsync"/>(Stop, <c>PreConfirmed</c> 문장별, TE-21).
/// 문장은 미리보기에 보인 <see cref="TableEditSession.Script"/> 의 같은 객체다(설계 4장). 다시 읽기·다시 계산은 부르는 쪽(객체 상세).
/// </summary>
public sealed class TableEditRunner
{
    /// <summary>
    /// 실행 이력의 출처. <c>HistorySource.TableEditor</c>(TE-35)는 te-bundle 이 더한다 — 머지되면 이 값만 바꾼다(그 전에는 스키마 브라우저 출처와 같게 둔다).
    /// </summary>
    public static HistorySource Source => HistorySource.SchemaBrowser;

    private readonly IExecutionService _exec;

    public TableEditRunner(IExecutionService exec) => _exec = exec ?? throw new ArgumentNullException(nameof(exec));

    /// <summary>정책(운영 확인을 한 번으로 모으려고 미리 판단한다). 없으면 미리 묻지 않고 실행 엔진의 관문에 맡긴다.</summary>
    public IExecutionPolicy? Policy { get; init; }

    /// <summary>운영 확인 창(<see cref="IExecutionInteraction.ConfirmBatchAsync"/>). 없으면 확인이 필요한 문장은 보내지 않는다.</summary>
    public IExecutionInteraction? Interaction { get; init; }

    /// <summary>검사 읽기. 없으면 서버 검사 없이 ⚠ 「확인 못 함」 한 줄.</summary>
    public ITableEditChecks? Checks { get; init; }

    /// <summary>
    /// ⚠ 가 있을 때 묻기(테스트 접속 등, 운영은 운영 확인 창에 같이 실림). null 이면 묻지 않고 진행. 답 true = 그래도 실행.
    /// </summary>
    public Func<IReadOnlyList<CheckFinding>, CancellationToken, Task<bool>>? ConfirmWarnings { get; init; }

    /// <summary>세션에 커밋하지 않은 변경이 있을 때 묻기(DDL 이 그것을 커밋한다, 설계 5장). null 이면 묻지 않고 진행.</summary>
    public Func<CancellationToken, Task<bool>>? ConfirmPendingCommit { get; init; }

    /// <summary>검사 질의 하나의 시간 제한(기본 10초, 설계 10장).</summary>
    public TimeSpan CheckTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>운영 큰 테이블 기준 행 수(기본 100만).</summary>
    public long LargeTableRows { get; init; } = 1_000_000;

    /// <summary>실행 전 검사만 한다(실행·묶음 넣기 앞, 「검사」 단추). ✖ → ⚠ → ✔ 순서.</summary>
    public async Task<IReadOnlyList<CheckFinding>> CheckAsync(ISession session, DbObjectRef table, TableEditSession edit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(edit);
        var options = new TableEditCheckOptions
        {
            Environment = session.Profile.Environment,
            Server = session.Server,
            Timeout = CheckTimeout,
            LargeTableRows = LargeTableRows,
        };

        IReadOnlyList<CheckFinding> findings;
        try
        {
            var plan = edit.Engine.Plan(edit.Original, edit.Draft, edit.Dialect, options);
            findings = Checks is null
                ? [.. plan.Findings, .. plan.Checks.Count == 0 ? [] : new[] { NotChecked(TableEditText.T("검사 읽기 구현이 없다", "no check reader for this DBMS")) }]
                : await edit.Engine.RunChecksAsync(plan, table, Checks, session, options, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            findings = [NotChecked(ex.Message)];
        }

        return [.. findings.OrderBy(f => f.Severity switch { CheckSeverity.Block => 0, CheckSeverity.Warning => 1, _ => 2 })];
    }

    /// <summary>검사 → 확인 → 문장별 실행. 다시 읽기는 하지 않는다.</summary>
    public async Task<TableEditRunResult> RunAsync(ISession session, DbObjectRef table, TableEditSession edit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(edit);
        var script = edit.Script; // 미리보기와 같은 객체(설계 4장)
        if (edit.ScriptError is not null || script.IsEmpty)
        {
            return new TableEditRunResult(TableEditRunOutcome.Nothing, [], 0, null)
            {
                Message = edit.ScriptError ?? TableEditText.T("실행할 문장이 없다", "Nothing to run"),
            };
        }

        var findings = await CheckAsync(session, table, edit, ct);
        if (findings.Any(f => f.Severity == CheckSeverity.Block))
        {
            return new TableEditRunResult(TableEditRunOutcome.Blocked, findings, 0, null)
            {
                Message = TableEditText.T("실행 전 검사에서 ✖ 가 있어 실행하지 않았다", "Not run: the pre-run check found blocking problems"),
            };
        }

        var statements = script.SqlStatements;
        var environment = session.Profile.Environment;
        var confirmed = new HashSet<string>(StringComparer.Ordinal);

        // 정책 판단을 미리 본다: 거부면 아무것도 보내지 않고, 확인이 필요하면(개발 접속 제외, D-065) 한 번만 묻는다(TE-21·TE-34).
        if (Policy is { } policy)
        {
            var decisions = new List<PolicyDecision>();
            foreach (var stmt in statements)
            {
                decisions.Add(await policy.EvaluateAsync(new PolicyContext(session.Profile, session.Server, Source), stmt, ct));
            }

            if (decisions.FirstOrDefault(d => d.Kind == PolicyDecisionKind.Deny) is { } deny)
            {
                return new TableEditRunResult(TableEditRunOutcome.Denied, findings, 0, null)
                {
                    Message = deny.Message ?? TableEditText.T("정책이 실행을 막았다", "Blocked by the execution policy"),
                };
            }

            var asks = decisions.Where(d => d.Kind == PolicyDecisionKind.Confirm).ToList();
            if (asks.Count > 0 && environment != ConnectionEnvironment.Development)
            {
                var shown = asks.FirstOrDefault(d => d.RetypeText is not null) ?? asks[0];
                if (Interaction is null || !await Interaction.ConfirmBatchAsync(statements, shown, findings, session.Profile, ct))
                {
                    return Declined(findings);
                }

                confirmed.UnionWith(statements.Select(s => s.Text));
            }
        }

        // 운영이 아닌 접속의 ⚠ 는 검사 확인 창으로(운영은 위 확인 창에 실렸다). 개발 접속은 창 없이 아래 줄에만(TE-20).
        if (confirmed.Count == 0 && environment != ConnectionEnvironment.Development
            && findings.Any(f => f.Severity == CheckSeverity.Warning) && ConfirmWarnings is { } warn && !await warn(findings, ct))
        {
            return Declined(findings);
        }

        if (session.HasPendingTransaction && ConfirmPendingCommit is { } pending && !await pending(ct))
        {
            return Declined(findings);
        }

        if (!ReferenceEquals(edit.Script, script))
        {
            // 검사·확인을 기다리는 사이 편집이 바뀌었다: 확인받은 문장과 지금 미리보기가 다르므로 보내지 않는다(설계 4장).
            return new TableEditRunResult(TableEditRunOutcome.Declined, findings, 0, null)
            {
                Message = TableEditText.T("확인하는 사이 편집이 바뀌어 실행하지 않았다. 다시 실행하세요", "The edit changed while confirming; not run. Run again"),
            };
        }

        var options = new ExecutionOptions { Source = Source, PreConfirmed = confirmed.Count == 0 ? null : confirmed };
        var succeeded = 0;
        ExecutionResult? failure = null;
        try
        {
            await foreach (var result in _exec.ExecuteScriptAsync(session, statements, ScriptErrorMode.Stop, options, ct))
            {
                if (result.Status == ExecutionStatus.Succeeded)
                {
                    succeeded++;
                    continue;
                }

                failure = result;
                break;
            }
        }
        catch (OperationCanceledException)
        {
            failure = new ExecutionResult(succeeded < statements.Count ? statements[succeeded] : statements[^1], ExecutionStatus.Cancelled);
        }

        if (failure is null && succeeded < statements.Count)
        {
            failure = new ExecutionResult(statements[succeeded], ExecutionStatus.Cancelled); // 엔진이 결과를 덜 냈다(멈춤)
        }

        if (failure is null)
        {
            return new TableEditRunResult(TableEditRunOutcome.Succeeded, findings, succeeded, null)
            {
                Script = script,
                Message = TableEditText.T($"{succeeded}문장을 실행했다", $"Ran {succeeded} statement(s)"),
            };
        }

        var outcome = failure.Status switch
        {
            ExecutionStatus.Denied when succeeded == 0 => TableEditRunOutcome.Denied,
            ExecutionStatus.Cancelled when succeeded == 0 => TableEditRunOutcome.Cancelled,
            _ when succeeded == 0 => TableEditRunOutcome.Failed,
            _ => TableEditRunOutcome.PartiallyFailed,
        };
        var why = failure.Error?.Message ?? failure.Policy?.Message ?? failure.Status.ToString();
        return new TableEditRunResult(outcome, findings, succeeded, failure)
        {
            Script = script,
            Message = succeeded == 0
                ? TableEditText.T($"실행하지 못했다: {why}", $"Not run: {why}")
                : TableEditText.T($"{statements.Count}문장 중 {succeeded}문장을 반영한 뒤 {succeeded + 1}번째 문장이 실패했다: {why}",
                                  $"Applied {succeeded} of {statements.Count} statement(s); statement {succeeded + 1} failed: {why}"),
        };
    }

    private static TableEditRunResult Declined(IReadOnlyList<CheckFinding> findings) =>
        new(TableEditRunOutcome.Declined, findings, 0, null) { Message = TableEditText.T("실행하지 않았다", "Not run") };

    private static CheckFinding NotChecked(string detail) =>
        new(CheckSeverity.Warning, CheckCode.NotChecked, TableDraft.TableRowKey) { Detail = detail };
}
