using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Features.Execution;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Sessions;
using Oranda.Core.Tuning;

namespace Oranda.App.Features.Tuning.Trace;

/// <summary>10053 실행 결과: 쓴 세션, 트레이스 파일, 사본에 붙인 표시.</summary>
public sealed record OptimizerTraceOutcome(ISession Session, TraceFileInfo File, string Marker);

/// <summary>
/// 「옵티마이저 트레이스(10053)」(TSK-06-04, TN-15). 순서: 이벤트 켜기 → 표시 주석을 붙인 사본을 <b>파싱만</b> → 이벤트 끄기(실패해도 끈다) → 이 세션의 트레이스 파일.
/// <para>
/// 결정 — 사본은 실행하지 않고 <c>DBMS_SQL.PARSE</c> 로 하드 파스만 시킨다. EXPLAIN PLAN 도 하드 파스를 하지만 PLAN_TABLE 에 행을 쓰고
/// (DB 접근 통제 도구가 쓰기로 본다), 사본을 실행하면 DML 이 데이터를 바꾼다. DBMS_SQL.PARSE 는 질의·DML 을 실행하지 않고 옵티마이저까지 돌려
/// 10053 을 남긴다(DDL 은 파싱만으로 실행되므로 질의·DML 만 받는다). 표시 주석이 글을 바꾸므로 공유 풀의 커서와 겹치지 않아 늘 하드 파스다.
/// </para>
/// <para>세 문장 모두 실행 엔진(정책 관문)을 거친다. ALTER SESSION 은 운영에서도 묻지 않지만, PL/SQL 블록은 운영 접속에서 확인을 묻는다.</para>
/// </summary>
public sealed class OptimizerTraceRunner(ExecutionState state, Func<ISession?> session, Func<IExecutionService> execution, Func<ITuningService> tuning)
{
    public const string EventOnSql = "ALTER SESSION SET EVENTS '10053 trace name context forever, level 1'";
    public const string EventOffSql = "ALTER SESSION SET EVENTS '10053 trace name context off'";
    public const string SqlBindName = "oranda_sql";

    /// <summary>사본을 파싱만 하는 블록. 사본 글은 바인드로 넘긴다(따옴표 이스케이프 없음).</summary>
    public const string ParseBlock =
        "DECLARE\n  c INTEGER := DBMS_SQL.OPEN_CURSOR;\nBEGIN\n  DBMS_SQL.PARSE(c, :oranda_sql, DBMS_SQL.NATIVE);\n  DBMS_SQL.CLOSE_CURSOR(c);\n"
        + "EXCEPTION\n  WHEN OTHERS THEN\n    IF DBMS_SQL.IS_OPEN(c) THEN\n      DBMS_SQL.CLOSE_CURSOR(c);\n    END IF;\n    RAISE;\nEND;";

    /// <summary>앱이 대신 보내는 문장이라 이력에 남기지 않는다(정책 관문은 그대로).</summary>
    private static readonly ExecutionOptions Options = new() { Source = HistorySource.Editor, SkipHistory = true };

    /// <summary>사본에 붙이는 표시(본문에서 찾는다).</summary>
    public static string NewMarker(DateTime now) => "ORANDA_10053_" + now.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);

    /// <summary>표시 주석을 앞에 붙인 사본 글. 질의·DML 이 아니면 이유를 담은 예외.</summary>
    public static string BuildCopy(SqlStatement stmt, string marker)
    {
        ArgumentNullException.ThrowIfNull(stmt);
        if (stmt.Kind is not (StatementKind.Query or StatementKind.Dml))
        {
            throw new InvalidOperationException("10053 은 질의(SELECT)·DML 만 됩니다. DDL·PL/SQL 은 파싱만으로도 실행되거나 옵티마이저를 거치지 않습니다.");
        }

        if (stmt.SubstitutionNames.Count > 0)
        {
            throw new InvalidOperationException("치환 변수(&)가 있는 문장은 10053 사본을 만들 수 없습니다. 값을 넣은 문장으로 다시 하세요.");
        }

        return $"/* {marker} */ {stmt.Text}";
    }

    public static SqlStatement Alter(string sql) => new(sql, default, StatementKind.Ddl) { Verb = "ALTER SESSION" };

    public static SqlStatement ParseStatement() => new(ParseBlock, default, StatementKind.PlSql) { Verb = "DECLARE", BindNames = [SqlBindName] };

    public async Task<OptimizerTraceOutcome> RunAsync(SqlStatement stmt, CancellationToken ct)
    {
        var marker = NewMarker(DateTime.Now);
        var copy = BuildCopy(stmt, marker);
        if (session() is not { } s)
        {
            throw new InvalidOperationException("접속이 없습니다. 접속한 뒤 다시 하세요.");
        }

        if (state.IsBroken(s.Id))
        {
            throw new InvalidOperationException("세션이 끊겼습니다. 다시 접속한 뒤 하세요.");
        }

        if (state.IsBusy(s.Id))
        {
            throw new InvalidOperationException("다른 문장을 실행하는 중입니다. 끝난 뒤 다시 하세요.");
        }

        state.BeginRun(s.Id);
        try
        {
            var exec = execution();
            var none = new Dictionary<string, BindValue>();
            Ensure(await Task.Run(() => exec.ExecuteAsync(s, Alter(EventOnSql), none, Options, ct), CancellationToken.None).ConfigureAwait(true), ct);
            try
            {
                var binds = new Dictionary<string, BindValue>(StringComparer.OrdinalIgnoreCase)
                {
                    [SqlBindName] = new(copy.Length > 32000 ? BindDataType.Clob : BindDataType.Varchar2, copy),
                };
                Ensure(await Task.Run(() => exec.ExecuteAsync(s, ParseStatement(), binds, Options, ct), CancellationToken.None).ConfigureAwait(true), ct);
            }
            finally
            {
                // 켠 이벤트는 실패·취소에도 끈다(이 세션의 다음 파스가 모두 10053 을 남기지 않게).
                await Task.Run(() => exec.ExecuteAsync(s, Alter(EventOffSql), none, Options, CancellationToken.None), CancellationToken.None).ConfigureAwait(true);
            }

            var file = await Task.Run(() => tuning().GetTraceFileAsync(s, default, ct), CancellationToken.None).ConfigureAwait(true);
            return new OptimizerTraceOutcome(s, file, marker);
        }
        finally
        {
            state.EndRun(s.Id);
        }
    }

    private static void Ensure(ExecutionResult result, CancellationToken ct)
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
}
