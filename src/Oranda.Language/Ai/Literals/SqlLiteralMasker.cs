using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Composition;
using Oranda.Language.Lexing;

namespace Oranda.Language.Ai.Literals;

/// <summary>
/// <see cref="ISqlLiteralMasker"/> 구현(설계 ai-tuning-2). 렉서(<see cref="SqlLexer"/>) 토큰 기준으로 SQL 글의 리터럴을 <c>:vN</c> 으로 가리고 되돌린다.
/// <list type="bullet">
/// <item>문자열(<c>'…'</c>·<c>N'…'</c>·<c>q'…'</c>)과 숫자를 가린다. <c>DATE '…'</c>·<c>TIMESTAMP '…'</c> 은 키워드까지 한 묶음이다. 줄을 넘는 문자열은 하나로 합친다.</item>
/// <item>주석 본문은 비우고(<c>--</c>·<c>/**/</c>) 힌트(<c>/*+ … */</c>)는 그대로 둔다.</item>
/// <item>읽을 수 없는 글은 닫힌 쪽으로 실패한다: 렉서 Error 토큰, 닫히지 않은 문자열·따옴표 식별자·힌트, 사전에 없는 우리 모양의 자리표시.</item>
/// </list>
/// </summary>
[OrandaService(typeof(ISqlLiteralMasker))]
internal sealed class SqlLiteralMasker : ISqlLiteralMasker
{
    private const string BlockCommentReplacement = "/**/";
    private const string LineCommentReplacement = "--";
    private const int MaxPrefixTries = 10_000;

    /// <summary>줄 경계를 넘는 토큰을 합친 토큰. 위치는 문서 오프셋이다.</summary>
    private readonly record struct Lexeme(int Start, int End, TokenKind Kind);

    public LiteralVault CreateVault(string statementText)
    {
        ArgumentNullException.ThrowIfNull(statementText);
        var binds = new List<string>();
        foreach (var lexeme in Lex(statementText, out _))
        {
            if (lexeme.Kind == TokenKind.Bind)
            {
                binds.Add(statementText[lexeme.Start..lexeme.End]);
            }
        }

        for (var i = 0; i < MaxPrefixTries; i++)
        {
            var prefix = i switch
            {
                0 => LiteralVault.DefaultPrefix,
                1 => "lit",
                _ => "lit" + i.ToString(CultureInfo.InvariantCulture),
            };
            var candidate = new LiteralVault(prefix);
            if (!binds.Exists(candidate.IsOwnPlaceholder))
            {
                return candidate;
            }
        }

        // 이만큼 겹치는 문장은 정상이 아니다. 자리표시가 겹치는 사전을 내주느니 실패시킨다.
        throw new InvalidOperationException("No free placeholder prefix for this statement.");
    }

    public SqlMaskResult Mask(string sql, LiteralVault vault)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(vault);
        var masked = Transform(sql, vault);
        if (masked is null)
        {
            return SqlMaskResult.Failed;
        }

        vault.RememberStatement(masked, sql);
        return SqlMaskResult.Ok(masked);
    }

    public string MaskPredicate(string predicate, LiteralVault vault)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(vault);
        if (string.IsNullOrWhiteSpace(predicate))
        {
            return predicate;
        }

        return Transform(predicate, vault) ?? ISqlLiteralMasker.RedactedPlaceholder;
    }

    public SqlRestoreResult Restore(string sql, LiteralVault vault)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(vault);

        // 가린 대상 문장을 그대로 돌려받았으면 주석·sql_id 가 남은 원래 글을 쓴다.
        if (vault.TryGetStatement(sql, out var original))
        {
            return SqlRestoreResult.Ok(original);
        }

        return RestoreText(sql, vault);
    }

    private static SqlRestoreResult RestoreText(string sql, LiteralVault vault)
    {
        var builder = new StringBuilder(sql.Length);
        var cursor = 0;
        foreach (var lexeme in Lex(sql, out _))
        {
            var text = sql[lexeme.Start..lexeme.End];

            // 힌트 안의 자리표시도 되돌린다(가릴 때 힌트 안의 값도 가렸다).
            if (lexeme.Kind == TokenKind.Hint && text.Length >= 5 && text.EndsWith("*/", StringComparison.Ordinal))
            {
                var inner = RestoreText(text[3..^2], vault);
                if (!inner.Succeeded)
                {
                    return inner;
                }

                builder.Append(sql, cursor, lexeme.Start - cursor).Append("/*+").Append(inner.Text).Append("*/");
                cursor = lexeme.End;
                continue;
            }

            var placeholder = lexeme.Kind switch
            {
                TokenKind.Bind => text,
                TokenKind.StringLiteral => QuotedPlaceholder(text),
                _ => null,
            };
            if (placeholder is null || !vault.IsOwnPlaceholder(placeholder))
            {
                continue;
            }

            if (!vault.TryGetRaw(placeholder, out var raw))
            {
                return SqlRestoreResult.Unknown(placeholder);
            }

            // 자리표시가 앞뒤 글자와 붙어 있으면 원래 값이 식별자·다른 문자열에 섞여 토큰 경계가 바뀌므로 공백으로 떼어 놓는다
            // (x:v2 → x12345 가 아니라 x 12345, 'abc':v1 → 'abc''KIM' 이 아니라 'abc' 'KIM').
            builder.Append(sql, cursor, lexeme.Start - cursor);
            if (builder.Length > 0 && (IsIdentPart(builder[^1]) || builder[^1] is '\'' or '"' or '.'))
            {
                builder.Append(' ');
            }

            builder.Append(raw);
            if (lexeme.End < sql.Length && (IsIdentPart(sql[lexeme.End]) || sql[lexeme.End] is '\'' or '"' or '.'))
            {
                builder.Append(' ');
            }

            cursor = lexeme.End;
        }

        builder.Append(sql, cursor, sql.Length - cursor);
        return SqlRestoreResult.Ok(builder.ToString());
    }

    /// <summary><c>':v1'</c> 처럼 따옴표로 감싼 자리표시의 안쪽 글(<c>:v1</c>). 그런 모양이 아니면 null.</summary>
    private static string? QuotedPlaceholder(string literal)
    {
        if (literal.Length < 4 || literal[0] != '\'' || literal[^1] != '\'')
        {
            return null;
        }

        var inner = literal[1..^1];
        return inner[0] == ':' ? inner : null;
    }

    /// <summary>
    /// 리터럴을 자리표시로 바꾼 글. 읽을 수 없으면 null(호출한 쪽이 닫는다).
    /// </summary>
    private static string? Transform(string text, LiteralVault vault, bool inHint = false)
    {
        var lexemes = Lex(text, out var unterminated);
        if (unterminated)
        {
            return null;
        }

        var builder = new StringBuilder(text.Length);
        var cursor = 0;
        for (var i = 0; i < lexemes.Count; i++)
        {
            var lexeme = lexemes[i];
            var end = lexeme.End;
            string replacement;
            switch (lexeme.Kind)
            {
                case TokenKind.Error:
                    return null;

                case TokenKind.Bind:
                    // 우리 모양의 바인드가 사전에 없으면 LLM 이 지어낸 것이거나 원래 문장의 바인드와 겹친 것이다. 되돌릴 때 모호해지므로 닫는다.
                    if (vault.IsOwnPlaceholder(text[lexeme.Start..lexeme.End]) && !vault.TryGetRaw(text[lexeme.Start..lexeme.End], out _))
                    {
                        return null;
                    }

                    continue;

                case TokenKind.Comment:
                    // 힌트 본문 안의 주석은 힌트 문법이 아니다. 힌트 전체를 비우도록 실패시킨다.
                    if (inHint)
                    {
                        return null;
                    }

                    replacement = text[lexeme.Start] == '-' ? LineCommentReplacement : BlockCommentReplacement;
                    break;

                case TokenKind.Hint:
                    if (inHint)
                    {
                        return null;
                    }

                    // 힌트로 남기는 것은 SELECT·INSERT·UPDATE·DELETE·MERGE 바로 뒤의 첫 /*+ */ 뿐이다(Oracle 이 힌트로 읽는 자리).
                    // 그 안의 문자열·숫자는 가린다. 다른 자리의 /*+ 는 평범한 주석이므로 본문을 비우고, 읽을 수 없는 힌트도 비운다.
                    replacement = MaskHint(text, lexemes, i, vault) ?? BlockCommentReplacement;
                    break;

                case TokenKind.Number:
                case TokenKind.StringLiteral:
                    replacement = vault.Add(text[lexeme.Start..lexeme.End]);
                    break;

                case TokenKind.Keyword when IsTypedLiteralKeyword(text, lexeme)
                    && i + 1 < lexemes.Count
                    && lexemes[i + 1].Kind == TokenKind.StringLiteral
                    && IsBlank(text, lexeme.End, lexemes[i + 1].Start):
                    end = lexemes[i + 1].End;
                    replacement = vault.Add(text[lexeme.Start..end]);
                    i++;
                    break;

                default:
                    continue;
            }

            // 자리표시 바로 뒤에 식별자 글자가 붙어 있으면(1then) 다른 바인드 이름(:v1then)으로 읽히므로 공백을 하나 넣는다.
            if (replacement[0] == ':' && end < text.Length && IsIdentPart(text[end]))
            {
                replacement += " ";
            }

            builder.Append(text, cursor, lexeme.Start - cursor).Append(replacement);
            cursor = end;
        }

        builder.Append(text, cursor, text.Length - cursor);
        return builder.ToString();
    }

    private static string? MaskHint(string text, List<Lexeme> lexemes, int index, LiteralVault vault)
    {
        var lexeme = lexemes[index];
        if (index == 0)
        {
            return null;
        }

        var owner = lexemes[index - 1];
        var word = text.AsSpan(owner.Start, owner.End - owner.Start);
        var ownsHint = owner.Kind == TokenKind.Keyword
            && (word.Equals("SELECT", StringComparison.OrdinalIgnoreCase)
                || word.Equals("INSERT", StringComparison.OrdinalIgnoreCase)
                || word.Equals("UPDATE", StringComparison.OrdinalIgnoreCase)
                || word.Equals("DELETE", StringComparison.OrdinalIgnoreCase)
                || word.Equals("MERGE", StringComparison.OrdinalIgnoreCase));
        if (!ownsHint || !IsBlank(text, owner.End, lexeme.Start))
        {
            return null;
        }

        var raw = text.AsSpan(lexeme.Start, lexeme.End - lexeme.Start);
        if (raw.Length < 5 || !raw.EndsWith("*/", StringComparison.Ordinal))
        {
            return null;
        }

        var body = Transform(raw[3..^2].ToString(), vault, inHint: true);

        // 힌트 모양이 아닌 글(이름·메일 같은 자유 글)은 Oracle 이 힌트로 읽지 않고 무시하므로 사실상 주석 본문이다. 비운다.
        return body is null || !LooksLikeHints(body) ? null : "/*+" + body + "*/";
    }

    /// <summary>
    /// 힌트 이름 꼴의 글인가: 괄호 밖의 낱말은 뒤에 <c>(</c> 가 오거나 인자 없는 알려진 힌트여야 하고, 낱말은 모두 ASCII 이며 괄호가 맞아야 한다.
    /// (괄호 안의 별칭·인덱스 이름은 메타데이터로 보고 둔다.)
    /// </summary>
    private static bool LooksLikeHints(string body)
    {
        var lexemes = Lex(body, out _);
        var depth = 0;
        for (var i = 0; i < lexemes.Count; i++)
        {
            var lexeme = lexemes[i];
            var word = body[lexeme.Start..lexeme.End];
            switch (lexeme.Kind)
            {
                case TokenKind.Punct:
                    if (word == "(")
                    {
                        depth++;
                    }
                    else if (word == ")" && --depth < 0)
                    {
                        return false;
                    }

                    break;

                case TokenKind.Identifier or TokenKind.Keyword or TokenKind.QuotedIdent:
                    if (!word.All(char.IsAscii))
                    {
                        return false;
                    }

                    if (depth == 0 && lexeme.Kind != TokenKind.QuotedIdent)
                    {
                        var followedByParen = i + 1 < lexemes.Count && body[lexemes[i + 1].Start] == '(' && lexemes[i + 1].Kind == TokenKind.Punct;
                        if (!followedByParen && !NoArgumentHints.Contains(word))
                        {
                            return false;
                        }
                    }

                    break;

                case TokenKind.Bind or TokenKind.Operator:
                    break;

                default:
                    return false;
            }
        }

        return depth == 0;
    }

    /// <summary>괄호 없이 쓰는 힌트 이름.</summary>
    private static readonly FrozenSet<string> NoArgumentHints = new[]
    {
        "ALL_ROWS", "ORDERED", "RESULT_CACHE", "NO_RESULT_CACHE", "NO_MERGE", "MERGE", "NO_QUERY_TRANSFORMATION", "NOPARALLEL", "NO_PARALLEL",
        "PARALLEL", "APPEND", "NOAPPEND", "CACHE", "NOCACHE", "UNNEST", "NO_UNNEST", "PUSH_SUBQ", "NO_PUSH_SUBQ", "NO_EXPAND", "USE_CONCAT",
        "REWRITE", "NO_REWRITE", "STAR_TRANSFORMATION", "NO_STAR_TRANSFORMATION", "GATHER_PLAN_STATISTICS", "MONITOR", "NO_MONITOR",
        "INMEMORY", "NO_INMEMORY", "STATEMENT_QUEUING", "NO_STATEMENT_QUEUING", "DRIVING_SITE", "FRESH_MV", "NO_FACT", "FACT",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '#';

    private static bool IsTypedLiteralKeyword(string text, Lexeme lexeme)
    {
        var word = text.AsSpan(lexeme.Start, lexeme.End - lexeme.Start);
        return word.Equals("DATE", StringComparison.OrdinalIgnoreCase) || word.Equals("TIMESTAMP", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBlank(string text, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 렉서를 줄 단위로 돌려 줄을 넘는 문자열·주석·힌트·따옴표 식별자를 토큰 하나로 합친다
    /// (<see cref="SqlLexer.Tokenize"/> 는 줄마다 토큰을 나누는데, 이어지는 줄인지 따로 닫힌 문자열인지 구분할 수 없다).
    /// 글 끝에서 닫히지 않은 문자열·따옴표 식별자·힌트가 있으면 <paramref name="unterminated"/> 가 참이다.
    /// </summary>
    private static List<Lexeme> Lex(string text, out bool unterminated)
    {
        var result = new List<Lexeme>();
        var line = new List<Token>();
        var state = SqlLexer.Normal;
        var open = -1;
        var pos = 0;
        while (pos <= text.Length)
        {
            var newline = text.IndexOf('\n', pos);
            var end = newline < 0 ? text.Length : newline;
            var before = state;
            line.Clear();
            state = SqlLexer.LexLine(text.AsSpan(pos, end - pos), state, line);

            for (var k = 0; k < line.Count; k++)
            {
                var token = line[k];
                var start = pos + token.Start;
                if (k == 0 && token.Start == 0 && Mode(before) != SqlLexer.Normal && open >= 0)
                {
                    result[open] = result[open] with { End = start + token.Length };
                    continue;
                }

                result.Add(new Lexeme(start, start + token.Length, token.Kind));
            }

            if (Mode(state) == SqlLexer.Normal)
            {
                open = -1;
            }
            else if (Mode(before) == SqlLexer.Normal || line.Count > 1)
            {
                // 이 줄에서 열려 다음 줄로 이어지는 토큰은 줄의 마지막 토큰이다. 앞 줄에서 이어지던 토큰이 이 줄에서 닫히고
                // 새 토큰이 열린 경우(토큰 2개 이상)도 마지막 토큰이다. 토큰 하나 이하면 앞 줄 토큰이 계속 열려 있다.
                open = result.Count - 1;
            }

            if (newline < 0)
            {
                break;
            }

            pos = newline + 1;
        }

        unterminated = Mode(state) is SqlLexer.InString or SqlLexer.InQString or SqlLexer.InQuotedIdent or SqlLexer.InHint;
        return result;
    }

    private static int Mode(int state) => state & 0xFF;
}
