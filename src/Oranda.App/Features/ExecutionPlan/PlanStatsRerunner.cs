using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Features.Binds;
using Oranda.App.Features.Execution;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;
using Oranda.Language.Ai.References;
using Oranda.Language.Lexing;

namespace Oranda.App.Features.ExecutionPlan;

/// <summary>A-Rows 를 받으며 다시 실행하는 방법(TN-02).</summary>
public enum PlanRerunMode
{
    /// <summary><c>/*+ GATHER_PLAN_STATISTICS */</c> 를 넣은 사본을 실행.</summary>
    Hint,

    /// <summary><c>ALTER SESSION SET STATISTICS_LEVEL = ALL</c> 뒤 같은 문장을 다시 실행.</summary>
    StatisticsLevel,
}

/// <summary>문장을 보내기 전에 막혔다(접속 없음·세션 끊김·실행 중). 아무것도 실행하지 않았으므로 자동 재실행을 다음 계획 열기에서 다시 시도해도 된다.</summary>
public sealed class PlanRerunNotStartedException(string message) : InvalidOperationException(message);

/// <summary>다시 실행한 결과: 쓴 세션, 새 커서, 가져온 행 수.</summary>
public sealed record PlanRerunOutcome(ISession Session, CursorRef Cursor, long Rows);

/// <summary>
/// 「실제 행 수 받으며 다시 실행」의 실행 쪽(TSK-06-04). 질의(SELECT)만 다시 실행한다 — DML 사본은 데이터를 또 바꾸므로 하지 않는다.
/// 실행 엔진(정책 관문)으로 보내고 모든 행을 끝까지 가져와(A-Rows 는 커서를 다 읽어야 채워진다) 닫은 뒤,
/// 다른 문장을 보내기 전에 <see cref="IExecutionPlanService.GetLastCursorAsync"/> 로 그 커서를 읽는다.
/// 바인드는 「바인드 변수」 목록의 지금 값을 쓴다(<see cref="GridRowCounter"/> 와 같은 방식). 세션을 「실행 중」으로 두어 다른 실행과 겹치지 않게 한다.
/// </summary>
public sealed partial class PlanStatsRerunner(
    ExecutionState state, Func<BindVariableStore> binds, Func<ISession?> session, Func<IExecutionService> execution, Func<IExecutionPlanService> plans,
    Func<object?>? owner = null)
{
    /// <summary>ALTER SESSION 문(운영에서도 묻지 않는 ALTER, <see cref="ExecutionPolicy"/>). 이 세션에만 걸리고 DB 에 쓰지 않는다.</summary>
    public const string StatisticsLevelSql = "ALTER SESSION SET STATISTICS_LEVEL = ALL";

    /// <summary>실행을 시작한 작업 공간을 알려 주는 방법(연결 확인용).</summary>
    internal Func<object?>? OwnerSource => owner;

    /// <summary>앱이 대신 보내는 문장이라 이력에 남기지 않는다(정책 관문은 그대로 거친다).</summary>
    private static readonly ExecutionOptions Options = new() { Source = HistorySource.Editor, SkipHistory = true };

    /// <summary>
    /// 묻지 않고 자동으로 다시 실행해도 되는 문장인가: 분류가 분명한 질의(SELECT·WITH … SELECT)이고 <c>FOR UPDATE</c> 가 없다.
    /// DML·DDL·PL/SQL 블록·분류가 불확실한 문장은 데이터를 바꾸거나 행을 잠글 수 있으므로 자동으로 하지 않는다(띠의 단추로 직접 누를 때만).
    /// </summary>
    public static bool CanAutoRerun(SqlStatement? stmt) =>
        stmt is { Kind: StatementKind.Query, IsClassificationUncertain: false } && !HasLockingClause(stmt.Text) && !IsWithChangingData(stmt.Text);

    /// <summary>
    /// 첫 낱말이 WITH 인 문장이 SELECT 가 아닌 본문(INSERT·UPDATE·DELETE·MERGE)을 가졌는가. 분류기는 <c>WITH a AS (…) DELETE FROM t</c> 도 질의로 보므로,
    /// 그런 문장의 힌트 사본이 데이터를 바꾸지 않게 거른다. 렉서 토큰으로 본다: 주석·문자열·바인드(:update)·큰따옴표 이름은 낱말이 아니고,
    /// 괄호 안(CTE 안쪽)·점 뒤(pkg.delete)의 낱말은 본문 동사가 아니다. 본문 동사는 괄호 밖의 첫 SELECT/INSERT/UPDATE/DELETE/MERGE 다.
    /// WITH 로 시작하지 않는 문장(SELECT merge FROM t 등)은 이 검사의 대상이 아니다.
    /// </summary>
    internal static bool IsWithChangingData(string sql)
    {
        var first = true;
        var depth = 0;
        var afterDot = false;
        var cteName = true;
        foreach (var t in SqlLexer.Tokenize(sql))
        {
            if (t.Kind is TokenKind.Comment or TokenKind.Hint)
            {
                continue;
            }

            var text = sql.Substring(t.Start, t.Length);
            var word = t.Kind is TokenKind.Keyword or TokenKind.Identifier;
            if (first)
            {
                first = false;
                if (!word || !text.Equals("WITH", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            else if (t.Kind == TokenKind.Punct && text == "(")
            {
                depth++;
            }
            else if (t.Kind == TokenKind.Punct && text == ")")
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (depth == 0 && t.Kind == TokenKind.Punct && text == ",")
            {
                cteName = true; // 다음 낱말은 CTE 이름
            }
            else if (depth == 0 && word && !afterDot && cteName)
            {
                cteName = false; // WITH merge AS (…) 처럼 이름이 동사와 같아도 본문 동사가 아니다
            }
            else if (depth == 0 && word && !afterDot)
            {
                if (text.Equals("SELECT", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (text.Equals("INSERT", StringComparison.OrdinalIgnoreCase) || text.Equals("UPDATE", StringComparison.OrdinalIgnoreCase)
                    || text.Equals("DELETE", StringComparison.OrdinalIgnoreCase) || text.Equals("MERGE", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            afterDot = text == ".";
        }

        return false;
    }

    /// <summary>FOR UPDATE 류 잠금 절이 있는가. 글자 정규식이 아니라 토큰으로 판정해, 주석·줄바꿈으로 갈라 쓴 것은 잡고 문자열·주석 안의 글은 잡지 않는다.</summary>
    private static bool HasLockingClause(string sql) => StatementReferenceExtractor.Extract(sql).HasLockingClause;

    /// <summary>다시 실행할 사본(<paramref name="mode"/> 가 힌트면 힌트를 넣은 글, 아니면 원문). 질의가 아니면 이유를 담은 예외.</summary>
    public static SqlStatement BuildCopy(SqlStatement stmt, PlanRerunMode mode)
    {
        ArgumentNullException.ThrowIfNull(stmt);
        if (stmt.Kind != StatementKind.Query || stmt.IsClassificationUncertain || IsWithChangingData(stmt.Text))
        {
            throw new InvalidOperationException("질의(SELECT)만 다시 실행합니다. DML 은 사본도 데이터를 바꾸므로 직접 실행한 뒤 실제 계획을 보세요.");
        }

        if (mode == PlanRerunMode.StatisticsLevel)
        {
            return stmt with { Range = default };
        }

        var text = PlanStatisticsHint.AddTo(stmt.Text) ?? throw new InvalidOperationException("힌트를 넣을 SELECT 를 찾지 못했습니다.");
        return stmt with { Text = text, Range = default };
    }

    /// <summary>ALTER SESSION 문장 하나.</summary>
    public static SqlStatement StatisticsLevelStatement() =>
        new(StatisticsLevelSql, default, StatementKind.Ddl) { Verb = "ALTER SESSION" };

    public async Task<PlanRerunOutcome> RunAsync(SqlStatement stmt, PlanRerunMode mode, CancellationToken ct)
    {
        var copy = BuildCopy(stmt, mode);
        if (session() is not { } s)
        {
            throw new PlanRerunNotStartedException("접속이 없습니다. 접속한 뒤 다시 실행하세요.");
        }

        if (state.IsBroken(s.Id))
        {
            throw new PlanRerunNotStartedException("세션이 끊겼습니다. 다시 접속한 뒤 실행하세요.");
        }

        if (state.IsBusy(s.Id))
        {
            throw new PlanRerunNotStartedException("다른 문장을 실행하는 중입니다. 끝난 뒤 다시 실행하세요.");
        }

        var values = GridRowCounter.BuildBinds(binds(), copy.BindNames, null);
        state.BeginRun(s.Id, owner?.Invoke()); // 시작한 작업 공간을 알려 그 화면의 바쁨 표시·「새 세션에서 실행」 안내가 맞게 한다(일반 실행과 같다)
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var cancelHandle = state.RegisterCancel(s.Id, linked.Cancel); // 실행 취소(Ctrl+Break)도 이 실행을 끊는다
        ct = linked.Token;
        try
        {
            var exec = execution();
            if (mode == PlanRerunMode.StatisticsLevel)
            {
                var alter = await Task.Run(() => exec.ExecuteAsync(s, StatisticsLevelStatement(), new Dictionary<string, BindValue>(), Options, ct), CancellationToken.None).ConfigureAwait(true);
                EnsureSucceeded(alter, ct);
            }

            var result = await Task.Run(() => exec.ExecuteAsync(s, copy, values, Options, ct), CancellationToken.None).ConfigureAwait(true);
            EnsureSucceeded(result, ct);
            var rows = await DrainAsync(result, ct).ConfigureAwait(true);
            var cursor = await Task.Run(() => plans().GetLastCursorAsync(s, ct), CancellationToken.None).ConfigureAwait(true)
                ?? throw new InvalidOperationException("다시 실행한 문장의 sql_id 를 읽지 못했습니다(DISPLAY_CURSOR 권한을 확인하세요).");

            // 직전 문장(DISPLAY_CURSOR(NULL))은 행을 읽는 사이 같은 연결에서 돈 다른 조회(자동 완성·메타데이터)일 수 있다: 받은 계획의 SQL 본문이 보낸 사본과 같을 때만 연다.
            var plan = await Task.Run(() => plans().GetCursorPlanAsync(s, cursor, "TYPICAL", ct), CancellationToken.None).ConfigureAwait(true);
            if (!PlanSqlMatch.Matches(plan.Text, result.Statement.Text))
            {
                throw new InvalidOperationException("읽은 계획이 실행한 문장의 것이 아닙니다(행을 읽는 사이 다른 조회가 끼어들었을 수 있습니다). 계획을 열지 않았습니다. 다시 실행하세요.");
            }

            return new PlanRerunOutcome(s, cursor, rows);
        }
        finally
        {
            state.EndRun(s.Id);
        }
    }

    private static void EnsureSucceeded(ExecutionResult result, CancellationToken ct)
    {
        switch (result.Status)
        {
            case ExecutionStatus.Cancelled:
                throw new OperationCanceledException(ct);
            case ExecutionStatus.Denied:
                throw new InvalidOperationException(result.Policy?.Message ?? "정책 관문이 막았습니다.");
            case ExecutionStatus.Failed:
                throw new InvalidOperationException(DbErrorText.Format(result.Error, "실행하지 못했습니다."));
        }
    }

    /// <summary>결과를 끝까지 읽고 닫는다(행은 버린다). 가져온 행 수.</summary>
    private static async Task<long> DrainAsync(ExecutionResult result, CancellationToken ct)
    {
        if (result.ResultSet is not { } rs)
        {
            return 0;
        }

        try
        {
            long rows = 0;
            for (var page = 0; ; page++)
            {
                var p = await Task.Run(() => rs.GetPageAsync(page, ct), CancellationToken.None).ConfigureAwait(true);
                rows += p.RowCount;
                if (p.IsLast || p.RowCount == 0)
                {
                    return rows;
                }
            }
        }
        finally
        {
            await rs.DisposeAsync().ConfigureAwait(true);
        }
    }
}
