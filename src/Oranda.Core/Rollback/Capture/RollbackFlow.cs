using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Sessions;

namespace Oranda.Core.Rollback.Capture;

/// <summary>트랜잭션 제어 문장의 종류(<see cref="RollbackFlow.ParseTransactionControl"/>).</summary>
public enum TransactionControlKind
{
    Commit,
    Rollback,
    RollbackToSavepoint,
    Savepoint,
}

/// <summary>트랜잭션 제어 문장 하나. <paramref name="Savepoint"/> 는 저장점 이름(따옴표 없는 이름은 대문자, 따옴표 이름은 그대로).</summary>
public sealed record TransactionControl(TransactionControlKind Kind, string? Savepoint = null);

/// <summary>
/// 공급자(Oracle·Tibero)가 함께 쓰는 판단(되돌리기 지점 설계 7.3 끝·13.5, RB-02). DBMS 와 상관없는 규칙만 둔다:
/// 트랜잭션 제어 문장 해석, 「이 문장이 트랜잭션을 끝낼 수 있는가」, 「끝 판정이 필요한가」, 문장의 기록 방식.
/// 공급자는 이 판단을 따로 구현하지 않는다.
/// </summary>
public static class RollbackFlow
{
    /// <summary>
    /// <c>COMMIT [WORK] [COMMENT …] [WRITE …]</c>, <c>ROLLBACK [WORK]</c>, <c>ROLLBACK [WORK] TO [SAVEPOINT] 이름</c>, <c>SAVEPOINT 이름</c> 을 알아본다.
    /// 그 밖(분산 트랜잭션 <c>COMMIT FORCE</c>·<c>ROLLBACK FORCE</c>, 알 수 없는 모양)은 null.
    /// </summary>
    public static TransactionControl? ParseTransactionControl(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var words = Words(text);
        if (words.Count == 0)
        {
            return null;
        }

        var i = 1;
        switch (words[0].ToUpperInvariant())
        {
            case "COMMIT":
                if (Is(words, 1, "WORK"))
                {
                    i++;
                }

                return Is(words, i, "FORCE") ? null : new TransactionControl(TransactionControlKind.Commit);
            case "ROLLBACK":
                if (Is(words, i, "WORK"))
                {
                    i++;
                }

                if (i >= words.Count)
                {
                    return new TransactionControl(TransactionControlKind.Rollback);
                }

                if (!Is(words, i, "TO"))
                {
                    return null; // ROLLBACK FORCE '…' 등
                }

                i++;
                if (Is(words, i, "SAVEPOINT"))
                {
                    i++;
                }

                return i < words.Count && i == words.Count - 1
                    ? new TransactionControl(TransactionControlKind.RollbackToSavepoint, Name(words[i]))
                    : null;
            case "SAVEPOINT":
                return words.Count == 2 ? new TransactionControl(TransactionControlKind.Savepoint, Name(words[1])) : null;
            default:
                return null;
        }
    }

    /// <summary>
    /// 원장에 기록하는 쓰기 문장인가(설계 7.1): DML, PL/SQL, 분류가 불확실한 문장. 질의·DDL·트랜잭션 제어·SQL*Plus 는 아니다.
    /// </summary>
    public static bool IsRecordedWrite(SqlStatement stmt)
    {
        ArgumentNullException.ThrowIfNull(stmt);
        return stmt.Kind is StatementKind.Dml or StatementKind.PlSql
               || (stmt.IsClassificationUncertain && stmt.Kind is not StatementKind.Query and not StatementKind.TransactionControl);
    }

    /// <summary>
    /// 보내기 전에 <see cref="IRollbackLedger.BeforeEndAsync"/> 를 불러야 하는 문장인가(설계 7.4, RD-7): 트랜잭션을 커밋으로 끝낼 수 있는 문장
    /// (COMMIT, DDL, PL/SQL, 분류 불확실). 롤백 문장(<c>ROLLBACK</c>·<c>ROLLBACK TO</c>) 앞에서는 저장하지 않는다.
    /// </summary>
    public static bool CanEndTransaction(SqlStatement stmt)
    {
        ArgumentNullException.ThrowIfNull(stmt);
        if (stmt.Kind == StatementKind.TransactionControl)
        {
            return ParseTransactionControl(stmt.Text) is { Kind: TransactionControlKind.Commit };
        }

        return stmt.Kind is StatementKind.Ddl or StatementKind.PlSql || stmt.IsClassificationUncertain;
    }

    /// <summary>
    /// 문장을 보낸 뒤 알게 된 트랜잭션 끝(설계 7.3 표). 성공한 트랜잭션 제어 문장은 그 종류대로 낸다.
    /// <para>
    /// DDL·PL/SQL·분류 불확실 문장은 (보내기 전 미커밋이 참 <b>또는</b> 원장에 열린 묶음이 있음) 이고 보낸 뒤 미커밋이 거짓이면 「모름」
    /// (<see cref="TransactionEndSource.Ddl"/>·<see cref="TransactionEndSource.PlSql"/>)이고, 공급자가 끝 판정을 한다.
    /// 둘 다 거짓이면 끝이 아니다(되돌리기를 끈 세션에서 DDL 마다 끝을 내지 않는다). 열린 묶음을 보는 까닭: 미커밋이 없던 세션에서
    /// <c>BEGIN INSERT …; COMMIT; END;</c> 는 그 블록 기록이 열린 묶음에 남는데, 보내기 전 미커밋만 보면 끝을 놓쳐 뒤의 ROLLBACK 이 커밋된 블록을 「롤백됨」으로 덮는다.
    /// 커밋하지 않는 DDL 분류 문장(<c>ALTER SESSION</c>, 구문 단계에서 실패한 DDL)은 미커밋이 남으므로 끝이 아니다.
    /// </para>
    /// <para>
    /// 그 밖의 문장(실패한 COMMIT 의 ORA-02091, 트랜잭션을 되돌리는 오류)은 보내기 전 미커밋이 참 → 보낸 뒤 거짓이면 「모름」(<see cref="TransactionEndSource.Statement"/>).
    /// 끝이 아니면 null. 저장점 표시(<c>SAVEPOINT</c>)도 끝이 아니므로 null 이다(<see cref="SavepointMarked"/>).
    /// </para>
    /// </summary>
    /// <param name="sessionId">세션</param>
    /// <param name="stmt">보낸 문장</param>
    /// <param name="succeeded">문장이 성공했는가</param>
    /// <param name="pendingBefore">보내기 전 미커밋 트랜잭션 여부</param>
    /// <param name="pendingAfter">보낸 뒤 미커밋 트랜잭션 여부</param>
    /// <param name="hasOpenGroup">원장에 이 세션의 열린 묶음이 있는가(<c>ledger.Open(sessionId) is not null</c>, 이 문장의 기록 뒤에 읽는다)</param>
    public static TransactionEnded? EndAfter(Guid sessionId, SqlStatement stmt, bool succeeded, bool pendingBefore, bool pendingAfter, bool hasOpenGroup)
    {
        ArgumentNullException.ThrowIfNull(stmt);
        var lost = pendingBefore && !pendingAfter;
        if (stmt.Kind == StatementKind.TransactionControl && succeeded && ParseTransactionControl(stmt.Text) is { } control)
        {
            return control.Kind switch
            {
                TransactionControlKind.Commit => new TransactionEnded(sessionId, TransactionEndKind.Commit, TransactionEndSource.Statement),
                TransactionControlKind.Rollback => new TransactionEnded(sessionId, TransactionEndKind.Rollback, TransactionEndSource.Statement),
                TransactionControlKind.RollbackToSavepoint => new TransactionEnded(sessionId, TransactionEndKind.RollbackToSavepoint, TransactionEndSource.Statement)
                {
                    Savepoint = control.Savepoint,
                },
                _ => lost ? new TransactionEnded(sessionId, TransactionEndKind.Unknown, TransactionEndSource.Statement) : null,
            };
        }

        var mayEnd = (pendingBefore || hasOpenGroup) && !pendingAfter;
        if (stmt.Kind == StatementKind.Ddl)
        {
            return mayEnd ? new TransactionEnded(sessionId, TransactionEndKind.Unknown, TransactionEndSource.Ddl) : null;
        }

        if (stmt.Kind == StatementKind.PlSql || stmt.IsClassificationUncertain)
        {
            return mayEnd ? new TransactionEnded(sessionId, TransactionEndKind.Unknown, TransactionEndSource.PlSql) : null;
        }

        return lost ? new TransactionEnded(sessionId, TransactionEndKind.Unknown, TransactionEndSource.Statement) : null;
    }

    /// <summary>
    /// 열린 묶음을 모르는 옛 판(옛 규칙 그대로): DDL 은 보낸 뒤 미커밋이 거짓이면 「모름」, PL/SQL·불확실 문장은 미커밋 참 → 거짓일 때만 「모름」.
    /// 미커밋이 없던 세션의 PL/SQL 안 COMMIT 을 놓치므로 공급자는 6인자 판
    /// <see cref="EndAfter(Guid, SqlStatement, bool, bool, bool, bool)"/> 로 옮긴다(공급자 레인이 옮긴 뒤 지운다).
    /// </summary>
    public static TransactionEnded? EndAfter(Guid sessionId, SqlStatement stmt, bool succeeded, bool pendingBefore, bool pendingAfter)
    {
        ArgumentNullException.ThrowIfNull(stmt);
        if (stmt.Kind == StatementKind.Ddl)
        {
            return pendingAfter ? null : new TransactionEnded(sessionId, TransactionEndKind.Unknown, TransactionEndSource.Ddl);
        }

        return EndAfter(sessionId, stmt, succeeded, pendingBefore, pendingAfter, hasOpenGroup: false);
    }

    /// <summary>성공한 <c>SAVEPOINT 이름</c> 이면 그 이름(원장 <see cref="IRollbackLedger.MarkSavepoint"/>). 내부 저장점 이름은 null.</summary>
    public static string? SavepointMarked(SqlStatement stmt, bool succeeded, string internalSavepoint = "ORANDA$RB")
    {
        ArgumentNullException.ThrowIfNull(stmt);
        return succeeded && stmt.Kind == StatementKind.TransactionControl
               && ParseTransactionControl(stmt.Text) is { Kind: TransactionControlKind.Savepoint, Savepoint: { } name }
               && !string.Equals(name, internalSavepoint, StringComparison.OrdinalIgnoreCase)
            ? name
            : null;
    }

    /// <summary>끝 판정 질의(<see cref="IRollbackCapture.ProbeEndAsync"/>)가 필요한 끝인가: 종류가 「모름」이고 세션에 열린 묶음이 있다.</summary>
    public static bool NeedsProbe(TransactionEnded ended, RollbackOpenState? open)
    {
        ArgumentNullException.ThrowIfNull(ended);
        return ended.Kind == TransactionEndKind.Unknown && open is not null;
    }

    /// <summary>끝난 직후 시점(커밋 시점)을 읽을 끝인가: 커밋이거나 모름.</summary>
    public static bool ReadsCommitPoint(TransactionEndKind kind) => kind is TransactionEndKind.Commit or TransactionEndKind.Unknown;

    /// <summary>
    /// 이 실행의 기록 방식(설계 7.1·7.5): 옵션의 <see cref="ExecutionOptions.Recording"/> 을 따르되 읽기 전용 프로필·에이전트·데이터 복사는 기록하지 않는다.
    /// 설정의 원복 스크립트·시점 기록 끔은 엔진이 따로 본다.
    /// </summary>
    public static ChangeRecording RecordingFor(ISession session, ExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        if (session.Profile.ReadOnly || options.Source is HistorySource.Agent or HistorySource.DataCopy)
        {
            return ChangeRecording.None;
        }

        return options.Recording;
    }

    /// <summary>
    /// 글에서 바인드 이름(<c>:name</c>, <c>:</c> 제외, 대문자 비교 없이 원래 모양)을 등장 순서대로 중복 없이 찾는다.
    /// 문자열·따옴표 이름·주석 안은 건너뛴다. <c>:=</c>(PL/SQL 대입)와 <c>::</c> 는 바인드가 아니다.
    /// </summary>
    public static IReadOnlyList<string> BindNamesIn(string? text)
    {
        var names = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            return names;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch is 'q' or 'Q' or 'n' or 'N' && QQuoteEnd(text, i) is { } qEnd)
            {
                i = qEnd;
            }
            else if (ch == '\'')
            {
                i = SkipQuoted(text, i, '\'');
            }
            else if (ch == '"')
            {
                i = SkipQuoted(text, i, '"');
            }
            else if (ch == '-' && i + 1 < text.Length && text[i + 1] == '-')
            {
                var end = text.IndexOf('\n', i);
                i = end < 0 ? text.Length : end;
            }
            else if (ch == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 1;
            }
            else if (ch == ':' && i + 1 < text.Length && (char.IsLetterOrDigit(text[i + 1]) || text[i + 1] == '_') && (i == 0 || text[i - 1] != ':'))
            {
                var start = i + 1;
                var end = start;
                while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] is '_' or '$' or '#'))
                {
                    end++;
                }

                var name = text[start..end];
                if (seen.Add(name))
                {
                    names.Add(name);
                }

                i = end - 1;
            }
        }

        return names;
    }

    /// <summary><paramref name="binds"/> 가운데 <paramref name="text"/> 에 나오는 이름만(대소문자 무시) 고른다.</summary>
    public static IReadOnlyDictionary<string, BindValue> BindsFor(string? text, IReadOnlyDictionary<string, BindValue> binds)
    {
        ArgumentNullException.ThrowIfNull(binds);
        var result = new Dictionary<string, BindValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in BindNamesIn(text))
        {
            foreach (var (key, value) in binds)
            {
                if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                {
                    result[name] = value;
                    break;
                }
            }
        }

        return result;
    }

    /// <summary><c>q'[…]'</c>·<c>nq'…'</c> 대체 따옴표 글이면 닫는 작은따옴표의 위치. 아니면 null.</summary>
    private static int? QQuoteEnd(string text, int i)
    {
        if (i > 0 && (char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '_'))
        {
            return null;
        }

        var q = text[i] is 'n' or 'N' ? i + 1 : i;
        if (q + 2 >= text.Length || text[q] is not ('q' or 'Q') || text[q + 1] != '\'')
        {
            return null;
        }

        var open = text[q + 2];
        var close = open switch { '[' => ']', '{' => '}', '(' => ')', '<' => '>', _ => open };
        var end = text.IndexOf(close + "'", q + 3, StringComparison.Ordinal);
        return end < 0 ? text.Length : end + 1;
    }

    private static int SkipQuoted(string text, int start, char quote)
    {
        for (var i = start + 1; i < text.Length; i++)
        {
            if (text[i] == quote)
            {
                if (i + 1 < text.Length && text[i + 1] == quote)
                {
                    i++;
                    continue;
                }

                return i;
            }
        }

        return text.Length;
    }

    /// <summary>주석을 걷고 공백으로 나눈 낱말(따옴표 이름은 한 낱말). 끝의 <c>;</c> 는 뺀다.</summary>
    private static List<string> Words(string text)
    {
        var words = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (char.IsWhiteSpace(ch) || ch == ';')
            {
                i++;
            }
            else if (ch == '-' && i + 1 < text.Length && text[i + 1] == '-')
            {
                var end = text.IndexOf('\n', i);
                i = end < 0 ? text.Length : end;
            }
            else if (ch == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 2;
            }
            else if (ch is '"' or '\'')
            {
                var end = SkipQuoted(text, i, ch);
                words.Add(text[i..Math.Min(end + 1, text.Length)]);
                i = end + 1;
            }
            else
            {
                var start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ';' and not '"' and not '\''
                       && !(i + 1 < text.Length && ((text[i] == '-' && text[i + 1] == '-') || (text[i] == '/' && text[i + 1] == '*'))))
                {
                    i++;
                }

                words.Add(text[start..i]);
            }
        }

        return words;
    }

    private static bool Is(List<string> words, int index, string word) =>
        index < words.Count && string.Equals(words[index], word, StringComparison.OrdinalIgnoreCase);

    private static string Name(string word) =>
        word.Length >= 2 && word[0] == '"' && word[^1] == '"' ? word[1..^1] : word.ToUpperInvariant();
}
