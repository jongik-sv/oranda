using System.Globalization;
using System.Text;
using Oranda.Core.Execution;
using Oranda.Core.Formatting;
using Oranda.Core.Language;
using Oranda.Core.MyBatis;
using Oranda.Language.Lexing;

namespace Oranda.App.Features.MyBatisWorkspace.Conversion;

/// <summary>
/// 일반 SQL 탭 → MyBatis 매퍼 문장 바꾸기 엔진(TSK-03-05 J1·J2, MB-08). 화면·파일 쓰기를 모르는 순수 계산이다.
/// 시작 시험 제약: 이 형들에는 Language 의 enum·구조체 필드를 두지 않는다(렉서 토큰은 <see cref="ConvertTok"/> 으로 옮겨 쓴다).
/// </summary>
public static partial class ConvertEngine
{
    /// <summary>
    /// 탭마다 문장을 나눠 줄을 만든다(문장 하나 = 줄 하나). 제안 id 는 줄끼리·<paramref name="existingStatementIds"/> 와 겹치지 않게 접미 2… 를 붙인다.
    /// </summary>
    public static IReadOnlyList<ConvertCandidate> Analyze(IReadOnlyList<ConvertSourceTab> tabs, IReadOnlyCollection<string> existingStatementIds)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        ArgumentNullException.ThrowIfNull(existingStatementIds);

        var taken = new HashSet<string>(existingStatementIds, StringComparer.Ordinal);
        var result = new List<ConvertCandidate>();
        int seq = 0;
        for (int tabIndex = 0; tabIndex < tabs.Count; tabIndex++)
        {
            var tab = tabs[tabIndex];
            var statements = StatementSplitter.Split(tab.Text, SplitOptions.Default);
            for (int si = 0; si < statements.Count; si++)
            {
                seq++;
                var statement = statements[si];
                var kind = KindOf(statement);
                string sql = statement.Text.Replace("\r\n", "\n", StringComparison.Ordinal);
                var toks = Lex(sql);

                var rewriter = new ConvertRewriter(sql, toks, null, tab.Binds, kind ?? ConvertStatementKind.Select, new ConvertOptions());
                rewriter.Run();
                var notes = new List<ConvertNote>();
                if (kind is null) notes.Add(new ConvertNote(ConvertNoteKind.Unsupported));
                else if (kind == ConvertStatementKind.Callable) notes.Add(new ConvertNote(ConvertNoteKind.PlSqlBlock));
                notes.AddRange(rewriter.Notes);

                string id = UniqueId(SuggestBase(kind, statement.Verb, tab.Title, sql, toks, seq), taken);
                result.Add(new ConvertCandidate(
                    tabIndex, tab.Title, si, statement.Text, kind,
                    IncludeByDefault: kind is not null and not ConvertStatementKind.Callable,
                    id, rewriter.BindNames.Count, notes));
            }
        }

        return result;
    }

    /// <summary>
    /// 줄 하나를 사용자가 고른 종류·id 로 바꾼다. <paramref name="binds"/> = 그 줄 탭의 바인드 변수 창 줄.
    /// 서식(<see cref="ConvertOptions.Format"/> 과 <paramref name="formatter"/> 가 모두 있을 때)은 바꾸기 전에 원래 SQL 에 적용한다.
    /// </summary>
    public static ConvertedStatement Convert(
        ConvertCandidate candidate,
        ConvertStatementKind kind,
        string id,
        IReadOnlyList<ConvertBind> binds,
        ConvertOptions options,
        ISqlFormatter? formatter)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(binds);
        ArgumentNullException.ThrowIfNull(options);

        var notes = new List<ConvertNote>();
        string original = candidate.Sql.Replace("\r\n", "\n", StringComparison.Ordinal);
        string sql = original;
        var toks = Lex(sql);
        List<string>? originalNames = null;

        if (options.Format is { } formatOptions && formatter is not null)
        {
            string? formatted = TryFormat(original, formatOptions, formatter);
            if (formatted is null)
            {
                notes.Add(new ConvertNote(ConvertNoteKind.FormatSkipped));
            }
            else
            {
                // 포매터가 바인드 이름 대소문자를 바꿔도 원래 철자로 되돌린다(토큰 열이 같으므로 순서로 짝지음).
                var before = NamesOf(original, toks);
                sql = formatted;
                toks = Lex(sql);
                originalNames = NamesOf(sql, toks).Count == before.Count ? before : null;
            }
        }

        var rewriter = new ConvertRewriter(sql, toks, originalNames, binds, kind, options);
        rewriter.Run();
        notes.AddRange(rewriter.Notes);

        ConvertWherePlan? plan = null;
        if (options.WrapWhere && kind is ConvertStatementKind.Select or ConvertStatementKind.Update or ConvertStatementKind.Delete)
        {
            var wrap = ConvertWhereWrap.Analyze(sql, toks);
            if (kind == ConvertStatementKind.Select && wrap.Outcome == ConvertWhereOutcome.Wrapped) plan = wrap.Plan;
            else if (wrap.Outcome != ConvertWhereOutcome.None) notes.Add(new ConvertNote(ConvertNoteKind.WhereNotWrapped));
        }

        var scenario = rewriter.ScenarioValues();
        // 감쌀 조건이 하나도 없으면(바인드가 전부 Null 값) 감싸지 않고 원래 모양 그대로 쓴다.
        if (plan is not null && !plan.Conditions.Any(c => IsIfWrapped(c, rewriter, scenario))) plan = null;
        var parts = BuildParts(sql, rewriter, plan, scenario);
        string element = Render(kind, id, parts, options.UseCdata);
        return new ConvertedStatement(id, kind, element, scenario, notes);
    }

    /// <summary>Java 식별자 규칙(글자·밑줄·$ 로 시작하고 글자·숫자·밑줄·$ 가 이어진다). 점은 허용하지 않는다.</summary>
    public static bool IsValidId(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        if (!(char.IsLetter(id[0]) || id[0] is '_' or '$')) return false;
        for (int i = 1; i < id.Length; i++)
        {
            if (!(char.IsLetterOrDigit(id[i]) || id[i] is '_' or '$')) return false;
        }

        return true;
    }

    /// <summary><paramref name="baseId"/>, 겹치면 <c>baseId2</c>, <c>baseId3</c>… 중 <paramref name="taken"/> 에 없는 첫 것. 고른 id 를 <paramref name="taken"/> 에 넣는다.</summary>
    public static string UniqueId(string baseId, ISet<string> taken)
    {
        ArgumentNullException.ThrowIfNull(baseId);
        ArgumentNullException.ThrowIfNull(taken);

        string id = baseId;
        for (int n = 2; taken.Contains(id); n++) id = baseId + n.ToString(CultureInfo.InvariantCulture);
        taken.Add(id);
        return id;
    }

    // ---- 종류·id 제안 ----

    static ConvertStatementKind? KindOf(SqlStatement s) => s.Kind switch
    {
        StatementKind.Query => ConvertStatementKind.Select,
        StatementKind.PlSql => ConvertStatementKind.Callable,
        StatementKind.Dml => s.Verb switch
        {
            "INSERT" => ConvertStatementKind.Insert,
            "UPDATE" or "MERGE" => ConvertStatementKind.Update,
            "DELETE" => ConvertStatementKind.Delete,
            _ => null,
        },
        _ => null,
    };

    static string SuggestBase(ConvertStatementKind? kind, string? verb, string tabTitle, string sql, List<ConvertTok> toks, int seq)
    {
        string prefix = kind switch
        {
            ConvertStatementKind.Select => "select",
            ConvertStatementKind.Insert => "insert",
            ConvertStatementKind.Update => verb == "MERGE" ? "merge" : "update",
            ConvertStatementKind.Delete => "delete",
            ConvertStatementKind.Callable => "call",
            _ => "statement",
        };
        string? name = PascalAscii(tabTitle) ?? PascalAscii(FirstTableName(sql, toks));
        return prefix + (name ?? seq.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>FROM·UPDATE·INSERT/MERGE INTO·DELETE 바로 뒤의 첫 테이블 이름(스키마 뒤 마지막 부분). 없으면 null.</summary>
    static string? FirstTableName(string sql, List<ConvertTok> toks)
    {
        for (int i = 0; i + 1 < toks.Count; i++)
        {
            if (toks[i].Kind != ConvertTokKind.Word) continue;
            bool target =
                ConvertTok.Is(sql, toks[i], "FROM") || ConvertTok.Is(sql, toks[i], "UPDATE") || ConvertTok.Is(sql, toks[i], "DELETE")
                || (ConvertTok.Is(sql, toks[i], "INTO") && i > 0
                    && (ConvertTok.Is(sql, toks[i - 1], "INSERT") || ConvertTok.Is(sql, toks[i - 1], "MERGE")));
            if (!target) continue;
            int j = i + 1;
            if (ConvertTok.Is(sql, toks[i], "DELETE") && j < toks.Count && ConvertTok.Is(sql, toks[j], "FROM")) j++;
            // 치환 스키마(&schema.ORDERS, &schema..ORDERS 의 마침표 포함)는 건너뛰고 뒤 이름을 쓴다(.. 는 렉서가 토큰 하나로 낸다).
            if (j < toks.Count && toks[j].Kind == ConvertTokKind.Subst)
            {
                j++;
                while (j < toks.Count && (toks[j].Kind == ConvertTokKind.Dot || ConvertTok.IsDoubleDot(sql, toks[j]))) j++;
            }

            string? last = null;
            while (j < toks.Count && toks[j].Kind is ConvertTokKind.Word or ConvertTokKind.Quoted)
            {
                last = ConvertTok.Name(sql, toks[j]);
                if (j + 2 < toks.Count && toks[j + 1].Kind == ConvertTokKind.Dot) j += 2;
                else break;
            }

            if (last is not null) return last;
        }

        return null;
    }

    /// <summary>ASCII 영숫자 단어로만 이뤄진 글을 PascalCase 로(구분자 = 그 밖의 ASCII 문자). 비 ASCII 글자가 있거나 글자가 없으면 null.</summary>
    static string? PascalAscii(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var sb = new StringBuilder();
        int wordStart = -1;
        bool anyLetter = false;
        for (int i = 0; i <= text.Length; i++)
        {
            bool inWord = i < text.Length && char.IsAsciiLetterOrDigit(text[i]);
            if (i < text.Length)
            {
                if (text[i] > 127) return null;
                anyLetter |= char.IsAsciiLetter(text[i]);
            }

            if (inWord)
            {
                if (wordStart < 0) wordStart = i;
                continue;
            }

            if (wordStart < 0) continue;
            string word = text[wordStart..i];
            wordStart = -1;
            bool uniform = !(word.Any(char.IsAsciiLetterUpper) && word.Any(char.IsAsciiLetterLower));
            sb.Append(char.ToUpperInvariant(word[0]));
            sb.Append(uniform ? word[1..].ToLowerInvariant() : word[1..]);
        }

        return anyLetter && sb.Length > 0 ? sb.ToString() : null;
    }

    // ---- 서식 ----

    static string? TryFormat(string original, FormatterOptions options, ISqlFormatter formatter)
    {
        // 이미 #{ }·${ } 가 든 글은 포매터가 모르는 표기라 넣지 않는다.
        if (original.Contains("#{", StringComparison.Ordinal) || original.Contains("${", StringComparison.Ordinal)) return null;
        var result = formatter.Format(original, options);
        if (!result.Succeeded) return null;
        string text = result.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>바인드·치환 토큰의 이름(접두 제외)을 나온 순서대로.</summary>
    static List<string> NamesOf(string sql, List<ConvertTok> toks)
    {
        var names = new List<string>();
        foreach (var t in toks)
        {
            if (t.Kind is ConvertTokKind.Bind or ConvertTokKind.Subst) names.Add(ConvertTok.Name(sql, t));
        }

        return names;
    }

    // ---- 렉싱 ----

    /// <summary>문장 글을 렉싱해 <see cref="ConvertTok"/> 목록으로 옮긴다(Language 형이 이 엔진 밖으로 새지 않게).</summary>
    internal static List<ConvertTok> Lex(string text)
    {
        var raw = SqlLexer.Tokenize(text);
        var list = new List<ConvertTok>(raw.Count);
        foreach (var t in raw)
        {
            var kind = t.Kind switch
            {
                TokenKind.Keyword or TokenKind.Identifier => ConvertTokKind.Word,
                TokenKind.QuotedIdent => ConvertTokKind.Quoted,
                TokenKind.Bind => ConvertTokKind.Bind,
                TokenKind.Substitution => ConvertTokKind.Subst,
                TokenKind.Comment or TokenKind.Hint => ConvertTokKind.Comment,
                TokenKind.Punct => text[t.Start] switch
                {
                    '(' => ConvertTokKind.Open,
                    ')' => ConvertTokKind.Close,
                    '.' => ConvertTokKind.Dot,
                    _ => ConvertTokKind.Other,
                },
                _ => ConvertTokKind.Other,
            };
            list.Add(new ConvertTok(t.Start, t.Length, kind));
        }

        return list;
    }

    /// <summary>줄 시작 상태가 문자열·따옴표 이름 안인가(그 줄은 들여쓰기하면 값이 바뀐다).</summary>
    internal static int LexLineState(ReadOnlySpan<char> line, int state) => SqlLexer.LexLine(line, state, null);

    internal static bool IsInsideLiteral(int state) => (state & 0xFF) is SqlLexer.InString or SqlLexer.InQString or SqlLexer.InQuotedIdent;
}
