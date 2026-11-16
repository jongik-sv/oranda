using Oranda.Core.Events;
using Oranda.Core.Sessions;

namespace Oranda.Core.Execution.Providers;

/// <summary>
/// 대량 실행 경로(<see cref="IBulkExecutionService"/>)의 DBMS 공통 규칙. Oracle·Tibero 구현이 같은 규칙을 쓴다(TB1 설계 3.4).
/// </summary>
public static class BulkStatements
{
    /// <summary>호출 한 번에 <see cref="StatementExecuted"/> 하나. 이력 저장 여부는 <see cref="ExecutionOptions.SkipHistory"/> 를 따른다.</summary>
    public static BulkResult Publish(IEventBus events, ISession s, SqlStatement stmt, ExecutionOptions options, BulkResult result, TimeSpan elapsed, bool pending)
    {
        var executed = new ExecutionResult(stmt, result.Status)
        {
            Elapsed = elapsed,
            RowsAffected = result.Status == ExecutionStatus.Succeeded ? result.RowsAffected : null,
            Error = result.Error,
            Policy = result.Policy,
            TransactionPending = pending,
        };
        events.Publish(new StatementExecuted(s.Id, stmt, executed, options));
        return result;
    }

    /// <summary>DDL·SQL*Plus 문장, 열 없는 여러 행, 값 수가 열 수와 다른 행을 <see cref="ArgumentException"/> 으로 막는다.</summary>
    public static void Validate(SqlStatement stmt, IReadOnlyList<BulkColumn> columns, IReadOnlyList<object?[]> rows)
    {
        // 분류가 불확실하면 EffectiveKind 는 DML 이 되므로, 붙은 분류와 실효 분류를 모두 본다.
        var rejected = stmt.Kind is StatementKind.Ddl or StatementKind.SqlPlus ? stmt.Kind : ExecutionPolicy.EffectiveKind(stmt);
        if (rejected is StatementKind.Ddl or StatementKind.SqlPlus)
        {
            throw new ArgumentException($"대량 실행 경로는 {rejected} 문장을 받지 않습니다. IExecutionService 로 실행하세요.", nameof(stmt));
        }

        if (columns.Count == 0 && rows.Count > 1)
        {
            throw new ArgumentException("바인드 열이 없는 문장은 한 번만 실행합니다.", nameof(rows));
        }

        for (var r = 0; r < rows.Count; r++)
        {
            if (rows[r] is null || rows[r].Length != columns.Count)
            {
                throw new ArgumentException($"{r}번째 행의 값 수가 열 수({columns.Count})와 다릅니다.", nameof(rows));
            }
        }
    }

    /// <summary>읽기 전용 프로필에서 지나는 글: SET TRANSACTION, COMMIT, ROLLBACK(ROLLBACK TO SAVEPOINT 포함).</summary>
    public static bool IsTransactionControlText(string text) => LeadingWords(text) switch
    {
        ("SET", "TRANSACTION") => true,
        ("COMMIT" or "ROLLBACK", _) => true,
        _ => false,
    };

    /// <summary>
    /// 글의 앞 두 낱말(대문자). 앞의 공백·주석(<c>--</c>, <c>/* */</c>)·여는 괄호를 건너뛴다. 낱말이 없으면 null.
    /// 분류(<see cref="SqlStatement.Kind"/>)를 한 번 더 확인하는 데만 쓴다.
    /// </summary>
    public static (string? First, string? Second) LeadingWords(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var i = 0;
        var first = NextWord(text, ref i);
        var second = first is null ? null : NextWord(text, ref i);
        return (first, second);
    }

    private static string? NextWord(string text, ref int i)
    {
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c) || c == '(')
            {
                i++;
            }
            else if (c == '-' && i + 1 < text.Length && text[i + 1] == '-')
            {
                var nl = text.IndexOf('\n', i);
                i = nl < 0 ? text.Length : nl + 1;
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? text.Length : close + 2;
            }
            else
            {
                break;
            }
        }

        var start = i;
        while (i < text.Length && (char.IsLetter(text[i]) || text[i] == '_'))
        {
            i++;
        }

        return i > start ? text[start..i].ToUpperInvariant() : null;
    }
}
