using System.Collections.Frozen;
using Oranda.Core.Execution;
using Oranda.Core.Journal;
using Oranda.Core.Text;

namespace Oranda.Language.Lexing;

/// <summary>문장 토큰으로 <see cref="SqlStatement"/>(분류·동사·바인드·치환 변수·위험 대상)를 만든다.</summary>
internal static class StatementClassifier
{
    static readonly FrozenSet<string> DmlWords = new[] { "INSERT", "UPDATE", "DELETE", "MERGE", "LOCK", "CALL" }
        .ToFrozenSet(StringComparer.Ordinal);

    static readonly FrozenSet<string> DdlWords = new[]
    {
        "CREATE", "ALTER", "DROP", "TRUNCATE", "RENAME", "COMMENT", "GRANT", "REVOKE", "ANALYZE", "AUDIT", "NOAUDIT",
        "FLASHBACK", "PURGE", "ASSOCIATE", "DISASSOCIATE",
    }.ToFrozenSet(StringComparer.Ordinal);

    static readonly FrozenSet<string> TxWords = new[] { "COMMIT", "ROLLBACK", "SAVEPOINT" }
        .ToFrozenSet(StringComparer.Ordinal);

    /// <summary>DROP/TRUNCATE 뒤 대상 이름 앞에 오는 종류·수식어(이름이 아님).</summary>
    static readonly FrozenSet<string> ObjectWords = new[]
    {
        "PUBLIC", "TABLE", "VIEW", "MATERIALIZED", "INDEX", "USER", "ROLE", "SYNONYM", "SEQUENCE", "PACKAGE", "BODY",
        "PROCEDURE", "FUNCTION", "TRIGGER", "TYPE", "DATABASE", "LINK", "TABLESPACE", "CLUSTER", "DIRECTORY", "PROFILE",
        "IF", "EXISTS", "DOMAIN", "SCHEMA", "LIBRARY", "OPERATOR", "CONTEXT", "EDITION", "PLUGGABLE", "JAVA", "SOURCE",
        "CLASS", "RESOURCE", "DIMENSION", "OUTLINE", "FLASHBACK", "ARCHIVE", "RESTORE", "POINT", "SNAPSHOT", "LOG", "ON",
    }.ToFrozenSet(StringComparer.Ordinal);

    public static SqlStatement Build(string text, List<Token> toks, int start, int end, bool blockType)
    {
        string body = text.Substring(start, end - start);
        string? w0 = Word(text, toks, 0), w1 = Word(text, toks, 1);
        var kind = StatementKind.Other;
        bool uncertain = false;
        string? verb = w0;

        if (w0 is null)
        {
            // 괄호로 시작(부분 질의) 또는 VALUES 등
            if (toks[0].Kind == TokenKind.Punct && text[toks[0].Start] == '(') kind = StatementKind.Query;
            else uncertain = true;
        }
        else if (w0 is "SELECT" or "WITH" or "VALUES" or "TABLE") kind = w0 == "TABLE" ? Uncertain(out uncertain) : StatementKind.Query;
        else if (w0 is "DECLARE" or "BEGIN") kind = StatementKind.PlSql;
        else if (w0 == "SET" && w1 is "TRANSACTION" or "CONSTRAINT" or "CONSTRAINTS") { kind = StatementKind.TransactionControl; verb = "SET " + w1; }
        else if (TxWords.Contains(w0)) kind = StatementKind.TransactionControl;
        else if (DmlWords.Contains(w0)) kind = StatementKind.Dml;
        else if (DdlWords.Contains(w0))
        {
            kind = StatementKind.Ddl;
            if (w0 == "ALTER" && w1 is "SYSTEM" or "SESSION") verb = "ALTER " + w1;
        }
        else if (w0 is "EXPLAIN" or "SET") kind = StatementKind.Other;
        else uncertain = true;

        bool plsqlBlock = blockType;
        var binds = new List<string>();
        var subs = new List<string>();
        int depth = 0;
        bool hasWhere = false;
        foreach (var t in toks)
        {
            switch (t.Kind)
            {
                case TokenKind.Bind when !plsqlBlock || kind == StatementKind.PlSql:
                    AddName(binds, text.Substring(t.Start + 1, t.Length - 1));
                    break;
                case TokenKind.Substitution:
                    {
                        int skip = text[t.Start + 1] == '&' ? 2 : 1;
                        AddName(subs, text.Substring(t.Start + skip, t.Length - skip));
                        break;
                    }

                case TokenKind.Punct:
                    if (text[t.Start] == '(') depth++;
                    else if (text[t.Start] == ')') depth--;
                    break;
                case TokenKind.Keyword or TokenKind.Identifier when depth == 0
                    && text.AsSpan(t.Start, t.Length).Equals("WHERE", StringComparison.OrdinalIgnoreCase):
                    hasWhere = true;
                    break;
            }
        }

        string? danger = null;
        if (kind == StatementKind.Ddl)
        {
            if (verb == "ALTER SYSTEM") danger = "SYSTEM";
            else if (w0 is "DROP" or "TRUNCATE") danger = TargetName(text, toks);
        }

        return new SqlStatement(body, new TextRange(start, end - start), kind)
        {
            Verb = verb,
            BindNames = binds,
            SubstitutionNames = subs,
            IsUnfilteredDml = kind == StatementKind.Dml && w0 is "UPDATE" or "DELETE" && !hasWhere,
            DangerousTarget = danger,
            ChangeTarget = kind == StatementKind.Ddl ? ChangeTargetOf(text, toks, w0) : null,
            IsClassificationUncertain = uncertain,
        };
    }

    static StatementKind Uncertain(out bool uncertain)
    {
        uncertain = true;
        return StatementKind.Other;
    }

    static void AddName(List<string> list, string name)
    {
        foreach (var x in list)
        {
            if (string.Equals(x, name, StringComparison.OrdinalIgnoreCase)) return;
        }

        list.Add(name);
    }

    /// <summary>첫 단어를 대문자로(식별자·키워드 토큰만). 아니면 null.</summary>
    static string? Word(string text, List<Token> toks, int i)
    {
        if (i >= toks.Count) return null;
        var t = toks[i];
        return t.Kind is TokenKind.Keyword or TokenKind.Identifier
            ? text.Substring(t.Start, t.Length).ToUpperInvariant()
            : null;
    }

    /// <summary>DROP/TRUNCATE 의 대상 이름(스키마.이름). 종류 단어는 건너뛴다.</summary>
    static string? TargetName(string text, List<Token> toks)
    {
        int i = 1;
        while (i < toks.Count && toks[i].Kind is TokenKind.Keyword or TokenKind.Identifier
               && ObjectWords.Contains(text.Substring(toks[i].Start, toks[i].Length).ToUpperInvariant()))
            i++;
        if (i >= toks.Count) return null;
        var sb = new System.Text.StringBuilder();
        while (i < toks.Count && toks[i].Kind is TokenKind.Keyword or TokenKind.Identifier or TokenKind.QuotedIdent)
        {
            sb.Append(text, toks[i].Start, toks[i].Length);
            if (i + 1 < toks.Count && toks[i + 1].Kind == TokenKind.Punct && text[toks[i + 1].Start] == '.') { sb.Append('.'); i += 2; }
            else break;
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    /// <summary>변경 일지가 이전 DDL 을 보관할 수 있는 객체 종류의 첫 단어(PACKAGE·TYPE 은 뒤에 BODY 가 올 수 있다).</summary>
    static readonly FrozenSet<string> JournalTypeWords = new[]
    {
        "TABLE", "INDEX", "VIEW", "SEQUENCE", "PROCEDURE", "FUNCTION", "TRIGGER", "SYNONYM", "PACKAGE", "TYPE", "MATERIALIZED", "DATABASE",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>CREATE OR REPLACE 와 객체 종류 사이에 올 수 있는 수식어.</summary>
    static readonly FrozenSet<string> CreateModifiers = new[] { "FORCE", "NOFORCE", "EDITIONABLE", "NONEDITIONABLE", "EDITIONING" }
        .ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// ALTER·DROP·CREATE OR REPLACE·RENAME 의 대상(변경 일지용). 종류가 보관 대상이 아니거나(ALTER SESSION·USER 등)
    /// 새 객체를 만드는 CREATE, TRUNCATE·COMMENT 는 null. 기존 분류 결과에는 영향을 주지 않는다.
    /// </summary>
    static ChangeTarget? ChangeTargetOf(string text, List<Token> toks, string? w0)
    {
        switch (w0)
        {
            case "RENAME":
                {
                    var rename = ReadName(text, toks, 1, out _);
                    return rename is { Count: 1 } ? new ChangeTarget(null, rename[0], null, ChangeOperation.Rename) : null;
                }

            case "ALTER":
                return ObjectTarget(text, toks, 1, ChangeOperation.Alter);
            case "DROP":
                return ObjectTarget(text, toks, 1, ChangeOperation.Drop);
            case "CREATE" when Is(text, toks, 1, "OR") && Is(text, toks, 2, "REPLACE"):
                {
                    var i = 3;
                    while (Word(text, toks, i) is { } w && (CreateModifiers.Contains(w) || (w == "NO" && Is(text, toks, i + 1, "FORCE"))))
                    {
                        i += w == "NO" ? 2 : 1;
                    }

                    return ObjectTarget(text, toks, i, ChangeOperation.CreateOrReplace);
                }

            default:
                return null;
        }
    }

    static ChangeTarget? ObjectTarget(string text, List<Token> toks, int i, ChangeOperation operation)
    {
        var isPublic = false;
        if (Is(text, toks, i, "PUBLIC"))
        {
            isPublic = true;
            i++;
        }

        string? type;
        switch (Word(text, toks, i))
        {
            case "PACKAGE" or "TYPE" when Is(text, toks, i + 1, "BODY"):
                type = Word(text, toks, i) + " BODY";
                i += 2;
                break;
            case "MATERIALIZED" when Is(text, toks, i + 1, "VIEW") && !Is(text, toks, i + 2, "LOG"): // MATERIALIZED VIEW LOG 는 객체 DDL 이 아니다.
                type = "MATERIALIZED VIEW";
                i += 2;
                break;
            case "DATABASE" when Is(text, toks, i + 1, "LINK"):
                type = "DATABASE LINK";
                i += 2;
                break;
            case { } w when JournalTypeWords.Contains(w) && w is not ("MATERIALIZED" or "DATABASE"):
                type = w;
                i++;
                break;
            default:
                return null;
        }

        if (isPublic && type is not ("SYNONYM" or "DATABASE LINK"))
        {
            return null;
        }

        if (Is(text, toks, i, "IF") && Is(text, toks, i + 1, "EXISTS")) // DROP/ALTER ... IF EXISTS name
        {
            i += 2;
        }

        var parts = ReadName(text, toks, i, out _);
        if (parts is null)
        {
            return null;
        }

        var publicOwner = isPublic ? "PUBLIC" : null;
        if (type == "DATABASE LINK")
        {
            return new ChangeTarget(publicOwner, string.Join('.', parts), type, operation); // 링크 이름은 점을 포함할 수 있다(sales.world).
        }

        return parts.Count switch
        {
            1 => new ChangeTarget(publicOwner, parts[0], type, operation),
            2 when !isPublic => new ChangeTarget(parts[0], parts[1], type, operation),
            _ => null,
        };
    }

    /// <summary><paramref name="i"/> 부터 <c>a.b.c</c> 모양의 이름을 읽는다. 따옴표 없는 부분은 대문자, 따옴표 이름은 따옴표를 벗기고 그대로.</summary>
    static List<string>? ReadName(string text, List<Token> toks, int i, out int next)
    {
        var parts = new List<string>();
        next = i;
        while (next < toks.Count && toks[next].Kind is TokenKind.Keyword or TokenKind.Identifier or TokenKind.QuotedIdent)
        {
            var t = toks[next];
            var raw = text.Substring(t.Start, t.Length);
            parts.Add(t.Kind == TokenKind.QuotedIdent && raw.Length >= 2 && raw[0] == '"'
                ? raw[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal)
                : raw.ToUpperInvariant());
            next++;
            if (next < toks.Count && toks[next].Kind == TokenKind.Punct && text[toks[next].Start] == '.')
            {
                next++;
                if (next >= toks.Count || toks[next].Kind is not (TokenKind.Keyword or TokenKind.Identifier or TokenKind.QuotedIdent))
                {
                    return null; // 점 뒤가 이름이 아니다(치환 변수 scott.&t 등): 대상을 모른다.
                }

                continue;
            }

            break;
        }

        return parts.Count == 0 ? null : parts;
    }

    static bool Is(string text, List<Token> toks, int i, string word) => Word(text, toks, i) == word;
}
