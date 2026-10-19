using Antlr4.Runtime;
using Antlr4.Runtime.Atn;
using Antlr4.Runtime.Misc;
using Antlr4.Runtime.Tree;
using Oranda.Core.Execution;
using Oranda.Core.Language;
using Oranda.Core.Metadata;
using Oranda.Core.Text;
using Oranda.Language.Lexing;
using Oranda.Language.Parsing.Grammar;

namespace Oranda.Language.Parsing;

/// <summary>문장 1개의 L2 분석 결과.</summary>
internal enum AnalysisOutcome
{
    /// <summary>문법 오류 없음.</summary>
    Ok,

    /// <summary>문법 오류가 있다(<see cref="Diagnostic"/> Error).</summary>
    Errors,

    /// <summary>문법이 모르는 구문(23ai/26ai 신규 구문 등) — 오류로 단정하지 않는다(Info 1건).</summary>
    Unsupported,

    /// <summary>분석 대상이 아니다(SQL*Plus 명령 등).</summary>
    Skipped,
}

/// <summary>문장 1개 분석 결과. <see cref="Diagnostics"/> 의 범위는 문장 시작 기준 오프셋.</summary>
internal sealed record StatementAnalysis(AnalysisOutcome Outcome, IReadOnlyList<Diagnostic> Diagnostics, bool UsedLl)
{
    public static StatementAnalysis Skipped { get; } = new(AnalysisOutcome.Skipped, [], false);
}

/// <summary>
/// ANTLR PL/SQL 분석기(TRD 4.5 L2). SLL + 즉시 중단으로 먼저 파싱하고, 실패할 때만 LL + 기본 오류 복구로 다시 파싱해 오류 위치를 얻는다.
/// 첫 오류 1건만 낸다(복구 뒤 이어지는 연쇄 오류는 잡음이다). 문법이 모르는 23ai/26ai 구문·명령은 <see cref="AnalysisOutcome.Unsupported"/>.
/// 스레드 안전(호출마다 렉서·파서를 새로 만든다. DFA 캐시는 ANTLR 가 정적으로 공유). 소유: TSK-04-03.
/// </summary>
internal static class PlSqlAnalyzer
{
    public const string ErrorCode = "L2.Syntax";
    public const string UnsupportedCode = "L2.Unsupported";

    private static int s_warmedUp;

    /// <summary>문법 오류 시작 단어로 알려진 Oracle 명령어. 첫 단어에서 파싱이 막혔을 때 이 목록에 있으면 「분석 불가」, 없으면 오류(오타).</summary>
    private static readonly HashSet<string> s_statementVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "ADMINISTER", "ALTER", "ANALYZE", "ASSOCIATE", "AUDIT", "BEGIN", "CALL", "COMMENT", "COMMIT", "CREATE", "DECLARE",
        "DELETE", "DISASSOCIATE", "DROP", "EXPLAIN", "FLASHBACK", "GRANT", "INSERT", "LOCK", "MERGE", "NOAUDIT", "PURGE",
        "RENAME", "REVOKE", "ROLLBACK", "SAVEPOINT", "SELECT", "SET", "TRUNCATE", "UPDATE", "WITH", "VALUES", "TABLE",
        "EXEC", "EXECUTE", "SHUTDOWN", "STARTUP",
    };

    /// <summary>
    /// 23ai/26ai 에서 생긴 구문의 표지 단어. 파싱이 실패한 문장에 이 단어가 있으면 오류 대신 「분석 불가」로 둔다
    /// (grammars-v4 문법은 아직 이 구문을 모른다 — 문법을 고치는 대신 표지로 거른다).
    /// </summary>
    private static readonly HashSet<string> s_modernMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "VECTOR", "VECTOR_DISTANCE", "VECTOR_EMBEDDING", "DOMAIN", "DOMAIN_DISPLAY", "DOMAIN_ORDER", "DUALITY", "GRAPH_TABLE",
        "GRAPH", "MLE", "ANNOTATIONS", "RESERVABLE", "LOCKFREE", "ASSERTION", "JSON_SCHEMA", "SHARDED",
        "BLOCKCHAIN", "IMMUTABLE", "SQL_MACRO", "PRECHECK",
    };

    /// <summary>SQL 문장에서만 23ai 표지로 보는 단어(PL/SQL 에서는 예전부터 쓰던 말이라 표지가 아니다).</summary>
    private static readonly HashSet<string> s_modernSqlMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "BOOLEAN", "TRUE", "FALSE",
    };

    /// <summary>앱 유휴 시간에 한 번 불러 ATN 역직렬화·JIT·DFA 캐시 비용(측정 약 2 s)을 미리 치른다. 여러 번 불러도 한 번만 일한다.</summary>
    public static bool WarmUp(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref s_warmedUp, 1) == 1)
        {
            return false;
        }

        foreach (var (sql, kind) in WarmUpSamples)
        {
            ct.ThrowIfCancellationRequested();
            Analyze(sql, kind, ct);
        }

        return true;
    }

    /// <summary>워밍업이 끝났는지(시험·진단용).</summary>
    public static bool IsWarm => Volatile.Read(ref s_warmedUp) == 1;

    private static readonly (string Sql, StatementKind Kind)[] WarmUpSamples =
    [
        ("SELECT d.dummy, COUNT(*) FROM dual d WHERE d.dummy = :x GROUP BY d.dummy ORDER BY 1", StatementKind.Query),
        ("UPDATE emp e SET e.sal = e.sal * 1.1 WHERE e.deptno IN (SELECT deptno FROM dept)", StatementKind.Dml),
        ("BEGIN\n  FOR r IN (SELECT 1 AS n FROM dual) LOOP\n    NULL;\n  END LOOP;\nEND;", StatementKind.PlSql),
        ("CREATE TABLE t (id NUMBER PRIMARY KEY, name VARCHAR2(30) NOT NULL)", StatementKind.Ddl),
    ];

    /// <summary>문장 1개를 분석한다. <paramref name="sql"/> 은 문서에서 잘라 낸 문장 원문(끝 표지 <c>;</c> 는 있어도 없어도 된다).</summary>
    public static StatementAnalysis Analyze(string sql, StatementKind kind, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sql);
        if (kind == StatementKind.SqlPlus || string.IsNullOrWhiteSpace(sql))
        {
            return StatementAnalysis.Skipped;
        }

        var l1 = SqlLexer.Tokenize(sql);
        var words = Words(sql, l1);
        var input = MaskSubstitutions(sql, l1);
        bool usedLl = false;
        SyntaxIssue? first = null;
        var masked = new HashSet<int>();
        for (int attempt = 0; attempt <= MaxNameRetries; attempt++)
        {
            first = Parse(input, ref usedLl, ct);

            // Oracle 은 대부분의 키워드를 이름으로 쓸 수 있는데 문법은 일부만 허용한다(예: t.compression).
            // 막힌 단어나 바로 앞 단어가 점으로 이어진 이름의 일부면 평범한 이름으로 가리고 다시 파싱한다(오프셋 유지).
            if (first is null || first.AtEnd || !TryFindKeywordName(sql, masked, words, first.Start, out var name))
            {
                break;
            }

            masked.Add(name.Start);
            var buf = input.ToCharArray();
            Array.Fill(buf, 'Q', name.Start, name.Length);
            input = new string(buf);
        }

        if (first is null)
        {
            return new StatementAnalysis(AnalysisOutcome.Ok, [], usedLl);
        }

        if (words.Count > 0 && !s_statementVerbs.Contains(words[0].Word) && StartsWithWord(sql, words[0].Range))
        {
            return new StatementAnalysis(AnalysisOutcome.Errors,
                [new Diagnostic(words[0].Range, DiagnosticSeverity.Error, $"알 수 없는 명령어: '{Shorten(words[0].Word)}'") { Code = ErrorCode }],
                usedLl);
        }

        if (IsUnsupported(first, words, kind))
        {
            var head = words.Count > 0 ? words[0].Range : new TextRange(0, 0);
            var unsupported = new Diagnostic(head, DiagnosticSeverity.Info,
                "분석 불가: 문법 검사기가 모르는 구문입니다(23ai/26ai 신규 구문 등). 오류로 보지 않습니다.")
            { Code = UnsupportedCode };
            return new StatementAnalysis(AnalysisOutcome.Unsupported, [unsupported], usedLl);
        }

        var range = first.AtEnd ? EndRange(sql) : new TextRange(first.Start, Math.Max(1, first.Length));
        return new StatementAnalysis(AnalysisOutcome.Errors,
            [new Diagnostic(range, DiagnosticSeverity.Error, first.Message) { Code = ErrorCode }], usedLl);
    }

    private const int MaxNameRetries = 8;

    /// <summary>SLL + 즉시 중단 → 실패하면 LL + 기본 복구. 첫 오류(렉서·파서 중 앞선 것)를 돌려준다.</summary>
    private static SyntaxIssue? Parse(string input, ref bool usedLl, CancellationToken ct)
    {
        var lexErrors = new LexerErrors();
        var lexer = new PlSqlLexer(new AntlrInputStream(input));
        lexer.RemoveErrorListeners();
        lexer.AddErrorListener(lexErrors);
        var tokens = new FurthestTokenStream(lexer);
        tokens.Fill();
        ct.ThrowIfCancellationRequested();

        var parser = new PlSqlParser(tokens) { BuildParseTree = false };
        parser.RemoveErrorListeners();
        parser.AddParseListener(new CancelListener(ct));
        parser.Interpreter.PredictionMode = PredictionMode.SLL;
        parser.ErrorHandler = new BailErrorStrategy();

        SyntaxIssue? first = null;
        try
        {
            parser.sql_script();
        }
        catch (ParseCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            usedLl = true;
            tokens.Seek(0);
            tokens.ResetFurthest();
            parser.Reset();
            var errors = new ParserErrors(tokens);
            parser.AddErrorListener(errors);
            parser.ErrorHandler = new DefaultErrorStrategy();
            parser.Interpreter.PredictionMode = PredictionMode.LL;
            parser.sql_script();
            first = errors.First;
        }

        if (lexErrors.First is { } lexIssue && (first is null || lexIssue.Start <= first.Start))
        {
            first = lexIssue;
        }

        return first;
    }

    private static bool StartsWithWord(string sql, TextRange firstWord)
    {
        for (int i = 0; i < firstWord.Start; i++)
        {
            if (!char.IsWhiteSpace(sql[i]))
            {
                return false; // 주석·괄호·라벨(<<x>>) 등으로 시작한다.
            }
        }

        return true;
    }

    /// <summary>오류 위치의 단어 또는 바로 앞 단어 중, 점으로 이어진 이름의 일부이고 아직 가리지 않은 것.</summary>
    private static bool TryFindKeywordName(string sql, HashSet<int> masked, List<(string Word, TextRange Range)> words, int errorStart,
        out TextRange name)
    {
        name = default;
        int at = words.FindIndex(w => w.Range.Start == errorStart);
        int before = words.FindLastIndex(w => w.Range.End <= errorStart);
        foreach (int i in new[] { at, before })
        {
            if (i < 0)
            {
                continue;
            }

            var r = words[i].Range;
            bool dotBefore = r.Start > 0 && sql[r.Start - 1] == '.';
            bool dotAfter = r.End < sql.Length && sql[r.End] == '.';
            if ((dotBefore || dotAfter) && !masked.Contains(r.Start))
            {
                name = r;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 서버 컴파일 오류(user_errors 의 line·position, 1 기반)를 문서 범위의 <see cref="Diagnostic"/> 로 바꾼다.
    /// <paramref name="sourceStart"/> = 컴파일한 소스(CREATE 문)가 문서에서 시작하는 오프셋. user_errors 의 1행은 CREATE 문의 첫 줄이다.
    /// 범위는 그 위치의 단어 하나(없으면 1글자).
    /// </summary>
    public static IReadOnlyList<Diagnostic> FromServerErrors(string text, int sourceStart, IEnumerable<CompileError> errors)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(errors);
        var result = new List<Diagnostic>();
        foreach (var e in errors)
        {
            int offset = OffsetOf(text, sourceStart, e.Line, e.Column);
            int end = offset;
            while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] is '_' or '$' or '#'))
            {
                end++;
            }

            if (end == offset && offset < text.Length)
            {
                end++;
            }

            result.Add(new Diagnostic(new TextRange(offset, end - offset),
                e.IsWarning ? DiagnosticSeverity.Warning : DiagnosticSeverity.Error, e.Message)
            { Code = "Server" });
        }

        return result;
    }

    private static int OffsetOf(string text, int start, int line, int column)
    {
        int pos = Math.Clamp(start, 0, text.Length);
        for (int l = 1; l < line; l++)
        {
            int nl = text.IndexOf('\n', pos);
            if (nl < 0)
            {
                return text.Length;
            }

            pos = nl + 1;
        }

        int lineEnd = text.IndexOf('\n', pos);
        if (lineEnd < 0)
        {
            lineEnd = text.Length;
        }

        return Math.Min(pos + Math.Max(0, column - 1), lineEnd);
    }

    private static bool IsUnsupported(SyntaxIssue first, List<(string Word, TextRange Range)> words, StatementKind kind)
    {
        foreach (var (w, _) in words)
        {
            if (s_modernMarkers.Contains(w) || (kind != StatementKind.PlSql && s_modernSqlMarkers.Contains(w)))
            {
                return true;
            }
        }

        // IF [NOT] EXISTS (23ai DDL)
        for (int i = 0; i + 1 < words.Count; i++)
        {
            if (words[i].Word.Equals("IF", StringComparison.OrdinalIgnoreCase)
                && (words[i + 1].Word.Equals("EXISTS", StringComparison.OrdinalIgnoreCase)
                    || (words[i + 1].Word.Equals("NOT", StringComparison.OrdinalIgnoreCase) && i + 2 < words.Count
                        && words[i + 2].Word.Equals("EXISTS", StringComparison.OrdinalIgnoreCase)))
                && kind != StatementKind.PlSql)
            {
                return true;
            }
        }

        // FROM 없는 SELECT (23ai)
        if (kind == StatementKind.Query && !words.Exists(w => w.Word.Equals("FROM", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // 첫 단어(명령어)나 DDL 의 둘째 단어(대상 종류)에서 막혔으면 문법이 그 명령 형태를 모르는 것이다.
        if (words.Count == 0 || !s_statementVerbs.Contains(words[0].Word))
        {
            return false;
        }

        if (first.Start <= words[0].Range.Start)
        {
            return true;
        }

        return kind is StatementKind.Ddl or StatementKind.Other && words.Count > 1 && first.Start <= words[1].Range.Start;
    }

    private static List<(string Word, TextRange Range)> Words(string sql, List<Token> l1)
    {
        var words = new List<(string, TextRange)>();
        foreach (var t in l1)
        {
            if (t.Kind is TokenKind.Identifier or TokenKind.Keyword)
            {
                words.Add((sql.Substring(t.Start, t.Length), new TextRange(t.Start, t.Length)));
            }
        }

        return words;
    }

    /// <summary>치환 변수(<c>&amp;name</c>)는 실행 때 바뀌므로 같은 길이의 식별자로 가린다(오프셋 유지).</summary>
    private static string MaskSubstitutions(string sql, List<Token> l1)
    {
        char[]? buf = null;
        foreach (var t in l1)
        {
            if (t.Kind != TokenKind.Substitution)
            {
                continue;
            }

            buf ??= sql.ToCharArray();
            for (int i = t.Start; i < t.End; i++)
            {
                if (buf[i] == '&')
                {
                    buf[i] = 'X';
                }
                else if (buf[i] == '.')
                {
                    buf[i] = ' ';
                }
            }
        }

        return buf is null ? sql : new string(buf);
    }

    private static TextRange EndRange(string sql)
    {
        int end = sql.Length;
        while (end > 0 && char.IsWhiteSpace(sql[end - 1]))
        {
            end--;
        }

        return end == 0 ? new TextRange(0, 0) : new TextRange(end - 1, 1);
    }

    private sealed record SyntaxIssue(int Start, int Length, bool AtEnd, string Message);

    private static string Shorten(string s) => s.Length <= 30 ? s : s[..30] + "…";

    private sealed class ParserErrors(FurthestTokenStream tokens) : BaseErrorListener
    {
        public SyntaxIssue? First { get; private set; }

        public override void SyntaxError(TextWriter output, IRecognizer recognizer, IToken offendingSymbol, int line,
            int charPositionInLine, string msg, RecognitionException e)
        {
            if (First is not null)
            {
                return;
            }

            // 선택 절(FROM 등)이 예측에서 떨어져 나가면 ANTLR 는 문장이 끝났다고 보고 「; 또는 / 필요」 오류를 그 절의 시작에서 낸다.
            // 그때는 예측이 실제로 막힌 곳(가장 멀리 본 토큰)이 더 정확하다. 다른 오류는 ANTLR 위치를 그대로 쓴다.
            var expected = ExpectedSet(recognizer, e);
            string detail = Expecting(recognizer, expected);
            if (expected is not null && expected.Count > 0
                && expected.ToArray().All(t => t is PlSqlParser.SEMICOLON or PlSqlParser.SOLIDUS or TokenConstants.EOF)
                && tokens.Furthest is { } far && (offendingSymbol is null || far.TokenIndex > offendingSymbol.TokenIndex))
            {
                offendingSymbol = far;
                detail = string.Empty;
            }

            if (offendingSymbol is null || offendingSymbol.Type == TokenConstants.EOF)
            {
                First = new SyntaxIssue(int.MaxValue, 0, true, "문장이 끝나지 않았습니다. 빠진 부분이 있는지 확인하세요." + detail);
                return;
            }

            int len = offendingSymbol.StopIndex - offendingSymbol.StartIndex + 1;
            var text = Shorten(offendingSymbol.Text ?? string.Empty);
            First = new SyntaxIssue(offendingSymbol.StartIndex, len, false, $"구문 오류: '{text}' 에서 문장을 이해할 수 없습니다." + detail);
        }

        private static IntervalSet? ExpectedSet(IRecognizer recognizer, RecognitionException? e)
        {
            try
            {
                return e?.GetExpectedTokens() ?? (recognizer as Parser)?.GetExpectedTokens();
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NullReferenceException)
            {
                return null; // 기대 토큰 계산이 실패해도 오류 위치는 낸다.
            }
        }

        private static string Expecting(IRecognizer recognizer, IntervalSet? set)
        {
            if (set is null || set.Count == 0 || set.Count > 5)
            {
                return string.Empty;
            }

            var names = set.ToArray()
                .Where(t => t > 0)
                .Select(t => recognizer.Vocabulary.GetLiteralName(t) ?? recognizer.Vocabulary.GetSymbolicName(t))
                .Where(n => !string.IsNullOrEmpty(n))
                .ToList();
            return names.Count == 0 ? string.Empty : " 필요한 것: " + string.Join(", ", names);
        }
    }

    private sealed class LexerErrors : IAntlrErrorListener<int>
    {
        public SyntaxIssue? First { get; private set; }

        public void SyntaxError(TextWriter output, IRecognizer recognizer, int offendingSymbol, int line, int charPositionInLine,
            string msg, RecognitionException e)
        {
            if (First is not null || recognizer is not Lexer lexer)
            {
                return;
            }

            int start = lexer.TokenStartCharIndex;
            int len = Math.Max(1, lexer.InputStream.Index - start);
            var text = ((ICharStream)lexer.InputStream).GetText(Interval.Of(start, start + len - 1));
            First = new SyntaxIssue(start, len, false, $"알 수 없는 문자: '{Shorten(text)}'");
        }
    }

    /// <summary>파서가 들여다본(예측 포함) 가장 먼 기본 채널 토큰을 기억하는 토큰 흐름.</summary>
    private sealed class FurthestTokenStream(ITokenSource source) : CommonTokenStream(source)
    {
        public IToken? Furthest { get; private set; }

        public void ResetFurthest() => Furthest = null;

        public override IToken LT(int k)
        {
            var t = base.LT(k);
            if (k > 0 && t is not null && (Furthest is null || t.TokenIndex > Furthest.TokenIndex))
            {
                Furthest = t;
            }

            return t!;
        }
    }

    /// <summary>규칙에 들어갈 때마다 취소를 본다(긴 문장 파싱도 즉시 멈춘다).</summary>
    private sealed class CancelListener(CancellationToken ct) : IParseTreeListener
    {
        public void EnterEveryRule(ParserRuleContext ctx) => ct.ThrowIfCancellationRequested();

        public void ExitEveryRule(ParserRuleContext ctx)
        {
        }

        public void VisitErrorNode(IErrorNode node)
        {
        }

        public void VisitTerminal(ITerminalNode node)
        {
        }
    }
}
